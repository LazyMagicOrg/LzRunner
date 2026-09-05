using System.Diagnostics.CodeAnalysis;
using NuGet.Versioning;

namespace Lz.Runner;

/// <summary>
/// The pure half of resolution: which <c>Lz.Cli.*.nupkg</c> file names carry a package
/// version, and which of several candidates is newest. No disk, so it is unit-tested
/// directly; <c>Program.PickNewestLzCliAcross</c> only enumerates the feeds and hands the
/// file names through here.
///
/// <para><b>Versions are parsed and ordered the way NuGet orders them</b> (NuGet.Versioning),
/// not with <see cref="System.Version"/>. Runner 1.3.0 and earlier filtered candidates with
/// <c>System.Version.TryParse</c>, which rejects anything carrying a prerelease label —
/// <c>0.11.2-alpha.5</c>, <c>0.12.0-local.g1a2b3c-dirty</c> — so under derived versioning, where
/// a local feed holds only prereleases until a release is cut, the filter found NOTHING and
/// <c>lz</c> failed in every workspace on the machine at once (MigrationPlan §2). Precedence is
/// SemVer 2 as NuGet applies it: <c>0.11.1 &lt; 0.11.2-alpha.1 &lt; 0.11.2-alpha.2 &lt;
/// 0.11.2-beta.1 &lt; 0.11.2</c>; numeric prerelease identifiers compare numerically
/// (<c>alpha.10</c> beats <c>alpha.9</c>); build metadata (<c>+g1a2b3c</c>) never affects
/// order.</para>
///
/// <para>The consequence for a local feed is the intended one: the highest version present
/// wins, prerelease or not, exactly as a <c>*-*</c> float would resolve — which is what a
/// developer building the framework locally wants, and what the six workspaces' pinned
/// <c>Lz.*</c> references will need the moment their feeds hold derived versions.</para>
/// </summary>
public static class LzCliPackage
{
    public const string FilePrefix = "Lz.Cli.";
    public const string FileSuffix = ".nupkg";

    /// <summary>
    /// Parse the version out of an <c>Lz.Cli.&lt;version&gt;.nupkg</c> file name. False for
    /// anything else in the feed — <c>Lz.Cli.Something.1.0.0.nupkg</c> (a different package
    /// whose id extends ours), symbol packages, other ids.
    /// </summary>
    public static bool TryParseFileName(string fileName, [NotNullWhen(true)] out NuGetVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(fileName)) return false;
        if (!fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!fileName.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        // "Lz.Cli.nupkg" passes both checks with nothing in between; without this guard the
        // substring length goes negative and throws instead of answering false.
        if (fileName.Length <= FilePrefix.Length + FileSuffix.Length) return false;

        var middle = fileName.Substring(FilePrefix.Length, fileName.Length - FilePrefix.Length - FileSuffix.Length);
        return NuGetVersion.TryParse(middle, out version);
    }

    /// <summary>
    /// The newest candidate by NuGet precedence, or null when none parses. Ties keep the
    /// FIRST candidate — feeds are enumerated in NuGet.Config precedence order, so a version
    /// present in two feeds resolves to the feed that runner 1.3.0 would have chosen.
    /// </summary>
    public static (T Item, NuGetVersion Version)? PickNewest<T>(IEnumerable<(T Item, string FileName)> candidates)
    {
        (T Item, NuGetVersion Version)? best = null;
        foreach (var (item, fileName) in candidates)
        {
            if (!TryParseFileName(fileName, out var version)) continue;
            if (best is null || VersionComparer.Default.Compare(version, best.Value.Version) > 0)
                best = (item, version);
        }
        return best;
    }

    /// <summary>
    /// The version as it appears in a cache-entry directory name: NuGet's normalized form,
    /// which drops build metadata and canonicalises numeric parts. It never contains
    /// <c>+</c>, which the runner uses to append the nupkg identity. For a plain
    /// <c>0.11.1</c> it is <c>0.11.1</c>, so entries extracted by runner 1.3.0 stay hits.
    /// </summary>
    public static string CacheKey(NuGetVersion version) => version.ToNormalizedString();
}
