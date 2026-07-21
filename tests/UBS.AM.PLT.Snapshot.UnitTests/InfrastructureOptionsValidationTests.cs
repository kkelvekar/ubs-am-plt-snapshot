using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.Snapshot.Infrastructure.Adls;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Gap 5: a misconfigured pod must crash-loop at startup with a clear reason. Each
/// validated configuration key, when blank, must produce an OptionsValidationException at
/// startup validation whose message names that key exactly.
/// </summary>
public class InfrastructureOptionsValidationTests
{
    [Theory]
    [InlineData("Kafka:BootstrapServers", "Kafka:BootstrapServers")]
    [InlineData("Kafka:Topic", "Kafka:Topic")]
    [InlineData("Kafka:ConsumerGroup", "Kafka:ConsumerGroup")]
    [InlineData("Database:ConnectionString", "Database:ConnectionString")]
    [InlineData("BlobStorage:ContainerName", "BlobStorage:ContainerName")]
    public void Blank_required_key_fails_startup_validation_with_key_in_message(string blankKey, string expectedInMessage)
    {
        var settings = ValidBaseSettings();
        settings[blankKey] = string.Empty;

        var ex = ValidateExpectingFailure(settings);

        Assert.Contains(expectedInMessage, ex.Message);
    }

    [Fact]
    public void Blank_both_blob_auth_options_fails_startup_validation()
    {
        var settings = ValidBaseSettings();
        settings["BlobStorage:ServiceUri"] = string.Empty;
        settings["BlobStorage:ConnectionString"] = string.Empty;

        var ex = ValidateExpectingFailure(settings);

        Assert.Contains("BlobStorage:ServiceUri or BlobStorage:ConnectionString", ex.Message);
    }

    [Fact]
    public void Fully_valid_configuration_passes_startup_validation()
    {
        var validator = BuildValidator(ValidBaseSettings());

        // No throw.
        validator.Validate();
    }

    private static OptionsValidationException ValidateExpectingFailure(Dictionary<string, string?> settings)
    {
        var validator = BuildValidator(settings);
        return Assert.Throws<OptionsValidationException>(validator.Validate);
    }

    private static IStartupValidator BuildValidator(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSqlInfrastructure(configuration["Database:ConnectionString"] ?? string.Empty);
        services.AddAdlsInfrastructure(
            configuration["BlobStorage:ServiceUri"] ?? string.Empty,
            configuration["BlobStorage:ContainerName"] ?? string.Empty,
            configuration["BlobStorage:ConnectionString"]);
        services.AddKafkaInfrastructure(configuration);
        services.AddSnapshotConfigInfrastructure(configuration);

        return services.BuildServiceProvider().GetRequiredService<IStartupValidator>();
    }

    private static Dictionary<string, string?> ValidBaseSettings() => new()
    {
        ["Kafka:BootstrapServers"] = "localhost:9092",
        ["Kafka:Topic"] = "ubs-advantage-snapshots",
        ["Kafka:ConsumerGroup"] = "snapshot-writer-api",
        ["BlobStorage:ServiceUri"] = "https://example.blob.core.windows.net",
        ["BlobStorage:ConnectionString"] = string.Empty,
        ["BlobStorage:ContainerName"] = "ubsadvsnapshots",
        ["Database:ConnectionString"] = "Server=tcp:example;Database=db;",
    };
}
