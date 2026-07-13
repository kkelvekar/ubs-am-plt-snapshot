namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

public sealed record IntegrationTestSettings
{
    public const string SectionName = "IntegrationTestSettings";

    public bool CleanupAfterTest { get; set; } = true;

    public bool CleanupExistingTestDataOnStart { get; set; } = true;

    public string[] TestAccountIds { get; set; } = [];
}
