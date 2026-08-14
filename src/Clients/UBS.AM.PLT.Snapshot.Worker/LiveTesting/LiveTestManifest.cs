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
        new("pascal-case-event-type", "pascalcase-eventtype-data.json", "Complete", string.Empty),
        new("missing-event-type", "missing-eventtype-data.json", "Complete", string.Empty),
        new("overlength-account-id", "overlength-accountid-data.json", "Failed", "FIELD_TOO_LONG"),
        new("invalid-account-id-characters", "invalidchar-accountid-data.json", "Failed", "INVALID_FIELD_CHARACTERS"),
    ];
}
