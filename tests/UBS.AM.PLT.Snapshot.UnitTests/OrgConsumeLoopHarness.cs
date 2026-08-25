using Ubs.Advantage.Core.Infrastructure.Commands;
using Ubs.Advantage.Core.Messaging.Kafka.Models;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Faithful replica of the real org platform's consumer semantics, used only to prove the
/// permanent-skip loss bug that motivated three-outcome classification: under the real library,
/// <see cref="CommandResult.Fail"/> only logs — the loop does not stop and does not seek — so a
/// LATER message's successful commit permanently skips an earlier uncommitted one, because Kafka
/// commits are positional. This harness is deliberately dumber than
/// <c>Ubs.Advantage.Core.Messaging.Kafka.MessageConsumerService</c>: a plain sequential
/// <c>foreach</c>, commit (record the key) only on <see cref="CommandResult.IsSuccess"/>, loop
/// straight on to the next message otherwise — no try/catch, no back-off, nothing that could
/// mask the bug it exists to demonstrate.
/// </summary>
public sealed class OrgConsumeLoopHarness<TKey, TValue>(ACommand<IMessage<TKey, TValue>> command)
{
    private readonly List<TKey> _committedOffsets = [];

    /// <summary>Keys of every message whose command reported success, in commit order.</summary>
    public IReadOnlyList<TKey> CommittedOffsets => _committedOffsets;

    public async Task RunAsync(IEnumerable<IMessage<TKey, TValue>> messages)
    {
        foreach (var message in messages)
        {
            var result = await command.ExecuteAsync(message);

            if (result.IsSuccess)
            {
                _committedOffsets.Add(message.Key);
            }

            // Fail: faithful to the real org library -- log only, no stop, no seek. The loop
            // moves straight on to the next message, which is exactly the behaviour that makes a
            // command's own park-before-returning-Fail necessary.
        }
    }
}
