using Microsoft.Extensions.Configuration;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Loads <see cref="BlobStorageOptions"/> the same way the Worker composition root
/// does: appsettings.json with an environment-variable override
/// (<c>BlobStorage__ConnectionString</c> / <c>BlobStorage__ContainerName</c>). Local
/// Azurite defaults live in appsettings.json — never hardcoded in test code, so the
/// same test binary can point at a different local Azurite/ADLS endpoint purely via
/// configuration.
/// </summary>
internal static class TestConfiguration
{
    public static BlobStorageOptions BlobStorageOptions { get; } = Load();

    private static BlobStorageOptions Load()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var options = new BlobStorageOptions();
        configuration.GetSection(BlobStorageOptions.SectionName).Bind(options);
        return options;
    }
}
