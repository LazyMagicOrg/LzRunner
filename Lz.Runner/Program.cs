using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;

namespace Lz.Runner;

/// <summary>
/// Thin dispatcher that finds the "correct" <c>Lz.Cli</c> package for the user's
/// current directory and invokes it via <c>dotnet &lt;dll&gt;</c>. The rule is
/// identical to what NuGet itself does at restore time: walk up for
/// <c>NuGet.Config</c>, read its <c>&lt;packageSources&gt;</c>, scan every local
/// source for the newest <c>Lz.Cli.*.nupkg</c>. Extract once into a per-version
/// cache, then forward the process.
///
/// When no <c>NuGet.Config</c> is in scope we fall back to the build-origin
/// feed + version that were baked into this assembly at build time — i.e. the
/// <c>_lz/Packages</c> this runner was installed from, at the version it was
/// installed as. That makes "just run <c>lz</c> from a random directory"
/// behave the way a user who installed it expects.
/// </summary>
internal static class Program
{
    private const string CachePrefix = "lz-runner";
    private const string VerboseEnv  = "LZ_RUNNER_VERBOSE";

    private static int Main(string[] args)
    {
        try
        {
            var (nupkgPath, feedDescription, cliVersion) = ResolveCliPackage();
            Verbose($"resolved {cliVersion} from {feedDescription}");
            var dllPath = EnsureExtracted(nupkgPath, cliVersion);
            return InvokeDll(dllPath, args);
        }
        catch (LzRunnerException ex)
        {
            Console.Error.WriteLine($"lz-runner: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"lz-runner: unexpected error: {ex}");
            return 1;
        }
    }

    // -----------------------------------------------------------------------
    // Resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Find the Lz.Cli nupkg that should serve the current invocation and
    /// return its full path plus a human-readable description of which feed
    /// it came from (for verbose output) plus the parsed version.
    ///
    /// Honours NuGet's own config-aggregation rules: walk machine-level,
    /// user-level, then every <c>NuGet.Config</c> from drive root down to
    /// cwd, merging <c>&lt;packageSources&gt;</c> as we go (with
    /// <c>&lt;clear/&gt;</c> / <c>&lt;remove/&gt;</c> / disabled sources
    /// respected). Each local source resolves relative to the config that
    /// declared it.
    /// </summary>
    private static (string NupkgPath, string FeedDescription, Version CliVersion) ResolveCliPackage()
    {
        var (configs, localFeeds) = ResolveEffectiveLocalFeeds(Directory.GetCurrentDirectory());

        if (configs.Count > 0)
        {
            Verbose($"effective NuGet.Config chain ({configs.Count}): {string.Join(" | ", configs)}");
            Verbose($"effective local feeds ({localFeeds.Count}): {string.Join(" | ", localFeeds)}");

            var pick = PickNewestLzCliAcross(localFeeds);
            if (pick != null)
                return (pick.Value.Path, pick.Value.Feed, pick.Value.Version);

            Verbose($"no Lz.Cli.*.nupkg found in any effective local feed — using build-origin default");
        }
        else
        {
            Verbose("no NuGet.Config found in ancestry, user-level, or machine-level — using build-origin default");
        }

        return FallBackToBuildOrigin();
    }

    /// <summary>
    /// Fall back to the feed + version baked into this assembly at build time.
    /// Prefer the exact runner-version match; drop to "newest in origin" if
    /// that exact file isn't there (useful after a local rebuild bumped the
    /// version but the runner hasn't been re-installed).
    /// </summary>
    private static (string NupkgPath, string FeedDescription, Version CliVersion) FallBackToBuildOrigin()
    {
        var originFeed = GetAssemblyMetadata("Lz.Runner.BuildOriginPackages");
        var defaultVersion = GetAssemblyMetadata("Lz.Runner.DefaultCliVersion");

        if (string.IsNullOrWhiteSpace(originFeed) || !Directory.Exists(originFeed))
            throw new LzRunnerException(
                "no NuGet.Config found in ancestry and build-origin feed is missing " +
                $"(baked path: '{originFeed ?? "<none>"}'). Reinstall Lz.Runner from an accessible _lz/Packages.");

        // Prefer Lz.Cli.<DefaultCliVersion>.nupkg if present.
        if (!string.IsNullOrWhiteSpace(defaultVersion))
        {
            var exact = Path.Combine(originFeed, $"Lz.Cli.{defaultVersion}.nupkg");
            if (File.Exists(exact) && Version.TryParse(defaultVersion, out var v))
                return (exact, $"{originFeed} (build-origin default)", v);
        }

        // Otherwise take the newest Lz.Cli.*.nupkg in the origin feed.
        var newest = PickNewestLzCliAcross(new[] { originFeed })
            ?? throw new LzRunnerException(
                $"no Lz.Cli.*.nupkg found at build-origin feed '{originFeed}'.");

        return (newest.Path, $"{originFeed} (build-origin, newest)", newest.Version);
    }

    /// <summary>
    /// Discover the full chain of NuGet configs that apply to
    /// <paramref name="startDir"/>, applied in NuGet's real precedence order,
    /// and return the resulting list of effective local (filesystem) sources.
    /// </summary>
    /// <remarks>
    /// Precedence (lowest first, closest-to-cwd last so closer wins):
    /// <list type="number">
    ///   <item>Machine-wide configs under <c>%ProgramData%\NuGet\Config\*.Config</c>.</item>
    ///   <item>User-level config at <c>%AppData%\NuGet\NuGet.Config</c>.</item>
    ///   <item>Every <c>NuGet.Config</c> walking from the drive root down to
    ///   <paramref name="startDir"/>.</item>
    /// </list>
    /// Within each config, <c>&lt;packageSources&gt;</c> is merged into the
    /// running set: <c>&lt;clear/&gt;</c> empties it, <c>&lt;add/&gt;</c>
    /// inserts (replacing by key), <c>&lt;remove/&gt;</c> removes by key.
    /// Relative source paths resolve against the directory of the owning
    /// config, not cwd. <c>&lt;disabledPackageSources&gt;</c> is honoured.
    /// Returns the configs that contributed and the effective list of local
    /// directories that exist on disk.
    /// </remarks>
    private static (List<string> Configs, List<string> LocalFeeds) ResolveEffectiveLocalFeeds(string startDir)
    {
        var configs = DiscoverConfigChain(startDir);

        // Apply each config in order. Use a preserve-insertion-order map keyed
        // by source name so later configs can override by key.
        var sources = new Dictionary<string, (string Value, int InsertOrder)>(StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int order = 0;

        foreach (var cfg in configs)
        {
            try
            {
                var doc = XDocument.Load(cfg);
                var cfgDir = Path.GetDirectoryName(cfg)
                    ?? throw new InvalidOperationException($"can't derive directory from {cfg}");

                ApplyPackageSources(doc, cfgDir, sources, ref order);
                ApplyDisabledSources(doc, disabled);
            }
            catch (Exception ex)
            {
                Verbose($"skipping malformed config {cfg}: {ex.Message}");
            }
        }

        var feeds = sources
            .Where(kv => !disabled.Contains(kv.Key))
            .Where(kv => !LooksLikeRemote(kv.Value.Value))
            .OrderBy(kv => kv.Value.InsertOrder)
            .Select(kv => kv.Value.Value)
            .Where(Directory.Exists)
            .ToList();

        return (configs, feeds);
    }

    private static List<string> DiscoverConfigChain(string startDir)
    {
        var list = new List<string>();

        // 1. Machine-wide: %ProgramData%\NuGet\Config\*.Config
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
        {
            var machineDir = Path.Combine(programData, "NuGet", "Config");
            if (Directory.Exists(machineDir))
                list.AddRange(Directory.EnumerateFiles(machineDir, "*.Config", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        }

        // 2. User-level: %AppData%\NuGet\NuGet.Config
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            var userCfg = Path.Combine(appData, "NuGet", "NuGet.Config");
            if (File.Exists(userCfg)) list.Add(userCfg);
        }

        // 3. Walk from startDir upward collecting every NuGet.Config we find,
        //    then reverse so root-of-filesystem is applied first and cwd last.
        var walked = new List<string>();
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            foreach (var name in new[] { "NuGet.Config", "nuget.config", "Nuget.Config" })
            {
                var candidate = Path.Combine(dir.FullName, name);
                if (File.Exists(candidate)) { walked.Add(candidate); break; }
            }
            dir = dir.Parent;
        }
        walked.Reverse();
        list.AddRange(walked);

        return list;
    }

    private static void ApplyPackageSources(
        XDocument doc,
        string cfgDir,
        Dictionary<string, (string Value, int InsertOrder)> sources,
        ref int order)
    {
        var pkgSources = doc.Root?.Element("packageSources");
        if (pkgSources == null) return;

        foreach (var elem in pkgSources.Elements())
        {
            switch (elem.Name.LocalName)
            {
                case "clear":
                    sources.Clear();
                    break;

                case "add":
                {
                    var key = (string?)elem.Attribute("key");
                    var value = (string?)elem.Attribute("value");
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) break;
                    sources[key] = (ResolveSourceValue(value, cfgDir), order++);
                    break;
                }

                case "remove":
                {
                    var key = (string?)elem.Attribute("key");
                    if (!string.IsNullOrWhiteSpace(key)) sources.Remove(key);
                    break;
                }
            }
        }
    }

    private static void ApplyDisabledSources(XDocument doc, HashSet<string> disabled)
    {
        var section = doc.Root?.Element("disabledPackageSources");
        if (section == null) return;

        foreach (var elem in section.Elements())
        {
            switch (elem.Name.LocalName)
            {
                case "clear":
                    disabled.Clear();
                    break;
                case "add":
                {
                    var key = (string?)elem.Attribute("key");
                    var value = (string?)elem.Attribute("value");
                    if (string.IsNullOrWhiteSpace(key)) break;
                    if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                        disabled.Add(key);
                    else
                        disabled.Remove(key);
                    break;
                }
            }
        }
    }

    private static string ResolveSourceValue(string value, string cfgDir)
    {
        if (LooksLikeRemote(value)) return value;        // leave remote URLs alone
        if (Path.IsPathRooted(value)) return Path.GetFullPath(value);
        return Path.GetFullPath(Path.Combine(cfgDir, value));
    }

    private static bool LooksLikeRemote(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Across the given feeds, find the highest-versioned Lz.Cli.*.nupkg and
    /// return its path + feed + version. Returns null if no feed has one.
    /// </summary>
    private static (string Path, string Feed, Version Version)? PickNewestLzCliAcross(IEnumerable<string> feeds)
    {
        (string Path, string Feed, Version Version)? best = null;
        foreach (var feed in feeds)
        {
            foreach (var file in Directory.EnumerateFiles(feed, "Lz.Cli.*.nupkg"))
            {
                var name = Path.GetFileName(file);
                // Matches Lz.Cli.<version>.nupkg (no extra dots after the version)
                const string prefix = "Lz.Cli.";
                const string suffix = ".nupkg";
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var verStr = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
                if (!Version.TryParse(verStr, out var v)) continue;

                if (best == null || v > best.Value.Version)
                    best = (file, feed, v);
            }
        }
        return best;
    }

    // -----------------------------------------------------------------------
    // Extraction + invocation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Ensure the nupkg is extracted to the per-version cache, then return
    /// the path to Lz.Cli.dll. Extraction is idempotent; subsequent calls
    /// against the same version skip the unpack.
    /// </summary>
    private static string EnsureExtracted(string nupkgPath, Version version)
    {
        var cacheRoot = GetCacheRoot();
        var cacheDir = Path.Combine(cacheRoot, version.ToString());
        var dllPath = Path.Combine(cacheDir, "tools", "net9.0", "any", "Lz.Cli.dll");

        if (File.Exists(dllPath)) return dllPath;

        Directory.CreateDirectory(cacheDir);
        try
        {
            ZipFile.ExtractToDirectory(nupkgPath, cacheDir, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            throw new LzRunnerException(
                $"failed to extract {nupkgPath} into {cacheDir}: {ex.Message}");
        }

        if (!File.Exists(dllPath))
            throw new LzRunnerException(
                $"extracted package at {cacheDir} did not contain the expected {dllPath}.");

        return dllPath;
    }

    /// <summary>
    /// Cache location — <c>%LOCALAPPDATA%\lz-runner\cache</c> on Windows,
    /// <c>$XDG_CACHE_HOME/lz-runner</c> on Unix, with sensible fallbacks.
    /// </summary>
    private static string GetCacheRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            return Path.Combine(localAppData, CachePrefix, "cache");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, $".{CachePrefix}", "cache");
    }

    /// <summary>
    /// Spawn <c>dotnet &lt;dll&gt; &lt;args&gt;</c>, forward stdio, return its exit code.
    /// </summary>
    private static int InvokeDll(string dllPath, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add(dllPath);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new LzRunnerException("failed to spawn dotnet — is it on PATH?");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    // -----------------------------------------------------------------------
    // Utilities
    // -----------------------------------------------------------------------

    /// <summary>
    /// Read a value baked into this assembly at build time via
    /// <see cref="AssemblyMetadataAttribute"/>. Returns null if absent.
    /// </summary>
    private static string? GetAssemblyMetadata(string key) =>
        typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))
            ?.Value;

    private static void Verbose(string message)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(VerboseEnv)))
            Console.Error.WriteLine($"lz-runner: {message}");
    }
}

internal sealed class LzRunnerException : Exception
{
    public LzRunnerException(string message) : base(message) { }
}
