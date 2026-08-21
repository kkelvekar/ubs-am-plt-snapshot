using System.Text.Json;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Builds the permanent index row written once a snapshot is complete, per design §4. The
/// only values read out of the header are <c>Payload.Event</c> and the raw text of
/// <c>Payload</c> itself; the envelope (<c>SnapshotId</c>, <c>Type</c>, the <c>Payload</c> key)
/// is never persisted.
/// </summary>
public static class PortfolioSnapshotIndexEntryBuilder
{
    /// <summary>
    /// Reads the two header values this service needs from a single resolved <c>Payload</c>
    /// element: <c>Payload.Event</c>, which has its own filterable SQL column, and the raw text
    /// of <c>Payload</c> itself, which becomes <c>display_data</c>. The document is disposed
    /// immediately and no other field is read.
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
    public static SnapshotHeaderValues ExtractHeaderValues(string headerJson)
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

        // GetRawText must run before the JsonDocument is disposed at the end of this using scope.
        return new SnapshotHeaderValues(eventType, payload.GetRawText());
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
    /// Builds the index row for a completed snapshot. <paramref name="headerValues"/>'s
    /// <c>DisplayData</c> (the header's nested <c>Payload</c> object text) is stored verbatim.
    /// </summary>
    public static PortfolioSnapshotIndexEntity Build(
        SnapshotMessage message,
        SnapshotTrackingEntity tracking,
        SnapshotHeaderValues headerValues)
        => new()
        {
            SnapshotId = message.SnapshotId,
            AccountId = message.AccountId,
            SnapshotDate = tracking.FirstReceivedAt,
            EventType = headerValues.EventType,
            AdlsPath = tracking.AdlsRootPath,
            DisplayData = headerValues.DisplayData,
        };
}
