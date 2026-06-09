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
/// If the resolved chain doesn't lead to a local feed containing an
/// <c>Lz.Cli.*.nupkg</c>, the runner emits a clear error and exits non-zero.
/// There is no baked-in fallback — <c>lz</c> is a tool, not a script that runs
/// "from any directory"; refusing to run beats silently dispatching to stale
/// logic. See <c>Platform/LzRunnerSplit.md</c> in the Monro repo for design
/// rationale.
///
/// Entry-point contract with Lz.Cli: before spawning dotnet, the runner sets
/// three environment variables that the Lz.Cli <c>--version</c> handler reads:
///   <c>LZ_RUNNER_VERSION</c>     — this runner's version (e.g. "1.0.0")
///   <c>LZ_RUNNER_NUPKG_PATH</c>  — absolute path to the resolved nupkg
///   <c>LZ_RUNNER_FEED</c>        — the local feed directory that nupkg came from
/// Keep this contract stable across Lz.Cli releases.
/// </summary>
internal static class Program
{
    private const string CachePrefix = "lz-runner";
    private const string VerboseEnv  = "LZ_RUNNER_VERBOSE";

    // Env-var names passed to the spawned Lz.Cli process so its --version
    // handler can report 3-line output (runner + cli + plugin).
    private const string EnvRunnerVersion = "LZ_RUNNER_VERSION";
    private const string EnvNupkgPath     = "LZ_RUNNER_NUPKG_PATH";
    private const string EnvFeed          = "LZ_RUNNER_FEED";

    private static int Main(string[] args)
    {
        try
        {
            var (nupkgPath, feedDescription, feedDir, cliVersion) = ResolveCliPackage();
            Verbose($"resolved {cliVersion} from {feedDescription}");
            var dllPath = EnsureExtracted(nupkgPath, cliVersion);
            return InvokeDll(dllPath, args, nupkgPath, feedDir);
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
    /// it came from (for verbose output), the feed directory itself (for
    /// passing through to Lz.Cli's --version handler), and the parsed version.
    ///
    /// Honours NuGet's own config-aggregation rules: walk machine-level,
    /// user-level, then every <c>NuGet.Config</c> from drive root down to
    /// cwd, merging <c>&lt;packageSources&gt;</c> as we go (with
    /// <c>&lt;clear/&gt;</c> / <c>&lt;remove/&gt;</c> / disabled sources
    /// respected). Each local source resolves relative to the config that
    /// declared it.
    ///
    /// If no in-scope feed contains an <c>Lz.Cli.*.nupkg</c>, throws
    /// <see cref="LzRunnerException"/> with a clear message — there is no
    /// baked-in fallback (intentional; see class doc).
    /// </summary>
    private static (string NupkgPath, string FeedDescription, string FeedDir, Version CliVersion) ResolveCliPackage()
    {
        var (configs, localFeeds) = ResolveEffectiveLocalFeeds(Directory.GetCurrentDirectory());

        Verbose($"effective NuGet.Config chain ({configs.Count}): {string.Join(" | ", configs)}");
        Verbose($"effective local feeds ({localFeeds.Count}): {string.Join(" | ", localFeeds)}");

        var pick = PickNewestLzCliAcross(localFeeds);
        if (pick != null)
            return (pick.Value.Path, pick.Value.Feed, pick.Value.Feed, pick.Value.Version);

        // No baked-in fallback — fail loudly. Tell the user exactly what to fix
        // and how to see what the runner tried.
        var detail = localFeeds.Count == 0
            ? "no local NuGet feeds in scope"
            : $"no Lz.Cli.*.nupkg in any of {localFeeds.Count} in-scope local feed(s)";

        throw new LzRunnerException(
            $"{detail} for current directory '{Directory.GetCurrentDirectory()}'." + Environment.NewLine + Environment.NewLine +
            "  Configure a feed in a NuGet.Config file (machine-wide, user-level, or" + Environment.NewLine +
            "  walking up from cwd) that contains the Lz packages, or cd into a tenant" + Environment.NewLine +
            "  repo that has them." + Environment.NewLine + Environment.NewLine +
            "  Run with LZ_RUNNER_VERBOSE=1 to see which configs and feeds were tried.");
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
    ///
    /// The Lz.Cli package layout is <c>tools/&lt;tfm&gt;/any/Lz.Cli.dll</c>
    /// where &lt;tfm&gt; is whatever TargetFramework Lz.Cli was packed for
    /// (net9.0, net10.0, etc.). We discover the TFM dynamically rather than
    /// hard-coding it so the runner survives Lz.Cli moving to newer .NET
    /// versions without itself needing to be re-released. Prefers the
    /// highest-numbered TFM directory when more than one exists (which
    /// shouldn't happen for a tool package, but defensive).
    /// </summary>
    private static string EnsureExtracted(string nupkgPath, Version version)
    {
        var cacheRoot = GetCacheRoot();
        var cacheDir = Path.Combine(cacheRoot, version.ToString());

        // Fast path: already extracted, just locate the dll.
        if (Directory.Exists(cacheDir))
        {
            var cached = FindCliDll(cacheDir);
            if (cached != null) return cached;
        }

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

        var dllPath = FindCliDll(cacheDir);
        if (dllPath == null)
            throw new LzRunnerException(
                $"extracted package at {cacheDir} did not contain a Lz.Cli.dll under tools/<tfm>/any/.");

        return dllPath;
    }

    /// <summary>
    /// Locate <c>Lz.Cli.dll</c> inside an extracted nupkg tree. Returns the
    /// path under the highest-numbered <c>tools/&lt;tfm&gt;/any/</c> directory
    /// (e.g. prefers net10.0 over net9.0). Returns null if not found.
    /// </summary>
    private static string? FindCliDll(string cacheDir)
    {
        var toolsDir = Path.Combine(cacheDir, "tools");
        if (!Directory.Exists(toolsDir)) return null;

        // Each tfm dir contains <rid>/Lz.Cli.dll (rid is usually "any" for
        // managed-only tools). Walk all tfm dirs, pick newest by name.
        string? best = null;
        string? bestTfm = null;
        foreach (var tfmDir in Directory.EnumerateDirectories(toolsDir))
        {
            var tfm = Path.GetFileName(tfmDir);
            var candidate = Path.Combine(tfmDir, "any", "Lz.Cli.dll");
            if (!File.Exists(candidate)) continue;

            if (bestTfm == null || string.Compare(tfm, bestTfm, StringComparison.OrdinalIgnoreCase) > 0)
            {
                best = candidate;
                bestTfm = tfm;
            }
        }
        return best;
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
    /// Sets <see cref="EnvRunnerVersion"/>, <see cref="EnvNupkgPath"/>, and
    /// <see cref="EnvFeed"/> on the child process so Lz.Cli's <c>--version</c>
    /// handler can render the 3-line output that names the runner + cli +
    /// plugin separately.
    /// </summary>
    private static int InvokeDll(string dllPath, string[] args, string nupkgPath, string feedDir)
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

        // Tell Lz.Cli who we are and where it came from. Lz.Cli's --version
        // handler reads these; everything else ignores them.
        psi.EnvironmentVariables[EnvRunnerVersion] = GetRunnerVersion();
        psi.EnvironmentVariables[EnvNupkgPath]     = nupkgPath;
        psi.EnvironmentVariables[EnvFeed]          = feedDir;

        using var proc = Process.Start(psi)
            ?? throw new LzRunnerException("failed to spawn dotnet — is it on PATH?");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    /// <summary>
    /// Read this assembly's <see cref="AssemblyInformationalVersionAttribute"/>
    /// (preferred — carries the +commit-hash MSBuild appends) or fall back to
    /// <see cref="AssemblyName.Version"/>.
    /// </summary>
    private static string GetRunnerVersion()
    {
        var asm = typeof(Program).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "(unknown)";
    }

    // -----------------------------------------------------------------------
    // Utilities
    // -----------------------------------------------------------------------

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
