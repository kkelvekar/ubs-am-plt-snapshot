using Microsoft.Extensions.Configuration;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Configuration;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Loads <see cref="BlobStorageOptions"/>, <see cref="DatabaseOptions"/> and the
/// <c>SnapshotConfig</c> required-files map the same way the Worker composition root
/// does: appsettings.json with environment-variable overrides
/// (<c>BlobStorage__ConnectionString</c> / <c>BlobStorage__ContainerName</c> /
/// <c>Database__ConnectionString</c>). Local Azurite/SQL Server defaults live in
/// appsettings.json — never hardcoded in test code, so the same test binary can point at
/// a different local or org endpoint purely via configuration.
/// </summary>
internal static class TestConfiguration
{
    public static BlobStorageOptions BlobStorageOptions { get; } = Load<BlobStorageOptions>(BlobStorageOptions.SectionName);

    public static DatabaseOptions DatabaseOptions { get; } = Load<DatabaseOptions>(DatabaseOptions.SectionName);

    /// <summary>
    /// The real <c>SnapshotConfig</c> required-files map (design §4), bound exactly like
    /// <see cref="SnapshotConfigRequiredFilesProvider"/> does in production — so completion
    /// integration tests exercise config-driven completeness, not a hardcoded stand-in.
    /// </summary>
    public static Dictionary<string, SnapshotTypeConfig> SnapshotConfig { get; } =
        Load<Dictionary<string, SnapshotTypeConfig>>(SnapshotConfigOptions.SectionName);

    private static T Load<T>(string sectionName) where T : new()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var options = new T();
        configuration.GetSection(sectionName).Bind(options);
        return options;
    }
}
