using System.Text.Json;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Builds the permanent index row written once a snapshot is complete, per design §4. The
/// only value read out of the header is <c>eventType</c>; the persisted display data is the
/// header text verbatim.
/// </summary>
public static class SnapshotIndexEntryBuilder
{
    /// <summary>
    /// Reads the one header value this service needs, <c>eventType</c>, which has its own
    /// filterable SQL column. Returns an empty string when the header carries no usable value.
    /// The document is disposed immediately and no other field is read.
    /// </summary>
    /// <remarks>
    /// Malformed header JSON propagates, so no index row is written, the tracking row stays
    /// RECEIVING and the offset is not committed; redelivery retries.
    ///
    /// A missing, non-string or over-long <c>eventType</c> falls back to an empty string and
    /// the row is still written: it is an upstream contract breach that retrying would never
    /// fix, and an over-long value cannot be caught by envelope validation because it comes
    /// from the header blob rather than the message. The header text still reaches
    /// display_data verbatim; only the filterable column falls back.
    /// </remarks>
    public static string ExtractEventType(string headerJson)
    {
        using var header = JsonDocument.Parse(headerJson);

        if (header.RootElement.ValueKind == JsonValueKind.Object)
        {
            // Matched case-insensitively by hand: TryGetProperty is case-sensitive and would
            // miss the PascalCase headers the wire contract also allows. On keys differing
            // only by case, document order decides, which is stable across redeliveries.
            foreach (var property in header.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "eventType", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } eventType
                    && eventType.Length <= SnapshotFieldLimits.EventTypeMaxLength)
                {
                    return eventType;
                }

                break;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Builds the index row for a completed snapshot. <paramref name="headerJson"/> is stored
    /// verbatim as the display data.
    /// </summary>
    public static SnapshotIndexEntity Build(
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
