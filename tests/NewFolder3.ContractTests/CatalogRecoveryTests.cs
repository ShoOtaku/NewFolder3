using System.Net;
using System.Reflection;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;

namespace NewFolder3;

internal static class CatalogRecoveryTests
{
    internal static void Run()
    {
        var log = typeof(DetailedMapHostOptions).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Service")!
            .GetProperty("Log", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousLog = log.GetValue(null);
        log.SetValue(null, DispatchProxy.Create<IPluginLog, SilentLog>());
        string root = Path.Combine(Path.GetFullPath("artifacts/upstream-integration-20261123/test-inputs"),
            $"catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            MissingYieldUsesCompleteEmbeddedRelease(root);
            OldScenarioFailureDoesNotBlockNewScenario(Path.Combine(root, "switch"));
            SuccessfulInstallRetainsItsEtag(Path.Combine(root, "install"));
            var settings = new DeepDungeon.Fsd.Runtime.FsdSettings();
            string fingerprint = FsdEngine.ComputeSettingsFingerprint(settings);
            settings.LifetimeHoardPickups = 120;
            Check(fingerprint == FsdEngine.ComputeSettingsFingerprint(settings),
                "An automatic hoard counter update must not invalidate a progression checkpoint.");
            settings.AggressiveChestInteraction = true;
            Check(fingerprint != FsdEngine.ComputeSettingsFingerprint(settings),
                "A gameplay configuration change must still invalidate the checkpoint.");
        }
        finally
        {
            log.SetValue(null, previousLog);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Scenario => DetailedMapScenarioCatalog.PilgrimsTraverse21To30.Key;
    private static DetailedMapHostOptions Options => new(new Uri("https://catalog.invalid/"), false, false, false);
    private static byte[] Read(string name)
    {
        using Stream stream = typeof(DetailedMapHostOptions).Assembly
            .GetManifestResourceStream($"Fsd.DetailedMaps.{Scenario}.{name}")!;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static void MissingYieldUsesCompleteEmbeddedRelease(string root)
    {
        var latest = DetailedMapCatalogContract.ParseLatest(Read(DetailedMapCatalogContract.LatestFileName));
        string scenarioRoot = Path.Combine(root, "DetailedMapCatalogs", Scenario);
        string releaseRoot = Path.Combine(scenarioRoot, "releases",
            $"{latest.ReleaseId}-{latest.CatalogSha256[..12]}");
        Directory.CreateDirectory(releaseRoot);
        File.WriteAllBytes(Path.Combine(scenarioRoot, "installed.json"), DetailedMapCatalogContract.SerializeLatest(latest));
        File.WriteAllBytes(Path.Combine(releaseRoot, DetailedMapCatalogContract.CatalogFileName),
            Read(DetailedMapCatalogContract.CatalogFileName));
        File.WriteAllBytes(Path.Combine(releaseRoot, DetailedMapCatalogContract.SignatureFileName),
            Read(DetailedMapCatalogContract.SignatureFileName));
        string yieldPath = Path.Combine(releaseRoot, HoardYieldCatalogContract.FileName);
        File.WriteAllBytes(yieldPath, Read(HoardYieldCatalogContract.FileName));
        using (var valid = new DetailedMapCatalogManager(root, Options, new FailingHandler()))
        {
            Check(!valid.IsUsingEmbeddedCatalog && valid.CurrentCatalog != null,
                "The fixture must first load as a valid signed disk release.");
        }
        File.Delete(yieldPath);
        using var manager = new DetailedMapCatalogManager(root, Options, new FailingHandler());
        Check(manager.IsUsingEmbeddedCatalog &&
              manager.TryAcquireRunSnapshot(true, Scenario, out var snapshot, out _) &&
              snapshot.HoardYield != null && snapshot.Catalog != null,
            "A valid signed disk map with missing yield must fall back to the complete embedded release.");
        Check(Field(manager, "_latestEtag") == null,
            "An embedded fallback must discard the disk cache ETag.");
    }

    private static void OldScenarioFailureDoesNotBlockNewScenario(string root)
    {
        var handler = new FailingHandler();
        using var manager = new DetailedMapCatalogManager(root, Options, handler);
        manager.Update(true, Scenario, false);
        manager.Update(true, DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key, false);
        Check(handler.Requests == 2 && (string)Field(manager, "_lastCheckError")! == "",
            "An old scenario failure must be observed without contaminating or blocking the new scenario.");
    }

    private static void SuccessfulInstallRetainsItsEtag(string root)
    {
        using var manager = new DetailedMapCatalogManager(root, Options, new FixtureHandler());
        // Force the download branch instead of accepting the identical bundled pointer.
        typeof(DetailedMapCatalogManager).GetField("_installedPointer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, null);
        manager.Update(true, Scenario, false);
        Check(((Task)Field(manager, "_checkTask")!).IsCompleted,
            "The in-memory HTTP fixture must complete without network or timing dependence.");
        manager.Update(true, Scenario, false);
        Check(!manager.IsUsingEmbeddedCatalog && (string?)Field(manager, "_latestEtag") == "\"fixture\"" &&
              Field(manager, "_lastSuccessfulCheckUtc") != null,
            "A fully reloaded installed release must retain the matching response ETag.");
    }

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new HttpRequestException("offline-fixture");
        }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Read(name)) };
            response.Headers.ETag = new("\"fixture\"");
            return Task.FromResult(response);
        }
    }

    public class SilentLog : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.ReturnType == typeof(void) ? null :
                targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
