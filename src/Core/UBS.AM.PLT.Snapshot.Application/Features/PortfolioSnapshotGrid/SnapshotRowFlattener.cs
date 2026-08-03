using System.Text.Json;

namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

/// <summary>
/// Flattens each PortfolioSnapshotIndexRow into one flat grid row: the fixed columns at
/// the top level, then every top-level property of the opaque DisplayData JSON copied
/// up alongside them. New display keys therefore flow through to the response with zero code
/// change (solution design section 7). Fixed columns win on any key collision, and null/blank/invalid
/// display JSON degrades to fixed-fields-only rather than throwing. AdlsPath is deliberately
/// never emitted in the response.
/// </summary>
public static class SnapshotRowFlattener
{
    // Fixed keys are already camelCase and match the API default camelCase JSON output.
    private const string SnapshotId = "snapshotId";
    private const string AccountId = "accountId";
    private const string SnapshotDate = "snapshotDate";
    private const string EventType = "eventType";
    private const string CreatedAt = "createdAt";

    public static IReadOnlyList<Dictionary<string, object?>> Flatten(IEnumerable<PortfolioSnapshotIndexRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var result = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            result.Add(FlattenRow(row));
        }

        return result;
    }

    private static Dictionary<string, object?> FlattenRow(PortfolioSnapshotIndexRow row)
    {
        var flat = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SnapshotId] = row.SnapshotId,
            [AccountId] = row.AccountId,
            [SnapshotDate] = row.SnapshotDate,
            [EventType] = row.EventType,
            [CreatedAt] = row.CreatedAt,
        };

        CopyDisplayData(row.DisplayDataJson, flat);

        return flat;
    }

    private static void CopyDisplayData(string? displayDataJson, Dictionary<string, object?> flat)
    {
        if (string.IsNullOrWhiteSpace(displayDataJson))
        {
            return;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(displayDataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // Clone so the value survives disposal of the JsonDocument.
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Invalid display JSON degrades to fixed-fields-only, never a failed request.
            return;
        }

        foreach (var property in root.EnumerateObject())
        {
            // Fixed columns win on collision - skip a duplicate display key.
            if (flat.ContainsKey(property.Name))
            {
                continue;
            }

            flat[property.Name] = property.Value;
        }
    }
}
