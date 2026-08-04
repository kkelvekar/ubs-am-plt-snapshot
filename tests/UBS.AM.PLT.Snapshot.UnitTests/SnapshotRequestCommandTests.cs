using Microsoft.Extensions.Logging;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.Advantage.Messaging;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;
using UBS.AM.PLT.Snapshot.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// The command's whole contract is the <see cref="CommandResult"/> it returns, because that is
/// the only lever it has over the Kafka offset: Success commits, Fail does not. These tests pin
/// which outcome each category of message produces.
/// </summary>
public class SnapshotRequestCommandTests
{
    private const string PayloadJson = """{"total":21,"equities":[],"futures":[],"cash":[]}""";

    [Fact]
    public async Task Handled_message_succeeds_so_the_offset_is_committed()
    {
        var handler = new FakeSnapshotMessageHandler();
        var command = CreateCommand(handler);

        var result = await command.ExecuteAsync(CreateMessage(CreateRequest()));

        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.Error);
        Assert.Single(handler.Handled);
    }

    [Fact]
    public async Task Request_is_mapped_onto_the_domain_envelope_with_the_payload_carried_by_reference()
    {
        var handler = new FakeSnapshotMessageHandler();
        var command = CreateCommand(handler);
        var request = CreateRequest();

        await command.ExecuteAsync(CreateMessage(request));

        var handled = Assert.Single(handler.Handled);
        Assert.Equal(request.SnapshotId, handled.SnapshotId);
        Assert.Equal(request.AccountId, handled.AccountId);
        Assert.Equal(request.SnapshotType, handled.SnapshotType);
        Assert.Equal(request.PayloadType, handled.PayloadType);
        Assert.Equal(request.PublishedAt, handled.PublishedAt);
        Assert.Equal(request.PublishedBy, handled.PublishedBy);
        // Same reference, never re-serialised, so the blob write stays byte-identical.
        Assert.Same(request.Payload, handled.Payload);
    }

    [Fact]
    public async Task Rejected_message_succeeds_so_the_offset_moves_past_the_poison_message()
    {
        // The handler has already written the FAILED tracking row and published the Failed
        // response by the time it throws, and the same bytes would fail identically forever, so
        // the only safe outcome is to commit past the message.
        var handler = new FakeSnapshotMessageHandler
        {
            ThrowOnHandle = InvalidSnapshotEnvelopeException.EmptyPayload(),
        };
        var logger = new CapturingLogger<SnapshotRequestCommand>();
        var command = CreateCommand(handler, logger);

        var result = await command.ExecuteAsync(CreateMessage(CreateRequest()));

        Assert.True(result.IsSuccess);
        var logged = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(InvalidSnapshotEnvelopeException.EmptyPayloadReason, logged.State["ReasonCode"]);
    }

    [Fact]
    public async Task Infrastructure_failure_fails_so_the_offset_stays_uncommitted()
    {
        var handler = new FakeSnapshotMessageHandler
        {
            ThrowOnHandle = new InvalidOperationException("blob write failed"),
        };
        var command = CreateCommand(handler);

        var result = await command.ExecuteAsync(CreateMessage(CreateRequest()));

        Assert.False(result.IsSuccess);
        Assert.Equal("blob write failed", result.Error);
    }

    [Fact]
    public async Task Null_request_fails_without_reaching_the_handler()
    {
        var handler = new FakeSnapshotMessageHandler();
        var logger = new CapturingLogger<SnapshotRequestCommand>();
        var command = CreateCommand(handler, logger);

        var result = await command.ExecuteAsync(CreateMessage(request: null));

        Assert.False(result.IsSuccess);
        Assert.Equal("Snapshot request is null", result.Error);
        Assert.Empty(handler.Handled);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Processing_log_lines_carry_snapshotId_accountId_and_payloadType()
    {
        var logger = new CapturingLogger<SnapshotRequestCommand>();
        var command = CreateCommand(new FakeSnapshotMessageHandler(), logger);
        var request = CreateRequest();

        await command.ExecuteAsync(CreateMessage(request));

        Assert.All(
            logger.Entries.Where(entry => entry.Level == LogLevel.Information),
            entry =>
            {
                Assert.Equal(request.SnapshotId, entry.State["SnapshotId"]);
                Assert.Equal(request.AccountId, entry.State["AccountId"]);
                Assert.Equal(request.PayloadType, entry.State["PayloadType"]);
            });
    }

    private static SnapshotRequestCommand CreateCommand(
        FakeSnapshotMessageHandler handler,
        ILogger<SnapshotRequestCommand>? logger = null)
        => new(logger ?? new CapturingLogger<SnapshotRequestCommand>(), handler);

    private static IMessage<string, SnapshotRequest> CreateMessage(SnapshotRequest? request)
        => new ConsumedMessage<string, SnapshotRequest>("00675442A", request);

    private static SnapshotRequest CreateRequest()
        => new()
        {
            SnapshotId = "corr98765",
            AccountId = "00675442A",
            SnapshotType = "portfolio",
            PayloadType = "orders",
            PublishedAt = "2026-05-22T06:10:14Z",
            PublishedBy = "PortfolioCalculation",
            Payload = PayloadJson,
        };
}
