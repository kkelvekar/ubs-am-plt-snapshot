using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Kafka;
using UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

/// <summary>
/// Covers the librdkafka error/log handler mapping (Gap 3). The handlers themselves are
/// wired onto the real ConsumerBuilder and cannot be invoked via the fake consumer, so the
/// mapping is factored into internal static methods and exercised directly here.
/// </summary>
public class KafkaConsumerFactoryTests
{
    [Fact]
    public void Fatal_error_maps_to_Critical()
        => Assert.Equal(
            LogLevel.Critical,
            KafkaConsumerFactory.LevelForError(new Error(ErrorCode.Local_Fatal, "fatal", isFatal: true)));

    [Fact]
    public void Non_fatal_error_maps_to_Warning()
        => Assert.Equal(
            LogLevel.Warning,
            KafkaConsumerFactory.LevelForError(new Error(ErrorCode.Local_AllBrokersDown, "transient blip")));

    [Fact]
    public void HandleError_logs_fatal_at_Critical_with_structured_fields()
    {
        var logger = new CapturingLogger<KafkaConsumerFactory>();
        var error = new Error(ErrorCode.Local_Fatal, "broker gone for good", isFatal: true);

        KafkaConsumerFactory.HandleError(logger, error);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Equal(ErrorCode.Local_Fatal, entry.State["ErrorCode"]);
        Assert.Equal("broker gone for good", entry.State["ErrorReason"]);
        Assert.Equal(true, entry.State["IsFatal"]);
    }

    [Fact]
    public void HandleError_logs_non_fatal_at_Warning()
    {
        var logger = new CapturingLogger<KafkaConsumerFactory>();
        var error = new Error(ErrorCode.Local_AllBrokersDown, "all brokers down");

        KafkaConsumerFactory.HandleError(logger, error);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(false, entry.State["IsFatal"]);
    }

    [Theory]
    [InlineData(SyslogLevel.Critical, LogLevel.Critical)]
    [InlineData(SyslogLevel.Error, LogLevel.Error)]
    [InlineData(SyslogLevel.Warning, LogLevel.Warning)]
    [InlineData(SyslogLevel.Debug, LogLevel.Debug)]
    public void LevelForLog_maps_syslog_level_to_microsoft_level(SyslogLevel syslog, LogLevel expected)
    {
        var logMessage = new LogMessage("rdkafka#consumer-1", syslog, "FAIL", "some detail");

        Assert.Equal(expected, KafkaConsumerFactory.LevelForLog(logMessage));
    }

    [Fact]
    public void HandleLog_logs_facility_name_and_message()
    {
        var logger = new CapturingLogger<KafkaConsumerFactory>();
        var logMessage = new LogMessage("rdkafka#consumer-1", SyslogLevel.Error, "FAIL", "connection refused");

        KafkaConsumerFactory.HandleLog(logger, logMessage);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("FAIL", entry.State["Facility"]);
        Assert.Equal("rdkafka#consumer-1", entry.State["Name"]);
        Assert.Equal("connection refused", entry.State["Message"]);
    }
}
