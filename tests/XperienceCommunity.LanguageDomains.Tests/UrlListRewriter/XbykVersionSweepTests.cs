using System.IO.Compression;
using System.Runtime.Loader;
using System.Text.Json;

namespace XperienceCommunity.LanguageDomains.Tests.UrlListRewriter;

/// <summary>
/// Drift sweep across every published <c>Kentico.Xperience.Admin</c> version
/// at or above the supported floor.
///
/// <para>Asserts, per version, that the proxy's load-bearing contract still
/// holds: <c>IWebPageUrlListItemsRetriever.Retrieve</c> exists with the
/// leading <c>(int, int, ...)</c> shape and returns
/// <c>Task&lt;IEnumerable&lt;UrlListItem&gt;&gt;</c>. If a future XbyK release
/// breaks any part of that, the corresponding test case fails with a directive
/// message - so drift is caught within one CI run after the bad release goes
/// live, not when a customer notices the URLs tab regressed.</para>
///
/// <para>Marked <see cref="ExplicitAttribute"/> so the default
/// <c>dotnet test</c> doesn't pay the download cost. CI opts in via
/// <c>dotnet test --filter "Category=XbykVersionSweep"</c>. Recommended
/// schedule: weekly cron - that bounds drift detection latency to ~7 days.</para>
///
/// <para>Versions are discovered live from NuGet's flat-container API at test
/// discovery time, so newly-released XbyK versions get covered automatically.
/// Each <c>.nupkg</c> is downloaded once and cached in <c>%TEMP%</c>; reruns
/// after the first cache fill are offline-capable.</para>
/// </summary>
[TestFixture]
[Explicit("Heavy: downloads one nupkg per supported XbyK version. Run via: dotnet test --filter 'Category=XbykVersionSweep'.")]
[Category("XbykVersionSweep")]
public class XbykVersionSweepTests
{
    private const string PackageIdLower = "kentico.xperience.admin";
    private const string FlatContainerBase = "https://api.nuget.org/v3-flatcontainer/";
    private const string InterfaceFullName = "Kentico.Xperience.Admin.Websites.UIPages.IWebPageUrlListItemsRetriever";
    private const string AdminWebsitesDll = "Kentico.Xperience.Admin.Websites.dll";

    // Floor below which the proxy's interface didn't yet exist in a form we
    // recognise. 30.6.0 is the lowest version this library declares it
    // supports.
    private static readonly Version _versionFloor = new(30, 6, 0);

    // CI overrides via XCLD_XBYK_SWEEP_CACHE_DIR so the cache lands on a
    // path the actions/cache step persists; locally we fall back to %TEMP%.
    // Re-read each call - the env var is set by the workflow step and we
    // want it to take effect even if the test type was loaded by NUnit
    // before the per-test fixture environment was fully populated.
    private static string GetCacheDir()
    {
        string? envDir = Environment.GetEnvironmentVariable("XCLD_XBYK_SWEEP_CACHE_DIR");
        return string.IsNullOrEmpty(envDir)
            ? Path.Combine(Path.GetTempPath(), "xcld-xbyk-version-sweep")
            : envDir;
    }

    private const string NuGetUnreachableSentinel = "[NUGET-UNREACHABLE]";

    /// <summary>
    /// Test-discovery-time data source. Synchronously hits NuGet for the
    /// version index; if that fails we still produce one test case so the run
    /// reports the failure mode instead of silently producing zero tests.
    /// </summary>
    public static IEnumerable<TestCaseData> GetVersionsToProbe()
    {
        IReadOnlyList<string> versions;
        string? fetchError = null;
        try
        {
            versions = FetchPublishedVersions();
        }
        catch (Exception ex)
        {
            versions = Array.Empty<string>();
            fetchError = ex.Message;
        }

        if (fetchError != null)
        {
            yield return new TestCaseData(NuGetUnreachableSentinel)
                .SetName("Sweep skipped — NuGet version index unreachable")
                .SetDescription(fetchError);
            yield break;
        }

        foreach (string version in versions
            .Where(IsAtOrAboveFloor)
            .OrderBy(ParseSemverKey))
        {
            yield return new TestCaseData(version).SetName($"Retrieve shape — v{version}");
        }
    }

    [TestCaseSource(nameof(GetVersionsToProbe))]
    public void Retrieve_HasExpectedShape(string version)
    {
        if (version == NuGetUnreachableSentinel)
        {
            Assert.Inconclusive(
                "Could not fetch the NuGet version index. Re-run with internet access, "
                + "or fall back to the manual sweep documented in memory/project_xbyk_retrieve_drift.md.");
            return;
        }

        string dllPath = ResolveOrDownloadAdminWebsitesDll(version);

        var ctx = new AssemblyLoadContext($"sweep-{version}", isCollectible: true);
        try
        {
            var asm = ctx.LoadFromAssemblyPath(dllPath);

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException rtle)
            {
                // The admin assembly's full type graph references types from
                // sibling Kentico assemblies we deliberately don't load - so
                // GetTypes() raises and we use the partially-loaded set. The
                // types we care about (the iface + UrlListItem itself) live
                // entirely within the Admin.Websites assembly, so they're
                // always among the survivors.
                types = rtle.Types.Where(t => t != null).ToArray()!;
            }

            var iface = types.FirstOrDefault(t => t.FullName == InterfaceFullName);
            Assert.That(iface, Is.Not.Null,
                $"v{version}: '{InterfaceFullName}' is missing from {AdminWebsitesDll}. "
                + "If this is a release we explicitly support, the proxy needs updating.");

            var retrieve = iface!.GetMethod("Retrieve");
            Assert.That(retrieve, Is.Not.Null,
                $"v{version}: 'Retrieve' method is missing from {InterfaceFullName}. "
                + "The URLs tab no longer dispatches through this entry point.");

            var parameters = retrieve!.GetParameters();
            Assert.That(parameters.Length, Is.GreaterThanOrEqualTo(2),
                $"v{version}: Retrieve has {parameters.Length} parameter(s); proxy assumes "
                + "leading (int webPageItemId, int languageId, ...) - that contract is broken.");
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(int)),
                $"v{version}: Retrieve's first parameter is no longer 'int'.");
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(int)),
                $"v{version}: Retrieve's second parameter is no longer 'int'.");

            // The return-type comparison can't use Type identity: UrlListItem
            // resolves through OUR loaded admin assembly (referenced by the
            // test project), not the version we just loaded into the
            // temporary ALC - assembly-level Type equality would always fail.
            // We match the open generic + closed-arg full names instead.
            string actualReturn = SimplifyReturnTypeName(retrieve.ReturnType);
            Assert.That(
                IsExpectedReturnTypeName(retrieve.ReturnType),
                Is.True,
                $"v{version}: Retrieve's return type is '{actualReturn}', but the rewriter expects "
                + "Task<IEnumerable<UrlListItem>>. Adjust the proxy's match condition.");
        }
        finally
        {
            ctx.Unload();
        }
    }

    // --- NuGet fetch ---------------------------------------------------------

    private static IReadOnlyList<string> FetchPublishedVersions()
    {
        string url = $"{FlatContainerBase}{PackageIdLower}/index.json";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // .GetAwaiter().GetResult() because TestCaseSource methods can't be async.
        string json = http.GetStringAsync(url).GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        var versions = doc.RootElement.GetProperty("versions");
        var list = new List<string>(versions.GetArrayLength());
        foreach (var v in versions.EnumerateArray())
        {
            string? s = v.GetString();
            if (!string.IsNullOrEmpty(s))
            {
                list.Add(s);
            }
        }
        return list;
    }

    private static bool IsAtOrAboveFloor(string version)
    {
        if (!Version.TryParse(StripPrerelease(version), out var v))
        {
            return false;
        }
        return v >= _versionFloor;
    }

    private static (int, int, int, int) ParseSemverKey(string version)
    {
        // Sort key for OrderBy; trailing 0s if components are missing.
        if (!Version.TryParse(StripPrerelease(version), out var v))
        {
            return (0, 0, 0, 0);
        }
        return (v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    private static string StripPrerelease(string version)
    {
        int dash = version.IndexOf('-');
        return dash < 0 ? version : version[..dash];
    }

    // --- Download & extract --------------------------------------------------

    private static string ResolveOrDownloadAdminWebsitesDll(string version)
    {
        string versionDir = Path.Combine(GetCacheDir(), version);
        string dllPath = Path.Combine(versionDir, AdminWebsitesDll);
        if (File.Exists(dllPath))
        {
            return dllPath;
        }

        Directory.CreateDirectory(versionDir);
        string nupkgPath = Path.Combine(versionDir, $"{PackageIdLower}.{version}.nupkg");

        if (!File.Exists(nupkgPath))
        {
            string url = $"{FlatContainerBase}{PackageIdLower}/{version}/{PackageIdLower}.{version}.nupkg";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var stream = http.GetStreamAsync(url).GetAwaiter().GetResult();
            using var file = File.Create(nupkgPath);
            stream.CopyTo(file);
        }

        using (var zip = ZipFile.OpenRead(nupkgPath))
        {
            // Match any TFM-specific lib folder (e.g. lib/net8.0/, lib/net9.0/).
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.StartsWith("lib/net", StringComparison.OrdinalIgnoreCase)
                && e.FullName.EndsWith("/" + AdminWebsitesDll, StringComparison.OrdinalIgnoreCase));
            Assert.That(entry, Is.Not.Null,
                $"v{version}: nupkg has no lib/net*/{AdminWebsitesDll} entry. "
                + "The package layout may have changed - the sweep needs adjusting.");
            entry!.ExtractToFile(dllPath, overwrite: true);
        }

        // Drop the 25-MB-ish nupkg now that we have the one DLL we need.
        try
        { File.Delete(nupkgPath); }
        catch { /* best-effort */ }

        return dllPath;
    }

    // --- Return-type comparison helpers -------------------------------------

    private static bool IsExpectedReturnTypeName(Type returnType)
    {
        // Match the open-generic, then walk the closed args by full name
        // (avoiding cross-ALC type-identity mismatches).
        if (!returnType.IsGenericType)
        {
            return false;
        }

        if (returnType.GetGenericTypeDefinition().FullName != "System.Threading.Tasks.Task`1")
        {
            return false;
        }

        var taskArg = returnType.GetGenericArguments()[0];
        if (!taskArg.IsGenericType)
        {
            return false;
        }

        if (taskArg.GetGenericTypeDefinition().FullName != "System.Collections.Generic.IEnumerable`1")
        {
            return false;
        }

        var elem = taskArg.GetGenericArguments()[0];
        return elem.FullName == "Kentico.Xperience.Admin.Websites.UIPages.UrlListItem";
    }

    private static string SimplifyReturnTypeName(Type t)
    {
        if (!t.IsGenericType)
        {
            return t.FullName ?? t.Name;
        }

        string def = t.GetGenericTypeDefinition().Name;
        string args = string.Join(", ", t.GetGenericArguments().Select(a => a.FullName ?? a.Name));
        return $"{def}<{args}>";
    }
}
