using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using UBS.AM.PLT.Snapshot.Worker.Controllers;
using UBS.AM.PLT.Snapshot.Worker.LiveTesting;

namespace UBS.AM.PLT.Snapshot.UnitTests;

public sealed class SnapshotSimulationTests
{
    [Fact]
    public void Generator_preserves_order_cycles_accounts_and_builds_dynamic_pascal_case_requests()
    {
        var startedAt = new DateTimeOffset(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);
        var template = CreateTemplate(["ACC-1", "ACC-2"]);

        var messages = SnapshotGenerator.Generate(
            template,
            new SnapshotSimulationRequest { SnapshotCount = 3, MessageDelay = TimeSpan.Zero },
            startedAt);

        Assert.Equal(12, messages.Count);
        Assert.Equal(
            ["header", "orders", "calculations", "settings"],
            messages.Take(4).Select(message => message.Request.PayloadType));
        Assert.Equal(["ACC-1", "ACC-2", "ACC-1"], messages.Chunk(4).Select(group => group[0].AccountId));
        var snapshotIds = messages.Chunk(4).Select(group => group[0].Request.SnapshotId).ToArray();
        Assert.All(snapshotIds, snapshotId => Assert.StartsWith("corr20260814123456000-", snapshotId));
        Assert.EndsWith("-0001", snapshotIds[0]);
        Assert.EndsWith("-0002", snapshotIds[1]);
        Assert.EndsWith("-0003", snapshotIds[2]);
        Assert.All(snapshotIds, snapshotId => Assert.InRange(snapshotId.Length, 1, 100));

        using var header = JsonDocument.Parse(messages[4].Request.Payload);
        Assert.Equal("123457", header.RootElement.GetProperty("programId").GetString());
        Assert.Equal("15885", header.RootElement.GetProperty("batchId").GetString());
        Assert.Equal(5, header.RootElement.GetProperty("numOrders").GetInt32());

        using var calculations = JsonDocument.Parse(messages[6].Request.Payload);
        Assert.True(calculations.RootElement.TryGetProperty("calculatedAt", out _));

        var wireJson = JsonSerializer.Serialize(messages[0].Request);
        using var wire = JsonDocument.Parse(wireJson);
        Assert.Equal(
            ["SnapshotId", "AccountId", "SnapshotType", "PayloadType", "PublishedAt", "PublishedBy", "Payload"],
            wire.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(messages[0].AccountId, wire.RootElement.GetProperty("AccountId").GetString());
    }

    [Fact]
    public async Task Generator_produces_disjoint_ids_for_concurrent_invocations_at_the_same_instant()
    {
        var startedAt = new DateTimeOffset(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);
        var request = new SnapshotSimulationRequest { SnapshotCount = 2, MessageDelay = TimeSpan.Zero };
        var generations = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                SnapshotGenerator.Generate(CreateTemplate(["ACC-1"]), request, startedAt))));

        var snapshotIds = generations
            .SelectMany(messages => messages.Select(message => message.Request.SnapshotId).Distinct())
            .ToArray();

        Assert.Equal(32, snapshotIds.Length);
        Assert.Equal(snapshotIds.Length, snapshotIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(snapshotIds, snapshotId => Assert.StartsWith("corr20260814123456000-", snapshotId));
        Assert.All(snapshotIds, snapshotId => Assert.InRange(snapshotId.Length, 1, 100));
    }

    [Theory]
    [InlineData("../snapshot-simulation-data.json")]
    [InlineData("missing.json")]
    public void Template_loader_rejects_unsafe_or_missing_template(string fileName)
    {
        var loader = new SnapshotTemplateLoader(new TestHostEnvironment
        {
            ContentRootPath = Path.GetTempPath(),
        });

        var exception = Assert.Throws<SnapshotSimulationValidationException>(() => loader.Load(fileName));

        Assert.NotEmpty(exception.Message);
    }

    [Fact]
    public async Task Controller_returns_delivery_result_after_success()
    {
        var result = new SnapshotSimulationResult(
            ["corr-1"],
            1,
            [new SnapshotSimulationDelivery("corr-1", "ACC-1", "header", "topic", 0, 12)]);
        var controller = CreateController((_, _) => Task.FromResult(result));

        var action = await controller.PublishAsync(new SnapshotSimulationRequest(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Same(result, ok.Value);
    }

    [Fact]
    public async Task Controller_propagates_request_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var controller = CreateController((_, token) => Task.FromCanceled<SnapshotSimulationResult>(token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.PublishAsync(new SnapshotSimulationRequest(), cancellation.Token));
    }

    [Fact]
    public async Task Controller_returns_400_for_invalid_request()
    {
        var controller = CreateController((_, _) => throw new SnapshotSimulationValidationException("bad template"));

        var action = await controller.PublishAsync(new SnapshotSimulationRequest(), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("bad template", Assert.IsType<ProblemDetails>(problem.Value).Detail);
    }

    [Fact]
    public async Task Controller_returns_503_for_kafka_delivery_failure()
    {
        var exception = new ProduceException<string, string>(
            new Error(ErrorCode.Local_AllBrokersDown, "broker unavailable"),
            new DeliveryResult<string, string>());
        var controller = CreateController((_, _) => throw exception);

        var action = await controller.PublishAsync(new SnapshotSimulationRequest(), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        Assert.Equal("broker unavailable", Assert.IsType<ProblemDetails>(problem.Value).Detail);
    }

    [Fact]
    public void Live_testing_services_and_endpoint_are_development_only()
    {
        var developmentBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        developmentBuilder.Services.AddSingleton(TimeProvider.System);
        WorkerLiveTesting.AddServices(developmentBuilder);
        var developmentApp = developmentBuilder.Build();
        WorkerLiveTesting.MapEndpoints(developmentApp);

        var productionBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        WorkerLiveTesting.AddServices(productionBuilder);
        var productionApp = productionBuilder.Build();
        WorkerLiveTesting.MapEndpoints(productionApp);

        Assert.Contains(
            developmentBuilder.Services,
            descriptor => descriptor.ServiceType == typeof(ISnapshotSimulationPublisher));
        Assert.DoesNotContain(
            productionBuilder.Services,
            descriptor => descriptor.ServiceType == typeof(ISnapshotSimulationPublisher));
        Assert.NotEmpty(((IEndpointRouteBuilder)developmentApp).DataSources);
        Assert.Empty(((IEndpointRouteBuilder)productionApp).DataSources);
    }

    private static SnapshotSimulationController CreateController(
        Func<SnapshotSimulationRequest, CancellationToken, Task<SnapshotSimulationResult>> publish) =>
        new(new StubPublisher(publish), NullLogger<SnapshotSimulationController>.Instance);

    private static SnapshotTemplate CreateTemplate(IReadOnlyList<string> accountIds)
    {
        using var document = JsonDocument.Parse(
            """
            {
              "header": { "programId": "1", "batchId": "1", "numOrders": 1 },
              "orders": { "total": 1 },
              "calculations": { "nav": 1 },
              "settings": { "tolerance": 0.1 }
            }
            """);

        return new SnapshotTemplate(
            accountIds,
            [
                new("header", "Portal", document.RootElement.GetProperty("header").Clone()),
                new("orders", "PortfolioCalculation", document.RootElement.GetProperty("orders").Clone()),
                new("calculations", "PortfolioCalculation", document.RootElement.GetProperty("calculations").Clone()),
                new("settings", "PortfolioCalculation", document.RootElement.GetProperty("settings").Clone()),
            ]);
    }

    private sealed class StubPublisher(
        Func<SnapshotSimulationRequest, CancellationToken, Task<SnapshotSimulationResult>> publish)
        : ISnapshotSimulationPublisher
    {
        public Task<SnapshotSimulationResult> PublishAsync(
            SnapshotSimulationRequest request,
            CancellationToken cancellationToken) => publish(request, cancellationToken);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = nameof(SnapshotSimulationTests);
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
