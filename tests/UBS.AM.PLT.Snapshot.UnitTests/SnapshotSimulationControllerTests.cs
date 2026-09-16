using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ubs.Advantage.Core.Messaging.Kafka;
using Ubs.Advantage.Core.Messaging.Kafka.Models;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.LiveTestCleanup;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Worker.Controllers;
using UBS.AM.PLT.Snapshot.Worker.LiveTesting;

namespace UBS.AM.PLT.Snapshot.UnitTests;

public sealed class SnapshotSimulationControllerTests
{
    [Fact]
    public void Publish_marks_all_cases_and_every_payload_with_a_unique_test_snapshot_id()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(host => host.ContentRootPath).Returns(AppContext.BaseDirectory);
        var requests = new List<SnapshotRequest>();
        var producer = new Mock<IProducer<string, SnapshotRequest>>();
        producer.Setup(service => service.Publish(It.IsAny<Message<string, SnapshotRequest>>()))
            .Callback<Message<string, SnapshotRequest>>(message => requests.Add(message.Value!));
        var controller = new SnapshotSimulationController(producer.Object,
            new SnapshotTemplateLoader(environment.Object), TimeProvider.System,
            new LiveTestSnapshotCleanup(Mock.Of<ILiveTestSnapshotCleanupStore>(), Mock.Of<ILiveTestSnapshotBlobCleanup>(),
                NullLogger<LiveTestSnapshotCleanup>.Instance), NullLogger<SnapshotSimulationController>.Instance);

        var result = Assert.IsType<SnapshotSimulationResult>(Assert.IsType<OkObjectResult>(controller.Publish(default)).Value);
        Assert.Equal(6, result.Cases.Count);
        Assert.Equal(36, result.MessageCount);
        Assert.Equal(result.Cases.Count, result.Cases.Select(testCase => testCase.SnapshotId).Distinct().Count());
        foreach (var testCase in result.Cases)
        {
            Assert.True(LiveTestSnapshot.IsTestId(testCase.SnapshotId));
            var payloads = requests.Where(request => request.SnapshotId == testCase.SnapshotId).ToArray();
            Assert.Equal(6, payloads.Length);
            Assert.Equal(["header", "portfolio", "orders", "compliances", "orders-history", "settings"],
                payloads.Select(request => request.PayloadType));
            using var header = System.Text.Json.JsonDocument.Parse(payloads[0].Payload);
            if (header.RootElement.TryGetProperty("SnapshotId", out var embeddedId))
            {
                Assert.Equal(testCase.SnapshotId, embeddedId.GetString());
            }
        }
    }
}
