using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces;
using UBS.AM.PLT.SnapshotWriter.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Blob;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Shared class fixture wiring the REAL production object graph — via the same
/// <c>AddApplication</c>/<c>AddInfrastructure</c> extensions the Worker uses — against
/// real Azure resources (ADLS Gen2 blob container + Azure SQL, both authenticated via
/// DefaultAzureCredential / az login). Kafka is bypassed entirely: tests call
/// <see cref="Handler"/> directly, and no hosted service is ever started because
/// <c>BuildServiceProvider()</c> never constructs <c>IHostedService</c> registrations —
/// only <c>IHost.StartAsync</c> does, and no host is ever built here.
/// The single mock in the project is <see cref="TimeProvider"/>, so tests can pin and
/// advance "now" deterministically for path/timestamp assertions.
/// </summary>
public sealed class SnapshotWriterFixture : IDisposable
{
    private readonly ServiceProvider _provider;

    public SnapshotWriterFixture()
    {
        Configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .Build();

        MockTime = new Mock<TimeProvider> { CallBase = true };
        MockTime.Setup(t => t.GetUtcNow()).Returns(() => CurrentTime);

        var services = new ServiceCollection();
        services.AddLogging(); // AddApplication/AddInfrastructure assume the host registered logging.
        services.AddSingleton(MockTime.Object);
        services.AddApplication().AddInfrastructure(Configuration);
        _provider = services.BuildServiceProvider();

        Handler = _provider.GetRequiredService<ISnapshotMessageHandler>();
        DbContextFactory = _provider.GetRequiredService<IDbContextFactory<SnapshotWriterDbContext>>();

        // Same auth-selection logic production uses (ServiceUri → DefaultAzureCredential),
        // reachable via InternalsVisibleTo — used only for assertions and cleanup.
        var blobOptions = Configuration.GetSection(BlobStorageOptions.SectionName).Get<BlobStorageOptions>()
            ?? throw new InvalidOperationException("BlobStorage configuration section is missing.");
        BlobContainer = BlobContainerClientFactory.Create(blobOptions);
    }

    public IConfiguration Configuration { get; }

    public ISnapshotMessageHandler Handler { get; }

    public Mock<TimeProvider> MockTime { get; }

    public BlobContainerClient BlobContainer { get; }

    public IDbContextFactory<SnapshotWriterDbContext> DbContextFactory { get; }

    /// <summary>
    /// The mocked "now". Tests pin this to a known value at the start and advance it
    /// between messages to assert last_updated_at behaviour.
    /// </summary>
    public DateTimeOffset CurrentTime { get; set; } = new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => _provider.Dispose();
}
