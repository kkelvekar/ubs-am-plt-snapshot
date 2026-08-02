using UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;

/// <summary>
/// Business rules for the snapshot-detail route identifiers, deliberately free of
/// ASP.NET/storage dependencies so they unit-test in isolation. Both values arrive straight
/// from the URL, so they are resolved before anything reaches SQL or the blob client.
/// </summary>
public static class SnapshotDetailRequest
{
    /// <summary>
    /// Trims the snapshotId and rejects a blank or over-long value. The length bound is the
    /// same <see cref="SnapshotFieldLimits.SnapshotIdMaxLength"/> the write path enforces, so
    /// a value that could never have been stored is refused before the SQL lookup.
    /// </summary>
    /// <exception cref="SnapshotDetailValidationException">Blank, or longer than the column width.</exception>
    public static string ResolveSnapshotId(string? snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            throw new SnapshotDetailValidationException("snapshotId is required.");
        }

        var resolved = snapshotId.Trim();

        if (resolved.Length > SnapshotFieldLimits.SnapshotIdMaxLength)
        {
            throw new SnapshotDetailValidationException(
                $"snapshotId must not exceed {SnapshotFieldLimits.SnapshotIdMaxLength} characters.");
        }

        return resolved;
    }

    /// <summary>
    /// Trims the payloadType and rejects a blank, over-long or path-unsafe value. Every
    /// character must be in <c>[A-Za-z0-9_-]</c> — stricter than the write-side envelope rule
    /// on purpose: no '.', '/', '\' or any other separator can appear, so no traversal
    /// sequence can ever reach the blob client through the composed blob name.
    /// </summary>
    /// <exception cref="SnapshotDetailValidationException">Blank, over-long, or containing any other character.</exception>
    public static string ResolvePayloadType(string? payloadType)
    {
        if (string.IsNullOrWhiteSpace(payloadType))
        {
            throw new SnapshotDetailValidationException("payloadType is required.");
        }

        var resolved = payloadType.Trim();

        if (resolved.Length > SnapshotFieldLimits.PayloadTypeMaxLength)
        {
            throw new SnapshotDetailValidationException(
                $"payloadType must not exceed {SnapshotFieldLimits.PayloadTypeMaxLength} characters.");
        }

        foreach (var character in resolved)
        {
            if (!IsAllowed(character))
            {
                throw new SnapshotDetailValidationException(
                    "payloadType may contain only letters, digits, '_' and '-'.");
            }
        }

        return resolved;
    }

    private static bool IsAllowed(char character)
        => character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';
}
