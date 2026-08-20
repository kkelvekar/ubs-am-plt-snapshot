using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// The required-files map is a library-owned code constant (<c>SnapshotConfigDefinition</c>),
/// no longer an appsettings section. These tests pin the shipped map and the per-message
/// failure behaviour for an unknown snapshot type.
/// </summary>
public class SnapshotConfigDefinitionTests
{
    private static IRequiredFilesProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSnapshotConfigInfrastructure();
        return services.BuildServiceProvider().GetRequiredService<IRequiredFilesProvider>();
    }

    [Fact]
    public void GetRequiredFiles_returns_the_portfolio_required_files()
    {
        var provider = BuildProvider();

        var required = provider.GetRequiredFiles("portfolio");

        Assert.Equal(
            new HashSet<string> { "header.json", "orders.json", "portfolio.json", "settings.json" },
            required);
    }

    [Fact]
    public void GetRequiredFiles_throws_KeyNotFoundException_for_an_unknown_type()
    {
        var provider = BuildProvider();

        var ex = Assert.Throws<KeyNotFoundException>(() => provider.GetRequiredFiles("mystery"));

        Assert.Contains("No SnapshotConfig entry configured for snapshotType 'mystery'.", ex.Message);
    }
}
