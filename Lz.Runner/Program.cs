using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Lz.Runner;

/// <summary>
/// Thin dispatcher that finds the "correct" <c>Lz.Cli</c> package for the user's
/// current directory and invokes it via <c>dotnet &lt;dll&gt;</c>. The rule is
/// identical to what NuGet itself does at restore time: walk up for
/// <c>NuGet.Config</c>, read its <c>&lt;packageSources&gt;</c>, scan every local
/// source for the newest <c>Lz.Cli.*.nupkg</c>. Extract into a cache entry
/// keyed by that nupkg's identity — version plus length plus mtime, so two
/// working copies that both pack "0.11.1" never share an entry (see
/// <see cref="EnsureExtracted"/>) — then forward the process.
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
    /// Ensure the RESOLVED nupkg is extracted into the cache and return the
    /// path to its Lz.Cli.dll.
    ///
    /// Cache entries are keyed by nupkg IDENTITY, not by version alone:
    /// <c>&lt;cacheRoot&gt;/&lt;version&gt;+&lt;length&gt;-&lt;mtimeUtcTicks&gt;/</c>.
    /// A version string does not identify content in this ecosystem —
    /// multiple working copies embed their own Lz repo and pack under
    /// whatever LzVersion.props says, so two systems (or one system
    /// re-packing) emit DIFFERENT bits under the SAME version. Giving every
    /// distinct nupkg its own entry means two working copies never share a
    /// folder, and a published entry is never deleted or rewritten. (Runner
    /// 1.2.0 kept one folder per version, <c>&lt;cacheRoot&gt;/&lt;version&gt;/</c>,
    /// and rebuilt it in place whenever the marker mismatched — which, when
    /// the mismatch came from another working copy, gutted the folder
    /// underneath the lz process still running from it there. The new
    /// entries sit BESIDE that folder, never inside it, so even a
    /// rolled-back 1.2.0 runner cannot reach them. The only delete that
    /// remains is in <see cref="RetireIncompleteEntry"/>, guarded so it
    /// cannot do that.)
    ///
    /// Each entry carries a <c>.source.json</c> marker recording the nupkg
    /// it was extracted from; it doubles as the "extraction completed"
    /// sentinel, and a hit requires it to match the resolved nupkg.
    /// Steady-state cost of the check is two stats.
    ///
    /// Extraction is atomic as far as other runners can see: the nupkg is
    /// unpacked into a private sibling staging directory
    /// (<c>&lt;identity&gt;.tmp-&lt;pid&gt;</c>), the marker is written
    /// there, and the finished directory is renamed into place in one step,
    /// so the final path is only ever absent or complete. Publishers of one
    /// entry are serialised by a short-lived lock file
    /// (<c>&lt;identity&gt;.lock</c>), so two runners racing to extract the
    /// same nupkg both succeed — the second adopts the first one's entry
    /// and discards its own staging copy.
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
        var source = SourceMarker.Capture(nupkgPath);
        var cacheRoot = GetCacheRoot();
        var cacheDir = Path.Combine(cacheRoot, $"{version}+{source.Identity}");

        // Fast path: this exact nupkg is already extracted and complete.
        if (TryUseEntry(cacheDir, source, out var cached))
        {
            Verbose($"cache hit: {cacheDir}");
            return cached;
        }
        Verbose(Directory.Exists(cacheDir)
            ? $"cache entry {cacheDir} is incomplete (marker missing/damaged or no Lz.Cli.dll); rebuilding from {nupkgPath}"
            : $"no cache entry for {nupkgPath}; extracting into {cacheDir}");

        Directory.CreateDirectory(cacheRoot);
        SweepAbandonedEntries(cacheRoot);

        var staging = ScratchName(cacheDir, StagingSuffix);
        try
        {
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(nupkgPath, staging, overwriteFiles: true);

            // Validate BEFORE publishing: a package with no Lz.Cli.dll must
            // never become an entry, or every later run would re-extract,
            // retire and re-publish it.
            if (FindCliDll(staging) == null)
                throw new LzRunnerException(
                    $"{nupkgPath} does not contain a Lz.Cli.dll under tools/<tfm>/any/; not caching it.");

            // Marker last: it doubles as the "extraction completed" sentinel.
            // A crash before this line leaves a marker-less staging dir that
            // SweepAbandonedEntries removes on a later run.
            WriteMarker(staging, source);
        }
        catch (LzRunnerException)
        {
            DeleteQuietly(staging);
            throw;
        }
        catch (Exception ex)
        {
            DeleteQuietly(staging);
            throw new LzRunnerException(
                $"failed to extract {nupkgPath} into {staging}: {ex.Message}");
        }

        return PublishEntry(staging, cacheDir, source);
    }

    /// <summary>
    /// Move a fully extracted staging directory into its final place and
    /// return the Lz.Cli.dll path. Runs under the entry's publish lock, so
    /// "is a complete entry already here?" and "rename mine into place" are
    /// one atomic step with respect to every other publisher of the same
    /// entry. Handles the two ways the final path can already be occupied:
    /// another runner finished the same entry first (adopt it, discard
    /// ours), or a damaged entry is sitting there (retire it first — see
    /// <see cref="RetireIncompleteEntry"/>).
    /// </summary>
    private static string PublishEntry(string staging, string cacheDir, SourceMarker source)
    {
        using var publishLock = AcquirePublishLock(cacheDir + LockSuffix, staging);

        if (TryUseEntry(cacheDir, source, out var dll))
        {
            Verbose($"another lz process published {cacheDir} first; using it");
            DeleteQuietly(staging);
            return dll;
        }

        if (Directory.Exists(cacheDir))
        {
            try
            {
                RetireIncompleteEntry(cacheDir);
            }
            catch
            {
                DeleteQuietly(staging);
                throw;
            }
        }

        try
        {
            MoveDirectoryWithRetry(staging, cacheDir);
        }
        catch (Exception ex)
        {
            DeleteQuietly(staging);
            throw new LzRunnerException(
                $"failed to move extracted package {staging} into place at {cacheDir}: {ex.Message}");
        }

        return FindCliDll(cacheDir)
            ?? throw new LzRunnerException(
                $"extracted package at {cacheDir} did not contain a Lz.Cli.dll under tools/<tfm>/any/.");
    }

    /// <summary>True when <paramref name="cacheDir"/> exists, its marker
    /// matches the nupkg identity this run resolved, and it contains an
    /// Lz.Cli.dll (whose path is returned).</summary>
    private static bool TryUseEntry(string cacheDir, SourceMarker source, out string dllPath)
    {
        dllPath = "";
        if (!Directory.Exists(cacheDir) || !MarkerMatches(cacheDir, source)) return false;
        var found = FindCliDll(cacheDir);
        if (found == null) return false;
        dllPath = found;
        return true;
    }

    /// <summary>
    /// This runner never leaves an incomplete entry at the final path: entries
    /// are published only by renaming a finished staging directory into
    /// place, under the publish lock. So an entry with no valid marker means
    /// something outside the runner damaged it — a hand-deleted marker, a
    /// disk problem, a foreign tool. Nothing should be running from it, but
    /// that is not certain, and a recursive delete on Windows removes every
    /// file it CAN before throwing on the first one it can't — which is
    /// exactly how a live process gets gutted. So the entry is renamed aside
    /// first: on Windows a rename is all-or-nothing and fails outright if any
    /// file inside is open, so a live user of the entry turns this into a
    /// clean error instead. (On Unix rename(2) succeeds regardless; a process
    /// already running from the entry keeps its open files, and the path is
    /// re-populated with identical bytes moments later.) Only after the
    /// rename succeeds is the retired copy deleted.
    /// </summary>
    private static void RetireIncompleteEntry(string cacheDir)
    {
        var retired = ScratchName(cacheDir, RetiredSuffix);
        try
        {
            MoveDirectoryWithRetry(cacheDir, retired);
        }
        catch (Exception ex)
        {
            throw new LzRunnerException(
                $"cache entry {cacheDir} is incomplete but could not be moved aside: {ex.Message}" + Environment.NewLine +
                $"  If an lz process is still running from it, wait for it to finish; otherwise delete {cacheDir} and retry.");
        }
        Verbose($"retired incomplete cache entry {cacheDir}");
        DeleteQuietly(retired);
    }

    private const string StagingSuffix = ".tmp-";
    private const string RetiredSuffix = ".stale-";
    private const string LockSuffix    = ".lock";

    /// <summary>
    /// Name for a private scratch directory beside an entry:
    /// <c>&lt;entry&gt;&lt;suffix&gt;&lt;pid&gt;-&lt;random&gt;</c>. The pid lets
    /// <see cref="SweepAbandonedEntries"/> tell abandoned from live; the random
    /// token means a reused pid can never collide with a dead process's
    /// leftovers, and no two live runners can ever be writing the same name —
    /// so nothing this runner creates is ever shared with, or swept from
    /// under, another runner.
    /// </summary>
    private static string ScratchName(string cacheDir, string suffix) =>
        $"{cacheDir}{suffix}{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// Take the publish lock for one cache entry: an exclusively opened,
    /// delete-on-close file beside the entry. Publishing is a single rename,
    /// so the lock is held for milliseconds; waiting is bounded so a wedged
    /// holder cannot hang every later run. The OS releases the lock if the
    /// holder dies. Extraction itself happens BEFORE the lock, in a private
    /// staging directory, so runners never serialise on the slow part.
    /// </summary>
    private static FileStream AcquirePublishLock(string lockPath, string staging)
    {
        var deadline = Environment.TickCount64 + 30_000;
        var reported = false;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    DeleteQuietly(staging);
                    throw new LzRunnerException(
                        $"timed out waiting for another lz process to finish publishing {Path.GetDirectoryName(lockPath)}: {ex.Message}" + Environment.NewLine +
                        $"  If no lz process is running, delete {lockPath} and retry.");
                }
                if (!reported) { Verbose($"waiting for another lz process to finish publishing ({lockPath})"); reported = true; }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Rename a directory, retrying for a couple of seconds. Windows refuses
    /// to rename a directory while any file beneath it is open, and real-time
    /// antivirus and the search indexer briefly open freshly written files —
    /// so the very first rename after an extraction can fail for no lasting
    /// reason. (The .NET SDK's own tool installer retries its staging → final
    /// rename for the same reason.) A rename whose target already exists is
    /// not transient and is reported at once.
    /// </summary>
    private static void MoveDirectoryWithRetry(string from, string to)
    {
        const int maxAttempts = 25;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                                       && attempt < maxAttempts && !Directory.Exists(to))
            {
                if (attempt == 1) Verbose($"rename of {Path.GetFileName(from)} failed ({ex.Message.TrimEnd('.')}); retrying");
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Best-effort removal of staging / retired directories left behind by
    /// runners that died part-way (their pid is in the directory name and
    /// is no longer running). Never touches a directory whose owner is
    /// alive or whose liveness can't be determined, and — because every
    /// scratch name also carries a random token — never one that a live
    /// runner could be about to create. Runs only on the slow path, so the
    /// cache-hit cost stays at two stats.
    /// </summary>
    private static void SweepAbandonedEntries(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot)) return;
        foreach (var suffix in new[] { StagingSuffix, RetiredSuffix })
        {
            foreach (var dir in Directory.EnumerateDirectories(cacheRoot, $"*{suffix}*"))
            {
                var name = Path.GetFileName(dir);
                var at = name.LastIndexOf(suffix, StringComparison.Ordinal);
                if (at < 0) continue;
                var rest = name.AsSpan(at + suffix.Length);          // "<pid>-<random>"
                var dash = rest.IndexOf('-');
                if (dash < 0 || !int.TryParse(rest[..dash], out var pid)) continue;
                if (pid == Environment.ProcessId || ProcessAlive(pid)) continue;
                Verbose($"removing abandoned cache directory {dir} (pid {pid} is gone)");
                DeleteQuietly(dir);
            }
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; } // no such process
        catch { return true; }                      // can't tell — leave it alone
    }

    private static void DeleteQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Verbose($"could not remove {dir}: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Cache-entry source marker
    // -----------------------------------------------------------------------

    /// <summary>Marker file written into each cache entry after a successful
    /// extraction, recording which nupkg the entry came from.</summary>
    private const string MarkerFileName = ".source.json";

    /// <summary>
    /// Identity of the nupkg a cache entry was extracted from. Length +
    /// last-write time (UTC ticks) identify the bytes for all practical
    /// purposes here: two different packs of the same version virtually never
    /// collide on both, while a byte-identical copy of the same nupkg (which
    /// legitimately may hit the cache) usually preserves them. The same pair
    /// names the entry's directory (<see cref="Identity"/>), so a foreign
    /// working copy's build or a same-version repack lands in its own
    /// directory instead of displacing this one. NupkgPath is recorded for
    /// diagnostics only and deliberately NOT compared — the same feed
    /// reached via a different path should still count as a match.
    /// </summary>
    private sealed record SourceMarker(string NupkgPath, long Length, long LastWriteTimeUtcTicks)
    {
        /// <summary>Snapshot the resolved nupkg's identity once, so the
        /// directory name and the marker written into it always agree even
        /// if the nupkg is repacked while this run is in flight.</summary>
        public static SourceMarker Capture(string nupkgPath)
        {
            var nupkg = new FileInfo(nupkgPath);
            if (!nupkg.Exists)
                throw new LzRunnerException($"resolved nupkg no longer exists: {nupkgPath}");
            return new SourceMarker(Path.GetFullPath(nupkgPath), nupkg.Length, nupkg.LastWriteTimeUtc.Ticks);
        }

        /// <summary>Directory name of this nupkg's cache entry under the
        /// version directory.</summary>
        [JsonIgnore]
        public string Identity => $"{Length}-{LastWriteTimeUtcTicks}";
    }

    /// <summary>
    /// Does the entry's marker record the identity this run resolved? The
    /// comparison is against the identity captured once at the start of the
    /// run — NOT a fresh stat of the nupkg — so an entry is judged purely on
    /// being complete and self-consistent. A nupkg repacked while this run is
    /// in flight can therefore neither invalidate the entry it is using nor
    /// send a complete entry down the retire path; the next run simply
    /// resolves to a new identity and a new entry.
    /// </summary>
    private static bool MarkerMatches(string cacheDir, SourceMarker source)
    {
        try
        {
            var markerPath = Path.Combine(cacheDir, MarkerFileName);
            if (!File.Exists(markerPath)) return false;

            var marker = JsonSerializer.Deserialize<SourceMarker>(File.ReadAllText(markerPath));
            return marker != null
                && marker.Length == source.Length
                && marker.LastWriteTimeUtcTicks == source.LastWriteTimeUtcTicks;
        }
        catch
        {
            // Unreadable/corrupt marker — treat as damaged; the caller rebuilds.
            return false;
        }
    }

    private static void WriteMarker(string cacheDir, SourceMarker marker)
    {
        File.WriteAllText(
            Path.Combine(cacheDir, MarkerFileName),
            JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
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
    /// Cache location: <c>LocalApplicationData</c> + <c>lz-runner/cache</c> —
    /// <c>%LOCALAPPDATA%\lz-runner\cache</c> on Windows,
    /// <c>$XDG_DATA_HOME/lz-runner/cache</c> (default
    /// <c>~/.local/share/lz-runner/cache</c>) on Linux,
    /// <c>~/Library/Application Support/lz-runner/cache</c> on macOS — falling
    /// back to <c>~/.lz-runner/cache</c> if that folder cannot be resolved.
    /// Layout beneath it is <c>&lt;version&gt;+&lt;length&gt;-&lt;mtimeUtcTicks&gt;/</c>
    /// per extracted nupkg (see <see cref="EnsureExtracted"/>). Runner 1.2.0
    /// and earlier extracted into <c>&lt;version&gt;/</c>; those directories
    /// are left untouched and are inert.
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
