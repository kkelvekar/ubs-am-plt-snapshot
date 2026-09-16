using System.Text.RegularExpressions;

namespace UBS.AM.PLT.Snapshot.Domain;

// Reserved for the Development simulator; ordinary producers must not use this namespace.
public static partial class LiveTestSnapshot
{
    public const string IdPrefix = "live-test-";
    public const string StoragePrefix = "portfolio_snapshots/";

    public static string NewId() => $"{IdPrefix}{Guid.NewGuid():N}";

    public static bool IsTestId(string snapshotId) => TestIdPattern().IsMatch(snapshotId);

    public static bool TryGetLocation(string rootPath, out string snapshotId, out string accountId)
    {
        var match = RootPattern().Match(rootPath);
        snapshotId = match.Groups["snapshotId"].Value;
        accountId = match.Groups["accountId"].Value;
        return match.Success;
    }

    [GeneratedRegex(@"\Alive-test-[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TestIdPattern();

    [GeneratedRegex(@"\Aportfolio_snapshots/year=[0-9]{4}/month=(0[1-9]|1[0-2])/accountId=(?<accountId>[A-Za-z0-9_-]{1,20})/snapshotId=(?<snapshotId>live-test-[0-9a-f]{32})\z", RegexOptions.CultureInvariant)]
    private static partial Regex RootPattern();
}
