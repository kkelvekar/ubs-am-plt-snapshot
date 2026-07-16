using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.SnapshotWriter.Application.Contracts;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;
using UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

/// <summary>
/// Gap 2: a fatal failure escaping the consume loop must emit exactly ONE Critical (with
/// topic + consumer group), stop the host so the pod restarts, and exit the process
/// non-zero — without the same exception being logged a second time by the hosting layer.
/// A clean shutdown / cancellation must exit quietly with no Error or Critical noise.
/// </summary>
public class KafkaSnapshotConsumerFatalTests
{
    private const string Topic = "ubs-advantage-snapshots";
    private const string ConsumerGroup = "snapshot-writer-api";

    /// <summary>
    /// End-to-end against the REAL generic Host (the production hosting pipeline, including
    /// BackgroundServiceExceptionBehavior.StopHost as Program.cs configures it). The consumer
    /// factory throws on Create(); we capture every log line across ALL categories and assert
    /// exactly one Critical total. This is the test that actually proves the fix: the earlier
    /// rethrow-based design produced a SECOND Critical (plus a BackgroundServiceFaulted Error)
    /// from Microsoft.Extensions.Hosting.Internal.Host when the faulted task propagated, which
    /// an isolated KafkaSnapshotConsumer test can never observe because it never runs the Host.
    /// </summary>
    [Fact]
    public async Task Fatal_failure_under_real_host_logs_exactly_one_Critical_across_all_categories()
    {
        var logs = new CapturingLoggerProvider();

        var builder = Host.CreateApplicationBuilder();

        // Mirror Program.cs: consumer death must stop the host so k8s restarts the pod.
        builder.Services.Configure<HostOptions>(o =>
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);

        builder.Services.AddSingleton<IKafkaConsumerFactory>(
            new ThrowingConsumerFactory(() => throw new InvalidOperationException("bad bootstrap config")));
        builder.Services.AddSingleton<ISnapshotMessageHandler, NoopHandler>();
        builder.Services.AddSingleton<TimeProvider>(new RecordingTimeProvider());
        builder.Services.Configure<KafkaConsumerOptions>(ConfigureOptions);
        builder.Services.AddHostedService<KafkaSnapshotConsumer>();

        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync();

        // The consumer catches the fatal throw, logs Critical, and calls StopApplication();
        // wait for that shutdown signal rather than racing it.
        await WaitForTokenAsync(lifetime.ApplicationStopping, TimeSpan.FromSeconds(10));
        await host.StopAsync();

        var critical = Assert.Single(logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.Equal(typeof(KafkaSnapshotConsumer).FullName, critical.Category);
        Assert.Equal(Topic, critical.State["Topic"]);
        Assert.Equal(ConsumerGroup, critical.State["ConsumerGroup"]);

        // The whole point of the fix: no second Critical and no BackgroundServiceFaulted
        // Error from the hosting layer for the same incident.
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.DoesNotContain(
            logs.Entries,
            e => e.Category?.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal) == true
                && e.Level >= LogLevel.Error);
    }

    /// <summary>
    /// Isolated view of the same path: the consumer must log exactly one Critical, trigger
    /// host shutdown via IHostApplicationLifetime, set a non-zero process exit code, and NOT
    /// fault its task (a faulted task is what previously caused the Host's duplicate log).
    /// </summary>
    [Fact]
    public async Task Exception_escaping_consume_loop_logs_one_Critical_stops_host_and_does_not_fault()
    {
        Environment.ExitCode = 0;

        var factory = new ThrowingConsumerFactory(
            () => throw new InvalidOperationException("bad bootstrap config"));
        var handler = new NoopHandler();
        var logger = new CapturingLogger<KafkaSnapshotConsumer>();
        var lifetime = new FakeHostApplicationLifetime();

        var service = new KafkaSnapshotConsumer(
            factory,
            handler,
            Options(),
            new RecordingTimeProvider(),
            lifetime,
            logger);

        await service.StartAsync(CancellationToken.None);

        // The task completes cleanly — it must NOT fault (no rethrow).
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);

        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(1, Environment.ExitCode);

        var critical = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Equal(Topic, critical.State["Topic"]);
        Assert.Equal(ConsumerGroup, critical.State["ConsumerGroup"]);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);

        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task Cancellation_during_consume_exits_quietly_with_no_Critical()
    {
        // The consumer's Consume throws OperationCanceledException (as it does on
        // SIGTERM-driven cancellation): the loop must exit cleanly, close the consumer,
        // and log nothing at Error or Critical.
        var consumer = new CancellingConsumer();
        var handler = new NoopHandler();
        var logger = new CapturingLogger<KafkaSnapshotConsumer>();
        var lifetime = new FakeHostApplicationLifetime();

        var service = new KafkaSnapshotConsumer(
            new FixedConsumerFactory(consumer),
            handler,
            Options(),
            new RecordingTimeProvider(),
            lifetime,
            logger);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.True(consumer.Closed);
        Assert.DoesNotContain(logger.Entries, e => e.Level is LogLevel.Error or LogLevel.Critical);
        // Clean shutdown is not a fatal path — the consumer must not force host shutdown.
        Assert.Equal(0, lifetime.StopApplicationCallCount);
    }

    private static void ConfigureOptions(KafkaConsumerOptions options)
    {
        options.BootstrapServers = "unused-by-fake:9092";
        options.Topic = Topic;
        options.ConsumerGroup = ConsumerGroup;
        options.RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
        options.AlertRepeatEveryFailures = 10;
    }

    private static IOptions<KafkaConsumerOptions> Options()
    {
        var options = new KafkaConsumerOptions();
        ConfigureOptions(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    private static async Task WaitForTokenAsync(CancellationToken token, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => tcs.TrySetResult());
        await tcs.Task.WaitAsync(timeout);
    }

    private sealed class ThrowingConsumerFactory(Func<IConsumer<string, string>> create) : IKafkaConsumerFactory
    {
        public IConsumer<string, string> Create() => create();
    }

    private sealed class FixedConsumerFactory(IConsumer<string, string> consumer) : IKafkaConsumerFactory
    {
        public IConsumer<string, string> Create() => consumer;
    }

    private sealed class NoopHandler : ISnapshotMessageHandler
    {
        public Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Captures log entries across every category (not just KafkaSnapshotConsumer) so a
    /// duplicate log emitted by the hosting layer under its own category is visible.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedEntry> _entries = new();

        public IReadOnlyCollection<CapturedEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CategoryLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CategoryLogger(string category, ConcurrentQueue<CapturedEntry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state as IReadOnlyList<KeyValuePair<string, object?>>
                    ?? Array.Empty<KeyValuePair<string, object?>>();

                sink.Enqueue(new CapturedEntry(
                    category,
                    logLevel,
                    formatter(state, exception),
                    values.ToDictionary(kv => kv.Key, kv => kv.Value)));
            }
        }
    }

    private sealed record CapturedEntry(
        string Category,
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> State);

    /// <summary>Consumer whose <see cref="Consume"/> models a cancelled poll.</summary>
    private sealed class CancellingConsumer : IConsumer<string, string>
    {
        public bool Closed { get; private set; }

        public ConsumeResult<string, string> Consume(CancellationToken cancellationToken = default)
            => throw new OperationCanceledException("poll cancelled");

        public void Subscribe(string topic)
        {
        }

        public void Subscribe(IEnumerable<string> topics)
        {
        }

        public void Close() => Closed = true;

        public void Dispose()
        {
        }

        // Unused by the consumer under test.
        public void Seek(TopicPartitionOffset tpo) => throw new NotSupportedException();
        public void Commit(ConsumeResult<string, string> result) => throw new NotSupportedException();
        public string MemberId => "fake";
        public List<TopicPartition> Assignment => [];
        public List<string> Subscription => [];
        public IConsumerGroupMetadata ConsumerGroupMetadata => throw new NotSupportedException();
        public Handle Handle => throw new NotSupportedException();
        public string Name => "cancelling-consumer";
        public void Unsubscribe() { }
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
}
