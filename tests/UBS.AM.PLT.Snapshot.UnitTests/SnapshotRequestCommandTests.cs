using Microsoft.Extensions.Logging;
using Ubs.Advantage.Core.Infrastructure.Commands;
using Ubs.Advantage.Core.Messaging.Kafka.Models;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;
using UBS.AM.PLT.Snapshot.UnitTests.Fakes;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Covers <see cref="SnapshotRequestCommand"/>'s three-outcome classification: handled/rejected
/// commit (<see cref="CommandResult.Success"/>), transient infrastructure failure (Critical log,
/// non-zero exit code, host stop, 30s park, then <see cref="CommandResult.Fail"/>), and poison
/// (durably recorded via <see cref="ISnapshotMessageHandler.RecordUnexpectedFailureAsync"/>,
/// then committed past). <see cref="Environment.ExitCode"/> is process-global, so every test
/// that touches it saves and restores it in a <c>finally</c> block; xUnit runs the methods of
/// one test class sequentially by default, so no cross-test race exists within this class.
/// </summary>
public sealed class SnapshotRequestCommandTests
{
    [Fact]
    public async Task HandlerSucceeds_ReturnsSuccess_NoStopNoRecord()
    {
        var handler = new FakeSnapshotMessageHandler();
        var lifetime = new FakeHostApplicationLifetime();
        var command = CreateCommand(handler, lifetime, out _, out _);

        var result = await command.ExecuteAsync(CreateMessage());

        Assert.True(result.IsSuccess);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.Empty(handler.RecordedUnexpectedFailures);
    }

    [Fact]
    public async Task HandlerThrowsRejection_ReturnsSuccess_NoStopNoRecord()
    {
        var handler = new FakeSnapshotMessageHandler { ThrowOnHandle = InvalidSnapshotEnvelopeException.EmptyPayload() };
        var lifetime = new FakeHostApplicationLifetime();
        var command = CreateCommand(handler, lifetime, out _, out _);

        var result = await command.ExecuteAsync(CreateMessage());

        Assert.True(result.IsSuccess);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.Empty(handler.RecordedUnexpectedFailures);
    }

    [Fact]
    public async Task TransientFailure_StopsHostSetsExitCodeAndFails()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            var handler = new FakeSnapshotMessageHandler { ThrowOnHandle = new InvalidOperationException("sql down") };
            var lifetime = new FakeHostApplicationLifetime();
            var classifier = new StubTransientFailureClassifier(_ => true);
            var command = CreateCommand(handler, lifetime, out var logger, out _, classifier);

            var result = await command.ExecuteAsync(CreateMessage());

            Assert.False(result.IsSuccess);
            Assert.Equal(1, Environment.ExitCode);
            Assert.Equal(1, lifetime.StopApplicationCallCount);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task TransientFailure_StopsApplicationBeforeParkCompletes()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            var handler = new FakeSnapshotMessageHandler { ThrowOnHandle = new InvalidOperationException("sql down") };
            var lifetime = new FakeHostApplicationLifetime();
            var classifier = new StubTransientFailureClassifier(_ => true);
            var time = new ControllableTimeProvider();
            var command = CreateCommand(handler, lifetime, out _, out _, classifier, time);

            var task = command.ExecuteAsync(CreateMessage());

            // StopApplication is called synchronously, before the park's Task.Delay ever
            // yields control back with the delay complete -- assert it happened while the
            // returned task is still incomplete.
            Assert.Equal(1, lifetime.StopApplicationCallCount);
            Assert.False(task.IsCompleted);

            time.ReleasePark();
            var result = await task;
            Assert.False(result.IsSuccess);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task TransientFailure_ParkHoldsUntilReleased()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            var handler = new FakeSnapshotMessageHandler { ThrowOnHandle = new InvalidOperationException("blob down") };
            var lifetime = new FakeHostApplicationLifetime();
            var classifier = new StubTransientFailureClassifier(_ => true);
            var time = new ControllableTimeProvider();
            var command = CreateCommand(handler, lifetime, out _, out _, classifier, time);

            var task = command.ExecuteAsync(CreateMessage());
            Assert.False(task.IsCompleted);

            time.ReleasePark();

            var result = await task;
            Assert.False(result.IsSuccess);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task TransientFailure_SkipBugRegression_NoSubsequentMessageCommitsAHigherOffset()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            var handler = new FakeSnapshotMessageHandler { ThrowOnHandle = new InvalidOperationException("sql down") };
            var lifetime = new FakeHostApplicationLifetime();
            var classifier = new StubTransientFailureClassifier(_ => true);
            var time = new ControllableTimeProvider();
            var command = CreateCommand(handler, lifetime, out _, out _, classifier, time);
            var harness = new OrgConsumeLoopHarness<string, SnapshotRequest>(command);

            var message1 = CreateMessage(key: "offset-1", snapshotId: "snap-1");
            var message2 = CreateMessage(key: "offset-2", snapshotId: "snap-2");

            // The park is never released, so message1's ExecuteAsync never returns: under the
            // real org library's log-only Fail, a naive loop would instead have already moved on
            // to message2 and committed its (higher) offset, permanently skipping message1. This
            // is the regression test for that permanent-skip data-loss bug.
            var runTask = harness.RunAsync([message1, message2]);

            Assert.False(runTask.IsCompleted);
            Assert.Empty(handler.RecordedUnexpectedFailures); // message2's handler was never invoked either
            Assert.Empty(harness.CommittedOffsets);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task NonTransientException_RecordsUnexpectedFailure_ReturnsSuccess_NoStop()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            var handler = new FakeSnapshotMessageHandler { ThrowOnHandle = new InvalidOperationException("code defect") };
            var lifetime = new FakeHostApplicationLifetime();
            var command = CreateCommand(handler, lifetime, out var logger, out _);
            var message = CreateMessage(key: "offset-7", snapshotId: "snap-7", accountId: "acc-7", payloadType: "orders");

            var result = await command.ExecuteAsync(message);

            Assert.True(result.IsSuccess);
            Assert.Equal(0, lifetime.StopApplicationCallCount);
            Assert.Equal(originalExitCode, Environment.ExitCode);

            var recorded = Assert.Single(handler.RecordedUnexpectedFailures);
            Assert.Equal("snap-7", recorded.Message.SnapshotId);
            Assert.Same(handler.ThrowOnHandle, recorded.Exception);

            var criticalEntry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
            Assert.Contains("offset-7", criticalEntry.Message);
            Assert.Contains("snap-7", criticalEntry.Message);
            Assert.Contains("acc-7", criticalEntry.Message);
            Assert.Contains("orders", criticalEntry.Message);
            Assert.Contains("snapshot-request", criticalEntry.Message);
            Assert.Contains("snapshot-writer-api", criticalEntry.Message);
            Assert.Contains("UNEXPECTED_ERROR", criticalEntry.Message);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task PoisonRecordItselfThrows_EscalatesToTransientBehaviour()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            var handler = new FakeSnapshotMessageHandler
            {
                ThrowOnHandle = new InvalidOperationException("code defect"),
                ThrowOnRecordUnexpectedFailure = new InvalidOperationException("tracking store also down"),
            };
            var lifetime = new FakeHostApplicationLifetime();
            var time = new ControllableTimeProvider();
            var command = CreateCommand(handler, lifetime, out _, out _, timeProvider: time);

            var task = command.ExecuteAsync(CreateMessage());
            Assert.False(task.IsCompleted);
            Assert.Equal(1, lifetime.StopApplicationCallCount);

            time.ReleasePark();
            var result = await task;

            Assert.False(result.IsSuccess);
            Assert.Equal(1, Environment.ExitCode);
            Assert.Equal(1, lifetime.StopApplicationCallCount);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task NullMessageValue_ReturnsSuccess_LogsCritical_NoStopNoTracking()
    {
        var handler = new FakeSnapshotMessageHandler();
        var lifetime = new FakeHostApplicationLifetime();
        var command = CreateCommand(handler, lifetime, out var logger, out _);

        var message = new Message<string, SnapshotRequest>("tombstone-key", null, []);

        var result = await command.ExecuteAsync(message);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, lifetime.StopApplicationCallCount);
        Assert.Empty(handler.RecordedUnexpectedFailures);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical);
    }

    private static SnapshotRequestCommand CreateCommand(
        FakeSnapshotMessageHandler handler,
        FakeHostApplicationLifetime lifetime,
        out CapturingLogger<SnapshotRequestCommand> logger,
        out RecordingTimeProvider recordingTime,
        ITransientFailureClassifier? classifier = null,
        TimeProvider? timeProvider = null)
    {
        logger = new CapturingLogger<SnapshotRequestCommand>();
        recordingTime = new RecordingTimeProvider();
        var classifiers = classifier is null
            ? Array.Empty<ITransientFailureClassifier>()
            : [classifier];

        return new SnapshotRequestCommand(
            logger,
            handler,
            lifetime,
            classifiers,
            timeProvider ?? recordingTime);
    }

    private static Message<string, SnapshotRequest> CreateMessage(
        string key = "offset-1",
        string snapshotId = "snap-1",
        string accountId = "acc-1",
        string payloadType = "orders")
        => new(
            key,
            new SnapshotRequest
            {
                SnapshotId = snapshotId,
                AccountId = accountId,
                SnapshotType = "portfolio",
                PayloadType = payloadType,
                PublishedAt = "2026-08-25T00:00:00Z",
                PublishedBy = "PortfolioCalculation",
                Payload = "{}",
            },
            []);

    private sealed class StubTransientFailureClassifier(Func<Exception, bool> predicate) : ITransientFailureClassifier
    {
        public bool IsTransient(Exception exception) => predicate(exception);
    }
}
