using System.Reflection;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using DeepDungeon.Fsd.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using VNav = DeepDungeon.Fsd.Dalamud.moveHelper.VNav;

internal static class ReviewBoundaryTests
{
    internal static void Run()
    {
        using var services = new ServiceScope();
        DeleteReceipt();
        AttachmentPause();
        MpHandshake();
        MpFloorSwitch();
        VNavAvailability();
        PausedPermissions();
        Pt30Monitoring();
        DisposeAfterFailedStop();
        Console.WriteLine("PASS review boundaries: one-shot delete dialog and lifecycle invalidation, MP live/cache handshake and floor switch IPC, spin handoff denial, invalid state/IPC failure/reload, VNav absent/partial providers, paused combat permissions and PT30 health monitoring.");
    }

    private static void DeleteReceipt()
    {
        var original = new SaveSlotSnapshot(0, false, 21, "21", "run-a") { RowFingerprint = "row-a" };
        var receipt = new DeleteSaveConfirmationReceipt();
        receipt.Arm(original, 100, "delete");
        Check(receipt.TryConsume(original, 100, "delete") && !receipt.TryConsume(original, 100, "delete"),
            "A delete confirmation must authorize exactly one dispatch.");
        foreach (var changed in new[]
        {
            original with { Index = 1 }, original with { Identity = "replacement" },
            original with { Progress = "31" }, original with { RowFingerprint = "changed" }
        })
        {
            receipt.Arm(original, 100, "delete");
            Check(!receipt.TryConsume(changed, 100, "delete"), "Changed source was allowed to consume a delete receipt.");
        }
        receipt.Arm(original, 100, "delete");
        Check(!receipt.TryConsume(original, 101, "delete"), "A replacement dialog reused the old authorization.");
        receipt.Arm(original, 100, "delete");
        Check(!receipt.TryConsume(original, 100, "other"), "A changed prompt reused the old authorization.");

        var listeners = new Dictionary<AddonEvent, IAddonLifecycle.AddonEventDelegate>();
        SetService("AddonLifecycle", ReviewServiceProxy.Create<IAddonLifecycle>((method, args) =>
        {
            if (method.Name == "RegisterListener")
                listeners[(AddonEvent)args[0]!] = (IAddonLifecycle.AddonEventDelegate)args[2]!;
            if (method.Name == "UnregisterListener") listeners.Clear();
            return null;
        }));
        using (var flow = new GenericDeleteSaveFlow(DungeonCatalog.PilgrimsTraverse, 0))
        {
            Invoke(flow, "StartConfirmationObservation");
            var actual = (DeleteSaveConfirmationReceipt)GetField(flow, "_confirmation")!;
            foreach (var change in new[]
            {
                AddonEvent.PreSetup, AddonEvent.PreRefresh, AddonEvent.PreOpen, AddonEvent.PreShow,
                AddonEvent.PreClose, AddonEvent.PreHide, AddonEvent.PreFinalize
            })
            {
                actual.Arm(original, 100, "delete");
                listeners[change](change, null!);
                Check(!actual.TryConsume(original, 100, "delete"),
                    "Cancel/close/reopen between framework samples retained delete authorization.");
            }
        }
        Check(listeners.Count == 0, "Disposing a delete flow leaked addon lifecycle subscriptions.");
    }

    private static void AttachmentPause()
    {
        var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 21, 1,
            false, false, true, true, false) { AttachCurrentDuty = true };
        var session = new FarmingSession(plan);
        var context = Uninitialized<RunContext>();
        var duty = new DutyState();
        typeof(DutyState).GetProperty("IsInDuty")!.SetValue(duty, true);
        SetField(context, "Duty", duty);
        var scenario = new FarmingScenario(session);
        SetField(scenario, "_context", context);
        SetField(scenario, "_entered", true);
        Check(!scenario.CanRequestPause, "An unidentifiable attachment promised a persistent checkpoint.");
        var source = session.PrepareSource();
        Check(source.Observe([new(1, false, 21, "21", "borrowed")], out _, out _),
            "Test continuation failed to verify a borrowed source.");
        Check(scenario.CanRequestPause && session.ContinuationSlot == 1 &&
              session.OwnedSlot == -1 && !session.CreatedBySession,
            "Verified borrowed continuation either lost pause support or acquired deletion ownership.");
    }

    private static void MpHandshake()
    {
        var mp = new MpHarness();
        mp.Install();
        bool allowControl = true;
        using var ipc = new PromeRadarMovementIpc(mp.Interface, () =>
        {
            if (mp.FailSuspend) throw new InvalidOperationException("suspend failed");
            mp.Suspensions++;
        }, () => allowControl);
        Check(mp.SetControl!(true), "A fresh waiting MP session could not obtain synchronous acknowledgement.");
        ipc.Refresh();
        Check(PromeRadarMovementGate.IsBlocked && PromeRadarMovementGate.HandoffComplete,
            "Cached HasMovementControl=false revoked a live acknowledged handshake.");
        for (int i = 0; i < 10; i++) ipc.Refresh();
        Check(mp.Suspensions == 1, "A stationary MP danger wait repeatedly cancelled movement.");
        mp.Live = false;
        ipc.Refresh();
        Check(!PromeRadarMovementGate.IsBlocked, "Explicit live release did not restore FSD permission.");

        mp.Live = true;
        mp.FailSuspend = true;
        ExpectFailure(() => mp.SetControl!(true));
        Check(PromeRadarMovementGate.IsBlocked && !PromeRadarMovementGate.HandoffComplete,
            "Failed movement cancellation acknowledged MP or reopened movement.");
        mp.FailSuspend = false;
        ipc.Refresh();
        Check(PromeRadarMovementGate.HandoffComplete, "Failed handoff was not retried before acknowledgement.");

        var good = mp.State;
        foreach (var invalid in new[]
        {
            good with { TerritoryId = 1 }, good with { CapturedAtUtc = DateTime.UtcNow.AddSeconds(-4) },
            good with { CapturedAtUtc = DateTime.UtcNow.AddMinutes(1) }, good with { ReplayActive = true },
            good with { Ready = false }, good with { SmartMovementEnabled = false }, good with { ApiVersion = 2 }
        })
        {
            mp.State = invalid;
            ExpectFailure(ipc.Refresh);
            Check(PromeRadarMovementGate.IsBlocked, "Unverifiable live ownership was treated as MP release.");
            mp.Live = false;
            ipc.Refresh();
            Check(!PromeRadarMovementGate.IsBlocked, "A real release was held hostage by stale JSON.");
            mp.Live = true;
            mp.State = good;
            ipc.Refresh();
        }
        mp.FailJson = true;
        ExpectFailure(ipc.Refresh);
        Check(PromeRadarMovementGate.IsBlocked, "Unreadable cached state released the live owner.");
        mp.FailJson = false;
        mp.JsonOverride = "invalid-json";
        ExpectFailure(ipc.Refresh, typeof(JsonException));
        Check(PromeRadarMovementGate.IsBlocked, "Malformed JSON released the live owner.");
        mp.JsonOverride = "null";
        ExpectFailure(ipc.Refresh);
        mp.JsonOverride = null;
        mp.StateAvailable = false;
        ExpectFailure(ipc.Refresh);
        Check(PromeRadarMovementGate.IsBlocked, "A missing state endpoint released the live owner.");
        mp.StateAvailable = true;
        mp.FailLive = true;
        ExpectFailure(ipc.Refresh);
        Check(PromeRadarMovementGate.IsBlocked, "A scalar IPC exception was treated as release.");
        mp.FailLive = false;
        mp.LiveAvailable = false;
        ipc.Refresh();
        Check(!PromeRadarMovementGate.IsBlocked, "Unloaded MP retained a stale handshake.");
        mp.LiveAvailable = true;
        ipc.Refresh();
        Check(PromeRadarMovementGate.HandoffComplete, "Reloaded MP was not quiesced before reacquiring.");
        mp.Live = false;
        ipc.Refresh();
        allowControl = false;
        Check(!mp.SetControl!(true) && !PromeRadarMovementGate.IsBlocked,
            "A spin floor acknowledged a new MP movement request.");
        mp.Live = true;
        ipc.Refresh();
        Check(PromeRadarMovementGate.IsBlocked,
            "Spin denial treated an existing live MP owner as already released.");
        mp.Live = false;
        ipc.Refresh();
        Check(!PromeRadarMovementGate.IsBlocked && mp.SetControl!(false),
            "Spin exclusion rejected MP's explicit release.");
        allowControl = true;
        Check(mp.SetControl!(true), "Normal floor failed to restore MP handoff permission.");
    }

    private static void MpFloorSwitch()
    {
        var mp = new MpHarness { Live = false };
        mp.State = mp.State with { BuildMvid = "mp-floor-switch-test" };
        mp.Install();
        using var policy = new PromeRadarFloorMovement(mp.Interface, (_, _) => { });
        var boss = new DeepDungeonStateSnapshot(true, true, true, 1, 10,
            DeepDungeonFloorKind.Boss, false, 1);
        Check(!policy.Update(true, boss) && mp.SwitchRequests.SequenceEqual(new[] { false }),
            "Floor policy failed to request the real smart movement setter.");
        mp.Switch = false;
        Check(policy.Update(true, boss), "Floor policy ignored the scalar switch acknowledgement.");
        mp.Live = true;
        Check(!policy.Update(true, boss), "Floor policy trusted disabled cached state over a live movement owner.");
        mp.Live = false;
        mp.FailJson = true;
        Check(!policy.Update(true, boss), "Malformed MP state authorized spin through the IPC adapter.");
        mp.FailJson = false;
        mp.StateAvailable = false;
        Check(!policy.Update(true, boss), "Partially registered MP endpoints were mistaken for a fully absent provider.");
        mp.StateAvailable = true;
        Check(policy.Update(true, boss), "Healthy IPC did not recover after a temporary state failure.");
        policy.Update(false, boss);
        Check(mp.SwitchRequests.SequenceEqual(new[] { false, true }),
            "Real IPC cleanup did not restore the original enabled switch.");
        mp.Switch = true;
        Check(policy.Update(false, boss), "Restored IPC state was not acknowledged.");
    }

    private static void VNavAvailability()
    {
        var endpoints = new HashSet<(string Name, Type Signature, bool Action)>();
        var values = new Dictionary<string, bool>();
        bool readFails = false;
        SetService("PluginInterface", ReviewServiceProxy.Create<IDalamudPluginInterface>((method, args) =>
            ReviewServiceProxy.Create(method.ReturnType, (call, _) =>
            {
                string endpoint = (string)args[0]!;
                if (readFails && call.Name == "get_HasFunction") throw new InvalidOperationException("IPC unavailable");
                if (call.Name is "get_HasFunction" or "get_HasAction")
                    return endpoints.Contains((endpoint, method.ReturnType, call.Name == "get_HasAction"));
                if (call.Name == "InvokeFunc") return values[endpoint];
                return null;
            })));
        Check(!VNav.ReadMovementState().Available, "Completely absent VNav was not distinguished from online idle.");
        foreach (var prefix in new[] { "vnavmesh.", "vnav." })
        {
            foreach (var (name, signature, action) in new[]
            {
                ("Path.Stop", typeof(ICallGateSubscriber<object>), true),
                ("Nav.PathfindCancelAll", typeof(ICallGateSubscriber<object>), true),
                ("Nav.IsReady", typeof(ICallGateSubscriber<bool>), false),
                ("SimpleMove.PathfindAndMoveTo", typeof(ICallGateSubscriber<Vector3, bool, bool>), false),
                ("SimpleMove.PathfindAndMoveTo", typeof(ICallGateSubscriber<Vector3, bool, object>), true),
                ("Nav.PathfindAndMoveTo", typeof(ICallGateSubscriber<Vector3, bool, bool>), false),
                ("Nav.PathfindAndMoveTo", typeof(ICallGateSubscriber<Vector3, bool, object>), true)
            })
            {
                endpoints.Add((prefix + name, signature, action));
                ExpectFailure(() => VNav.ReadMovementState());
                endpoints.Clear();
            }
        }
        endpoints.Add(("vnavmesh.Path.IsRunning", typeof(ICallGateSubscriber<bool>), false));
        values["vnavmesh.Path.IsRunning"] = false;
        ExpectFailure(() => VNav.ReadMovementState());
        endpoints.Add(("vnavmesh.SimpleMove.PathfindInProgress", typeof(ICallGateSubscriber<bool>), false));
        values["vnavmesh.SimpleMove.PathfindInProgress"] = true;
        Check(VNav.ReadMovementState() is { Available: true, Running: false, Pending: true },
            "Registered pending work was reported absent.");
        readFails = true;
        ExpectFailure(() => VNav.ReadMovementState());
        endpoints.Clear();
        readFails = false;
        Check(!VNav.ReadMovementState().Available, "VNav unload did not clear verified provider presence.");
    }

    private static unsafe void PausedPermissions()
    {
        bool inCombat = false;
        SetService("Condition", ReviewServiceProxy.Create<ICondition>((_, _) => inCombat));
        var duty = new DutyState();
        var context = Uninitialized<RunContext>();
        SetField(context, "Duty", duty);
        var floor = Uninitialized<FloorExplorationController>();
        SetField(floor, "_ctx", context);
        SetField(floor, "<Kind>k__BackingField", FloorRuntimeKind.Normal);
        SetField(floor, "<Floor>k__BackingField", (byte)21);
        SetField(floor, "<DungeonId>k__BackingField", 4u);
        SetField(floor, "<Objectives>k__BackingField", new FloorObjectiveSession(1));
        InstanceContentDeepDungeon native = default;
        native.Floor = 21;
        native.DeepDungeonId = 4;
        floor.SetExternalMovement(true);
        floor.RefreshObjectiveDecision(&native);
        Check(floor.Objectives.HasObjectiveDecision &&
              floor.Objectives.ObjectiveDecision.Channels.Combat == CommandChannelPermission.CompatibleSidecar,
            "Initial movement pause suppressed automatic combat decision.");
        typeof(DutyState).GetProperty("PassageOpen")!.SetValue(duty, true);
        floor.RefreshObjectiveDecision(&native);
        Check(floor.Objectives.ObjectiveDecision.Channels.Combat == CommandChannelPermission.Blocked,
            "Paused permissions used stale passage activity from the scheduler.");
        inCombat = true;
        floor.RefreshObjectiveDecision(&native);
        Check(floor.Objectives.ObjectiveDecision.Channels.Combat != CommandChannelPermission.Blocked,
            "Movement pause lost the obligation to finish active combat.");
        var controller = Uninitialized<FloorPhaseController>();
        SetField(controller, "_ctx", context);
        SetField(controller, "_floorRuntime", floor);
        context.StatusIsError = true;
        Check(!controller.TickCombatChannel(out _),
            "A newly reported scenario error still entered the combat sidecar.");
    }

    private static void Pt30Monitoring()
    {
        var mp = new MpHarness();
        mp.Install();
        SetService("Condition", ReviewServiceProxy.Create<ICondition>((_, _) => false));
        var context = Uninitialized<RunContext>();
        var floor = Uninitialized<FloorExplorationController>();
        var boss = Uninitialized<FloorBossController>();
        var navigator = new FakeNavigator();
        SetField(boss, "_navHelper", new NavigationHelper(navigator, () => false, () => DateTime.UtcNow,
            _ => { }, _ => { }, () => false, () => false));
        SetField(floor, "<Boss>k__BackingField", boss);
        var events = new List<string>();
        var strategy = new PromeRadarBossMovementStrategy(floor, context, (name, _) => events.Add(name));
        strategy.Start();
        SetField(strategy, "startedAtUtc", DateTime.UtcNow.AddMinutes(-1));
        strategy.ObserveMovementPause();
        Check(!context.StatusIsError && strategy.Diagnostics?.Pt30ScriptReady == true && navigator.Starts == 0,
            "Protected casting skipped healthy PT30 observation or initiated approach movement.");
        mp.State = mp.State with { Pt30ScriptReady = false };
        strategy.ObserveMovementPause();
        Check(!context.StatusIsError, "Healthy observations during a long cast failed to renew the timeout.");
        SetField(strategy, "startedAtUtc", DateTime.UtcNow.AddSeconds(-11));
        strategy.ObserveMovementPause();
        Check(context.StatusIsError && events.Contains("mp-boss-movement-unavailable") && navigator.Starts == 0,
            "PT30 handshake loss was not detected while movement remained paused.");
    }

    private static void DisposeAfterFailedStop()
    {
        var mp = new MpHarness();
        mp.Install();
        SetService("Condition", ReviewServiceProxy.Create<ICondition>((_, _) => false));
        SetService("GameObjects", ReviewServiceProxy.Create<IObjectTable>((_, _) => null));
        SetService("Log", ReviewServiceProxy.Create<IPluginLog>((_, _) => null));
        var duty = new DutyState();
        typeof(DutyState).GetProperty("IsInDuty")!.SetValue(duty, true);
        var context = Uninitialized<RunContext>();
        SetField(context, "<Knowledge>k__BackingField", new RunKnowledge());
        SetField(context, "CancellationSource", new CancellationTokenSource());
        SetField(context, "Navigator", new FakeNavigator());
        SetField(context, "Duty", duty);
        context.PromeRadarMovement = new PromeRadarFarmingMovement(context);
        var host = Uninitialized<RunHost>();
        SetField(host, "_context", context);
        SetField(host, "_isFsdMode", true);
        SetField(host, "_dutyAttemptEventLock", new object());
        SetField(host, "_dutyState", duty);
        SetField(host, "_floorController", Uninitialized<FloorPhaseController>());
        using var lease = new FsdExecutionLease("review-boundary-tests");
        lease.Acquire();
        var engine = Uninitialized<FsdEngine>();
        var diagnostics = new FsdDiagnosticJournal();
        SetField(engine, "diagnostics", diagnostics);
        SetField(engine, "_ddHost", host);
        SetField(engine, "_dutyState", duty);
        SetField(engine, "_executionLease", lease);
        SetField(engine, "manualNavigator", new NavigatorVNavAdapter());
        MovementCoordinator.Shared.SetAutomatic(true, false);
        try
        {
            // The harness exposes running state, but no cancellation actions.
            engine.Dispose();
            Check(mp.Sessions.SequenceEqual(new[] { true, false }) && host.Context == null && !lease.IsHeld,
                "Failed VNav stop skipped MP release, context teardown or execution lease release.");
            Check(GetField(engine, "_dutyState") == null &&
                  diagnostics.GetReport().Contains("fsd-dispose-movement", StringComparison.Ordinal) &&
                  diagnostics.GetReport().Contains("VNav pathfinding cancellation IPC is unavailable.", StringComparison.Ordinal),
                "Failed movement cleanup was neither recorded nor isolated from later cleanup.");
        }
        finally
        {
            SetService("PluginInterface", ReviewServiceProxy.Create<IDalamudPluginInterface>((method, _) =>
                ReviewServiceProxy.Create(method.ReturnType, (call, _) =>
                    call.Name is "get_HasFunction" or "get_HasAction" ? false : null)));
            MovementCoordinator.Shared.SetAutomatic(false, false);
            MovementCoordinator.Shared.QuiesceVNav();
        }
    }

    private static T Uninitialized<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);
    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static void SetService(string name, object? value) =>
        typeof(Service).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static void ExpectFailure(Action action, Type? expectedType = null)
    {
        bool failed = false;
        try { action(); }
        catch (Exception error) when ((expectedType ?? typeof(InvalidOperationException)).IsInstanceOfType(error)) { failed = true; }
        Check(failed, "Expected an unsafe or incomplete operation to be rejected.");
    }

    private sealed class ServiceScope : IDisposable
    {
        private readonly Dictionary<string, object?> saved = new[] { "PluginInterface", "Framework", "ClientState", "Condition", "AddonLifecycle", "GameObjects", "Log" }
            .ToDictionary(name => name, name => typeof(Service).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        public void Dispose()
        {
            PromeRadarMovementGate.SetControl(false, () => { });
            foreach (var (name, value) in saved) SetService(name, value);
        }
    }

    private sealed class MpHarness
    {
        internal bool Live = true, LiveAvailable = true, StateAvailable = true, FailLive, FailJson, FailSuspend;
        internal string? JsonOverride;
        internal int Suspensions;
        internal bool Switch = true;
        internal readonly List<bool> SwitchRequests = [];
        internal readonly List<bool> Sessions = [];
        internal Func<bool, bool>? SetControl;
        internal PromeRadarMovementState State = new()
        {
            ApiVersion = 1, Ready = true, CapturedAtUtc = DateTime.UtcNow, TerritoryId = 1283,
            SmartMovementEnabled = true, FsdSessionActive = true, CanMove = true,
            NavigationActive = true, Pt30ScriptAvailable = true, Pt30ScriptReady = true,
            Pt30ScriptVersion = "1.0.0", HasMovementControl = false
        };
        internal IDalamudPluginInterface Interface => ReviewServiceProxy.Create<IDalamudPluginInterface>((method, args) =>
            ReviewServiceProxy.Create(method.ReturnType, (call, values) =>
            {
                string endpoint = (string)args[0]!;
                if (call.Name == "RegisterFunc") { SetControl = (Func<bool, bool>)values[0]!; return null; }
                if (call.Name == "UnregisterFunc") { SetControl = null; return null; }
                if (call.Name == "InvokeAction")
                {
                    if (endpoint == "PromeRadar.SetSmartMovementEnabled") SwitchRequests.Add((bool)values[0]!);
                    else Sessions.Add((bool)values[0]!);
                    return null;
                }
                if (call.Name == "get_HasAction") return endpoint == "PromeRadar.SetSmartMovementEnabled";
                if (call.Name == "get_HasFunction") return endpoint == "PromeRadar.GetState" ? StateAvailable : LiveAvailable;
                if (call.Name == "InvokeFunc")
                {
                    if (endpoint == "PromeRadar.GetState")
                        return FailJson ? throw new InvalidOperationException("JSON read failed") : JsonOverride ?? JsonSerializer.Serialize(State);
                    if (endpoint == "PromeRadar.GetSmartMovementEnabled") return Switch;
                    return FailLive ? throw new InvalidOperationException("live read failed") : Live;
                }
                return null;
            }));
        internal void Install()
        {
            SetService("Framework", ReviewServiceProxy.Create<IFramework>((_, _) => true));
            SetService("ClientState", ReviewServiceProxy.Create<IClientState>((_, _) => 1283u));
            SetService("PluginInterface", Interface);
        }
    }
}
