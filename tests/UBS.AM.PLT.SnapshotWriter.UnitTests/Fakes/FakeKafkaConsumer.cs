using Confluent.Kafka;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;

/// <summary>
/// Scripted <see cref="IConsumer{TKey,TValue}"/> fake modelling Kafka's per-partition
/// delivery: each partition has its own ordered queue and cursor, and <see cref="Consume(CancellationToken)"/>
/// round-robins across partitions with pending messages — so a message seeked back on one
/// partition interleaves with fresh messages on others, exactly like a real multi-partition
/// assignment. <see cref="Seek"/> moves only that partition's cursor back so the same
/// message is redelivered. Enqueued <see cref="ConsumeException"/>s are thrown (in order)
/// before any message delivery. Throws <see cref="OperationCanceledException"/> when the
/// script is exhausted or <see cref="MaxConsumeCalls"/> is reached, ending the consume loop.
/// </summary>
public sealed class FakeKafkaConsumer : IConsumer<string, string>
{
    private readonly Queue<ConsumeException> _errors = new();
    private readonly Dictionary<TopicPartition, List<ConsumeResult<string, string>>> _partitions = [];
    private readonly Dictionary<TopicPartition, int> _cursors = [];
    private readonly List<TopicPartition> _roundRobinOrder = [];
    private int _nextPartition;
    private int _consumeCalls;

    public int MaxConsumeCalls { get; set; } = 50;

    public List<TopicPartitionOffset> Commits { get; } = [];

    public List<TopicPartitionOffset> Seeks { get; } = [];

    public List<string> SubscribedTopics { get; } = [];

    public bool Closed { get; private set; }

    public void Enqueue(ConsumeResult<string, string> result)
    {
        var partition = result.TopicPartition;
        if (!_partitions.TryGetValue(partition, out var queue))
        {
            queue = [];
            _partitions[partition] = queue;
            _cursors[partition] = 0;
            _roundRobinOrder.Add(partition);
        }

        queue.Add(result);
    }

    public void Enqueue(ConsumeException error) => _errors.Enqueue(error);

    public ConsumeResult<string, string> Consume(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_consumeCalls >= MaxConsumeCalls)
        {
            throw new OperationCanceledException("Fake consumer call budget exhausted.");
        }

        _consumeCalls++;

        if (_errors.Count > 0)
        {
            throw _errors.Dequeue();
        }

        for (var i = 0; i < _roundRobinOrder.Count; i++)
        {
            var partition = _roundRobinOrder[(_nextPartition + i) % _roundRobinOrder.Count];
            var queue = _partitions[partition];
            var cursor = _cursors[partition];
            if (cursor < queue.Count)
            {
                _nextPartition = (_nextPartition + i + 1) % _roundRobinOrder.Count;
                _cursors[partition] = cursor + 1;
                return queue[cursor];
            }
        }

        throw new OperationCanceledException("Fake consumer script exhausted.");
    }

    public void Seek(TopicPartitionOffset tpo)
    {
        Seeks.Add(tpo);

        if (_partitions.TryGetValue(tpo.TopicPartition, out var queue))
        {
            var index = queue.FindIndex(r => r.TopicPartitionOffset.Equals(tpo));
            if (index >= 0)
            {
                _cursors[tpo.TopicPartition] = index;
            }
        }
    }

    public void Commit(ConsumeResult<string, string> result) => Commits.Add(result.TopicPartitionOffset);

    public void Subscribe(string topic) => SubscribedTopics.Add(topic);

    public void Subscribe(IEnumerable<string> topics) => SubscribedTopics.AddRange(topics);

    public void Unsubscribe()
    {
    }

    public void Close() => Closed = true;

    public void Dispose()
    {
    }

    // Members below are not used by the consumer under test.

    public string MemberId => "fake-member";

    public List<TopicPartition> Assignment => [];

    public List<string> Subscription => SubscribedTopics;

    public IConsumerGroupMetadata ConsumerGroupMetadata => throw new NotSupportedException();

    public Handle Handle => throw new NotSupportedException();

    public string Name => "fake-consumer";

    public ConsumeResult<string, string> Consume(int millisecondsTimeout) => throw new NotSupportedException();

    public ConsumeResult<string, string> Consume(TimeSpan timeout) => throw new NotSupportedException();

    public void Assign(TopicPartition partition) => throw new NotSupportedException();

    public void Assign(TopicPartitionOffset partition) => throw new NotSupportedException();

    public void Assign(IEnumerable<TopicPartitionOffset> partitions) => throw new NotSupportedException();

    public void Assign(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void IncrementalAssign(IEnumerable<TopicPartitionOffset> partitions) => throw new NotSupportedException();

    public void IncrementalAssign(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void IncrementalUnassign(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void Unassign() => throw new NotSupportedException();

    public void StoreOffset(ConsumeResult<string, string> result) => throw new NotSupportedException();

    public void StoreOffset(TopicPartitionOffset offset) => throw new NotSupportedException();

    public List<TopicPartitionOffset> Commit() => throw new NotSupportedException();

    public void Commit(IEnumerable<TopicPartitionOffset> offsets) => throw new NotSupportedException();

    public List<TopicPartitionOffset> Committed(TimeSpan timeout) => throw new NotSupportedException();

    public List<TopicPartitionOffset> Committed(IEnumerable<TopicPartition> partitions, TimeSpan timeout) => throw new NotSupportedException();

    public Offset Position(TopicPartition partition) => throw new NotSupportedException();

    public List<TopicPartitionOffset> OffsetsForTimes(IEnumerable<TopicPartitionTimestamp> timestampsToSearch, TimeSpan timeout) => throw new NotSupportedException();

    public WatermarkOffsets GetWatermarkOffsets(TopicPartition topicPartition) => throw new NotSupportedException();

    public WatermarkOffsets QueryWatermarkOffsets(TopicPartition topicPartition, TimeSpan timeout) => throw new NotSupportedException();

    public void Pause(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void Resume(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public int AddBrokers(string brokers) => throw new NotSupportedException();

    public void SetSaslCredentials(string username, string password) => throw new NotSupportedException();
}
