using System.Text.Json;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// Builds the permanent index row written once a snapshot is complete, per design §4.
/// The header stays opaque: the only value read out of it is <c>eventType</c>, and the
/// persisted display data is always the header text verbatim.
/// </summary>
public static class SnapshotIndexEntryBuilder
{
    /// <summary>
    /// Reads the ONE header value this service needs: <c>eventType</c>, which has its own
    /// filterable SQL column. The header otherwise stays opaque — the document is disposed
    /// immediately, no other field is ever read, and nothing from this parse is written
    /// (the persisted display data is always the header text verbatim).
    /// </summary>
    /// <remarks>
    /// Malformed header JSON throws out of <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/>
    /// and propagates: no index row, no MarkComplete, no offset commit, recovery by
    /// redelivery. A well-formed header that simply lacks a usable <c>eventType</c> is an
    /// upstream contract breach, not a transport failure — an empty string is returned and
    /// the row is still written, because retrying it forever would never fix it.
    /// </remarks>
    public static string ExtractEventType(string headerJson)
    {
        using var header = JsonDocument.Parse(headerJson);

        if (header.RootElement.ValueKind == JsonValueKind.Object)
        {
            // JsonSerializerDefaults.Web used to bind eventType and EventType alike;
            // JsonDocument.TryGetProperty is case-SENSITIVE, so match explicitly or every
            // PascalCase header silently regresses to an empty EventType. On duplicate keys
            // differing only by case, document order decides — deterministic across redeliveries.
            foreach (var property in header.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "eventType", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } eventType)
                {
                    return eventType;
                }

                break;
            }
        }

        return string.Empty;
    }

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
