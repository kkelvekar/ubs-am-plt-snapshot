using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;
using UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

public class KafkaSnapshotConsumerTests
{
    private const string Topic = "ubs-advantage-snapshots";
    private const string DefaultSnapshotId = "corr98765";
    private const string DefaultMessageKey = "00675442A";

    [Fact]
    public async Task Commits_exactly_once_after_successful_handle()
    {
        var consumer = new FakeKafkaConsumer();
        consumer.Enqueue(Result(MessageJson(DefaultSnapshotId), offset: 7));
        var handler = new ScriptedHandler();

        await RunToCompletionAsync(consumer, handler);

        Assert.Single(handler.Handled);
        var committed = Assert.Single(consumer.Commits);
        Assert.Equal(new TopicPartitionOffset(Topic, new Partition(0), new Offset(7)), committed);
        Assert.Empty(consumer.Seeks);
    }

    [Fact]
    public async Task Handler_exception_means_no_commit_and_seek_back_to_failed_offset()
    {
        var consumer = new FakeKafkaConsumer { MaxConsumeCalls = 5 };
        consumer.Enqueue(Result(MessageJson(DefaultSnapshotId), offset: 3));
        var handler = new ScriptedHandler();
        handler.FailAlways(DefaultSnapshotId, new InvalidOperationException("blob write failed"));

        var logger = new CapturingLogger<KafkaSnapshotConsumer>();
        var timeProvider = new RecordingTimeProvider();
        await RunToCompletionAsync(consumer, handler, logger, timeProvider);

        Assert.Empty(consumer.Commits);
        Assert.Equal(5, consumer.Seeks.Count);
        Assert.All(consumer.Seeks, tpo =>
            Assert.Equal(new TopicPartitionOffset(Topic, new Partition(0), new Offset(3)), tpo));

        // Same message redelivered after each seek — the consumer never advances past it.
        Assert.Equal(5, handler.Handled.Count);

        // Error on every failed attempt; operations alert exactly once, after the 3rd
        // consecutive failure (next re-alert would be at 13, beyond the call budget).
        Assert.Equal(5, logger.Entries.Count(e => e.Level == LogLevel.Error));
        var alert = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Equal(3, alert.State["FailureCount"]);
        Assert.Equal(new TopicPartitionOffset(Topic, new Partition(0), new Offset(3)), alert.State["TopicPartitionOffset"]);
        Assert.Equal(timeProvider.UtcNow, alert.State["BlockedSince"]);
        Assert.Equal(DefaultSnapshotId, alert.State["SnapshotId"]);
        Assert.Equal(DefaultMessageKey, alert.State["AccountId"]);
        Assert.Equal("instruments", alert.State["PayloadType"]);
        Assert.Equal(DefaultMessageKey, alert.State["MessageKey"]);
    }

    [Fact]
    public async Task Malformed_json_means_no_commit_and_seek_back_to_failed_offset()
    {
        var consumer = new FakeKafkaConsumer { MaxConsumeCalls = 4 };
        consumer.Enqueue(Result("{ this is not valid json", offset: 0));
        var handler = new ScriptedHandler();

        var logger = new CapturingLogger<KafkaSnapshotConsumer>();
        await RunToCompletionAsync(consumer, handler, logger);

        Assert.Empty(consumer.Commits);
        Assert.Empty(handler.Handled);
        Assert.Equal(4, consumer.Seeks.Count);
        Assert.All(consumer.Seeks, tpo =>
            Assert.Equal(new TopicPartitionOffset(Topic, new Partition(0), new Offset(0)), tpo));

        // The envelope never deserialises, so the poison message is identifiable by its
        // message key; snapshot identity fields are null.
        var alert = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Equal(DefaultMessageKey, alert.State["MessageKey"]);
        Assert.Null(alert.State["SnapshotId"]);
        Assert.Null(alert.State["AccountId"]);
        Assert.Null(alert.State["PayloadType"]);
    }

    [Fact]
    public async Task Alert_repeats_at_configured_cadence_while_partition_stays_blocked()
    {
        var consumer = new FakeKafkaConsumer { MaxConsumeCalls = 25 };
        consumer.Enqueue(Result(MessageJson(DefaultSnapshotId), offset: 3));
        var handler = new ScriptedHandler();
        handler.FailAlways(DefaultSnapshotId, new InvalidOperationException("still failing"));

        var logger = new CapturingLogger<KafkaSnapshotConsumer>();
        await RunToCompletionAsync(consumer, handler, logger, alertRepeatEveryFailures: 10);

        // Threshold 3 (three configured retry delays), re-alert every 10 further failures.
        var criticalCounts = logger.Entries
            .Where(e => e.Level == LogLevel.Critical)
            .Select(e => (int)e.State["FailureCount"]!)
            .ToList();
        Assert.Equal([3, 13, 23], criticalCounts);

        // Error still logged on every failed attempt — Critical is additional.
        Assert.Equal(25, logger.Entries.Count(e => e.Level == LogLevel.Error));
        Assert.Empty(consumer.Commits);
    }

    [Fact]
    public async Task Alert_fires_once_only_when_repeat_is_disabled()
    {
        var consumer = new FakeKafkaConsumer { MaxConsumeCalls = 25 };
        consumer.Enqueue(Result(MessageJson(DefaultSnapshotId), offset: 3));
        var handler = new ScriptedHandler();
        handler.FailAlways(DefaultSnapshotId, new InvalidOperationException("still failing"));

        var logger = new CapturingLogger<KafkaSnapshotConsumer>();
        await RunToCompletionAsync(consumer, handler, logger, alertRepeatEveryFailures: 0);

        var alert = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Equal(3, alert.State["FailureCount"]);
        Assert.Equal(25, logger.Entries.Count(e => e.Level == LogLevel.Error));
    }

    [Fact]
    public async Task Success_resets_failure_count_and_blocked_since_for_next_stuck_message()
    {
        // Message A fails twice then succeeds; message B (next offset, same partition)
        // is stuck. If success did not reset the state, B would alert on its first
        // failure (carried-over count) with A's BlockedSince.
        var consumer = new FakeKafkaConsumer { MaxConsumeCalls = 6 };
        consumer.Enqueue(Result(MessageJson("corrA"), offset: 3));
        consumer.Enqueue(Result(MessageJson("corrB"), offset: 4));

        var timeProvider = new RecordingTimeProvider();
        var blockedSinceA = timeProvider.UtcNow;
        var blockedSinceB = blockedSinceA.AddMinutes(10);

        var handler = new ScriptedHandler();
        handler.FailFirst("corrA", times: 2, new InvalidOperationException("transient"));
        handler.FailAlways("corrB", new InvalidOperationException("stuck"));
        handler.OnSuccess = _ => timeProvider.UtcNow = blockedSinceB;

        var logger = new CapturingLogger<KafkaSnapshotConsumer>();
        await RunToCompletionAsync(consumer, handler, logger, timeProvider);

        var committed = Assert.Single(consumer.Commits);
        Assert.Equal(new TopicPartitionOffset(Topic, new Partition(0), new Offset(3)), committed);

        // Fresh alert cadence for B: Critical at its own 3rd failure, with B's identity
        // and a BlockedSince captured when B first failed.
        var alert = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Equal(3, alert.State["FailureCount"]);
        Assert.Equal(new TopicPartitionOffset(Topic, new Partition(0), new Offset(4)), alert.State["TopicPartitionOffset"]);
        Assert.Equal("corrB", alert.State["SnapshotId"]);
        Assert.Equal(blockedSinceB, alert.State["BlockedSince"]);
    }

    [Fact]
    public async Task Blocked_partition_alerts_at_threshold_despite_interleaved_success_on_other_partition()
    {
        // Partition 0's message is stuck; partition 1 keeps flowing. The fake consumer
        // round-robins across partitions, so deliveries interleave: P0 fail, P1 success,
        // P0 fail, ... Partition 1's successes must not reset partition 0's failure count.
        var consumer = new FakeKafkaConsumer { MaxConsumeCalls = 9 };
        consumer.Enqueue(Result(MessageJson("corrStuck"), offset: 3, partition: 0));
        for (var offset = 0; offset < 4; offset++)
        {
            consumer.Enqueue(Result(MessageJson($"corrOk{offset}"), offset: offset, partition: 1, key: "00999999B"));
        }

        var handler = new ScriptedHandler();
        handler.FailAlways("corrStuck", new InvalidOperationException("poison"));

        var logger = new CapturingLogger<KafkaSnapshotConsumer>();
        await RunToCompletionAsync(consumer, handler, logger);

        // All partition 1 messages committed, partition 0 never committed.
        Assert.Equal(4, consumer.Commits.Count);
        Assert.All(consumer.Commits, tpo => Assert.Equal(new Partition(1), tpo.Partition));

        // Partition 0 failed 5 times (interleaved with the 4 successes) and alerted at
        // its own 3rd failure — the interleaved successes did not reset its state.
        Assert.Equal(5, consumer.Seeks.Count);
        Assert.Equal(5, logger.Entries.Count(e => e.Level == LogLevel.Error));
        var alert = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Equal(3, alert.State["FailureCount"]);
        Assert.Equal(new TopicPartitionOffset(Topic, new Partition(0), new Offset(3)), alert.State["TopicPartitionOffset"]);
        Assert.Equal("corrStuck", alert.State["SnapshotId"]);
    }

    [Fact]
    public async Task Consume_error_backs_off_before_next_consume()
    {
        var consumer = new FakeKafkaConsumer();
        consumer.Enqueue(new ConsumeException(
            new ConsumeResult<byte[], byte[]>(),
            new Error(ErrorCode.Local_AllBrokersDown, "all brokers down")));
        consumer.Enqueue(Result(MessageJson(DefaultSnapshotId), offset: 7));

        var handler = new ScriptedHandler();
        var timeProvider = new RecordingTimeProvider();
        var retryDelays = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30) };

        await RunToCompletionAsync(consumer, handler, timeProvider: timeProvider, retryDelays: retryDelays);

        // Backed off at the largest configured retry delay instead of hot-spinning, then
        // carried on consuming normally.
        var delay = Assert.Single(timeProvider.Delays);
        Assert.Equal(TimeSpan.FromSeconds(30), delay);
        Assert.Single(consumer.Commits);
    }

    [Fact]
    public async Task Subscribes_to_topic_from_configuration()
    {
        var consumer = new FakeKafkaConsumer();
        var handler = new ScriptedHandler();

        await RunToCompletionAsync(consumer, handler);

        var subscribed = Assert.Single(consumer.SubscribedTopics);
        Assert.Equal(Topic, subscribed);
        Assert.True(consumer.Closed);
    }

    private static async Task RunToCompletionAsync(
        FakeKafkaConsumer consumer,
        ISnapshotMessageHandler handler,
        CapturingLogger<KafkaSnapshotConsumer>? logger = null,
        RecordingTimeProvider? timeProvider = null,
        int alertRepeatEveryFailures = 10,
        TimeSpan[]? retryDelays = null)
    {
        var options = Options.Create(new KafkaConsumerOptions
        {
            BootstrapServers = "unused-by-fake:9092",
            Topic = Topic,
            ConsumerGroup = "snapshot-writer-api",
            // Zero delays keep retry tests fast; cadence logic itself is delay-count driven.
            RetryDelays = retryDelays ?? [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            AlertRepeatEveryFailures = alertRepeatEveryFailures,
        });

        var service = new KafkaSnapshotConsumer(
            new FixedConsumerFactory(consumer),
            handler,
            options,
            timeProvider ?? new RecordingTimeProvider(),
            logger ?? new CapturingLogger<KafkaSnapshotConsumer>());

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);
    }

    private static string MessageJson(string snapshotId) => $$"""
        {
          "snapshotId": "{{snapshotId}}",
          "accountId": "00675442A",
          "snapshotType": "portfolio",
          "payloadType": "instruments",
          "stage": "PreTrade",
          "publishedAt": "2026-05-22T06:10:14Z",
          "publishedBy": "PortfolioCalculation",
          "schemaVersion": "1.0",
          "payload": { "total": 21, "equities": [], "futures": [], "cash": [] }
        }
        """;

    private static ConsumeResult<string, string> Result(
        string value,
        long offset,
        int partition = 0,
        string key = DefaultMessageKey) => new()
    {
        Message = new Message<string, string> { Key = key, Value = value },
        TopicPartitionOffset = new TopicPartitionOffset(Topic, new Partition(partition), new Offset(offset)),
    };

    private sealed class FixedConsumerFactory(IConsumer<string, string> consumer) : IKafkaConsumerFactory
    {
        public IConsumer<string, string> Create() => consumer;
    }

    /// <summary>
    /// Handler whose outcome is scripted per snapshotId: fail the first N invocations,
    /// fail always, or (default) succeed. Records every invocation.
    /// </summary>
    private sealed class ScriptedHandler : ISnapshotMessageHandler
    {
        private readonly Dictionary<string, (int Remaining, Exception Failure)> _failures = [];

        public List<SnapshotMessage> Handled { get; } = [];

        public Action<SnapshotMessage>? OnSuccess { get; set; }

        public void FailFirst(string snapshotId, int times, Exception failure) =>
            _failures[snapshotId] = (times, failure);

        public void FailAlways(string snapshotId, Exception failure) =>
            _failures[snapshotId] = (-1, failure);

        public Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken)
        {
            Handled.Add(message);

            if (_failures.TryGetValue(message.SnapshotId, out var script) && script.Remaining != 0)
            {
                if (script.Remaining > 0)
                {
                    _failures[message.SnapshotId] = (script.Remaining - 1, script.Failure);
                }

                return Task.FromException(script.Failure);
            }

            OnSuccess?.Invoke(message);
            return Task.CompletedTask;
        }
    }
}
