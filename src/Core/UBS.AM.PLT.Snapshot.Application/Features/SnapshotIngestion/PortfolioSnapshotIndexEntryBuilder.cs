using System.Text.Json;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Builds the permanent index row written once a snapshot is complete, per design §4. The
/// only value read out of the header is <c>Payload.Event</c>; the persisted display data is the
/// header text verbatim.
/// </summary>
public static class PortfolioSnapshotIndexEntryBuilder
{
    /// <summary>
    /// Reads the one header value this service needs, <c>Payload.Event</c>, which has its own
    /// filterable SQL column. The document is disposed immediately and no other field is read.
    /// </summary>
    /// <remarks>
    /// Malformed header JSON propagates, so no index row is written, the tracking row stays
    /// RECEIVING and the offset is not committed; redelivery retries.
    ///
    /// A missing, non-object <c>Payload</c>, non-string, blank or over-long
    /// <c>Payload.Event</c> is a non-retryable upstream contract breach. The handler records
    /// the existing tracking row as FAILED and publishes a rejection response. Malformed JSON
    /// remains a retryable processing error and propagates as <see cref="JsonException"/>.
    /// </remarks>
    public static string ExtractEventType(string headerJson)
    {
        using var header = JsonDocument.Parse(headerJson);

        if (header.RootElement.ValueKind != JsonValueKind.Object
            || !TryGetFirstPropertyIgnoreCase(header.RootElement, "Payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object
            || !TryGetFirstPropertyIgnoreCase(payload, "Event", out var eventProperty)
            || eventProperty.ValueKind != JsonValueKind.String
            || eventProperty.GetString() is not { } eventType
            || string.IsNullOrWhiteSpace(eventType)
            || eventType.Length > SnapshotFieldLimits.EventTypeMaxLength)
        {
            throw InvalidSnapshotHeaderException.InvalidEvent();
        }

        return eventType;
    }

    private static bool TryGetFirstPropertyIgnoreCase(
        JsonElement objectElement,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in objectElement.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Builds the index row for a completed snapshot. <paramref name="headerJson"/> is stored
    /// verbatim as the display data.
    /// </summary>
    public static PortfolioSnapshotIndexEntity Build(
        SnapshotMessage message,
        SnapshotTrackingEntity tracking,
        string headerJson,
        string eventType)
        => new()
        {
            SnapshotId = message.SnapshotId,
            AccountId = message.AccountId,
            SnapshotDate = tracking.FirstReceivedAt,
            EventType = eventType,
            AdlsPath = tracking.AdlsRootPath,
            DisplayData = headerJson,
        };
}
