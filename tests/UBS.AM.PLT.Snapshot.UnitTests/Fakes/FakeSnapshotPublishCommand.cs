using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.Advantage.Messaging;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

public sealed class FakeSnapshotPublishCommand : ACommand<IMessage<string, SnapshotResponse>>
{
    private readonly List<IMessage<string, SnapshotResponse>> _executed = [];

    public IReadOnlyList<IMessage<string, SnapshotResponse>> Executed => _executed;

    /// <summary>Reason to fail with; null means every execution succeeds.</summary>
    public string? FailWith { get; set; }

    public override Task<CommandResult> ExecuteAsync(IMessage<string, SnapshotResponse> message)
    {
        _executed.Add(message);

        return Task.FromResult(FailWith is null ? CommandResult.Success : CommandResult.Fail(FailWith));
    }
}
