using System.Text.Json;
using UBS.AM.PLT.Snapshot.Application.Models;

namespace UBS.AM.PLT.Snapshot.ReadApi;

/// <summary>
/// Flattens each <see cref="SnapshotIndexRow"/> into one flat grid row: the fixed columns at
/// the top level, then every top-level property of the opaque <c>DisplayData</c> JSON copied
/// up alongside them. New display keys therefore flow through to the response with zero code
/// change (solution design §7). Fixed columns win on any key collision, and null/blank/invalid
/// display JSON degrades to fixed-fields-only rather than throwing.
/// </summary>
public static class SnapshotRowFlattener
{
    // Fixed keys are already camelCase and match ASP.NET's default camelCase JSON output.
    private const string SnapshotId = "snapshotId";
    private const string AccountId = "accountId";
    private const string SnapshotDate = "snapshotDate";
    private const string EventType = "eventType";
    private const string AdlsPath = "adlsPath";
    private const string CreatedAt = "createdAt";

    public static IReadOnlyList<Dictionary<string, object?>> Flatten(IEnumerable<SnapshotIndexRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var result = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            result.Add(FlattenRow(row));
        }

        return result;
    }

    private static Dictionary<string, object?> FlattenRow(SnapshotIndexRow row)
    {
        var flat = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SnapshotId] = row.SnapshotId,
            [AccountId] = row.AccountId,
            [SnapshotDate] = row.SnapshotDate,
            [EventType] = row.EventType,
            [AdlsPath] = row.AdlsPath,
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
            // Fixed columns win on collision — skip a duplicate display key.
            if (flat.ContainsKey(property.Name))
            {
                continue;
            }

            flat[property.Name] = property.Value;
        }
    }
}
