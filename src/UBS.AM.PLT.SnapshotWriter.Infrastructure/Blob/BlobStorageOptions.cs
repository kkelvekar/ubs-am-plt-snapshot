namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;

/// <summary>
/// Bound from the <c>BlobStorage</c> configuration section. All values come from
/// configuration (with environment-variable overrides, e.g.
/// <c>BlobStorage__ConnectionString</c>) — never from code.
/// </summary>
public sealed record BlobStorageOptions
{
    public const string SectionName = "BlobStorage";

    /// <summary>
    /// Blob service endpoint (e.g. <c>https://account.blob.core.windows.net</c>). When
    /// set, the client authenticates with <c>DefaultAzureCredential</c> (az login
    /// locally, managed identity in AKS) and this wins over
    /// <see cref="ConnectionString"/>.
    /// </summary>
    public string ServiceUri { get; set; } = string.Empty;

    /// <summary>
    /// Azurite/local fallback used only when <see cref="ServiceUri"/> is empty.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string ContainerName { get; set; } = string.Empty;
}
