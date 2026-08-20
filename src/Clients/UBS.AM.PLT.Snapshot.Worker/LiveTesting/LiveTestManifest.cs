namespace UBS.AM.PLT.Snapshot.Worker.LiveTesting;

internal sealed record LiveTestCaseDefinition(
    string Name,
    string TemplateFileName,
    string ExpectedStatus,
    string ExpectedReasonCode);

internal static class LiveTestManifest
{
    public static IReadOnlyList<LiveTestCaseDefinition> Cases { get; } =
    [
        new("complete", "snapshot-simulation-data.json", "Complete", string.Empty),
        new("unknown-header-fields", "bottleneck-proof-data.json", "Complete", string.Empty),
        new("nested-event", "nested-event-data.json", "Complete", string.Empty),
        new("missing-nested-event", "missing-nested-event-data.json", "Failed", "INVALID_HEADER_EVENT"),
        new("overlength-account-id", "overlength-accountid-data.json", "Failed", "FIELD_TOO_LONG"),
        new("invalid-account-id-characters", "invalidchar-accountid-data.json", "Failed", "INVALID_FIELD_CHARACTERS"),
    ];
}
