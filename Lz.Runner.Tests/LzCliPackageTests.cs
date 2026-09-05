using NuGet.Versioning;

namespace Lz.Runner.Tests;

/// <summary>
/// The pure half of resolution. Runner 1.3.0 filtered feed files with System.Version.TryParse,
/// which rejects every prerelease, so a feed holding only derived versions
/// (0.11.2-alpha.5, 0.12.0-local.g1a2b3c-dirty) resolved to NOTHING and `lz` failed in every
/// workspace on the machine at once. These pin the replacement: NuGet's own parsing and
/// precedence.
/// </summary>
public class LzCliPackageTests
{
    // ---- TryParseFileName: what counts as an Lz.Cli package ----

    [Theory]
    [InlineData("Lz.Cli.0.11.1.nupkg", "0.11.1")]
    [InlineData("Lz.Cli.0.11.2-alpha.5.nupkg", "0.11.2-alpha.5")]
    [InlineData("Lz.Cli.0.12.0-local.g1a2b3c-dirty.nupkg", "0.12.0-local.g1a2b3c-dirty")]
    [InlineData("Lz.Cli.0.11.2-alpha.5+g1a2b3c.nupkg", "0.11.2-alpha.5+g1a2b3c")]
    [InlineData("lz.cli.0.11.1.NUPKG", "0.11.1")]
    public void ParsesReleaseAndPrereleaseFileNames(string fileName, string expected)
    {
        Assert.True(LzCliPackage.TryParseFileName(fileName, out var version));
        Assert.Equal(NuGetVersion.Parse(expected), version);
    }

    [Theory]
    [InlineData("Lz.Cli.Something.1.0.0.nupkg")]   // a different package whose id extends ours
    [InlineData("Lz.Cli.0.11.1.snupkg")]           // symbols
    [InlineData("Lz.Core.0.11.1.nupkg")]           // a sibling package
    [InlineData("Lz.Cli.nupkg")]                   // no version at all
    [InlineData("Lz.Cli..nupkg")]
    [InlineData("")]
    public void RejectsEverythingElseInTheFeed(string fileName)
        => Assert.False(LzCliPackage.TryParseFileName(fileName, out _));

    // ---- PickNewest: NuGet precedence, not string order and not System.Version ----

    [Fact]
    public void APrereleaseAboveTheHighestRelease_Wins()
    {
        // THE FIX. Under derived versioning the local feed holds 0.11.2-alpha.N above the last
        // release 0.11.1; runner 1.3.0 skipped the alpha and kept dispatching to 0.11.1 — or,
        // when the feed held only prereleases, dispatched to nothing.
        var pick = LzCliPackage.PickNewest(new[]
        {
            ("a", "Lz.Cli.0.11.1.nupkg"),
            ("b", "Lz.Cli.0.11.2-alpha.5.nupkg"),
        });

        Assert.Equal("b", pick!.Value.Item);
        Assert.Equal(NuGetVersion.Parse("0.11.2-alpha.5"), pick.Value.Version);
    }

    [Fact]
    public void TheReleaseOutranksItsOwnPrereleases()
    {
        var pick = LzCliPackage.PickNewest(new[]
        {
            ("rc", "Lz.Cli.0.11.2-rc.1.nupkg"),
            ("release", "Lz.Cli.0.11.2.nupkg"),
            ("alpha", "Lz.Cli.0.11.2-alpha.9.nupkg"),
        });

        Assert.Equal("release", pick!.Value.Item);
    }

    [Fact]
    public void PrereleaseLabelsOrderAsSemVer_NumericIdentifiersNumerically()
    {
        // alpha.10 > alpha.9 (numeric), and beta > alpha (alphabetical). A string sort would
        // put alpha.9 above alpha.10; the decided tag vocabulary relies on this order.
        var pick = LzCliPackage.PickNewest(new[]
        {
            ("a9", "Lz.Cli.0.11.2-alpha.9.nupkg"),
            ("a10", "Lz.Cli.0.11.2-alpha.10.nupkg"),
        });
        Assert.Equal("a10", pick!.Value.Item);

        pick = LzCliPackage.PickNewest(new[]
        {
            ("beta", "Lz.Cli.0.11.2-beta.1.nupkg"),
            ("alpha", "Lz.Cli.0.11.2-alpha.99.nupkg"),
        });
        Assert.Equal("beta", pick!.Value.Item);
    }

    [Fact]
    public void TheDecidedLabelsOrderOrdinally_AndLocalOutranksAlphaAndBeta()
    {
        // SdlcVersioning section 7 chose the labels alpha (automatic), beta and rc (by tag) and
        // local (never published). NuGet compares non-numeric identifiers ordinally, so the order
        // is alpha < beta < local < rc < release - a developer's own local build outranks any
        // alpha or beta of the same base version in a feed and loses only to rc and the release.
        // That is the intended local-lane behaviour; pinned here so nobody assumes 'local' sorts
        // below alpha (it does not, and neither does 'dev').
        var pick = LzCliPackage.PickNewest(new[]
        {
            ("alpha", "Lz.Cli.0.11.2-alpha.40.nupkg"),
            ("beta", "Lz.Cli.0.11.2-beta.3.nupkg"),
            ("local", "Lz.Cli.0.11.2-local.g1a2b3c-dirty.nupkg"),
        });
        Assert.Equal("local", pick!.Value.Item);

        pick = LzCliPackage.PickNewest(new[]
        {
            ("local", "Lz.Cli.0.11.2-local.g1a2b3c.nupkg"),
            ("rc", "Lz.Cli.0.11.2-rc.1.nupkg"),
        });
        Assert.Equal("rc", pick!.Value.Item);
    }

    [Fact]
    public void BuildMetadataNeverAffectsTheOrder_SoATieKeepsTheFirstFeed()
    {
        // Two feeds can hold the same version packed from different commits. Metadata is not
        // precedence (SemVer 2), so this is a tie, and a tie keeps the FIRST candidate — feeds
        // arrive in the order the config chain is walked (machine, user, then root-to-cwd), so
        // the OUTERMOST config's feed wins a tie, exactly as runner 1.3.0 behaved.
        var pick = LzCliPackage.PickNewest(new[]
        {
            ("first", "Lz.Cli.0.11.1+gaaaaaaa.nupkg"),
            ("second", "Lz.Cli.0.11.1+gbbbbbbb.nupkg"),
            ("third", "Lz.Cli.0.11.1.nupkg"),
        });

        Assert.Equal("first", pick!.Value.Item);
    }

    [Fact]
    public void UnparseableCandidatesAreSkipped_NotFatal()
    {
        var pick = LzCliPackage.PickNewest(new[]
        {
            ("decoy", "Lz.Cli.Tools.9.9.9.nupkg"),
            ("real", "Lz.Cli.0.11.1.nupkg"),
        });

        Assert.Equal("real", pick!.Value.Item);
    }

    [Fact]
    public void NothingParseable_IsNull()
        => Assert.Null(LzCliPackage.PickNewest(new[] { ("x", "Lz.Cli.Tools.9.9.9.nupkg") }));

    // ---- CacheKey: the directory name half that must never contain '+' ----

    [Theory]
    [InlineData("0.11.1", "0.11.1")]                              // unchanged from runner 1.3.0: existing entries stay hits
    [InlineData("0.11.2-alpha.5+g1a2b3c", "0.11.2-alpha.5")]      // metadata dropped: '+' is the identity separator
    [InlineData("0.11.2-Alpha.5", "0.11.2-Alpha.5")]              // labels keep their case
    public void CacheKeyIsTheNormalizedVersion_WithoutMetadata(string version, string expected)
    {
        var key = LzCliPackage.CacheKey(NuGetVersion.Parse(version));

        Assert.Equal(expected, key);
        Assert.DoesNotContain("+", key);
    }
}

/// <summary>
/// The disk half: feed enumeration feeding the pure picker. Temp feeds, real files, so a
/// regression to System.Version would be caught here as well as above.
/// </summary>
public class PickNewestLzCliAcrossTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lz-runner-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private string Feed(string name, params string[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "not a real package");
        return dir;
    }

    [Fact]
    public void PicksThePrereleaseInALaterFeed_OverTheReleaseInAnEarlierOne()
    {
        var a = Feed("a", "Lz.Cli.0.11.1.nupkg", "Lz.Core.0.11.1.nupkg");
        var b = Feed("b", "Lz.Cli.0.11.2-alpha.1.nupkg", "Lz.Cli.Tools.9.9.9.nupkg", "Lz.Cli.0.11.2-alpha.1.snupkg");

        var pick = Program.PickNewestLzCliAcross(new[] { a, b });

        Assert.NotNull(pick);
        Assert.Equal(Path.Combine(b, "Lz.Cli.0.11.2-alpha.1.nupkg"), pick.Value.Path);
        Assert.Equal(b, pick.Value.Feed);
        Assert.Equal(NuGetVersion.Parse("0.11.2-alpha.1"), pick.Value.Version);
    }

    [Fact]
    public void SameVersionInTwoFeeds_KeepsTheFirstFeed()
    {
        var a = Feed("a", "Lz.Cli.0.11.1.nupkg");
        var b = Feed("b", "Lz.Cli.0.11.1.nupkg");

        var pick = Program.PickNewestLzCliAcross(new[] { a, b });

        Assert.Equal(a, pick!.Value.Feed);
    }

    [Fact]
    public void NoLzCliAnywhere_IsNull()
    {
        var a = Feed("a", "Lz.Core.0.11.1.nupkg");
        Assert.Null(Program.PickNewestLzCliAcross(new[] { a }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
