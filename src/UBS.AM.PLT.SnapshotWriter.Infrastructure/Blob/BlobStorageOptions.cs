namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;

/// <summary>
/// Bound from the <c>BlobStorage</c> configuration section. All values come from
/// configuration (with environment-variable overrides, e.g.
/// <c>BlobStorage__ConnectionString</c>) — never from code.
/// </summary>
public sealed record BlobStorageOptions
{
    public const string SectionName = "BlobStorage";

    public string ConnectionString { get; set; } = string.Empty;

    public string ContainerName { get; set; } = string.Empty;
}
