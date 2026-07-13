using Microsoft.Extensions.Configuration;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

public class KafkaConsumerOptionsBindingTests
{
    // Regression: the configuration binder appends configured array entries onto a
    // non-empty in-code default instead of replacing it. A non-empty RetryDelays default
    // doubled the bound array to 6 entries, which both delayed the operations alert
    // (threshold Max(RetryDelays.Length, 1) became 6) and reset the retry backoff to a
    // 0-second hot retry every 3rd attempt during a sustained outage.
    [Fact]
    public void Binding_RetryDelays_from_configuration_yields_exactly_the_configured_entries()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:RetryDelays:0"] = "00:00:00",
                ["Kafka:RetryDelays:1"] = "00:00:05",
                ["Kafka:RetryDelays:2"] = "00:00:30",
            })
            .Build();

        var options = new KafkaConsumerOptions();
        configuration.GetSection(KafkaConsumerOptions.SectionName).Bind(options);

        Assert.Equal(3, options.RetryDelays.Length);
        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)],
            options.RetryDelays);
    }
}
