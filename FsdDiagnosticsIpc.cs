using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;

namespace NewFolder3;

/// <summary>Versioned, read-only JSON IPC for AI and local support tooling.</summary>
internal sealed class FsdDiagnosticsIpc : IDisposable
{
    private readonly List<Action> unregister = [];
    private readonly IPluginLog log;

    public FsdDiagnosticsIpc(IDalamudPluginInterface plugin, FsdApplication application, IPluginLog log)
    {
        this.log = log;
        try
        {
            string contract = JsonSerializer.Serialize(new
            {
                ok = true, schemaVersion = FsdDiagnosticJournal.SCHEMA_VERSION,
                plugin = "NewFolder3", readOnly = true,
                endpoints = new[]
                {
                    new { name = "NewFolder3.Diagnostics.GetContract", signature = "Func<string>", purpose = "IPC schema and limits" },
                    new { name = "NewFolder3.Diagnostics.GetSnapshot", signature = "Func<string>", purpose = "Cached runtime state, blocker, progress timers, heartbeat and last exception" },
                    new { name = "NewFolder3.Diagnostics.GetEvents", signature = "Func<string, string>", purpose = "Cursor event page; JSON argument: instanceId?, afterSequence?, limit?, eventType?" },
                    new { name = "NewFolder3.Diagnostics.GetReport", signature = "Func<string>", purpose = "Snapshot and a bounded recent event page; follow recent.nextAfterSequence if hasMore" }
                },
                eventCapacity = FsdDiagnosticJournal.CAPACITY, eventLimit = 100,
                eventMaxCharacters = FsdDiagnosticJournal.MAX_EVENT_CHARACTERS,
                sampleIntervalSeconds = 1, heartbeatIntervalSeconds = 10,
                assessmentIsHypothesis = true,
                cursor = "After a reload, reset when instanceId changes. Inspect gap, hasMore, nextAfterSequence; do not use lastSequence as the page cursor.",
                privacy = "No character names, account IDs, credentials or chat content are collected. Positions, target IDs and runtime evidence remain local.",
                recording = "Detailed JSONL files: plugin configuration directory/DeepDungeonRuns; snapshot.lastRecorderPath retains the latest file."
            });
            var contractGate = plugin.GetIpcProvider<string>("NewFolder3.Diagnostics.GetContract");
            contractGate.RegisterFunc(() => contract);
            unregister.Add(contractGate.UnregisterFunc);
            var snapshotGate = plugin.GetIpcProvider<string>("NewFolder3.Diagnostics.GetSnapshot");
            snapshotGate.RegisterFunc(application.GetDiagnosticSnapshot);
            unregister.Add(snapshotGate.UnregisterFunc);
            var eventsGate = plugin.GetIpcProvider<string, string>("NewFolder3.Diagnostics.GetEvents");
            eventsGate.RegisterFunc(application.GetDiagnosticEvents);
            unregister.Add(eventsGate.UnregisterFunc);
            var reportGate = plugin.GetIpcProvider<string>("NewFolder3.Diagnostics.GetReport");
            reportGate.RegisterFunc(application.GetDiagnosticReport);
            unregister.Add(reportGate.UnregisterFunc);
            log.Information("[FSD.Diagnostics] Registered four read-only IPC endpoints: NewFolder3.Diagnostics.GetContract/GetSnapshot/GetEvents/GetReport (schema 1).");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        for (int i = unregister.Count - 1; i >= 0; --i)
        {
            try { unregister[i](); }
            catch (Exception error) { log.Error(error, "[FSD.Diagnostics] Failed to unregister diagnostic IPC."); }
        }
        unregister.Clear();
    }
}
