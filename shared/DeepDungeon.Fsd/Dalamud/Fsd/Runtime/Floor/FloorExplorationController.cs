using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using MasterPlayer.MechanicRuntime;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.Coord;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using DeepDungeon.Fsd.Dalamud.Map;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal enum FloorRuntimeKind { Normal, Boss }

/// <summary>Owns the search, chest, patrol and passage execution of one stable floor.</summary>
internal sealed partial class FloorExplorationController : IDisposable
{
    internal delegate void NativeStateRecorder(string checkpoint, bool force);
    internal delegate void NativeStateSnapshotRecorder(string checkpoint, bool force, bool nativeStateAvailable, bool nativeIntuitionActive);
    internal delegate void HoardWaitRecorder(string eventType, int? remainingWaitMilliseconds = null);
    internal delegate void HoardWaitEnder(string outcome, string? expectedEventType = null);
    internal unsafe delegate void PassageExitRecorder(InstanceContentDeepDungeon* dd);
    internal unsafe delegate void ChaseFailureRecorder(InstanceContentDeepDungeon* dd, EnemyChaseAcquisitionFailure failure);
    internal delegate void PassageNavigationRecorder(string eventType, string result, bool usedActor, int passageRoomIndex, int playerRoom);
    private readonly RunContext _ctx;
    private readonly NavigationHelper _navHelper;
    private readonly NavigationDriver _navDriver;
    private readonly EnemyChaseHelper _chaseHelper;
    private readonly PomanderManager _pomanderManager;
    private readonly ChatWatchers? _chatWatchers;
    private readonly IRunTelemetryObserver? _runTelemetryObserver;
    private readonly PassageDestinationResolver _passageDestination;
    private CombatRecoveryBudget RecoveryBudget { get; } = new();
    private readonly Func<FloorPhase> _getPhase;
    private readonly Action<FloorPhase> _setPhase;
    private readonly Func<string> _getStatus;
    private readonly Action<string> _setStatus;
    private readonly Func<bool> _getNativeIntuition;
    private readonly Action CancelActiveMovement;
    private readonly Action<string, object> RecordReplayEvent;
    private readonly NativeStateRecorder RecordNativeIntuitionState;
    private readonly NativeStateSnapshotRecorder RecordNativeIntuitionSnapshot;
    private readonly HoardWaitRecorder RecordHoardEvidenceWait;
    private readonly HoardWaitEnder EndHoardEvidenceWait;
    private readonly Func<RunOptions> SnapshotRunOptions;
    private readonly Func<long> _nextRoomSearchRequestId;
    private readonly PassageExitRecorder RecordPassageExitDelayedByCombat;
    private readonly Action<string, EnemyChaseTarget> RecordChaseTargetEvent;
    private readonly ChaseFailureRecorder RecordChaseAcquisitionFailure;
    private readonly PassageNavigationRecorder RecordPassageNavigationEvent;
    private readonly Action ClearChaseAcquisitionFailure;
    private FloorPhase _phase { get => _getPhase(); set => _setPhase(value); }
    private string _status { get => _getStatus(); set => _setStatus(value); }
    private bool _nativeIntuitionActive => _getNativeIntuition();
    internal Vector3? CameraChestPosition => _coordPipeline?.ActiveChestAttempt?.Waypoint.Position;
    internal TaskPhase CurrentTaskPhase => _taskRunner?.Phase ?? TaskPhase.Idle;
    internal void ResetTaskRunner(bool cancelNavigation = true) => _taskRunner?.Reset(cancelNavigation);
    internal void ClearActiveWaypoint() => _activeWaypoint = null;
    internal void SuspendTask(bool suspended) => _taskRunner?.SetSuspended(suspended);
    private AutoPilotExecutor? _executor => Executor;
    private WaypointTaskRunner? _taskRunner => ActiveExecution?.TaskRunner;
    private string _blockedMovementOperation = string.Empty;
    private string _blockedTransitionOperation = string.Empty;
    private static readonly TimeSpan GeneralTickInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RoomMismatchReenterDelay = TimeSpan.FromSeconds(3);
    private const int RoomReentryAbortLimit = 3;
    private RoomSearchContext? _roomPresenceContext;
    private DateTime _roomMismatchSinceUtc = DateTime.MinValue;
    private int _roomReentryAbortCount;
    private ObjectiveArbiterDecision CurrentObjectiveDecision => Objectives.ObjectiveDecision;
    private bool AllowsChestSidecarInteraction => Objectives.HasObjectiveDecision &&
        Objectives.ObjectiveDecision.Channels.Interaction != CommandChannelPermission.Blocked;
    private Vector3 ResolvePassageWalkingPosition(Vector3 actor) => _passageDestination.Resolve(actor, Generation);
    private FarmingItemPolicy ItemPolicy => FarmingItemPolicy.For(_ctx.FarmingPlan?.Mode);
    private bool EntryIncenseWindowOpen() => this.Survey.EntryIncenseWindowOpen();
    private bool CombatBuffUsable(uint slot, bool overcapRelief = false) =>
        IsPomanderAvailableForFloorUse(slot) && ItemPolicy.AllowsCombatBuff(
            _pomanderManager.GetCount(slot), _ctx?.Duty.IsBossFloor == true, overcapRelief);



    public FloorExplorationController(
        long generation,
        uint dungeonId,
        byte floor,
        FloorRuntimeKind kind,
        DateTime readyAtUtc,
        NormalFloorGraphSnapshot? normalGraph,
        bool? nativeIntuitionActive,
        DetailedMapRunSnapshot detailedMap,
        RunFloorTelemetryTrace? runTelemetry, FloorItemExecutor items,
        RunContext context, NavigationHelper navigation, NavigationDriver navigationDriver,
        EnemyChaseHelper chase, PomanderManager pomanders, ChatWatchers? watchers,
        IRunTelemetryObserver? telemetryObserver, PassageDestinationResolver passageDestination,
        Func<FloorPhase> getPhase, Action<FloorPhase> setPhase,
        Func<string> getStatus, Action<string> setStatus, Func<bool> getNativeIntuition,
        Action cancelMovement, Action<string, object> record,
        NativeStateRecorder recordNative, HoardWaitRecorder recordHoardWait,
        HoardWaitEnder endHoardWait, Func<RunOptions> snapshotRunOptions,
        Func<long> nextRoomRequest,
        PassageExitRecorder recordPassageExit, Action<string, EnemyChaseTarget> recordChaseTarget,
        ChaseFailureRecorder recordChaseFailure, PassageNavigationRecorder recordPassageNavigation, Action clearChaseAcquisitionFailure, NativeStateSnapshotRecorder recordNativeSnapshot)
    {
        Generation = generation;
        DungeonId = dungeonId;
        Floor = floor;
        Kind = kind;
        ReadyAtUtc = readyAtUtc;
        NormalGraph = normalGraph;
        Objectives = new FloorObjectiveSession(generation);
        Executor = kind == FloorRuntimeKind.Normal
            ? new AutoPilotExecutor(Objectives.GetRoomProgress, detailedMap)
            : null;
        // Floor-scoped knowledge binds before any normal decision; the
        // spawn room and hoard baseline seal on the first native ingest.
        context.Knowledge.BeginFloor(generation, dungeonId, floor, -1, -1);
        NativeIntuitionActive = nativeIntuitionActive;
        ObjectEvidence = new FloorObjectEvidenceTracker();
        RunTelemetry = runTelemetry;
        Items = items;
        _ctx = context; _navHelper = navigation; _navDriver = navigationDriver;
        // Knowledge projections bind only where coordination actually
        // drives the floor — controlled survey and boss paths keep their
        // own executor behavior untouched.
        if (UsesSiteCoordination)
            Executor?.AttachKnowledge(context.Knowledge);
        _chaseHelper = chase; _pomanderManager = pomanders; _chatWatchers = watchers;
        context.CombatRecoveryBudget = RecoveryBudget;
        chase.IsTargetBlocked = context.IsCombatTargetSuppressed;
        _runTelemetryObserver = telemetryObserver; _passageDestination = passageDestination;
        _getPhase = getPhase; _setPhase = setPhase; _getStatus = getStatus; _setStatus = setStatus;
        _getNativeIntuition = getNativeIntuition; CancelActiveMovement = cancelMovement;
        RecordReplayEvent = record; RecordNativeIntuitionState = recordNative;
        RecordHoardEvidenceWait = recordHoardWait; EndHoardEvidenceWait = endHoardWait;
        SnapshotRunOptions = snapshotRunOptions; _nextRoomSearchRequestId = nextRoomRequest;
        RecordPassageExitDelayedByCombat = recordPassageExit;
        RecordChaseTargetEvent = recordChaseTarget; RecordChaseAcquisitionFailure = recordChaseFailure;
        RecordPassageNavigationEvent = recordPassageNavigation; ClearChaseAcquisitionFailure = clearChaseAcquisitionFailure; RecordNativeIntuitionSnapshot = recordNativeSnapshot;
    }

    public long Generation { get; }
    public uint DungeonId { get; }
    public byte Floor { get; }
    public FloorRuntimeKind Kind { get; }
    public DateTime ReadyAtUtc { get; }
    public NormalFloorGraphSnapshot? NormalGraph { get; private set; }
    public AutoPilotExecutor? Executor { get; }
    private bool? NativeIntuitionActive { get; set; }
    public FloorObjectiveSession Objectives { get; }
    private ObjectiveExecution? ActiveExecution { get; set; }
    public FloorPlanningSession PlanningState { get; } = new();
    public PendingIntuitionState PendingIntuition => Items.PendingIntuition;
    public FloorItemExecutor Items { get; }
    public FloorItemUseSession ItemUse => Items.Session;
    private FloorSearchState SearchState { get; } = new();
    public FloorObjectEvidenceTracker ObjectEvidence { get; }
    public RunFloorTelemetryTrace? RunTelemetry { get; }
    private RunFloorStateCumulativePublisher? RunFloorStatePublisher { get; set; }
    private int LastObservedRoomIndex { get; set; } = -1;
    private int? ActiveRoomNavigationTarget { get; set; }
    private BandedRevealPending? BandedRevealExpectation { get; set; }
    private bool FarmingPassageItemConfirmed { get; set; }
    public FloorBossController Boss { get; private set; } = null!;
    public void AttachBoss(FloorBossController boss) => Boss = boss;
    public FloorSurveyController Survey { get; private set; } = null!;
    public void AttachSurvey(FloorSurveyController survey) => Survey = survey;
    public bool IsDisposed { get; private set; }

    private void ReplaceObjectiveExecution(FloorObjectiveKind objective, NavigationHelper navigation)
    {
        ActiveExecution?.Dispose();
        ActiveExecution = objective == FloorObjectiveKind.None ? null : new ObjectiveExecution(objective, navigation);
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        IsDisposed = true;
        NormalGraph = null;
        NativeIntuitionActive = null;
        LastObservedRoomIndex = -1;
        ActiveRoomNavigationTarget = null;
        BandedRevealExpectation = null;
        Survey.Dispose();
        Boss?.Dispose();
        Items.Dispose();
        ActiveExecution?.Dispose();
        ActiveExecution = null;
        ObjectEvidence.Dispose();
        // Retire work, release the shared navigator, then drop floor-local data.
        _coordPipeline?.Reset();
        _navigation?.Cancel();
        _navigation?.Dispose();
        _siteDriver?.Dispose();
        _coordRecoveryController = null;
        // Clear the diagnostics publication only while THIS owner still
        // owns it — never erase a newer publication.
        if (Coord.NavDiag.Navigation == _navigation)
            Coord.NavDiag.Navigation = null;
        if (Coord.NavDiag.Map == _navBitmap)
            Coord.NavDiag.Map = null;
        _navFailureReported.Clear();
        _coordOvercapRequest = null;
        _coordOvercapAttempt = null;
        _siteSearch = null;
        _coordPipeline = null;
        _siteDriver = null;
        _navigation = null;
        _ctx?.Knowledge.EndFloor(Generation);
        RunFloorStatePublisher = null;
        Objectives.Dispose();
    }
    internal bool RequireMovementPermission(
string operation,
FloorObjectiveKind? requiredObjective = null,
bool primaryOwnsOperation = true)
    {
        var decision = this.Objectives.ObjectiveDecision;
        bool allowed = this.Objectives.HasObjectiveDecision == true &&
                       decision.Channels.Movement != CommandChannelPermission.Blocked &&
                       primaryOwnsOperation &&
                       (!requiredObjective.HasValue || decision.PrimaryObjective == requiredObjective.Value);
        if (allowed)
        {
            ClearPermissionBlock(ref _blockedMovementOperation, "Movement");
            return true;
        }

        RecordPermissionBlock(ref _blockedMovementOperation, "Movement", operation, requiredObjective);
        _status = $"Waiting: movement permission blocked for {operation} ({decision.PrimaryObjective})";
        return false;
    }

    private static bool IsSearchObjective(FloorObjectiveKind objective) =>
        objective is FloorObjectiveKind.OpenVisibleBandedChest or
            FloorObjectiveKind.CompleteKnownHoard or
            FloorObjectiveKind.DiscoverHoard or FloorObjectiveKind.OpenPlannedChest;

    private bool ShouldContinuePlannedRouteForPassageActivation(FloorObjectiveKind objective) =>
        _ctx?.ControlledPtSurvey == null &&
        objective == FloorObjectiveKind.ActivatePassage &&
        _ctx?.Duty.PassageOpen != true &&
        _executor?.PlannedRouteCount > 0;

    private bool ClearingMovementOwnedByCurrentObjective()
    {
        return CurrentObjectiveDecision.PrimaryObjective is
            FloorObjectiveKind.ActivatePassage or
            FloorObjectiveKind.FinishCombatBeforePassage;
    }

    private bool HasActiveSearchExecution()
    {
        return _activeWaypoint.HasValue ||
               (_taskRunner != null && _taskRunner.Phase != TaskPhase.Idle) ||
               _executor?.RoomContext != null ||
               (this.ActiveExecution?.ObjectiveRecords.Count ?? 0) > 0;
    }

    /// <summary>Coord-facing room finish: runs the real FinalizeRoomSearch
    /// domain path (outcome → progress marks → plan regen → context clear).
    /// Rejected when there's no active objective execution to apply outcomes
    /// to (terminal for the room — caller force-advances, not a wait);
    /// Deferred while evidence is pending; Done when the room finalized.</summary>
    private DateTime _coordFinishRejectedLogAt = DateTime.MinValue;

    /// <summary>
    /// The floor-scope profile: nav impls, danger source, bitmap/coverage and
    /// the boundary are ensured here for ANY floor kind — boss floors get the
    /// same scope so the escape claim path is live everywhere. Backend
    /// selection happens once per floor; switching cancels the old backend.
    /// </summary>
    private EncounterPlanningSnapshot? ReadNavigationSnapshot() =>
        Kind == FloorRuntimeKind.Normal
            ? EncounterPlanningSnapshotRegistry.ReadLocalHazards()
            : EncounterPlanningSnapshotRegistry.Read();

    private unsafe void EnsureCoordScope(InstanceContentDeepDungeon* dd)
    {
        _scopeDd = dd;
        _coordPipeline ??= new CoordPipeline(RecordReplayEvent);
        _navigation ??= new Coord.NavigationSurface(() => _navBitmap, RecordReplayEvent, ReadNavigationSnapshot)
        { AttackSuspended = () => _coordRecoveryController?.Suspend() };
        // Rebuild floor terrain and coverage once per floor. All movement
        // uses the same navigator, irrespective of the selected task.
        var mapKey = dd == null ? null : FloorMapKey.For(dd);
        if (dd != null && (Floor != _navBitmapFloor || (mapKey.HasValue ? _navBitmap?.Matches(mapKey, dd) != true : _navBitmap != null)))
        {
            if (mapKey is { } key)
            {
                var map = FloorMap.Get(key) ?? throw new InvalidDataException(FloorMap.Error);
                _navBitmap = Coord.NavBitmap.Create(map, dd);
                _coverage = new Coord.CoverageSet(
                    _navBitmap.SampleCoverageAnchors(_navBitmap.RoomAnchors),
                    _navBitmap.RoomFlags,
                    RoomGraph.GetLocalPlayerRoomIndex(dd));
                RecordReplayEvent("coord-floor-map", new
                {
                    floor = dd->Floor, territory = key.Territory, layout = key.Layout,
                    anchors = _navBitmap.RoomAnchors.Count, closedBoxes = _navBitmap.ClosedBoxes
                });
            }
            else
            {
                _navBitmap = null;
                _coverage = null;
            }
            _navBitmapFloor = Floor;
            Coord.NavDiag.Map = _navBitmap;
            _navFailureReported.Clear();   // dedup is per floor+target
            _siteDriver ??= new Coord.ObjectiveSiteDriver();
            // Quota-position preview is a read-only last-target
            // observation — the driver must never ACQUIRE a target just
            // to describe the work.
            _siteDriver.ChasePositionProvider =
                () => _chaseHelper?.LastTarget is { } lt ? lt.LivePosition : null;
            var siteOpts = SnapshotRunOptions();
            _siteDriver.Configure(new Coord.ObjectiveSiteSource.ObjectiveSiteFilter(
                OpenBronze: siteOpts.OpenBronze,
                OpenSilver: siteOpts.OpenSilver,
                OpenGold: siteOpts.OpenGold,
                Banded: siteOpts.BandedEnabled,
                HuntMobs: true, ProbeRooms: true, Exit: true),
                new Coord.ObjectiveTunables());
            _siteDriver.SetNormalGraph(NormalGraph);
            if (UsesSiteCoordination && _ctx != null)
            {
                // Knowledge-driven path: run-pinned map policy/catalog,
                // shared run knowledge, mode policy — never the live
                // catalog manager or a second evidence scan.
                _siteDriver.ConfigureWork(
                    _ctx.DetailedMap, _ctx.Knowledge,
                    () => FloorWorkPolicyBuilder.Build(
                        _ctx.FarmingPlan, SnapshotRunOptions()),
                    () => _ctx.Duty.PassageOpen == true,
                    RecordReplayEvent);
            }
            _siteSearch = new Coord.SiteSearchBoundary(
                _siteDriver, () => _scopeDd,
                () => Service.LocalPlayer?.Position ?? Vector3.Zero,
                () => ObjectEvidence.Current);
            _navigation?.Cancel();
            UpdateEncounterTask();
            Coord.NavDiag.Navigation = _navigation;
        }
        _coordReal ??= new RealNativeBoundary(
            () => _navigation!,
            () => Service.LocalPlayer?.Position ?? Vector3.Zero,
            () => _ctx?.ChestInteraction,
            () => _siteSearch,
            // The full chase target — identity, leg endpoint and live
            // position travel together; the boundary never flattens them.
            (pos, allowNew) => _chaseHelper?.GetClearingTarget(
                _scopeDd, this.NormalGraph, this.LastObservedRoomIndex, pos,
                out _, allowNew),
            NavigateToAttack,
            id => _chaseHelper?.CompleteChaseLeg(id),
            ResolveCoordChestOvercap,
            snapshot => _ctx?.CombatAssist.SelectForNavigation(_ctx, snapshot));
    }

    private static object V(Vector3 p) => new { p.X, p.Y, p.Z };

    /// <summary>Bounded local bitmap window for a failure replay event —
    /// ±half meters of cell states around the player, one char per cell
    /// ('#' blocked, '.' free), capped to keep the payload sane.</summary>
    private static object? BitmapWindow(
        Coord.NavBitmap? map, Vector3 center, float half)
    {
        if (map == null)
            return null;
        var (xa, ya) = map.WorldToGrid(center + new Vector3(-half, 0, -half));
        var (xb, yb) = map.WorldToGrid(center + new Vector3(half, 0, half));
        // min/max come from the ORIGINAL pair, before either bound is
        // assigned — the previous clamped-x0 comparison was wrong.
        var x0 = Math.Clamp(Math.Min(xa, xb), 0, map.Width - 1);
        var x1 = Math.Clamp(Math.Max(xa, xb), 0, map.Width - 1);
        var yA = Math.Clamp(Math.Min(ya, yb), 0, map.Height - 1);
        var yB = Math.Clamp(Math.Max(ya, yb), 0, map.Height - 1);
        var truncated = false;
        if (x1 - x0 > 256) { x1 = x0 + 256; truncated = true; }
        if (yB - yA > 256) { yB = yA + 256; truncated = true; }
        var rows = new List<string>(yB - yA + 1);
        for (var y = yA; y <= yB; y++)
        {
            var sb = new System.Text.StringBuilder(x1 - x0 + 1);
            for (var x = x0; x <= x1; x++)
                sb.Append(map.IsFree(x, y) ? '.' : '#');
            rows.Add(sb.ToString());
        }
        // World origin + dimensions + truncation keep the window
        // spatially reconstructible.
        var origin = map.GridToWorld(x0, yA);
        return new
        {
            x0, y0 = yA, x1, y1 = yB, pixel = map.PixelSize,
            origin = new { origin.X, origin.Y, origin.Z },
            width = x1 - x0 + 1, height = yB - yA + 1, truncated, rows,
        };
    }

    private NavOutcome NavigateToAttack(AttackTargetVerb goal)
    {
        if (_ctx == null || Service.LocalPlayer is not { IsDead: false } player ||
            CombatTargetingHelpers.GetBattleCharaByGameObjectId(goal.TargetId) is not { IsDead: false, CurrentHp: > 0 } target)
        {
            _navigation!.Cancel();
            return NavOutcome.Pending;
        }
        _coordRecoveryController ??= new(_ctx, _chaseHelper, RecoveryBudget, RunTelemetry,
            status => _status = status, RecordReplayEvent);
        var range = _ctx.CombatAssist.GetEngageRange();
        if (!float.IsFinite(range) || range <= 0) range = CombatPositionPolicy.DefaultAttackRange(player.ClassJob.RowId);
        return _coordRecoveryController.Navigate(_navigation!, target, player.Position, goal.Position, range, DateTime.UtcNow);
    }

    /// <summary>True while the coord escape claim owns movement — boss
    /// controllers suppress their own movement on this flag.</summary>
    internal bool CoordEscapeActive { get; private set; }

    /// <summary>Normal-floor site coordination: the committed site-work
    /// pipeline owns decisions and movement. Controlled PT capture keeps
    /// its separate specialized workflow.</summary>
    internal bool UsesSiteCoordination =>
        Kind == FloorRuntimeKind.Normal && _ctx?.ControlledPtSurvey == null;

    /// <summary>Internal boss navigation becomes available when the shared
    /// mechanic host recognizes the encounter. A bound encounter retains
    /// ownership during missing frames; unsupported bosses keep their flow.</summary>
    internal bool UsesEncounterCoordination =>
        Kind == FloorRuntimeKind.Boss
        && _ctx?.ControlledPtSurvey == null
        && (_coordPipeline?.CurrentEncounterOp != null
            || MasterPlayer.MechanicRuntime.EncounterPlanningSnapshotRegistry.Read() is { Hints: not null } snapshot
            && snapshot.Territory == (ushort)Service.ClientState.TerritoryType);

    // Task selection does not select a different navigator.
    private void UpdateEncounterTask()
    {
        if (!UsesEncounterCoordination) _coordPipeline?.RetireEncounter();
        _navBackend = _navigation?.Solver ?? "navigation";
        Coord.NavDiag.Navigation = _navigation;
    }

    /// <summary>Encounter-driven tick for the supported profile. Returns
    /// false only for another profile — this profile owns the flow even
    /// while waiting for data or a blocked channel. A settings-mode
    /// switch mid-floor still unwinds the wrapper before returning.</summary>
    internal unsafe bool UpdateBossEncounter(InstanceContentDeepDungeon* dd)
    {
        if (!UsesEncounterCoordination)
        {
            if (_coordPipeline?.CurrentEncounterOp != null)
                UpdateEncounterTask();
            return false;
        }
        if (Service.Condition[ConditionFlag.BetweenAreas]
            || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            _navigation?.Cancel();
            _coordPipeline?.SuspendEncounter();
            _status = "boss encounter - waiting for floor load";
            return true;
        }
        if (Service.LocalPlayer is not { } p || p.IsDead)
        {
            _navigation?.Cancel();
            _coordPipeline?.SuspendEncounter();
            _status = "boss encounter - waiting for player";
            return true;
        }
        if (!RequireMovementPermission("boss encounter", FloorObjectiveKind.DefeatBoss))
        {
            _navigation?.Cancel();
            _coordPipeline?.SuspendEncounter();
            return true;
        }
        EnsureCoordScope(dd);
        UpdateEncounterTask();
        var snap = MasterPlayer.MechanicRuntime.EncounterPlanningSnapshotRegistry.Read();
        if (snap != null && snap.Territory != (ushort)Service.ClientState.TerritoryType)
            snap = null;
        _coordPipeline?.TickEncounter(this.Floor, RoomGraph.GetLocalPlayerRoomIndex(dd),
            DateTime.UtcNow, p.Position,
            Service.Condition[ConditionFlag.InCombat], p.IsDead,
            snap, _coordSnapshot, ObjectEvidence.Current, _coordReal!);
        // Buffs only while a FRESH live boss remains and the op runs —
        // a stale-but-alive snapshot never consumes them.
        var live = snap is { BossDead: false }
            && DateTime.UtcNow - snap.CapturedAt <= TimeSpan.FromMilliseconds(500)
            && _coordPipeline?.EncounterStatus == Coord.OpStatus.Running;
        Boss.UpdateManagedEncounter(dd,
            _coordPipeline?.CurrentEncounterOp is { } encOp
                ? $"{encOp.Describe()}|nav:{_navigation?.Status ?? _navBackend}"
                : _status, live);
        return true;
    }

    /// <summary>
    /// Boss-floor escape path: same claim arbitration as normal floors,
    /// escape slice only — the single movement owner on every floor kind.
    /// </summary>
    internal unsafe void UpdateBossEscape(InstanceContentDeepDungeon* dd)
    {
        CoordEscapeActive = false;
        if (Service.LocalPlayer is not { } p)
            return;
        EnsureCoordScope(dd);
        _coverage?.Update(p.Position);
        CoordEscapeActive = !p.IsDead && _navigation!.NeedsSafetyPlan;
        _coordPipeline!.ExecuteFloorMovement(_coordReal!, null, CoordEscapeActive, p.Position);
    }

    /// <summary>Normal coord decision prep — invoked by the outer update
    /// once per frame AFTER ingest and BEFORE the completion check and the
    /// pipeline tick: coord scope, options, intuition timeout, general
    /// pomander cadence, then the site scheduler's decision tick. The
    /// driver is not ticked a second time inside UpdateFloorActive.</summary>
    internal unsafe void PrepareNormalDecision(InstanceContentDeepDungeon* dd)
    {
        if (Service.LocalPlayer is { } coordPlayer)
        {
            EnsureCoordScope(dd);
            ResolveCurrentFloorIntuitionTimeoutIfNeeded(dd);
            SyncLiveRunOptions(dd, coordPlayer);
            if (ShouldRunGeneralTick())
                TryUseGeneralAutoPomander(dd);
            // One activity sample per decision tick — the same booleans
            // feed the driver rebuild, the pipeline tick and channel
            // permissions; a transition dirties the route immediately.
            _normalActivity = new Coord.FloorActivity(
                _ctx?.Duty.PassageOpen == true,
                Service.Condition[ConditionFlag.InCombat]);
            _siteSearch?.SetActivity(_normalActivity);
            _siteSearch?.Tick();
            // Diagnostic snapshot built from current knowledge + the
            // captured activity — never fabricated from legacy executor
            // defaults (the coord path skips that branch entirely).
            var fk = _ctx?.Knowledge?.Floor;
            if (fk != null)
            {
                _coordSnapshot = new ObjectiveArbiterSnapshot(
                    BossObjective: false,
                    VisibleBandedChest: fk.PendingBandedWork ||
                        fk.ObservedBandedPositions.Count > 0,
                    KnownOrConfirmedHoard: fk.EvidenceState is
                        HoardEvidenceState.IntuitionDirect or
                        HoardEvidenceState.IntuitionWaitingForIndicator,
                    RequiredHoardDiscovery: fk.EvidenceState is
                        HoardEvidenceState.BlindSearch or
                        HoardEvidenceState.IntuitionActiveUnconfirmed &&
                        !fk.SearchObligationsResolved,
                    MandatoryHoardTerminal: fk.SearchObligationsResolved,
                    PassageOpen: _normalActivity.PassageOpen,
                    PassageActivationRequired: !_normalActivity.PassageOpen &&
                        fk.SearchObligationsResolved,
                    CombatInProgress: _normalActivity.InCombat,
                    RoutineCombatAllowed: _normalActivity.InCombat ||
                        !_normalActivity.PassageOpen,
                    ActiveChestInteraction: CurrentInteractionAttempt != null,
                    SuppressCombat: _ctx?.FarmingPlan?.ReusesSave == true,
                    RequiredChestWork: fk.RequiredWorkRemaining > 0);
            }
        }
    }

    internal unsafe void UpdateFloorActive(InstanceContentDeepDungeon* dd)
    {
        // Coord floors (UsesSiteCoordination): the committed work pipeline
        // owns movement + interaction; the non-coord path retains its
        // compatibility behavior and the pipeline runs as comparison only.
        if (Service.LocalPlayer is { } coordPlayer)
        {
            if (!UsesSiteCoordination)
            {
                EnsureCoordScope(dd);
                _siteSearch?.Tick();
            }
            _coverage?.Update(coordPlayer.Position);
            Vector3? passageDest = null, passageCenter = null, passageActorPos = null;
            int passageRoomIdx = -1;
            var evidence = this.ObjectEvidence.Current;
            if (evidence?.Available == true &&
                _siteSearch is { KnowledgeDriven: true, Head: { } passageHead } &&
                passageHead.Kind == Coord.ObjectiveSiteKind.Passage)
            {
                // Execute the committed entity. A fresh nearest-actor lookup can
                // silently navigate to another beacon on HoH's large floors.
                var position = passageHead.Position;
                foreach (var actor in evidence.PassageActors)
                    if (actor.GameObjectId == passageHead.ActorId)
                    {
                        position = actor.Position;
                        passageActorPos = position;
                        break;
                    }
                passageDest = passageHead.ActorId != 0
                    ? ResolvePassageWalkingPosition(position) : position;
                passageRoomIdx = passageHead.RoomIndex;
                if (MapPos.TryGetRoomCenter(dd, passageRoomIdx, out var center))
                    passageCenter = center;
            }
            else if (evidence?.Available == true &&
                PassageLocator.TryResolvePassageDestination(dd, evidence, out var pd, out bool usedActor, out var passageRoom))
            {
                // Raw actor position stays a distinct fact from the
                // projected walking point — ExitPassageOp needs both.
                passageDest = usedActor ? ResolvePassageWalkingPosition(pd) : pd;
                if (usedActor)
                    passageActorPos = pd;
                passageRoomIdx = passageRoom;
                if (MapPos.TryGetRoomCenter(dd, passageRoom, out var center))
                    passageCenter = center;
            }
            RoomWaypoint? chestWaypoint = null;
            // Fact-driven: a visible banded chest is an opportunistic entity,
            // not something the domain decision must schedule.
            if (evidence != null &&
                BandedChestLocator.TryFindNearestToPlayer(evidence, out var bandedPos) &&
                bandedPos.HasValue)
            {
                chestWaypoint = new RoomWaypoint(bandedPos.Value, RoomObjectiveType.ChestBanded);
            }
            // Coord is the floor driver on every normal floor with a coverage
            // set — ops are selected from facts inside the pipeline.
            bool takeover = this.Kind == FloorRuntimeKind.Normal && _coverage != null;
            // Ownership is published BEFORE the pipeline tick — a chest
            // interaction dispatched inside it can synchronously raise an
            // overcap event whose handlers read CurrentInteractionAttempt.
            var takeoverChanged = takeover != _coordTookOver;
            _coordTookOver = takeover;
            var boundary = (INativeBoundary)_coordReal!;
            var pipeline = _coordPipeline!;
            pipeline.Tick(
                this.Floor,
                RoomGraph.GetLocalPlayerRoomIndex(dd),
                DateTime.UtcNow,
                coordPlayer.Position,
                _normalActivity.InCombat,
                coordPlayer.IsDead,
                _normalActivity.PassageOpen,
                _navigation!.NeedsSafetyPlan,
                _coordSnapshot,
                passageDest,
                passageCenter,
                passageRoomIdx,
                evidence,
                chestWaypoint,
                _coverage,
                boundary,
                passageActorPos);
            if (_coordRecoveryController?.FailureReason is { } combatFailure)
            {
                RestartAfterCombatFailure(combatFailure);
                return;
            }
            if (UsesSiteCoordination && !coordPlayer.IsDead && _normalActivity.InCombat && _normalActivity.PassageOpen &&
                pipeline.CurrentObjective == FloorObjectiveKind.FinishCombatBeforePassage &&
                _chaseHelper.LastTarget == null && HasOnlyExhaustedAggroTargets())
            {
                RestartAfterCombatFailure("All remaining aggro targets exhausted combat position recovery.",
                    onlyExhaustedAggroRemains: true);
                return;
            }
            if (takeoverChanged)
            {
                RecordReplayEvent("coord-takeover", new
                {
                    floor = this.Floor,
                    active = takeover,
                    op = pipeline.CurrentObjective.ToString(),
                    nav = _navBackend
                });
            }
            // First no-progress failure for a work target/floor: capture
            // the local bitmap window + BOTH paths + the follow state
            // while they still exist (before any cancel clears them).
            if (_navigation?.Failure is { Length: > 0 } navFailure &&
                navFailure.StartsWith("no-progress", StringComparison.Ordinal) &&
                _navFailureReported.Add(
                    $"{this.Floor}|{_siteDriver?.HeadToken.FloorGeneration}|" +
                    $"{_siteDriver?.HeadToken.Key}|" +
                    $"{_siteDriver?.HeadToken.Attempt}"))
            {
                var raw = _navigation.RawPath;
                var smoothed = _navigation.SmoothedPath;
                Utilities.DirectMovementController.TryReadNativeMotion(out var mot);
                RecordReplayEvent("coord-nav-no-progress", new
                {
                    floor = this.Floor,
                    reason = navFailure,
                    headToken = _siteDriver?.HeadToken,
                    movementOwner = pipeline.MovementOwner,
                    leg = _navigation.LegIndex,
                    legs = smoothed.Count,
                    player = V(coordPlayer.Position),
                    desired = V(_navigation.CommandedTarget),
                    final = V(_navigation.FinalDestination),
                    radius = _navigation.ArrivalRadius,
                    inputEnabled = _navigation.InputEnabled,
                    nativeInput = new { mot.HasInput, mot.Speed },
                    rawCount = raw.Count,
                    rawTruncated = raw.Count > 4096,
                    rawPath = raw.Take(4096).Select(p => V(p)).ToArray(),
                    smoothedCount = smoothed.Count,
                    smoothedTruncated = smoothed.Count > 4096,
                    smoothedPath = smoothed.Take(4096).Select(p => V(p)).ToArray(),
                    bitmap = BitmapWindow(
                        _navigation.CurrentBitmap, coordPlayer.Position, 10f),
                });
            }
            if (UsesSiteCoordination)
            {
                // Committed site work executes through the pipeline only —
                // no legacy waypoint execution, no class-wide blocking that
                // could cancel the freshly bound op.
                return;
            }
            if (pipeline.IsObjectiveBlocked(pipeline.SelectedObjective))
            {
                // No fallback: a blocked objective fails explicitly instead of
                // silently handing back to the legacy path.
                _status = $"coord objective failed (no fallback): {pipeline.SelectedObjective}";
                return;
            }
            if (takeover)
            {
                // Outcome accounting belongs to the observation layer, not the
                // execution path — the ledger must stay open while coord ops
                // drive, otherwise outcomes are silently dropped. Keyed on
                // the op the pipeline actually selected from facts.
                if (pipeline.CurrentObjective != FloorObjectiveKind.None)
                    EnsureObjectiveExecution(pipeline.CurrentObjective);
                return;
            }
            // On non-coord floors (boss/terminal) the pipeline still ticks for
            // hazard-aware movement; objective status stays with the floor's
            // own controller.
            if (this.Kind == FloorRuntimeKind.Normal)
                _status = "objective unhandled (no coverage): " +
                    pipeline.SelectedObjective;
        }
    }

    private bool HasOnlyExhaustedAggroTargets()
    {
        if (!RecoveryBudget.HasExhaustedTargets) return false;

        // Examine live aggro before suppression. Temporary skips and dead targets cannot exhaust recovery.
        var exhausted = CombatTargetingHelpers.PickAggroedHostile(float.MaxValue, out _,
            predicate: target => RecoveryBudget.IsTargetExhausted(target.GameObjectId));
        return exhausted != null && CombatTargetingHelpers.PickAggroedHostile(float.MaxValue, out _,
            predicate: target => !RecoveryBudget.IsTargetExhausted(target.GameObjectId)) == null;
    }

    private void RestartAfterCombatFailure(string reason, bool onlyExhaustedAggroRemains = false)
    {
        if (_ctx is not { } context) return;

        int failures = RecoveryBudget.TotalFailures;
        if (context.TryHoldFailedAttempt(reason))
        {
            _status = context.StatusLine;
        }
        else if (UsesSiteCoordination && context.FarmingPlan?.ReusesSave != true &&
                 !context.DutyCompletionObserved && !context.DutyFailureObserved && RecoveryBudget.TryRestart(onlyExhaustedAggroRemains))
        {
            // Rebind the floor's work from live observations while retaining completed work and duty progress.
            _coordPipeline!.Reset();
            _coordPipeline = new(RecordReplayEvent);
            _coordRecoveryController = null;
            _chaseHelper.Reset();
            context.ClearSuppressedCombatTargets();
            context.ClearPreferredAggroTarget();
            _status = $"第 {Floor} 层战斗站位重试耗尽，FSD 已原地重启。";
            RecordReplayEvent("fsd-combat-restarted", new { floor = Floor, reason, failures,
                restarts = RecoveryBudget.Restarts, limit = CombatRecoveryBudget.MaximumRestartsPerFloor });
            return;
        }
        else
        {
            _status = context.StatusLine = RecoveryBudget.Restarts > 0
                ? $"第 {Floor} 层原地重启后仍无法恢复战斗，FSD 已停止。"
                : reason;
            context.StatusIsError = true;
        }
        RecordReplayEvent("sight-recovery-stopped", new { floor = Floor, reason, failures,
            restarts = RecoveryBudget.Restarts });
    }

    private bool EnsureObjectiveExecution(FloorObjectiveKind objective)
    {
        if (this.IsDisposed || _navHelper == null)
            return false;
        if (this.ActiveExecution?.Objective == objective)
            return true;
        if (this.ActiveExecution != null &&
            HasActiveSearchExecution() &&
            ShouldContinuePlannedRouteForPassageActivation(objective))
        {
            this.ActiveExecution.Objective = objective;
            return true;
        }
        if (this.ActiveExecution != null)
        {
            if (HasActiveSearchExecution())
            {
                if (!StopActiveSearchExecution(objective))
                    return false;
            }
            else
            {
                CancelActiveMovement();
            }
            _chaseHelper.Reset();
            _ctx?.ClearPreferredAggroTarget();
        }

        ReplaceObjectiveExecution(objective, _navHelper);
        return true;
    }

    /// <summary>Cancel the coord movement surface — the real surface, not
    /// only the legacy nav helper. Claims resolve fresh next tick.</summary>
    internal void CancelCoordMovement()
    {
        _navigation?.Cancel();
        if (UsesEncounterCoordination || _coordPipeline?.CurrentEncounterOp != null)
            _coordPipeline?.SuspendEncounter();
    }

    internal void SetExternalMovement(bool active)
    {
        if (active) CancelCoordMovement();
        SuspendTask(active);
        _coordPipeline?.SetExternalMovement(active, DateTime.UtcNow);
    }

    private readonly int[] _pomanderStock = new int[RunKnowledge.PomanderSlotCount];
    private readonly bool[] _pomanderActive = new bool[RunKnowledge.PomanderSlotCount];

    /// <summary>
    /// Outer-update knowledge ingest: seal floor facts once, refresh the
    /// pomander/native snapshot, chat-derived intuition facts, the hoard
    /// counter (acquisition = native delta) and shared object evidence —
    /// sight positions, observed banded, exact reveal. Knowledge updates
    /// only mark the route dirty; nothing here replans or moves.
    /// </summary>
    internal unsafe void IngestKnowledge(InstanceContentDeepDungeon* dd)
    {
        var knowledge = _ctx?.Knowledge;
        var fk = knowledge?.Floor;
        if (knowledge == null || fk == null || dd == null)
            return;
        // The floor baseline seals before any count-derived lifecycle
        // observation — a -1 baseline must never be eligible.
        fk.SealFloor(RoomGraph.GetLocalPlayerRoomIndex(dd), dd->HoardCount);
        for (var i = 0; i < RunKnowledge.PomanderSlotCount; i++)
        {
            _pomanderStock[i] = _pomanderManager.GetCount((uint)i);
            _pomanderActive[i] = _pomanderManager.IsActive((uint)i);
        }
        knowledge.SetPomanderSnapshot(_pomanderStock, _pomanderActive);
        fk.ObserveNativeIntuition(_nativeIntuitionActive);
        fk.ObserveSightState(_chatWatchers?.SightState ?? SightUseState.None);
        fk.ObserveIntuitionFacts(
            _chatWatchers?.UsedIntuitionThisFloor == true,
            _chatWatchers?.IntuitionUsePendingThisFloor == true,
            _chatWatchers?.ChatSaysHoard == true,
            _chatWatchers?.ChatSaysNoHoard == true);
        if (_chatWatchers?.ChatSaysNoHoard == true)
            _executor?.ClearCachedHoardIndicator();
        ObserveHoardCount(dd, "coord-ingest");

        var evidence = ObjectEvidence.Current;
        if (evidence?.Available != true)
            return;
        _executor?.ObserveSightTrapIndicators(evidence);
        // Chest entities/kind/position persist across object unload — the
        // point is knowledge, never a live pointer.
        foreach (var chest in evidence.Chests)
        {
            fk.ObserveChest(chest.Object.EntityId, (int)chest.Kind,
                chest.Object.Position,
                RoomIndexOf(dd, chest.Object.Position) ?? -1);
            if (chest.Kind == FloorChestKind.Banded)
                fk.ObserveBanded(chest.Object.Position);
        }
        // Exact reveal: a position fact that survives object unload — only
        // accepted while the lifecycle still wants indicators and no
        // accepted/inherited NoHoard stands; after acquisition a lingering
        // loaded marker must not re-add reveal work.
        if (BandedChestLocator.TryFindHoardIndicatorMatch(evidence, out var indicator) &&
            indicator.HasValue && !fk.HoardAcquired &&
            !fk.ChatSaysNoHoard && !fk.InheritedNoHoardInferred &&
            (_executor?.CanAcceptHoardIndicator ?? true))
        {
            var had = fk.ExactHoardPosition;
            fk.SetExactHoard(indicator.Value.Position,
                RoomIndexOf(dd, indicator.Value.Position));
            if (!had.HasValue && fk.ExactHoardPosition.HasValue)
                RecordReplayEvent("cached-hoard-indicator-updated", new
                {
                    x = indicator.Value.Position.X,
                    y = indicator.Value.Position.Y,
                    z = indicator.Value.Position.Z
                });
        }
    }

    private unsafe int? RoomIndexOf(InstanceContentDeepDungeon* dd, Vector3 pos)
    {
        // Nearest center within the 30m disk — adjacent room centers'
        // disks overlap, so first-match order must not pick the room.
        var best = -1;
        var bestD = 30f * 30f;
        for (var r = 0; r < RoomGraph.MaxRooms; r++)
            if (MapPos.TryGetRoomCenter(dd, r, out var c))
            {
                var dx = c.X - pos.X;
                var dz = c.Z - pos.Z;
                var d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = r; }
            }
        return best < 0 ? null : best;
    }

    /// <summary>Coord-mode permissions: movement and the primary channel
    /// belong to the pipeline's actual objective; combat permission derives
    /// from real combat/passage state, and chest interaction stays
    /// op-owned (no legacy waypoint sidecar).</summary>
    private void RefreshCoordObjectiveDecision()
    {
        var primary = _coordPipeline?.SelectedObjective ?? FloorObjectiveKind.None;
        var passageOpen = _normalActivity.PassageOpen;
        var inCombat = _normalActivity.InCombat;
        var combat = primary == FloorObjectiveKind.FinishCombatBeforePassage
            ? CommandChannelPermission.PrimaryObjective
            : inCombat || !passageOpen
                ? CommandChannelPermission.CompatibleSidecar
                : CommandChannelPermission.Blocked;
        var channels = new ObjectiveChannelPermissions(
            primary == FloorObjectiveKind.None
                ? CommandChannelPermission.Blocked
                : CommandChannelPermission.PrimaryObjective,
            combat,
            CommandChannelPermission.Blocked,
            primary == FloorObjectiveKind.EnterPassage
                ? CommandChannelPermission.PrimaryObjective
                : CommandChannelPermission.Blocked);
        Objectives.SetObjectiveDecision(new ObjectiveArbiterDecision(primary, channels));
    }


    internal bool RequireTransitionPermission(string operation, FloorObjectiveKind requiredObjective)
    {
        var decision = this.Objectives.ObjectiveDecision;
        bool allowed = this.Objectives.HasObjectiveDecision == true &&
                       decision.PrimaryObjective == requiredObjective &&
                       decision.Channels.Transition == CommandChannelPermission.PrimaryObjective;
        if (allowed)
        {
            ClearPermissionBlock(ref _blockedTransitionOperation, "Transition");
            return true;
        }

        RecordPermissionBlock(ref _blockedTransitionOperation, "Transition", operation, requiredObjective);
        _status = $"Waiting: transition permission blocked for {operation} ({decision.PrimaryObjective})";
        return false;
    }

    private void RecordPermissionBlock(
        ref string blockedOperation,
        string channel,
        string operation,
        FloorObjectiveKind? requiredObjective)
    {
        if (string.Equals(blockedOperation, operation, StringComparison.Ordinal))
            return;

        blockedOperation = operation;
        _navDriver?.Cancel();
        var decision = this.Objectives.ObjectiveDecision;
        RecordReplayEvent("objective-permission-blocked", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            phase = _phase.ToString(),
            channel,
            operation,
            requiredObjective = requiredObjective?.ToString(),
            primaryObjective = decision.PrimaryObjective.ToString(),
            movement = decision.Channels.Movement.ToString(),
            combat = decision.Channels.Combat.ToString(),
            transition = decision.Channels.Transition.ToString()
        });
    }

    private void ClearPermissionBlock(ref string blockedOperation, string channel)
    {
        if (string.IsNullOrEmpty(blockedOperation))
            return;

        string operation = blockedOperation;
        blockedOperation = string.Empty;
        var decision = this.Objectives.ObjectiveDecision;
        RecordReplayEvent("objective-permission-restored", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            phase = _phase.ToString(),
            channel,
            operation,
            primaryObjective = decision.PrimaryObjective.ToString()
        });
    }

    internal void ResetPermissionBlocks()
    {
        _blockedMovementOperation = string.Empty;
        _blockedTransitionOperation = string.Empty;
    }

    public void TickInteractionChannel(IFramework _)
    {
        if (this.ItemUse.DispatchedThisUpdate == true ||
            this.IsDisposed ||
            !AllowsChestSidecarInteraction)
        {
            return;
        }

        var waypoint = _activeWaypoint;
        var evidence = this.ObjectEvidence.Current;
        if (!waypoint.HasValue || !IsChestWaypoint(waypoint.Value) || evidence?.Available != true)
            return;

        if (waypoint.Value.Type == RoomObjectiveType.ChestBanded && _ctx?.RunOptions.Current.DiscoveryOnly == true)
            return;
        if (_ctx?.ChestInteraction.TryInteract(ActiveChestAttempt, evidence, out var snapshot, out bool retry) == true)
        {
            if (!retry || _ctx?.Configuration.AggressiveChestInteraction != true)
            {
                this.ObjectEvidence.Invalidate();
                RecordChestInteractionStarted(waypoint.Value, snapshot, retry);
            }
        }
    }

    internal unsafe void RefreshObjectiveDecision(InstanceContentDeepDungeon* dd)
    {
        if (this.IsDisposed ||
            this.Floor != dd->Floor ||
            this.DungeonId != dd->DeepDungeonId)
        {
            return;
        }
        if (UsesSiteCoordination)
        {
            RefreshCoordObjectiveDecision();
            return;
        }
        var objectEvidence = this.Kind == FloorRuntimeKind.Normal
            ? this.ObjectEvidence.Current
            : null;
        if (this.Kind == FloorRuntimeKind.Normal && objectEvidence?.Available != true)
        {
            this.Objectives.ClearObjectiveDecision();
            return;
        }

        var executor = _executor;
        var evidenceState = executor?.HoardEvidenceState ?? HoardEvidenceState.Disabled;
        var activeWaypoint = _activeWaypoint ?? executor?.CurrentWaypoint;
        bool passageOpen = _ctx?.Duty.PassageOpen == true;
        bool combatInProgress = Service.Condition[ConditionFlag.InCombat];
        bool suppressCombat = _ctx?.FarmingPlan?.ReusesSave == true;
        bool routineCombatAllowed = !suppressCombat && (combatInProgress || !passageOpen);
        bool chestInteractionAllowed =
            objectEvidence != null &&
            _activeWaypoint.HasValue &&
            IsChestWaypoint(_activeWaypoint.Value);
        bool visibleBandedChest =
            activeWaypoint?.Type == RoomObjectiveType.ChestBanded ||
            executor?.HasPendingBandedWaypoint == true ||
            _searchExecutionKind == SearchExecutionKind.BandedReentry;
        if (!visibleBandedChest &&
            executor?.ConfigSnapshot.BandedEnabled == true &&
            !executor.HasOpenedHoardThisFloor &&
            objectEvidence != null &&
            BandedChestLocator.TryFindNearestToPlayer(objectEvidence, out var visibleBandedPosition))
        {
            visibleBandedChest = visibleBandedPosition.HasValue;
        }
        bool bandedRevealPending = IsBandedRevealExpectationPending();
        bool hoardWorkResolved =
            this.Kind == FloorRuntimeKind.Boss ||
            executor?.IsHoardWorkResolved == true;
        bool mandatoryHoardTerminal =
            hoardWorkResolved && !bandedRevealPending && !visibleBandedChest;
        bool knownOrConfirmedHoard =
            !hoardWorkResolved &&
            evidenceState is HoardEvidenceState.IntuitionDirect or HoardEvidenceState.IntuitionWaitingForIndicator;
        bool requiredHoardDiscovery =
            !hoardWorkResolved &&
            evidenceState is HoardEvidenceState.BlindSearch or HoardEvidenceState.IntuitionActiveUnconfirmed;
        bool passageActivationRequired =
            this.Kind == FloorRuntimeKind.Normal &&
            !passageOpen &&
            hoardWorkResolved &&
            evidenceState != HoardEvidenceState.IntuitionPending;
        if (_ctx?.ControlledPtSurvey != null && this.Kind == FloorRuntimeKind.Normal)
        {
            visibleBandedChest = false;
            knownOrConfirmedHoard = false;
            requiredHoardDiscovery = false;
            mandatoryHoardTerminal = this.Survey.ControlledIntuitionResolved;
            passageActivationRequired = !passageOpen && this.Survey.ControlledIntuitionResolved;
        }
        var snapshot = new ObjectiveArbiterSnapshot(
            BossObjective: this.Kind == FloorRuntimeKind.Boss,
            VisibleBandedChest: visibleBandedChest,
            KnownOrConfirmedHoard: knownOrConfirmedHoard,
            RequiredHoardDiscovery: requiredHoardDiscovery,
            MandatoryHoardTerminal: mandatoryHoardTerminal,
            PassageOpen: passageOpen,
            PassageActivationRequired: passageActivationRequired,
            CombatInProgress: combatInProgress,
            RoutineCombatAllowed: routineCombatAllowed,
            ActiveChestInteraction: chestInteractionAllowed,
            SuppressCombat: suppressCombat,
            RequiredChestWork: _ctx?.RunOptions.Current.HarvestChestsRequired == true &&
                (executor?.PlannedRouteCount > 0 || executor?.RoomContext != null ||
                 _activeWaypoint.HasValue || (_taskRunner != null && _taskRunner.Phase != TaskPhase.Idle)));
        _coordSnapshot = snapshot;
        if (!this.Objectives.RefreshObjectiveDecision(
                snapshot,
                objectEvidence?.Version ?? 0,
                _ctx?.RunOptions.Version ?? 0,
                this.Objectives.ObjectiveLedger.Version,
                out var decision))
            return;

        RecordReplayEvent("objective-arbiter-decision", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            primaryObjective = decision.PrimaryObjective.ToString(),
            movement = decision.Channels.Movement.ToString(),
            combat = decision.Channels.Combat.ToString(),
            interaction = decision.Channels.Interaction.ToString(),
            interactionOwner = "ChestSidecar",
            transition = decision.Channels.Transition.ToString(),
            evidenceState = evidenceState.ToString(),
            bossObjective = this.Kind == FloorRuntimeKind.Boss,
            visibleBandedChest,
            bandedRevealPending,
            hoardWorkResolved,
            knownOrConfirmedHoard,
            requiredHoardDiscovery,
            mandatoryHoardTerminal,
            passageActivationRequired,
            passageOpen,
            combatInProgress,
            routineCombatAllowed,
            activeChestInteraction = chestInteractionAllowed
        });
    }

    internal void RequestPlanRefresh(string reason)
    {
        if (this.Kind != FloorRuntimeKind.Normal || this.IsDisposed)
            return;

        PlanningState.RequestRefresh(this.Floor, this.DungeonId, reason);
        // Coord path: a refresh request marks the route dirty only — never
        // a synchronous replan or movement callback.
        if (UsesSiteCoordination)
            _siteDriver?.MarkRouteDirty();
    }

    internal void ObserveFloorRuntimeNativeIntuitionEdge()
    {
        if (this.Kind != FloorRuntimeKind.Normal || this.IsDisposed)
            return;

        if (!this.NativeIntuitionActive.HasValue)
        {
            this.NativeIntuitionActive = _nativeIntuitionActive;
            return;
        }

        bool previous = this.NativeIntuitionActive.Value;
        if (!ReadyFloorIntuitionPlanner.ShouldRequestPlanRefresh(previous, _nativeIntuitionActive))
            return;

        this.NativeIntuitionActive = _nativeIntuitionActive;
        string reason = _nativeIntuitionActive
            ? "native-intuition-activated"
            : "native-intuition-deactivated";
        RequestPlanRefresh(reason);
        RecordReplayEvent("native-intuition-edge", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            previous,
            current = _nativeIntuitionActive,
            reason
        });
    }

    internal void MarkPlanRefreshConsumed(long consumedVersion)
    {
        if (this.Kind == FloorRuntimeKind.Normal &&
            !this.IsDisposed &&
            this.Floor == PlanningState.PendingEvidenceFloor &&
            this.DungeonId == PlanningState.PendingEvidenceDungeonId)
        {
            PlanningState.Acknowledge(consumedVersion);
        }
    }

    internal unsafe bool TryReconcileDelayedHoardEvidence(InstanceContentDeepDungeon* dd)
    {
        if (this.IsDisposed ||
            this.Kind != FloorRuntimeKind.Normal ||
            _phase != FloorPhase.FloorActive ||
            HasActiveSearchExecution() ||
            _executor == null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (PlanningState.TryBeginLateEvidencePoll(now, GeneralTickInterval))
        {
            if (!RefreshCachedHoardIndicator(dd))
            {
                _status = "Waiting for hoard indicator evidence...";
                return true;
            }
        }

        bool matchesCurrentFloor =
            PlanningState.PendingEvidenceFloor == this.Floor &&
            PlanningState.PendingEvidenceDungeonId == this.DungeonId;
        var gate = LateHoardEvidencePlanner.Decide(new LateHoardEvidenceSnapshot
        {
            PendingVersion = PlanningState.PendingEvidenceVersion,
            ReconciledVersion = PlanningState.ReconciledEvidenceVersion,
            StableNormalFloor = true,
            EvidenceMatchesCurrentFloor = matchesCurrentFloor,
            FloorActiveAllowsReplan = true,
            MandatoryHoardWorkResolved = _executor.IsHoardWorkResolved,
            RefreshedPlan = Array.Empty<RoomPlanEntry>()
        });
        if (!gate.ShouldRegeneratePlan)
        {
            if (PlanningState.PendingEvidenceVersion > PlanningState.ReconciledEvidenceVersion && !matchesCurrentFloor)
            {
                PlanningState.DiscardPending();
                RecordReplayEvent("late-hoard-evidence-ignored", new
                {
                    floor = this.Floor,
                    floorGeneration = this.Generation,
                    dungeonId = this.DungeonId,
                    evidenceFloor = PlanningState.PendingEvidenceFloor,
                    evidenceDungeonId = PlanningState.PendingEvidenceDungeonId,
                    evidenceVersion = PlanningState.PendingEvidenceVersion,
                    reason = "noncurrent-floor"
                });
            }
            return false;
        }

        var player = Service.LocalPlayer;
        var normalGraph = this.NormalGraph;
        if (player == null || normalGraph == null)
            return false;

        long evidenceVersion = PlanningState.PendingEvidenceVersion;
        string evidenceReason = PlanningState.PendingEvidenceReason;
        _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
        if (!_executor.HasPlanningSnapshot)
        {
            _status = "Waiting for floorset or player-room evidence...";
            return true;
        }

        bool bandedEligible = _executor.ConfigSnapshot.BandedEnabled && !_executor.HasOpenedHoardThisFloor;
        Vector3? visibleBanded = null;
        if (bandedEligible && !BandedChestLocator.TryFindNearestToPlayer(this.ObjectEvidence.Current!, out visibleBanded))
        {
            _status = "Waiting for banded chest evidence...";
            return true;
        }
        bool pendingBanded = bandedEligible &&
            (_searchExecutionKind == SearchExecutionKind.BandedReentry ||
             _activeWaypoint?.Type == RoomObjectiveType.ChestBanded ||
             _executor.HasPendingBandedWaypoint);
        var decision = LateHoardEvidencePlanner.Decide(new LateHoardEvidenceSnapshot
        {
            PendingVersion = evidenceVersion,
            ReconciledVersion = PlanningState.ReconciledEvidenceVersion,
            StableNormalFloor = true,
            EvidenceMatchesCurrentFloor = true,
            FloorActiveAllowsReplan = true,
            MandatoryHoardWorkResolved = _executor.IsHoardWorkResolved,
            PendingOrVisibleBandedWork = pendingBanded || visibleBanded.HasValue,
            RefreshedPlan = _executor.SnapshotPlannedRoute()
        });

        bool startedVisibleBanded = false;
        if (decision.ShouldResumeHoardWork && visibleBanded.HasValue)
        {
            int bandedRoom = RoomGraph.GetRoomIndexForPosition(
                dd,
                visibleBanded.Value,
                normalGraph.ReachableRooms,
                -1);
            if (bandedRoom < 0)
            {
                _status = "Waiting to resolve visible banded chest room...";
                return true;
            }
            _executor.ClearRoomContext();
            startedVisibleBanded = _executor.StartBandedOnlyRoomSearch(dd, bandedRoom, player.Position, visibleBanded.Value);
            if (!startedVisibleBanded && !decision.HasRequiredHoardWork)
            {
                CancelActiveMovement();
                _status = "Visible banded chest could not be queued";
                RecordReplayEvent("late-hoard-evidence-reconcile-failed", new
                {
                    floor = this.Floor,
                    floorGeneration = this.Generation,
                    phase = _phase.ToString(),
                    evidenceVersion,
                    evidenceReason,
                    bandedRoom,
                    reason = "visible-banded-room-search-build-failed"
                });
                return true;
            }
        }

        MarkPlanRefreshConsumed(evidenceVersion);
        if (decision.BlockUnroutableMandatoryWork)
        {
            _status = $"Waiting for mandatory hoard evidence ({_executor.HoardEvidenceState})";
            RecordHoardEvidenceWait("late-hoard-evidence-waiting-unresolved");
            RecordReplayEvent("late-hoard-evidence-reconciled", new
            {
                floor = this.Floor,
                floorGeneration = this.Generation,
                phase = _phase.ToString(),
                evidenceVersion,
                evidenceReason,
                hoardEvidenceState = _executor.HoardEvidenceState.ToString(),
                reason = "non-routable-mandatory-work"
            });
            return true;
        }

        if (!decision.ShouldResumeHoardWork)
        {
            RecordReplayEvent("late-hoard-evidence-reconciled", new
            {
                floor = this.Floor,
                floorGeneration = this.Generation,
                phase = _phase.ToString(),
                evidenceVersion,
                evidenceReason,
                hoardEvidenceState = _executor.HoardEvidenceState.ToString(),
                reason = "no-required-hoard-work"
            });
            return false;
        }

        CancelActiveMovement();
        ResetPatrolPlan();
        _ctx?.ClearPreferredAggroTarget();
        _chaseHelper.Reset();
        _activeWaypoint = null;
        _searchExecutionKind = startedVisibleBanded
            ? SearchExecutionKind.BandedReentry
            : SearchExecutionKind.PlannedRoom;
        if (startedVisibleBanded)
            ClearPostRoomPomanderRetry();
        _status = startedVisibleBanded
            ? "Late hoard evidence revealed a banded chest"
            : "Late hoard evidence reopened search";
        string reentryReason = startedVisibleBanded
            ? "visible-banded-work"
            : pendingBanded
                ? "pending-banded-work"
                : "required-hoard-work";
        RecordReplayEvent("late-hoard-evidence-reconciled", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            evidenceVersion,
            evidenceReason,
            hoardEvidenceState = _executor.HoardEvidenceState.ToString(),
            reason = reentryReason
        });
        RecordReplayEvent("floor-active-mechanic-selected", new
        {
            mechanic = "Search",
            reason = $"late-hoard-evidence-{reentryReason}"
        });
        return true;
    }

    internal unsafe void UpdateFloorSetup(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        if (player == null)
        {
            _status = "Waiting for player position";
            return;
        }

        if (_ctx!.Duty.IsBossFloor)
        {
            _phase = FloorPhase.BossFloor;
            _status = "Boss floor";
            Service.Log.Info("[FloorPhase] Boss floor detected -> BossFloor");
            return;
        }

        ResolveCurrentFloorIntuitionTimeoutIfNeeded(dd);

        var normalGraph = this.NormalGraph;
        if (normalGraph == null)
        {
            _status = "Waiting for room graph...";
            return;
        }

        if (this.Survey is
            {
                InheritedIntuitionAttemptId: > 0,
                InheritedIntuitionDecision: null
            })
        {
            _status = "Waiting for inherited Intuition result";
            return;
        }

        if (_ctx?.ControlledPtSurvey != null)
        {
            if (!PlanningState.SetupPlanGenerated)
            {
                if (!ShouldRunGeneralTick())
                    return;

                _executor!.ResetForFloor(dd, SnapshotRunOptions());
                PlanningState.ObserveHoardCount(dd->HoardCount);
                PlanningState.MarkSetupPlanGenerated();
            }

            if (this.Survey.ControlledIntuitionResolved != true)
            {
                _status = "Controlled PT: waiting for terminal Intuition semantics";
                return;
            }

            _phase = FloorPhase.FloorActive;
            _status = "Controlled PT floor active";
            RecordReplayEvent("floor-lifecycle-transition", new
            {
                from = FloorPhase.FloorSetup.ToString(),
                to = FloorPhase.FloorActive.ToString(),
                reason = "controlled-intuition-terminal"
            });
            return;
        }

        if (!PlanningState.SetupPlanGenerated)
        {
            if (!ShouldRunGeneralTick())
                return;

            _executor!.ResetForFloor(dd, SnapshotRunOptions());
            PlanningState.ObserveHoardCount(dd->HoardCount);
            if (EntryIncenseWindowOpen() &&
                _ctx!.Duty.PassageOpen != true && !this.Survey.NaturalPoisonfruitAttempted &&
                !this.Survey.NaturalMazerootAttemptedOrAdopted &&
                DeepDungeonFloorItemUsePolicy.CanUsePtIncense(dd->DeepDungeonBanId) &&
                (GetStoneCountAvailableForFloorUse(1) > 0 || GetStoneCountAvailableForFloorUse(2) > 0))
            {
                if (this.ItemUse.Pending != null || !CanAttemptPomanderUse())
                {
                    _status = "Preparing entry incense before exploration";
                    return;
                }
                if (this.Survey.TryUseNaturalPassageAcceleration(dd))
                    return;
            }
            TryUseFloorInitPomander(dd);
            if (this.ItemUse.Pending is
                {
                    BlocksFloorSetup: true
                } pendingFloorItemUse)
            {
                _status =
                    $"Waiting for {pendingFloorItemUse.Key.Kind} {pendingFloorItemUse.Key.ItemId} use confirmation";
                return;
            }
            if (UsesSiteCoordination)
            {
                // Coord floors have no generated room queue: knowledge is
                // sealed by the outer ingest and the route is committed
                // lazily by the site scheduler on the first active tick.
                // A positive Intuition with an unloaded position becomes
                // room-intel work, not a setup gate.
                PlanningState.MarkSetupPlanGenerated();
                RecordNativeIntuitionSnapshot("first-floor-plan-generated", force: true, nativeStateAvailable: true, nativeIntuitionActive: _nativeIntuitionActive);
                RecordReplayEvent("floor-plan-generated", new
                {
                    floor = dd->Floor,
                    reason = "floor-setup-generated",
                    coordination = "site-work"
                });
                PublishInitialRunFloorState(dd, player.Position);
            }
            else
            {
                if (!RefreshCachedHoardIndicator(dd))
                {
                    _status = "Waiting for hoard indicator evidence...";
                    return;
                }
                long evidenceVersion = PlanningState.PendingEvidenceVersion;
                _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
                MarkPlanRefreshConsumed(evidenceVersion);
                PlanningState.MarkSetupPlanGenerated(_executor.HasPlanningSnapshot);
                if (!PlanningState.SetupPlanGenerated)
                {
                    _status = "Waiting for floor planning evidence...";
                    return;
                }
                RecordNativeIntuitionSnapshot("first-floor-plan-generated", force: true, nativeStateAvailable: true, nativeIntuitionActive: _nativeIntuitionActive);
                RecordReplayEvent("floor-plan-generated", BuildPlanReplayPayload(dd->Floor, "floor-setup-generated"));
                PublishInitialRunFloorState(dd, player.Position);
            }
        }
        else
        {
            if (UsesSiteCoordination)
            {
                if (PlanningState.RefreshRequested)
                    MarkPlanRefreshConsumed(PlanningState.PendingEvidenceVersion);
            }
            else
            {
                if (!RefreshCachedHoardIndicator(dd))
                {
                    _status = "Waiting for hoard indicator evidence...";
                    return;
                }
                if (PlanningState.RefreshRequested)
                {
                    long evidenceVersion = PlanningState.PendingEvidenceVersion;
                    _executor!.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
                    MarkPlanRefreshConsumed(evidenceVersion);
                    RecordReplayEvent("floor-plan-regenerated-evidence", BuildPlanReplayPayload(dd->Floor, "floor-setup-evidence-refresh"));
                }
            }
        }

        var executor = _executor;
        if (executor == null)
        {
            _status = "Waiting for floor executor...";
            return;
        }

        if (UsesSiteCoordination)
        {
            // Preserve the pending-chat resolution wait only: positive
            // Intuition with an unloaded position must NOT gate here — it
            // becomes room-intel work under the scheduler.
            var intuitionPending = _chatWatchers?.UsedIntuitionThisFloor == true &&
                _chatWatchers.ChatSaysHoard != true && _chatWatchers.ChatSaysNoHoard != true;
            if (intuitionPending)
            {
                _status = $"Waiting for hoard evidence ({HoardEvidenceState.IntuitionPending})";
                RecordHoardEvidenceWait("floor-setup-waiting-hoard-evidence");
                return;
            }
        }
        else if (executor.HoardEvidenceState == HoardEvidenceState.IntuitionPending ||
            (executor.IsComplete && !executor.IsHoardWorkResolved))
        {
            _status = $"Waiting for hoard evidence ({executor.HoardEvidenceState})";
            RecordHoardEvidenceWait("floor-setup-waiting-hoard-evidence");
            return;
        }
        EndHoardEvidenceWait("floor-setup-wait-ended", "floor-setup-waiting-hoard-evidence");

        _phase = FloorPhase.FloorActive;
        _status = executor.IsComplete ? "Floor active" : "Starting floor objective";
        RecordReplayEvent("floor-lifecycle-transition", new
        {
            from = FloorPhase.FloorSetup.ToString(),
            to = FloorPhase.FloorActive.ToString(),
            reason = executor.IsComplete ? "floor-ready-no-initial-route" : "floor-ready-plan-available"
        });
    }

    internal unsafe bool ResolveCurrentFloorIntuitionTimeoutIfNeeded(InstanceContentDeepDungeon* dd)
    {
        if (dd == null || _chatWatchers == null)
            return false;

        if (!PendingIntuition.TryGetCurrentFloorUseElapsedMilliseconds(dd->Floor, DateTime.UtcNow, out var elapsedMilliseconds))
            return false;

        var decision = CurrentIntuitionResolutionPlanner.Decide(new CurrentIntuitionResolutionSnapshot
        {
            UsedIntuitionThisFloor = _chatWatchers.UsedIntuitionThisFloor,
            ChatSaysHoard = _chatWatchers.ChatSaysHoard,
            ChatSaysNoHoard = _chatWatchers.ChatSaysNoHoard,
            ElapsedMillisecondsSinceUse = elapsedMilliseconds,
            ResolutionWindowMilliseconds = FloorSurveyController.IntuitionResolutionWindowMilliseconds
        });

        if (decision.Kind != CurrentIntuitionResolutionKind.Wait ||
            decision.RemainingWaitMilliseconds > 0 ||
            !PendingIntuition.TryMarkOverdueRecorded())
        {
            return false;
        }

        RecordReplayEvent("current-intuition-evidence-overdue", new
        {
            floor = dd->Floor,
            windowMilliseconds = FloorSurveyController.IntuitionResolutionWindowMilliseconds,
            reason = "authoritative-7272-or-7273-still-required"
        });
        return false;
    }

    internal bool HandleNoHoardEvidenceInvalidated(string reason)
    {
        if (UsesSiteCoordination)
        {
            // Knowledge path: the chat/inherited resolver already wrote the
            // observation — clear the cached reveal point and mark the
            // route dirty. No old objective completion or movement cancel.
            RecordReplayEvent("cached-hoard-indicator-cleared", new { reason });
            _executor?.ClearCachedHoardIndicator();
            _siteDriver?.MarkRouteDirty();
            return true;
        }
        var currentPlanEntry = _executor?.CurrentPlanEntry;
        bool activeChestObjective =
            this.ActiveExecution?.ObjectiveRecords.Any(
                execution => execution.Category == RoomObjectiveCategory.Chests) == true;
        var decision = HoardWorkInvalidationPlanner.Decide(new HoardWorkInvalidationSnapshot
        {
            NoHoardEvidenceActive = true,
            HasCachedHoardIndicator = _executor?.CachedHoardIndicatorPos.HasValue == true,
            ActiveWaypointPresent = _activeWaypoint.HasValue,
            ActiveWaypointIsTrap = _activeWaypoint?.Type == RoomObjectiveType.Trap,
            CurrentPlanShouldProbeHoard = currentPlanEntry?.ShouldProbeHoard == true,
            CurrentPlanShouldSearchChests = currentPlanEntry?.ShouldSearchChests == true,
            ActiveChestObjectivePresent = activeChestObjective,
            CurrentPlanShouldVisitForIntel = currentPlanEntry?.ShouldVisitForIntel == true
        });

        if (decision.ClearCachedIndicator && _executor?.ClearCachedHoardIndicator() == true)
        {
            RecordReplayEvent("cached-hoard-indicator-cleared", new
            {
                reason
            });
        }

        if (decision.AbortActiveHoardWork)
        {
            int roomIndex = _executor?.RoomContext?.RoomIndex ?? currentPlanEntry?.RoomIndex ?? -1;
            var objectiveOutcome = new RoomObjectiveOutcomeResult(
                decision.HoardOutcome,
                decision.ChestsOutcome,
                decision.IntelOutcome);
            if (!TryApplyActiveObjectiveOutcomes(roomIndex, objectiveOutcome, reason))
                return true;
            CancelActiveMovement();
            _executor?.ClearRoomContext();
            ClearRoomIntelSettle();
            _status = "No-hoard evidence invalidated hoard work";
            RecordReplayEvent("hoard-work-aborted-by-no-hoard", new
            {
                reason
            });
        }

        return decision.RequestPlanRefresh;
    }

    internal object BuildPlanReplayPayload(byte floor, string reason, int? roomIndex = null)
    {
        var plan = _executor?.PlannedRoute ?? Array.Empty<RoomPlanEntry>();
        var trace = _executor?.LastPlanTrace ?? default;
        return new
        {
            floor,
            reason,
            roomIndex,
            phase = _phase.ToString(),
            status = _status,
            hoardEvidenceState = _executor?.HoardEvidenceState.ToString() ?? string.Empty,
            currentTargetRoomIndex = _executor?.CurrentTargetRoomIndex,
            planCount = plan.Count,
            roomPlan = plan.Select(entry => new
            {
                entry.RoomIndex,
                entry.ShouldProbeHoard,
                entry.ShouldSearchChests,
                entry.ShouldVisitForIntel,
                hoardEvidenceState = entry.HoardEvidenceState.ToString()
            }).ToArray(),
            candidates = trace.Candidates?.Select(candidate => new
            {
                candidate.RoomIndex,
                candidate.Eligible,
                candidate.ShouldProbeHoard,
                candidate.ShouldSearchChests,
                candidate.ShouldVisitForIntel,
                hoardEvidenceState = candidate.HoardEvidenceState.ToString(),
                candidate.BasePriority,
                candidate.Reason
            }).ToArray() ?? [],
            selections = trace.Selections?.Select(selection => new
            {
                selection.Step,
                selection.FromRoomIndex,
                selection.SelectedRoomIndex,
                selection.Distance,
                selection.PassageDistance,
                selection.BasePriority
            }).ToArray() ?? [],
            rejectionReason = trace.RejectionReason ?? string.Empty
        };
    }

    internal unsafe void ObserveCurrentRoom(InstanceContentDeepDungeon* dd)
    {
        if (dd == null ||
            this.IsDisposed ||
            this.Floor != dd->Floor ||
            this.DungeonId != dd->DeepDungeonId)
            return;

        int roomIndex = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if (roomIndex < 0)
            return;

        this.Survey.EvidenceSession?.ObserveRoomVisit(roomIndex);
        if (roomIndex == this.LastObservedRoomIndex)
            return;

        this.LastObservedRoomIndex = roomIndex;
        if (this.ActiveRoomNavigationTarget == roomIndex)
        {
            this.ActiveRoomNavigationTarget = null;
        }

        RecordReplayEvent("room-entered", new
        {
            floor = dd->Floor,
            floorGeneration = this.Generation,
            roomIndex,
            phase = _phase.ToString(),
            status = _status
        });
    }

    internal unsafe NavDriveResult NavigateToRoom(InstanceContentDeepDungeon* dd, int targetRoom, global::Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
    {
        if (this.IsDisposed ||
            this.Floor != dd->Floor ||
            this.DungeonId != dd->DeepDungeonId)
        {
            return NavDriveResult.Failed;
        }

        if (!MapPos.TryGetRoomCenter(dd, targetRoom, out var target))
        {
            _status = $"Can't resolve room {targetRoom} center";
            return NavDriveResult.Failed;
        }
        int playerRoom = GameState.RoomGraph.GetLocalPlayerRoomIndex(dd);

        var result = _navDriver!.Drive(target, player.Position, 1.2f, dd, playerRoom, targetRoom);

        switch (result)
        {
            case NavDriveResult.Moving:
                if (this.ActiveRoomNavigationTarget != targetRoom)
                {
                    this.ActiveRoomNavigationTarget = targetRoom;
                    RecordReplayEvent("room-navigation-started", new
                    {
                        floor = dd->Floor,
                        floorGeneration = this.Generation,
                        fromRoom = playerRoom,
                        targetRoom,
                        mode = "Moving",
                        stage = _navDriver.StageLabel
                    });
                }
                _status = _navDriver.IsStaging
                    ? $"Navigating to room {targetRoom} ({_navDriver.StageLabel})"
                    : $"Navigating to room {targetRoom}";
                break;
            case NavDriveResult.Staging:
                if (this.ActiveRoomNavigationTarget != targetRoom)
                {
                    this.ActiveRoomNavigationTarget = targetRoom;
                    RecordReplayEvent("room-navigation-started", new
                    {
                        floor = dd->Floor,
                        floorGeneration = this.Generation,
                        fromRoom = playerRoom,
                        targetRoom,
                        mode = "Staging",
                        stage = _navDriver.StageLabel
                    });
                }
                _status = $"Staging: {_navDriver.StageLabel}";
                break;
            case NavDriveResult.Arrived:
                this.ActiveRoomNavigationTarget = null;
                break;
            case NavDriveResult.StuckRetrying:
                this.RunTelemetry?.ObserveNavigationIssue();
                _status = $"Repathing ({_navDriver.StuckRetryCount}/3)";
                break;
            case NavDriveResult.Failed:
                this.RunTelemetry?.ObserveNavigationIssue();
                this.ActiveRoomNavigationTarget = null;
                Service.Log.Warning($"[FloorPhase] Room {targetRoom} navigation failed");
                _navDriver.Cancel();
                break;
        }
        return result;
    }

    internal void HandleDirectNavState(NavigationState state, string context)
    {
        switch (state)
        {
            case NavigationState.Arrived:
                _status = $"{context}: arrived";
                break;
            case NavigationState.StuckRepathing:
                this.RunTelemetry?.ObserveNavigationIssue();
                _status = $"{context}: stuck ({_navHelper?.StuckRetryCount ?? 0}/3)";
                break;
            case NavigationState.StuckGiveUp:
                this.RunTelemetry?.ObserveNavigationIssue();
                _status = $"{context}: failed";
                break;
            case NavigationState.Failed:
                this.RunTelemetry?.ObserveNavigationIssue();
                _status = $"{context}: navigation unavailable";
                break;
        }
    }
    internal const uint PtHasteStatusId = 4718;
    private enum SearchExecutionKind
    {
        PlannedRoom,
        BandedReentry
    }

    private sealed class ObjectiveExecution : IDisposable
    {
        public ObjectiveExecution(FloorObjectiveKind objective, NavigationHelper navigation)
        {
            Objective = objective;
            TaskRunner = new WaypointTaskRunner(navigation);
        }

        public FloorObjectiveKind Objective { get; set; }
        public WaypointTaskRunner TaskRunner { get; }
        public RoomWaypoint? ActiveWaypoint { get; set; }
        public RunWaypointTelemetryTrace? WaypointTelemetry { get; set; }
        public ChestInteractionAttempt? ChestAttempt { get; set; }
        public SearchExecutionKind SearchKind { get; set; }
        public List<ActiveObjectiveExecution> ObjectiveRecords { get; } = new(3);
        public List<int> PatrolRooms { get; } = new();
        public int PatrolIndex;
        public int CurrentPatrolRoom = -1;

        public void Dispose()
        {
            TaskRunner.Reset(cancelNavigation: false);
            ActiveWaypoint = null;
            WaypointTelemetry = null;
            ChestAttempt = null;
            ObjectiveRecords.Clear();
            PatrolRooms.Clear();
        }
    }

    private enum RoomFinishPomanderOutcome
    {
        NotNeeded,
        PendingRetry
    }

    private sealed class PostRoomPomanderRetry
    {
        public int FinishedRoomIndex;
        public DateTime ExpiresAt;
    }

    private readonly record struct BandedRevealPending(DateTime ExpiresAtUtc, long EvidenceSequence);

    private readonly record struct TrapObservation(DateTime TimestampUtc, int RoomIndex, int WaypointIndex, SearchExecutionKind ExecutionKind, Vector3 Position);

    private sealed class FloorSearchState
    {
        public PostRoomPomanderRetry? PostRoomPomanderRetry;
        public TrapObservation? LastTrapTriggered;
        public TrapObservation? LastTrapCompleted;
        public int IntelSettleRoomIndex = -1;
        public DateTime IntelSettleUntil = DateTime.MinValue;
        public DateTime ObjectiveRetryNotBefore = DateTime.MinValue;
        public bool MandatoryObjectiveBlocked;
        public int MandatoryObjectiveBlockedRoom = -1;
        public ObjectiveIdentity? MandatoryObjectiveBlockedIdentity;
        public RoomObjectiveCategory MandatoryObjectiveBlockedCategory;
        public FloorObjectiveKind MandatoryObjectiveBlockedKind;
        public long MandatoryObjectiveBlockedEvidenceVersion;
        public bool ObjectiveOutcomeRejected;
        public DateTime RoomEvidenceRetryAt = DateTime.MinValue;
    }

    private RoomWaypoint? _activeWaypoint
    {
        get => this.ActiveExecution?.ActiveWaypoint;
        set
        {
            if (this.ActiveExecution != null)
            {
                if (this.ActiveExecution.ActiveWaypoint.HasValue &&
                    (!value.HasValue ||
                     this.ActiveExecution.ActiveWaypoint.Value != value.Value))
                {
                    EndActiveWaypointTelemetry(RunWaypointTerminalOutcome.Aborted, "WaypointCleared");
                }
                this.ActiveExecution.ActiveWaypoint = value;
                if (!value.HasValue || !IsChestWaypoint(value.Value))
                    this.ActiveExecution.ChestAttempt = null;
            }
            else if (value.HasValue)
                throw new InvalidOperationException("Cannot set a waypoint without an active objective execution.");
        }
    }
    private SearchExecutionKind _searchExecutionKind
    {
        get => this.ActiveExecution?.SearchKind ?? SearchExecutionKind.PlannedRoom;
        set
        {
            if (this.ActiveExecution != null)
                this.ActiveExecution.SearchKind = value;
            else if (value != SearchExecutionKind.PlannedRoom)
                throw new InvalidOperationException("Cannot start a non-default search execution without an active objective execution.");
        }
    }
    private ChestInteractionAttempt? ActiveChestAttempt => this.ActiveExecution?.ChestAttempt;

    /// <summary>Coord drives this floor's work — the knowledge pipeline or
    /// a legacy floor that took over the pipeline path.</summary>
    private bool CoordOwnsWork => UsesSiteCoordination || _coordTookOver;

    /// <summary>The chest attempt the actual work pipeline owns: the coord
    /// op's attempt while coordination drives this floor — never a stale
    /// legacy sidecar when coord owns work and has no active chest —
    /// else the legacy execution's attempt.</summary>
    private ChestInteractionAttempt? CurrentInteractionAttempt =>
        CoordOwnsWork ? _coordPipeline?.ActiveChestAttempt : ActiveChestAttempt;

    private const float TrapStandDurationSeconds = 3.0f;
    private const float IntelSettleDurationSeconds = 1.0f;
    // Shared with Coord.OpenChestOp — the EXISTING silver precondition
    // wait, not a new limit.
    internal const float SilverWaitTimeoutSeconds = 30.0f;
    private const float PostRoomPomanderRetrySeconds = 3.0f;
    private const float BandedRevealExpectationSeconds = 3.0f;
    internal const uint StrengthStatusId = 687;
    internal const uint SteelStatusId = 1100;
    internal const uint DeepDungeonCurseStatusId = 1087;


    private unsafe bool TryApplyPassageWorkPolicy(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_ctx?.RunOptions.Current.HarvestChestsRequired == true ||
            _ctx?.Duty.PassageOpen != true || _executor == null)
        {
            return false;
        }

        if (Service.Condition[ConditionFlag.InCombat])
            return false;

        bool pendingBandedWork =
            _searchExecutionKind == SearchExecutionKind.BandedReentry ||
            _activeWaypoint?.Type == RoomObjectiveType.ChestBanded ||
            _executor.HasPendingBandedWaypoint;
        var originalRoute = _executor.SnapshotPlannedRoute();
        var decision = PassageWorkPlanner.Decide(new PassageWorkSnapshot
        {
            PassageOpen = true,
            HoardWorkResolved = _executor.IsHoardWorkResolved,
            VisibleBandedWork = pendingBandedWork,
            PlannedRoute = originalRoute
        });
        bool currentRemoved = _executor.ApplyRetainedRoute(decision.PlannedRoute);

        if (currentRemoved)
        {
            var abortedEntry = originalRoute.Count > 0 ? originalRoute[0] : default;
            bool activeSearchRemoved = this.ActiveExecution?.ObjectiveRecords.Count > 0 &&
                                       _executor.RoomContext?.RoomIndex == abortedEntry.RoomIndex;
            if (activeSearchRemoved)
            {
                bool authoritativeHoardResolved = _executor.HasAuthoritativeHoardResolution;
                var abortedOutcome = new RoomObjectiveOutcomeResult(
                    abortedEntry.ShouldProbeHoard
                        ? authoritativeHoardResolved ? ObjectiveOutcomeKind.Succeeded : ObjectiveOutcomeKind.Preempted
                        : ObjectiveOutcomeKind.NotRequested,
                    abortedEntry.ShouldSearchChests ? ObjectiveOutcomeKind.Preempted : ObjectiveOutcomeKind.NotRequested,
                    abortedEntry.ShouldVisitForIntel ? ObjectiveOutcomeKind.Preempted : ObjectiveOutcomeKind.NotRequested);
                if (!TryApplyActiveObjectiveOutcomes(abortedEntry.RoomIndex, abortedOutcome, "PassageOpenHoardResolved"))
                    return true;
                RecordReplayEvent("passage-open-active-search-aborted", new
                {
                    floor = dd->Floor,
                    roomIndex = abortedEntry.RoomIndex,
                    executionKind = _searchExecutionKind.ToString(),
                    outcome = ObjectiveOutcomeKind.Preempted.ToString(),
                    hoard = abortedOutcome.Hoard.ToString(),
                    chests = abortedOutcome.Chests.ToString(),
                    intel = abortedOutcome.Intel.ToString(),
                    taskPhase = _taskRunner?.Phase.ToString() ?? TaskPhase.Idle.ToString(),
                    waypointType = (_activeWaypoint ?? _executor.CurrentWaypoint)?.Type.ToString(),
                    reason = "passage-open-hoard-resolved"
                });
            }
            CancelActiveMovement();
            _executor.ClearRoomContext();
            _activeWaypoint = null;
            _searchExecutionKind = SearchExecutionKind.PlannedRoom;
            _ctx.ClearPreferredAggroTarget();
        }

        if (originalRoute.Count != decision.PlannedRoute.Count)
        {
            RecordReplayEvent("passage-open-search-pruned", new
            {
                floor = dd->Floor,
                hoardEvidenceState = _executor.HoardEvidenceState.ToString(),
                hoardWorkResolved = _executor.IsHoardWorkResolved,
                currentRemoved,
                before = originalRoute.Select(entry => entry.RoomIndex).ToArray(),
                after = decision.PlannedRoute.Select(entry => entry.RoomIndex).ToArray(),
                reason = "passage-open-pruned-chest-only-work"
            });
        }

        if (!decision.ShouldExit)
        {
            if (_executor.IsComplete && !decision.RetainVisibleBandedWork)
            {
                _status = $"Passage open, waiting for hoard work ({_executor.HoardEvidenceState})";
                RecordHoardEvidenceWait("passage-open-waiting-hoard-work");
                return true;
            }
            return false;
        }

        CancelActiveMovement();
        ClearPostRoomPomanderRetry();
        _executor.ClearRoomContext();
        _activeWaypoint = null;
        _searchExecutionKind = SearchExecutionKind.PlannedRoom;
        _chaseHelper.Reset();
        _ctx.ClearPreferredAggroTarget();
        _status = "Passage open, hoard work resolved";
        RecordReplayEvent("passage-open-exit-after-hoard-resolved", new
        {
            floor = dd->Floor,
            hoardEvidenceState = _executor.HoardEvidenceState.ToString(),
            reason = "passage-open-hoard-resolved-no-banded-work"
        });
        RecordReplayEvent("floor-active-mechanic-completed", new
        {
            mechanic = "Search",
            reason = "passage-open-hoard-resolved",
            nextObjective = FloorObjectiveKind.EnterPassage.ToString()
        });
        return true;
    }

    private unsafe bool RunGeneralSearchTick(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_executor?.HasPlanningSnapshot == false)
        {
            var normalGraph = this.NormalGraph;
            if (normalGraph == null)
            {
                _status = "Waiting for room graph...";
                return true;
            }

            _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
            if (!_executor.HasPlanningSnapshot)
            {
                _status = "Waiting for floor planning evidence...";
                return true;
            }
        }

        SyncLiveRunOptions(dd, player);
        ObserveHoardCount(dd, "general-search-tick");
        if (!RefreshCachedHoardIndicator(dd))
        {
            _status = "Waiting for hoard indicator evidence...";
            return true;
        }
        ResolveCurrentFloorIntuitionTimeoutIfNeeded(dd);
        TryUseGeneralAutoPomander(dd);
        RegeneratePlanForEvidenceIfIdle(dd, player);

        return UpdatePostRoomPomanderRetry(dd, player);
    }

    private unsafe void BeginPlannedRoomSearch(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        int? targetRoom = _executor!.CurrentTargetRoomIndex;
        if (!targetRoom.HasValue)
            return;
        if (DateTime.UtcNow < SearchState.RoomEvidenceRetryAt)
        {
            _status = $"Waiting for room {targetRoom.Value} evidence...";
            return;
        }

        _searchExecutionKind = SearchExecutionKind.PlannedRoom;
        long requestId = _nextRoomSearchRequestId();
        var objectEvidence = this.ObjectEvidence.Current!;
        int observedRoomIndex = this.LastObservedRoomIndex;
        int currentPlayerRoomIndex = RoomGraph.GetLocalPlayerRoomIndex(dd);
        bool started = _executor.StartCurrentPlanRoomSearch(
            dd,
            objectEvidence,
            player.Position,
            out bool evidenceUnavailable,
            out RoomSearchChestDiagnostic? chestDiagnostic);
        if (chestDiagnostic != null)
        {
            RecordReplayEvent("room-search-chest-diagnostic", new
            {
                requestId,
                snapshotBuilt = started,
                objectEvidence = new
                {
                    objectEvidence.RefreshSequence,
                    objectEvidence.CapturedAtUtc,
                    playerPosition = objectEvidence.PlayerPosition.HasValue
                        ? new
                        {
                            x = objectEvidence.PlayerPosition.Value.X,
                            y = objectEvidence.PlayerPosition.Value.Y,
                            z = objectEvidence.PlayerPosition.Value.Z
                        }
                        : null,
                    observedRoomIndex
                },
                currentPlayerRoomIndex,
                chestDiagnostic
            });
        }
        if (evidenceUnavailable)
        {
            SearchState.RoomEvidenceRetryAt = DateTime.UtcNow.Add(GeneralTickInterval);
            _status = $"Waiting for room {targetRoom.Value} evidence...";
            return;
        }
        SearchState.RoomEvidenceRetryAt = DateTime.MinValue;
        if (!BeginRoomObjectiveExecutions(targetRoom.Value, _executor.CurrentPlanEntry))
        {
            PreemptActiveObjectiveExecutions("ObjectiveStartRejected");
            CancelActiveMovement();
            _executor.ClearRoomContext();
            return;
        }
        if (!started)
        {
            RecordReplayEvent("room-search-start-failed", new
            {
                roomIndex = targetRoom.Value,
                executionKind = _searchExecutionKind.ToString()
            });
            FinalizeRoomSearch(
                dd,
                player,
                targetRoom.Value,
                useRoomFinishPomander: true,
                allowBandedRevealExpectation: true,
                explicitOutcome: BuildTerminalOutcomeSnapshot(_executor.CurrentPlanEntry, ObjectiveOutcomeKind.Failed),
                finalizeReason: "RoomSearchBuildFailed");
            return;
        }

        RecordReplayEvent("room-search-started", new
        {
            roomIndex = targetRoom.Value,
            executionKind = _searchExecutionKind.ToString(),
            remainingWaypointCount = _executor.RoomContext?.RemainingWaypointCount ?? 0,
            blindFallbackUnavailable =
                _executor.RoomContext?.BlindFallbackUnavailable ?? false,
            fallbackCandidates = _executor.RoomContext == null
                ? null
                : new
                {
                    catalog = _executor.RoomContext.FallbackCatalogCandidateCount,
                    palantir = _executor.RoomContext.FallbackPalantirCandidateCount,
                    union = _executor.RoomContext.FallbackUnionCandidateCount
                },
            planEntry = _executor.CurrentPlanEntry.HasValue
                ? new
                {
                    _executor.CurrentPlanEntry.Value.RoomIndex,
                    _executor.CurrentPlanEntry.Value.ShouldProbeHoard,
                    _executor.CurrentPlanEntry.Value.ShouldSearchChests,
                    _executor.CurrentPlanEntry.Value.ShouldVisitForIntel,
                    hoardEvidenceState = _executor.CurrentPlanEntry.Value.HoardEvidenceState.ToString()
                }
                : null,
            waypoints = _executor.RoomContext?.Waypoints.Select(waypoint => new
            {
                type = waypoint.Type.ToString(),
                arrivalRadius = waypoint.ArrivalRadius,
                x = waypoint.Position.X,
                y = waypoint.Position.Y,
                z = waypoint.Position.Z
            }).ToArray() ?? []
        });

        if (!_executor.RoomContext!.HasWaypoints)
        {
            if (_executor.CurrentPlanEntry?.ShouldVisitForIntel == true)
            {
                BeginRoomIntelSettle(_executor.RoomContext.RoomIndex);
                return;
            }

            FinalizeRoomSearch(dd, player, _executor.RoomContext.RoomIndex, useRoomFinishPomander: true, allowBandedRevealExpectation: true);
        }
    }

    private bool BeginRoomObjectiveExecutions(int roomIndex, RoomPlanEntry? entry)
    {
        if (this.IsDisposed)
        {
            _status = "Cannot start room objectives without an active floor runtime";
            Service.Log.Error($"[FloorPhase] {_status}");
            return false;
        }
        var activeExecution = this.ActiveExecution;
        if (activeExecution == null)
        {
            _status = "Cannot start room objectives without an active objective execution";
            Service.Log.Error($"[FloorPhase] {_status}");
            return false;
        }
        if (activeExecution.ObjectiveRecords.Count != 0)
        {
            _status = "Cannot start room objectives while another objective attempt is active";
            Service.Log.Error($"[FloorPhase] {_status}");
            return false;
        }

        if (_searchExecutionKind == SearchExecutionKind.BandedReentry)
        {
            return TryBeginObjectiveExecution(
                activeExecution,
                roomIndex,
                RoomObjectiveCategory.Hoard,
                FloorObjectiveKind.OpenVisibleBandedChest,
                required: true);
        }

        if (entry?.ShouldProbeHoard == true &&
            !TryBeginObjectiveExecution(
                activeExecution,
                roomIndex,
                RoomObjectiveCategory.Hoard,
                entry.Value.HoardEvidenceState == HoardEvidenceState.IntuitionDirect
                    ? FloorObjectiveKind.CompleteKnownHoard
                    : FloorObjectiveKind.DiscoverHoard,
                required: true))
        {
            return false;
        }
        if (entry?.ShouldSearchChests == true &&
            !TryBeginObjectiveExecution(
                activeExecution,
                roomIndex,
                RoomObjectiveCategory.Chests,
                FloorObjectiveKind.OpenPlannedChest,
                required: false))
        {
            return false;
        }
        if (entry?.ShouldVisitForIntel == true &&
            !TryBeginObjectiveExecution(
                activeExecution,
                roomIndex,
                RoomObjectiveCategory.Intel,
                FloorObjectiveKind.DiscoverHoard,
                required: true))
        {
            return false;
        }

        return true;
    }

    private bool TryBeginObjectiveExecution(
        ObjectiveExecution activeExecution,
        int roomIndex,
        RoomObjectiveCategory category,
        FloorObjectiveKind kind,
        bool required)
    {
        var objective = this.Objectives.GetOrCreateObjective(new RoomObjectiveKey(roomIndex, category, kind), kind, required);
        if (objective.Outcome != ObjectiveOutcomeKind.Pending)
        {
            if (objective.Outcome != ObjectiveOutcomeKind.Preempted &&
                (!required || !RoomObjectiveOutcomePlanner.IsRetryableFailure(objective.Outcome)))
            {
                RecordObjectiveExecutionRejected(objective.Identity, kind, category, "Start", ObjectiveRestartStatus.NotRestartable.ToString());
                return false;
            }

            var restart = this.Objectives.ObjectiveLedger.Restart(objective.Identity);
            if (restart.Status != ObjectiveRestartStatus.Restarted)
            {
                RecordObjectiveExecutionRejected(objective.Identity, kind, category, "Restart", restart.Status.ToString());
                return false;
            }
            objective = restart.Objective;
        }

        activeExecution.ObjectiveRecords.Add(new ActiveObjectiveExecution(objective.Identity, kind, required, category));
        this.RunTelemetry?.ObserveObjectiveStart(
            kind,
            category == RoomObjectiveCategory.Intel);
        RecordReplayEvent("objective-execution-started", new
        {
            floorGeneration = objective.Identity.FloorGeneration,
            objectiveId = objective.Identity.ObjectiveId,
            attempt = objective.Identity.Attempt,
            objectiveKind = kind.ToString(),
            category = category.ToString(),
            required,
            roomIndex,
            executionKind = _searchExecutionKind.ToString()
        });
        return true;
    }

    private bool TryApplyActiveObjectiveOutcomes(
        int roomIndex,
        in RoomObjectiveOutcomeResult outcome,
        string reason,
        FloorObjectiveKind? replacementObjective = null)
    {
        var activeExecution = this.ActiveExecution;
        if (this.IsDisposed || activeExecution == null)
        {
            _status = activeExecution == null
                ? "Rejected objective outcome without an active objective execution"
                : "Rejected objective outcome without its active floor runtime";
            RecordReplayEvent("objective-outcome-rejected-no-active-execution", new
            {
                floorGeneration = this.Generation,
                roomIndex,
                reason,
                status = _status
            });
            Service.Log.Error($"[FloorPhase] {_status}");
            return false;
        }
        var objectiveRecords = activeExecution.ObjectiveRecords;
        var searchState = this.SearchState;

        foreach (var execution in objectiveRecords)
        {
            var terminalOutcome = GetCategoryOutcome(execution.Category, outcome);
            var status = this.Objectives.ObjectiveLedger.ValidateOutcome(execution.Identity, terminalOutcome, out _);
            if (status == ObjectiveOutcomeApplyStatus.Accepted)
                continue;

            RecordObjectiveExecutionRejected(execution.Identity, execution.Kind, execution.Category, "Outcome", status.ToString());
            _status = $"Rejected objective outcome {execution.Identity.ObjectiveId}/{execution.Identity.Attempt}: {status}";
            searchState.ObjectiveOutcomeRejected = true;
            Service.Log.Error($"[FloorPhase] {_status}");
            return false;
        }

        foreach (var execution in objectiveRecords)
        {
            var terminalOutcome = GetCategoryOutcome(execution.Category, outcome);
            var result = this.Objectives.ObjectiveLedger.ApplyOutcome(execution.Identity, terminalOutcome);
            RecordReplayEvent(
                terminalOutcome == ObjectiveOutcomeKind.Preempted
                    ? "objective-execution-preempted"
                    : "objective-execution-outcome",
                new
                {
                    floorGeneration = execution.Identity.FloorGeneration,
                    objectiveId = execution.Identity.ObjectiveId,
                    attempt = execution.Identity.Attempt,
                    objectiveKind = execution.Kind.ToString(),
                    category = execution.Category.ToString(),
                    execution.Required,
                    roomIndex,
                    executionKind = _searchExecutionKind.ToString(),
                    outcome = terminalOutcome.ToString(),
                    applyStatus = result.Status.ToString(),
                    failureCount = result.FailureCount,
                    reason,
                    replacementObjective = replacementObjective?.ToString()
                });
        }

        objectiveRecords.Clear();
        return true;
    }

    internal bool PreemptActiveObjectiveExecutions(
        string reason,
        FloorObjectiveKind? replacementObjective = null)
    {
        if (this.ActiveExecution?.ObjectiveRecords.Count is null or 0)
            return true;

        var outcome = new RoomObjectiveOutcomeResult(
            ObjectiveOutcomeKind.Preempted,
            ObjectiveOutcomeKind.Preempted,
            ObjectiveOutcomeKind.Preempted);
        return TryApplyActiveObjectiveOutcomes(
            _executor?.RoomContext?.RoomIndex ?? _executor?.CurrentTargetRoomIndex ?? -1,
            outcome,
            reason,
            replacementObjective);
    }

    private bool StopActiveSearchExecution(FloorObjectiveKind replacementObjective)
    {
        var previousObjective = this.ActiveExecution?.Objective ?? FloorObjectiveKind.None;
        int roomIndex = _executor?.RoomContext?.RoomIndex ?? _executor?.CurrentTargetRoomIndex ?? -1;
        var executionKind = _searchExecutionKind;
        string reason = $"ObjectiveChanged:{replacementObjective}";
        if (!PreemptActiveObjectiveExecutions(reason, replacementObjective))
            return false;

        RecordReplayEvent("room-search-aborted", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            roomIndex,
            executionKind = executionKind.ToString(),
            previousObjective = previousObjective.ToString(),
            replacementObjective = replacementObjective.ToString(),
            outcome = ObjectiveOutcomeKind.Preempted.ToString()
        });

        CancelActiveMovement();
        _executor?.ClearRoomContext();
        ClearRoomIntelSettle();
        _searchExecutionKind = SearchExecutionKind.PlannedRoom;
        return true;
    }

    private void RecordObjectiveExecutionRejected(
        ObjectiveIdentity identity,
        FloorObjectiveKind kind,
        RoomObjectiveCategory category,
        string operation,
        string rejectionStatus)
    {
        RecordReplayEvent("objective-execution-rejected", new
        {
            floorGeneration = identity.FloorGeneration,
            objectiveId = identity.ObjectiveId,
            attempt = identity.Attempt,
            objectiveKind = kind.ToString(),
            category = category.ToString(),
            operation,
            rejectionStatus
        });
    }

    private static ObjectiveOutcomeKind GetCategoryOutcome(
        RoomObjectiveCategory category,
        in RoomObjectiveOutcomeResult outcome)
    {
        return category switch
        {
            RoomObjectiveCategory.Hoard => outcome.Hoard,
            RoomObjectiveCategory.Chests => outcome.Chests,
            RoomObjectiveCategory.Intel => outcome.Intel,
            _ => ObjectiveOutcomeKind.NotRequested
        };
    }

    private enum RoomPresenceDecision
    {
        Continue,
        Reenter,
        GiveUp
    }

    private RoomPresenceDecision EvaluateRoomPresence(RoomSearchContext roomContext, int playerRoom)
    {
        if (!ReferenceEquals(_roomPresenceContext, roomContext))
        {
            _roomPresenceContext = roomContext;
            _roomReentryAbortCount = 0;
            _roomMismatchSinceUtc = DateTime.MinValue;
        }

        if (playerRoom == roomContext.RoomIndex)
        {
            _roomMismatchSinceUtc = DateTime.MinValue;
            return RoomPresenceDecision.Continue;
        }

        if (playerRoom < 0)
            return RoomPresenceDecision.Continue;

        var now = DateTime.UtcNow;
        if (_roomMismatchSinceUtc == DateTime.MinValue)
        {
            _roomMismatchSinceUtc = now;
            return RoomPresenceDecision.Continue;
        }

        if (now - _roomMismatchSinceUtc < RoomMismatchReenterDelay)
            return RoomPresenceDecision.Continue;

        _roomMismatchSinceUtc = DateTime.MinValue;
        _roomReentryAbortCount++;
        return _roomReentryAbortCount > RoomReentryAbortLimit
            ? RoomPresenceDecision.GiveUp
            : RoomPresenceDecision.Reenter;
    }

    private unsafe void AbandonRoomAfterReentryExhaustion(InstanceContentDeepDungeon* dd, IPlayerCharacter player, int roomIndex)
    {
        RecordReplayEvent("room-reentry-exhausted", new
        {
            floor = dd->Floor,
            floorGeneration = this.Generation,
            roomIndex,
            abortCount = _roomReentryAbortCount,
            executionKind = _searchExecutionKind.ToString(),
            taskPhase = _taskRunner?.Phase.ToString() ?? TaskPhase.Idle.ToString(),
            status = _status
        });
        FinalizeRoomSearch(
            dd,
            player,
            roomIndex,
            useRoomFinishPomander: true,
            allowBandedRevealExpectation: _searchExecutionKind == SearchExecutionKind.PlannedRoom,
            explicitOutcome: _searchExecutionKind == SearchExecutionKind.BandedReentry
                ? BuildBandedTerminalOutcome(ObjectiveOutcomeKind.Failed)
                : BuildTerminalOutcomeSnapshot(_executor!.CurrentPlanEntry, ObjectiveOutcomeKind.Failed),
            finalizeReason: "RoomReentryExhausted");
    }

    private unsafe void ContinueRoomSearch(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var roomContext = _executor!.RoomContext;
        if (roomContext == null)
            return;

        int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        switch (EvaluateRoomPresence(roomContext, playerRoom))
        {
            case RoomPresenceDecision.Reenter:
                NavigateToRoom(dd, roomContext.RoomIndex, player);
                _status = $"Re-entering room {roomContext.RoomIndex}";
                return;
            case RoomPresenceDecision.GiveUp:
                AbandonRoomAfterReentryExhaustion(dd, player, roomContext.RoomIndex);
                return;
        }

        var waypoint = _executor.CurrentWaypoint;
        if (waypoint.HasValue)
        {
            ConfigureTaskForWaypoint(waypoint.Value);
            return;
        }

        if (UpdateRoomIntelSettle(dd, player, roomContext.RoomIndex))
            return;

        FinalizeRoomSearch(
            dd,
            player,
            roomContext.RoomIndex,
            useRoomFinishPomander: true,
            allowBandedRevealExpectation: _searchExecutionKind == SearchExecutionKind.PlannedRoom);
    }

    private unsafe void UpdateActiveTask(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_executor!.RoomContext is { } activeRoomContext)
        {
            int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
            switch (EvaluateRoomPresence(activeRoomContext, playerRoom))
            {
                case RoomPresenceDecision.Reenter:
                    _taskRunner!.Reset();
                    _activeWaypoint = null;
                    NavigateToRoom(dd, activeRoomContext.RoomIndex, player);
                    _status = $"Re-entering room {activeRoomContext.RoomIndex}";
                    return;
                case RoomPresenceDecision.GiveUp:
                    AbandonRoomAfterReentryExhaustion(dd, player, activeRoomContext.RoomIndex);
                    return;
            }
        }

        if (TryHandleGoldChestOvercap(dd, player))
            return;
        if (TryHandleSilverChestOvercap(dd, player))
            return;
        if (TryUpdateChestReapproach(dd, player))
            return;

        var taskPhaseBefore = _taskRunner!.Phase;
        double elapsedBefore = _taskRunner.ElapsedSeconds;
        SampleActiveWaypointTelemetry(taskPhaseBefore, player.Position);
        var result = _taskRunner!.Update(player.Position);
        var taskPhaseAfter = _taskRunner.Phase;
        UpdateTaskStatus();

        switch (result)
        {
            case TaskResult.Arrived:
                if (_activeWaypoint.HasValue && IsChestWaypoint(_activeWaypoint.Value) &&
                    !FsdChestInteraction.HasInteraction(ActiveChestAttempt) &&
                    this.ObjectEvidence.Current is { } arrivalEvidence &&
                    _ctx?.ChestInteraction.IsTargetMissing(ActiveChestAttempt, arrivalEvidence) == true)
                {
                    _taskRunner.Reset();
                    HandleTaskSkip(dd, player, "TargetLost", WaypointOutcomeKind.Deferred, elapsedBefore, taskPhaseBefore);
                }
                else
                {
                    HandleTaskArrived(dd, taskPhaseAfter);
                }
                break;
            case TaskResult.Complete:
                HandleTaskComplete(dd, player, elapsedBefore, taskPhaseBefore);
                break;
            case TaskResult.TimedOut:
                Service.Log.Warning("[FloorPhase] Task timed out, skipping");
                HandleTaskSkip(dd, player, "TimedOut", WaypointOutcomeKind.Failed, elapsedBefore, taskPhaseBefore);
                break;
            case TaskResult.NavigationFailed:
                Service.Log.Warning("[FloorPhase] Waypoint navigation failed, skipping");
                HandleTaskSkip(dd, player, "NavigationFailed", WaypointOutcomeKind.Failed, elapsedBefore, taskPhaseBefore);
                break;
        }
    }

    private unsafe bool TryUpdateChestReapproach(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var attempt = ActiveChestAttempt;
        if (attempt == null ||
            _activeWaypoint is not { } waypoint ||
            !IsChestWaypoint(waypoint) ||
            _taskRunner?.Phase != TaskPhase.WaitingPost ||
            _ctx?.ChestInteraction == null)
        {
            return false;
        }

        if (_taskRunner.ElapsedSeconds >= _ctx.ChestInteraction.GetOpenTimeoutSeconds(waypoint))
        {
            if (attempt.Reapproaching)
                FinishChestReapproach(attempt, waypoint, false, "interaction-window-expired");
            return false;
        }

        if (!_ctx.ChestInteraction.TryBeginOrContinueReapproach(
                attempt,
                player.Position,
                out bool started))
        {
            return false;
        }

        if (started)
        {
            this.RunTelemetry?.ObserveChestRecoveryStarted();
            RecordReplayEvent("chest-recovery-started", new
            {
                floor = dd->Floor,
                roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
                waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
                chestType = waypoint.Type.ToString(),
                entityId = attempt.EntityId,
                reason = "outside-interaction-range"
            });
        }

        var state = _navHelper!.Navigate(waypoint.Position, player.Position, _ctx!.ChestInteraction.GetInteractionRange());
        switch (state)
        {
            case NavigationState.Moving:
                _status = $"Returning to {DescribeChest(waypoint.Type)} interaction range";
                return true;
            case NavigationState.StuckRepathing:
                _status = $"Reapproaching {DescribeChest(waypoint.Type)} ({_navHelper.StuckRetryCount}/3)";
                return true;
            case NavigationState.Arrived:
                FinishChestReapproach(attempt, waypoint, true, "returned-to-chest");
                return false;
            case NavigationState.StuckGiveUp:
                _status = $"Retrying route to {DescribeChest(waypoint.Type)}";
                return true;
            case NavigationState.Failed:
                _status = $"Waiting to retry route to {DescribeChest(waypoint.Type)}";
                return true;
            default:
                return true;
        }
    }

    private void FinishChestReapproach(
        ChestInteractionAttempt attempt,
        RoomWaypoint waypoint,
        bool succeeded,
        string reason)
    {
        _ctx?.ChestInteraction.FinishReapproach(attempt);
        _navHelper?.Cancel();
        RecordReplayEvent(succeeded ? "chest-recovery-completed" : "chest-recovery-failed", new
        {
            roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
            waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
            chestType = waypoint.Type.ToString(),
            entityId = attempt.EntityId,
            reason
        });
    }

    private unsafe bool TryHandleGoldChestOvercap(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var chestAttempt = ActiveChestAttempt;
        if (chestAttempt == null ||
            _activeWaypoint == null ||
            _activeWaypoint.Value.Type != RoomObjectiveType.ChestGold ||
            chestAttempt.EntityId == 0 ||
            !chestAttempt.PendingGoldOvercapSlotIndex.HasValue)
        {
            return false;
        }
        uint slotIndex = chestAttempt.PendingGoldOvercapSlotIndex.Value;
        if (this.ItemUse.Pending is
            {
                Purpose: FloorItemUsePurpose.GoldChestOvercap,
                Key:
                {
                    Kind: FloorItemUseKind.Pomander,
                    ItemId: var pendingSlotIndex
                }
            } &&
            pendingSlotIndex == slotIndex)
        {
            return true;
        }

        if (ShouldUseGoldChestOvercapPomander(slotIndex))
        {
            if (!CanAttemptPomanderUse()) return true;
            if (CanAttemptPomanderUse() &&
                TryUsePomander(
                    slotIndex,
                    dd,
                    $"gold overcap relief ({DescribePomanderSlot(slotIndex)})",
                    FloorItemUsePurpose.GoldChestOvercap))
            {
                return true;
            }

            return false;
        }

        Service.Log.Info($"[FloorPhase] Gold chest overcap on {DescribePomanderSlot(slotIndex)}, skipping chest");
        chestAttempt.PendingGoldOvercapSlotIndex = null;
        var taskPhaseBefore = _taskRunner!.Phase;
        double elapsedBefore = _taskRunner.ElapsedSeconds;
        _taskRunner!.Reset();
        HandleTaskSkip(dd, player, "GoldChestOvercapSkip", WaypointOutcomeKind.PolicySkipped, elapsedBefore, taskPhaseBefore);
        return true;
    }

    internal void HandleGoldChestOvercapObserved(uint? slotIndex)
    {
        var attempt = CurrentInteractionAttempt;
        if (!slotIndex.HasValue ||
            this.IsDisposed ||
            attempt == null ||
            attempt.EntityId == 0 ||
            attempt.Waypoint.Type != RoomObjectiveType.ChestGold)
        {
            RecordReplayEvent("gold-chest-overcap-rejected", new
            {
                floor = this.Floor,
                floorGeneration = this.Generation,
                slotIndex,
                reason = "no-active-gold-interaction-attempt"
            });
            return;
        }

        attempt.PendingGoldOvercapSlotIndex = slotIndex.Value;
        RecordReplayEvent("gold-chest-overcap-correlated", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            // The real work's waypoint — the legacy executor's room/context
            // does not identify coord-owned chests.
            waypoint = attempt.Waypoint.Position,
            entityId = attempt.EntityId,
            slotIndex = slotIndex.Value
        });
    }

    internal unsafe void HandleSilverChestOvercapObserved(uint? stoneRowId)
    {
        var efw = EventFramework.Instance();
        var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
        var attempt = CurrentInteractionAttempt;
        if (dd == null || dd->DeepDungeonId is not (2 or 4) ||
            IsDisposed || this.Floor != dd->Floor ||
            attempt == null || attempt.EntityId == 0 ||
            attempt.Waypoint.Type != RoomObjectiveType.ChestSilver ||
            // Legacy direct-controller gate only — coord validates the
            // actual dispatched attempt above (EntityId != 0).
            (!CoordOwnsWork && _taskRunner?.Phase != TaskPhase.WaitingPost) ||
            !stoneRowId.HasValue)
            return;
        attempt.PendingSilverOvercapStoneRowId = stoneRowId;
        RecordReplayEvent("silver-chest-overcap-correlated", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            entityId = attempt.EntityId,
            stoneRowId
        });
    }

    private unsafe bool TryHandleSilverChestOvercap(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var attempt = ActiveChestAttempt;
        if (attempt?.PendingSilverOvercapStoneRowId == null ||
            _activeWaypoint?.Type != RoomObjectiveType.ChestSilver)
            return false;
        var decision = ResolveSilverChestOvercap(attempt, dd);
        if (decision == Coord.ChestOvercapProgress.Ready) return false;
        if (decision == Coord.ChestOvercapProgress.Waiting) return true;
        Service.Log.Info("[FloorPhase] Silver stone overcap cannot be relieved, skipping chest");
        attempt.PendingSilverOvercapStoneRowId = null;
        var phase = _taskRunner!.Phase;
        double elapsed = _taskRunner.ElapsedSeconds;
        _taskRunner.Reset();
        HandleTaskSkip(dd, player, "SilverChestOvercapSkip", WaypointOutcomeKind.PolicySkipped, elapsed, phase);
        return true;
    }

    /// <summary>Coord-work overcap relief: the same item policies as the
    /// legacy task path, evaluated against the op's real attempt. Returns
    /// Waiting whenever relief is pending or in progress — the chest never
    /// dispatches in parallel; PolicySkipped is the existing skip decision
    /// (recorded by the op, not completed).</summary>
    private unsafe Coord.ChestOvercapProgress ResolveCoordChestOvercap(
        ChestInteractionAttempt attempt)
    {
        if (this.ItemUse.DispatchedThisUpdate)
            return Coord.ChestOvercapProgress.Waiting;
        if (attempt.PendingGoldOvercapSlotIndex is { } slotIndex)
        {
            if (this.ItemUse.Pending is
                {
                    Purpose: FloorItemUsePurpose.GoldChestOvercap,
                    Key: { Kind: FloorItemUseKind.Pomander, ItemId: var pendingSlot }
                } && pendingSlot == slotIndex)
                return Coord.ChestOvercapProgress.Waiting;
            if (!ShouldUseGoldChestOvercapPomander(slotIndex))
            {
                attempt.PendingGoldOvercapSlotIndex = null;
                return Coord.ChestOvercapProgress.PolicySkipped;
            }
            if (!CanAttemptPomanderUse()) return Coord.ChestOvercapProgress.Waiting;
            if (CanAttemptPomanderUse() &&
                TryUsePomander(slotIndex, _scopeDd,
                    $"gold overcap relief ({DescribePomanderSlot(slotIndex)})",
                    FloorItemUsePurpose.GoldChestOvercap))
            {
                _coordOvercapRequest = this.ItemUse.Pending;
                _coordOvercapAttempt = attempt;
            }
            // Permitted but channel busy, or just dispatched — either way
            // no parallel chest interaction while relief is outstanding.
            return Coord.ChestOvercapProgress.Waiting;
        }
        if (attempt.PendingSilverOvercapStoneRowId.HasValue)
            return ResolveSilverChestOvercap(attempt, _scopeDd);
        return Coord.ChestOvercapProgress.Ready;
    }

    private void ConfigureTaskForWaypoint(RoomWaypoint waypoint)
    {
        _activeWaypoint = waypoint;
        if (IsChestWaypoint(waypoint))
            this.ActiveExecution!.ChestAttempt = new ChestInteractionAttempt(waypoint);
        RecordWaypointStarted(waypoint);
        StartActiveWaypointTelemetry(waypoint);

        switch (waypoint.Type)
        {
            case RoomObjectiveType.Trap:
                float trapArrivalRadius = waypoint.HasExplicitArrivalRadius
                    ? waypoint.ArrivalRadius
                    : 1.4f;
                _taskRunner!.Configure(
                    waypoint.Position, trapArrivalRadius,
                    preCondition: null, preTimeoutSeconds: 0,
                    postCondition: elapsed => elapsed >= TrapStandDurationSeconds,
                    postTimeoutSeconds: TrapStandDurationSeconds + 1);
                break;

            case RoomObjectiveType.ChestBanded:
                _chatWatchers?.ExpectHoardCofferFound(this.Floor);
                _taskRunner!.Configure(
                    waypoint.Position, 1.5f,
                    preCondition: null, preTimeoutSeconds: 0,
                    postCondition: _ => IsChestAccepted(waypoint),
                    postTimeoutSeconds: _ctx!.ChestInteraction.GetOpenTimeoutSeconds(waypoint));
                break;

            case RoomObjectiveType.ChestSilver:
                _taskRunner!.Configure(
                    waypoint.Position, 1.5f,
                    preCondition: () => _ctx?.ChestInteraction.CanStart(ActiveChestAttempt, waypoint) == true,
                    preTimeoutSeconds: SilverWaitTimeoutSeconds,
                    postCondition: _ => IsChestAccepted(waypoint),
                    postTimeoutSeconds: _ctx!.ChestInteraction.GetOpenTimeoutSeconds(waypoint));
                break;

            default:
                _taskRunner!.Configure(
                    waypoint.Position, 1.5f,
                    preCondition: null, preTimeoutSeconds: 0,
                    postCondition: _ => IsChestAccepted(waypoint),
                    postTimeoutSeconds: _ctx!.ChestInteraction.GetOpenTimeoutSeconds(waypoint));
                break;
        }
    }

    private unsafe void HandleTaskArrived(InstanceContentDeepDungeon* dd, TaskPhase taskPhaseAfter)
    {
        if (_activeWaypoint == null)
            return;

        var waypoint = _activeWaypoint.Value;
        int roomIndex = _executor?.RoomContext?.RoomIndex ?? -1;
        int waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1;
        RecordReplayEvent("waypoint-arrived", new
        {
            roomIndex,
            waypointIndex,
            executionKind = _searchExecutionKind.ToString(),
            objectiveType = waypoint.Type.ToString(),
            nextTaskPhase = taskPhaseAfter.ToString(),
            x = waypoint.Position.X,
            y = waypoint.Position.Y,
            z = waypoint.Position.Z
        });

        if (waypoint.Type == RoomObjectiveType.Trap)
        {
            SearchState.LastTrapTriggered = new TrapObservation(
                DateTime.UtcNow,
                roomIndex,
                waypointIndex,
                _searchExecutionKind,
                waypoint.Position);
            RecordReplayEvent("trap-triggered", new
            {
                roomIndex,
                waypointIndex,
                executionKind = _searchExecutionKind.ToString(),
                x = waypoint.Position.X,
                y = waypoint.Position.Y,
                z = waypoint.Position.Z
            });
        }
        else
        {
            this.ObjectEvidence.Invalidate();
        }
    }

    private unsafe void HandleTaskComplete(InstanceContentDeepDungeon* dd, IPlayerCharacter player, double elapsedSeconds, TaskPhase taskPhaseBefore)
    {
        if (_activeWaypoint == null)
            return;

        var completedWaypoint = _activeWaypoint.Value;
        if (completedWaypoint.Type == RoomObjectiveType.ChestBanded)
            _chatWatchers?.CancelExpectedHoardCofferFound();

        RecordWaypointResolved(completedWaypoint, WaypointOutcomeKind.Completed, "Completed", elapsedSeconds, taskPhaseBefore);
        EndActiveWaypointTelemetry(RunWaypointTerminalOutcome.Completed, "Completed", taskPhaseBefore);
        _executor!.RoomContext?.RecordWaypointOutcome(completedWaypoint, WaypointOutcomeKind.Completed, "Completed");
        _executor!.AdvanceWaypoint();
        _activeWaypoint = null;

        FinishFinalWaypoint(dd, player);
    }

    private unsafe void HandleTaskSkip(
        InstanceContentDeepDungeon* dd,
        IPlayerCharacter player,
        string reason,
        WaypointOutcomeKind outcome,
        double elapsedSeconds,
        TaskPhase taskPhaseBefore)
    {
        if (_activeWaypoint != null)
        {
            if (_activeWaypoint.Value.Type == RoomObjectiveType.ChestBanded)
                _chatWatchers?.CancelExpectedHoardCofferFound();
            RecordWaypointResolved(_activeWaypoint.Value, outcome, reason, elapsedSeconds, taskPhaseBefore);
            EndActiveWaypointTelemetry(
                reason == "NavigationFailed"
                    ? RunWaypointTerminalOutcome.NavigationFailed
                    : RunWaypointTerminalOutcome.Skipped,
                reason,
                taskPhaseBefore);
            _executor!.RoomContext?.RecordWaypointOutcome(_activeWaypoint.Value, outcome, reason);
        }

        _executor!.AdvanceWaypoint();
        _activeWaypoint = null;

        FinishFinalWaypoint(dd, player);
    }

    private unsafe void FinishFinalWaypoint(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_executor!.CurrentWaypoint != null || _executor.RoomContext == null)
            return;

        if (_searchExecutionKind == SearchExecutionKind.PlannedRoom &&
            _executor.CurrentPlanEntry?.ShouldVisitForIntel == true)
        {
            BeginRoomIntelSettle(_executor.RoomContext.RoomIndex);
            return;
        }

        FinalizeRoomSearch(
            dd,
            player,
            _executor.RoomContext.RoomIndex,
            useRoomFinishPomander: true,
            allowBandedRevealExpectation: _searchExecutionKind == SearchExecutionKind.PlannedRoom);
    }

    private unsafe void FinalizeRoomSearch(
        InstanceContentDeepDungeon* dd,
        IPlayerCharacter player,
        int roomIndex,
        bool useRoomFinishPomander,
        bool allowBandedRevealExpectation,
        RoomObjectiveOutcomeSnapshot? explicitOutcome = null,
        string finalizeReason = "RoomExecutionFinished")
    {
        if (!RefreshCachedHoardIndicator(dd))
        {
            _status = "Waiting for hoard indicator evidence...";
            return;
        }

        var completedEntry = _executor!.CurrentPlanEntry;
        if (_searchExecutionKind == SearchExecutionKind.BandedReentry)
        {
            completedEntry = new RoomPlanEntry(
                roomIndex,
                shouldProbeHoard: true,
                shouldSearchChests: false,
                shouldVisitForIntel: false,
                HoardEvidenceState.IntuitionDirect);
        }

        bool completedRoomWasHoardSearch =
            completedEntry.HasValue &&
            completedEntry.Value.RoomIndex == roomIndex &&
            completedEntry.Value.ShouldSearchHoard;

        bool authoritativeHoardResolved = _executor.HasOpenedHoardThisFloor || dd->HoardCount > PlanningState.LastKnownHoardCount;
        var roomContext = _executor.RoomContext;
        var outcomeSnapshot = explicitOutcome ?? roomContext?.BuildOutcomeSnapshot(authoritativeHoardResolved)
            ?? BuildTerminalOutcomeSnapshot(completedEntry, ObjectiveOutcomeKind.Deferred);
        var objectiveOutcome = RoomObjectiveOutcomePlanner.Decide(outcomeSnapshot);
        objectiveOutcome = RoomObjectiveOutcomePlanner.RequireAuthoritativeDirectHoard(
            objectiveOutcome,
            completedEntry?.HoardEvidenceState == HoardEvidenceState.IntuitionDirect,
            authoritativeHoardResolved);
        objectiveOutcome = ApplyRoomObjectiveRetryPolicy(roomIndex, completedEntry, objectiveOutcome);
        string outcomeReason = finalizeReason == "RoomExecutionFinished" && !string.IsNullOrEmpty(roomContext?.LastOutcomeReason)
            ? roomContext.LastOutcomeReason
            : finalizeReason;
        if (!TryApplyActiveObjectiveOutcomes(roomIndex, objectiveOutcome, outcomeReason))
            return;

        _taskRunner!.Reset();
        _activeWaypoint = null;
        ClearRoomIntelSettle();
        _executor.ClearRoomContext();
        ObserveHoardCount(dd, "room-search-finalized");
        RecordReplayEvent("room-objective-outcome", new
        {
            roomIndex,
            executionKind = _searchExecutionKind.ToString(),
            reason = outcomeReason,
            hoard = objectiveOutcome.Hoard.ToString(),
            chests = objectiveOutcome.Chests.ToString(),
            intel = objectiveOutcome.Intel.ToString(),
            markHoardSearched = objectiveOutcome.MarkHoardSearched,
            markChestsSearched = objectiveOutcome.MarkChestsSearched,
            markIntelVisited = objectiveOutcome.MarkIntelVisited
        });

        var pomanderOutcome = RoomFinishPomanderOutcome.NotNeeded;
        if (useRoomFinishPomander)
        {
            pomanderOutcome = ProcessRoomFinishPomander(dd);
        }

        var normalGraph = this.NormalGraph;
        if (normalGraph == null)
        {
            _status = "Waiting for room graph...";
            return;
        }

        _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
        RecordReplayEvent("room-search-finished", new
        {
            roomIndex,
            executionKind = _searchExecutionKind.ToString(),
            useRoomFinishPomander,
            allowBandedRevealExpectation,
            pomanderOutcome = pomanderOutcome.ToString(),
            remainingPlanCount = _executor.PlannedRouteCount,
            completedEntry = completedEntry.HasValue
                ? new
                {
                    completedEntry.Value.RoomIndex,
                    completedEntry.Value.ShouldProbeHoard,
                    completedEntry.Value.ShouldSearchChests,
                    completedEntry.Value.ShouldVisitForIntel,
                    hoardEvidenceState = completedEntry.Value.HoardEvidenceState.ToString()
                }
                : null
        });
        _searchExecutionKind = SearchExecutionKind.PlannedRoom;

        if (allowBandedRevealExpectation &&
            completedRoomWasHoardSearch &&
            objectiveOutcome.Hoard == ObjectiveOutcomeKind.Succeeded &&
            _executor.ConfigSnapshot.BandedEnabled &&
            !_executor.HasOpenedHoardThisFloor)
        {
            StartBandedRevealExpectation(roomIndex);
        }

        if (pomanderOutcome == RoomFinishPomanderOutcome.PendingRetry)
            StartPostRoomPomanderRetry(roomIndex, _executor.IsComplete);
        else
            ClearPostRoomPomanderRetry();

        TryCompleteSearchExecution();
    }

    private static RoomObjectiveOutcomeSnapshot BuildTerminalOutcomeSnapshot(RoomPlanEntry? entry, ObjectiveOutcomeKind outcome)
    {
        bool shouldProbeHoard = entry?.ShouldProbeHoard == true;
        bool shouldSearchChests = entry?.ShouldSearchChests == true;
        bool shouldVisitForIntel = entry?.ShouldVisitForIntel == true;
        return new RoomObjectiveOutcomeSnapshot(
            BuildTerminalCategory(shouldProbeHoard, outcome),
            BuildTerminalCategory(shouldSearchChests, outcome),
            BuildTerminalCategory(shouldVisitForIntel, outcome));
    }

    private static RoomObjectiveOutcomeSnapshot BuildBandedTerminalOutcome(ObjectiveOutcomeKind outcome)
    {
        return new RoomObjectiveOutcomeSnapshot(
            BuildTerminalCategory(true, outcome),
            BuildTerminalCategory(false, outcome),
            BuildTerminalCategory(false, outcome));
    }

    private static ObjectiveCategoryProgress BuildTerminalCategory(bool requested, ObjectiveOutcomeKind outcome)
    {
        return new ObjectiveCategoryProgress(
            requested,
            0,
            0,
            0,
            false,
            requested && outcome == ObjectiveOutcomeKind.Failed,
            requested && outcome == ObjectiveOutcomeKind.Deferred,
            requested && outcome == ObjectiveOutcomeKind.Preempted);
    }

    private RoomObjectiveOutcomeResult ApplyRoomObjectiveRetryPolicy(
        int roomIndex,
        RoomPlanEntry? entry,
        RoomObjectiveOutcomeResult outcome)
    {
        var mandatoryExecution = FindFailedMandatoryExecution(outcome);
        int previousFailures = 0;
        if (mandatoryExecution.HasValue &&
            this.Objectives.ObjectiveLedger.TryGetObjective(mandatoryExecution.Value.Identity.ObjectiveId, out var mandatoryObjective) == true)
        {
            previousFailures = mandatoryObjective.FailureCount;
        }
        var decision = RoomObjectiveOutcomePlanner.DecideRetry(new RoomObjectiveRetrySnapshot(
            HoardRequested: entry?.ShouldProbeHoard == true,
            HoardOutcome: outcome.Hoard,
            IntelRequested: entry?.ShouldVisitForIntel == true,
            IntelOutcome: outcome.Intel,
            ChestsRequested: entry?.ShouldSearchChests == true,
            ChestsOutcome: outcome.Chests,
            PreviousMandatoryFailureCount: previousFailures));

        if (decision.SkipOptionalChests)
            outcome = outcome with { Chests = ObjectiveOutcomeKind.Skipped };

        if (decision.BlockMandatory)
        {
            if (!mandatoryExecution.HasValue)
                throw new InvalidOperationException("Mandatory retry policy blocked without an active mandatory objective identity.");
            SearchState.MandatoryObjectiveBlocked = true;
            SearchState.MandatoryObjectiveBlockedRoom = roomIndex;
            SearchState.MandatoryObjectiveBlockedIdentity = mandatoryExecution.Value.Identity;
            SearchState.MandatoryObjectiveBlockedCategory = mandatoryExecution.Value.Category;
            SearchState.MandatoryObjectiveBlockedKind = mandatoryExecution.Value.Kind;
            SearchState.MandatoryObjectiveBlockedEvidenceVersion = PlanningState.PendingEvidenceVersion;
            SearchState.ObjectiveRetryNotBefore = DateTime.MinValue;
            _status = BuildMandatoryObjectiveBlockedStatus();
            Service.Log.Error($"[FloorPhase] {_status}");
            RecordReplayEvent("mandatory-room-objective-blocked", new
            {
                roomIndex,
                failureCount = decision.MandatoryFailureCount,
                failureLimit = RoomObjectiveOutcomePlanner.MandatoryFailureLimit,
                retryBackoffMilliseconds = RoomObjectiveOutcomePlanner.RetryBackoffMilliseconds,
                hoard = outcome.Hoard.ToString(),
                intel = outcome.Intel.ToString()
            });
        }
        else if (decision.RetryMandatory)
        {
            SearchState.ObjectiveRetryNotBefore = DateTime.UtcNow.AddMilliseconds(RoomObjectiveOutcomePlanner.RetryBackoffMilliseconds);
            _status = $"Mandatory hoard work failed in room {roomIndex}; retry {decision.MandatoryFailureCount + 1}/{RoomObjectiveOutcomePlanner.MandatoryFailureLimit} in {RoomObjectiveOutcomePlanner.RetryBackoffMilliseconds / 1000.0:F1}s";
        }

        return outcome;
    }

    private ActiveObjectiveExecution? FindFailedMandatoryExecution(in RoomObjectiveOutcomeResult outcome)
    {
        var objectiveRecords = this.ActiveExecution?.ObjectiveRecords;
        if (objectiveRecords == null)
            return null;

        foreach (var execution in objectiveRecords)
        {
            if (execution.Required && RoomObjectiveOutcomePlanner.IsRetryableFailure(GetCategoryOutcome(execution.Category, outcome)))
                return execution;
        }

        return null;
    }

    private bool ShouldPauseForObjectiveRetry()
    {
        if (SearchState.MandatoryObjectiveBlocked)
        {
            bool newEvidence = PlanningState.PendingEvidenceVersion > SearchState.MandatoryObjectiveBlockedEvidenceVersion;
            if ((_executor?.HasAuthoritativeHoardResolution ?? false) || newEvidence)
            {
                if (SearchState.MandatoryObjectiveBlockedIdentity.HasValue &&
                    this != null &&
                    _executor?.HasAuthoritativeHoardResolution != true)
                {
                    if (!this.Objectives.ObjectiveLedger.ResetFailureCount(SearchState.MandatoryObjectiveBlockedIdentity.Value))
                    {
                        RecordObjectiveExecutionRejected(
                            SearchState.MandatoryObjectiveBlockedIdentity.Value,
                            SearchState.MandatoryObjectiveBlockedKind,
                            SearchState.MandatoryObjectiveBlockedCategory,
                            "EvidenceFailureReset",
                            "StaleIdentity");
                        _status = "Blocked mandatory objective failure count could not reset: stale identity";
                        return true;
                    }
                }
                SearchState.MandatoryObjectiveBlocked = false;
                SearchState.MandatoryObjectiveBlockedRoom = -1;
                SearchState.MandatoryObjectiveBlockedIdentity = null;
                SearchState.MandatoryObjectiveBlockedCategory = default;
                SearchState.MandatoryObjectiveBlockedKind = FloorObjectiveKind.None;
                SearchState.MandatoryObjectiveBlockedEvidenceVersion = 0;
                _status = newEvidence
                    ? "New hoard evidence received; resuming mandatory work"
                    : "Mandatory hoard work resolved; resuming floor flow";
                return false;
            }

            _status = BuildMandatoryObjectiveBlockedStatus();
            return true;
        }

        if (DateTime.UtcNow < SearchState.ObjectiveRetryNotBefore)
        {
            double remaining = Math.Max(0, (SearchState.ObjectiveRetryNotBefore - DateTime.UtcNow).TotalSeconds);
            _status = $"Waiting {remaining:F1}s before retrying mandatory hoard work";
            return true;
        }

        SearchState.ObjectiveRetryNotBefore = DateTime.MinValue;
        return false;
    }

    private string BuildMandatoryObjectiveBlockedStatus()
    {
        return $"Blocked: mandatory hoard work in room {SearchState.MandatoryObjectiveBlockedRoom} failed {RoomObjectiveOutcomePlanner.MandatoryFailureLimit} times; waiting for new evidence or manual intervention";
    }

    private unsafe void HandleVisibleBandedDetection(InstanceContentDeepDungeon* dd, IPlayerCharacter player, int roomIndex, Vector3 bandedPosition)
    {
        var executor = _executor;
        if (executor == null)
            return;
        ClearPostRoomPomanderRetry();
        if (!RefreshCachedHoardIndicator(dd))
        {
            _status = "Waiting for hoard indicator evidence...";
            return;
        }

        _taskRunner!.Reset();
        _activeWaypoint = null;
        if (!PreemptActiveObjectiveExecutions("BandedReentry"))
            return;
        this.ActiveExecution!.Objective = FloorObjectiveKind.OpenVisibleBandedChest;
        _searchExecutionKind = SearchExecutionKind.BandedReentry;

        bool started = roomIndex >= 0 && executor.StartBandedOnlyRoomSearch(dd, roomIndex, player.Position, bandedPosition);
        if (roomIndex >= 0 && !BeginRoomObjectiveExecutions(roomIndex, null))
            return;
        if (started)
        {
            _status = "Banded detected, opening it";
            RecordReplayEvent("room-search-switched-to-banded", new
            {
                roomIndex,
                executionKind = _searchExecutionKind.ToString(),
                x = bandedPosition.X,
                y = bandedPosition.Y,
                z = bandedPosition.Z
            });
        }
        else if (roomIndex >= 0)
        {
            FinalizeRoomSearch(
                dd,
                player,
                roomIndex,
                useRoomFinishPomander: true,
                allowBandedRevealExpectation: false,
                explicitOutcome: BuildBandedTerminalOutcome(ObjectiveOutcomeKind.Failed),
                finalizeReason: "BandedRoomSearchBuildFailed");
        }
    }

    private unsafe bool TryCompleteSearchExecution()
    {
        var executor = _executor!;
        if (executor.IsComplete && executor.IsHoardEvidenceUnstable)
        {
            _status = $"Waiting for hoard evidence ({executor.HoardEvidenceState})";
            RecordHoardEvidenceWait("search-waiting-hoard-evidence");
            return false;
        }
        EndHoardEvidenceWait("search-wait-ended", "search-waiting-hoard-evidence");

        if (!executor.IsComplete)
            return false;

        _taskRunner!.Reset();
        _navDriver!.Cancel();
        _activeWaypoint = null;
        _searchExecutionKind = SearchExecutionKind.PlannedRoom;
        _chaseHelper.Reset();
        ResetPatrolPlan();
        _status = _ctx!.Duty.PassageOpen
            ? "Search complete, passage ready"
            : "Search complete, activating passage";
        RecordReplayEvent("floor-active-mechanic-completed", new
        {
            mechanic = "Search",
            reason = "search-complete",
            nextObjective = (_ctx.Duty.PassageOpen
                ? FloorObjectiveKind.EnterPassage
                : FloorObjectiveKind.ActivatePassage).ToString()
        });
        return true;
    }

    private unsafe void RegeneratePlanForEvidenceIfIdle(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_executor == null ||
            !PlanningState.RefreshRequested ||
            _taskRunner?.Phase != TaskPhase.Idle ||
            _executor.RoomContext != null)
        {
            return;
        }

        var normalGraph = this.NormalGraph;
        if (normalGraph == null)
        {
            return;
        }

        long evidenceVersion = PlanningState.PendingEvidenceVersion;
        _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
        MarkPlanRefreshConsumed(evidenceVersion);
        RecordReplayEvent("floor-plan-regenerated-evidence", BuildPlanReplayPayload(dd->Floor, "search-idle-evidence-refresh"));
    }

    private void UpdateTaskStatus()
    {
        if (_activeWaypoint == null) return;
        var wp = _activeWaypoint.Value;

        switch (_taskRunner!.Phase)
        {
            case TaskPhase.Traveling:
                if (_navHelper!.StuckRetryCount > 0)
                    _status = $"Stuck, repathing ({_navHelper.StuckRetryCount}/3)";
                else if (wp.Type == RoomObjectiveType.Trap)
                    _status = $"Navigating to trap ({_executor!.RemainingWaypointCount} remaining)";
                else
                    _status = $"Navigating to {DescribeChest(wp.Type)}";
                break;

            case TaskPhase.WaitingPost:
                if (wp.Type == RoomObjectiveType.Trap)
                {
                    double remaining = Math.Max(0, TrapStandDurationSeconds - _taskRunner.ElapsedSeconds);
                    _status = $"Standing to reveal ({remaining:F1}s)";
                }
                else
                {
                    _status = $"Opening {DescribeChest(wp.Type)}... ({_taskRunner.ElapsedSeconds:F1}s)";
                }
                break;

            case TaskPhase.WaitingPre:
                {
                    var p = Service.LocalPlayer;
                    if (p != null)
                    {
                        float hpPct = (float)p.CurrentHp / Math.Max(1u, p.MaxHp);
                        double remaining = Math.Max(0, SilverWaitTimeoutSeconds - _taskRunner.ElapsedSeconds);
                        _status = $"Waiting for HP ({hpPct * 100:F0}%, {remaining:F0}s)";
                    }
                    break;
                }
        }
    }

    private static bool IsChestWaypoint(RoomWaypoint waypoint)
    {
        return waypoint.Type != RoomObjectiveType.Trap;
    }

    private void RecordChestInteractionStarted(
        RoomWaypoint waypoint,
        ChestLifecycleSnapshot snapshot,
        bool retry)
    {
        var chest = snapshot.Evidence;
        RecordReplayEvent("chest-interaction-started", new
        {
            roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
            waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
            executionKind = _searchExecutionKind.ToString(),
            chestType = waypoint.Type.ToString(),
            entityId = snapshot.ExpectedEntityId,
            interactionStartedAtUtc = snapshot.InteractionStartedAtUtc,
            completionSource = DescribeNativeChestCompletionSource(chest.NativeCompletionKind),
            nativeStateAvailable = chest.NativeStateAvailable,
            nativeCompletionKind = chest.NativeCompletionKind.ToString(),
            nativeIsTargetable = chest.Object.IsTargetable,
            nativeTreasureState = chest.State.ToString(),
            nativeTreasureFlags = chest.Flags.ToString(),
            evidenceSequenceAtStart = snapshot.EvidenceSequenceAtStart,
            retry,
            x = waypoint.Position.X,
            y = waypoint.Position.Y,
            z = waypoint.Position.Z
        });
    }

    private bool IsChestAccepted(RoomWaypoint waypoint)
    {
        if (_ctx?.ChestInteraction.IsAccepted(
                ActiveChestAttempt,
                waypoint,
                this.ObjectEvidence.Current,
                out var snapshot,
                out bool newlyAccepted) != true)
            return false;

        if (newlyAccepted)
        {
            var chest = snapshot.Evidence;
            RecordReplayEvent("chest-native-state-accepted", new
            {
                roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
                waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
                executionKind = _searchExecutionKind.ToString(),
                chestType = waypoint.Type.ToString(),
                completionSource = DescribeNativeChestCompletionSource(chest.NativeCompletionKind),
                expectedEntityId = snapshot.ExpectedEntityId,
                evidenceEntityId = chest.Object.EntityId,
                nativeStateAvailable = chest.NativeStateAvailable,
                nativeCompletionKind = chest.NativeCompletionKind.ToString(),
                nativeIsTargetable = chest.Object.IsTargetable,
                nativeTreasureState = chest.State.ToString(),
                nativeTreasureFlags = chest.Flags.ToString(),
                evidenceSequenceAtStart = snapshot.EvidenceSequenceAtStart,
                evidenceSequence = snapshot.EvidenceSequence,
                elapsedSinceInteractionStartedMilliseconds =
                    (DateTime.UtcNow - snapshot.InteractionStartedAtUtc).TotalMilliseconds,
                completionStatus = snapshot.Decision.Status.ToString()
            });
        }

        return true;
    }

    private static string DescribeNativeChestCompletionSource(NativeTreasureCompletionKind kind)
    {
        return kind switch
        {
            NativeTreasureCompletionKind.TreasureState => "FFXIVClientStructs.Treasure",
            NativeTreasureCompletionKind.EventObjectTargetable => "global::Dalamud.EventObj.IsTargetable",
            _ => "None"
        };
    }


    private static string DescribeChest(RoomObjectiveType type)
    {
        return type switch
        {
            RoomObjectiveType.ChestBanded => "banded chest",
            RoomObjectiveType.ChestGold => "gold chest",
            RoomObjectiveType.ChestSilver => "silver chest",
            RoomObjectiveType.ChestBronze => "bronze chest",
            _ => "chest"
        };
    }

    private unsafe bool RefreshCachedHoardIndicator(InstanceContentDeepDungeon* dd)
    {
        if (_ctx?.ControlledPtSurvey != null)
            return true;

        if (_chatWatchers?.ChatSaysNoHoard == true)
        {
            if (HandleNoHoardEvidenceInvalidated("no-hoard-refresh"))
                RequestPlanRefresh("no-hoard-refresh");
            return true;
        }
        if (_executor?.CanAcceptHoardIndicator != true)
            return true;

        var before = _executor?.CachedHoardIndicatorPos;
        if (!BandedChestLocator.TryFindHoardIndicatorMatch(this.ObjectEvidence.Current!, out var indicator))
            return false;
        if (indicator.HasValue)
        {
            var match = indicator.Value;
            _executor!.UpdateCachedHoardIndicator(match.Position);
            var after = _executor.CachedHoardIndicatorPos;
            if (after.HasValue && (!before.HasValue || Vector3.DistanceSquared(before.Value, after.Value) > 0.01f))
            {
                RequestPlanRefresh("hoard-indicator-updated");
                RecordReplayEvent("cached-hoard-indicator-updated", new
                {
                    x = after.Value.X,
                    y = after.Value.Y,
                    z = after.Value.Z
                });

                var now = DateTime.UtcNow;
                var player = Service.LocalPlayer;
                float? distanceToPlayer = player != null
                    ? Vector3.Distance(player.Position, after.Value)
                    : null;
                int playerRoom = dd != null ? RoomGraph.GetLocalPlayerRoomIndex(dd) : -1;
                var activeWaypoint = _activeWaypoint;
                var lastTrapTriggered = SearchState.LastTrapTriggered;
                var lastTrapCompleted = SearchState.LastTrapCompleted;
                int? elapsedSinceLastTrapTriggeredMs = lastTrapTriggered.HasValue
                    ? (int)Math.Max(0, (now - lastTrapTriggered.Value.TimestampUtc).TotalMilliseconds)
                    : null;
                int? elapsedSinceLastTrapCompletedMs = lastTrapCompleted.HasValue
                    ? (int)Math.Max(0, (now - lastTrapCompleted.Value.TimestampUtc).TotalMilliseconds)
                    : null;
                string? activeWaypointType = activeWaypoint.HasValue ? activeWaypoint.Value.Type.ToString() : null;
                float? activeWaypointX = activeWaypoint.HasValue ? activeWaypoint.Value.Position.X : null;
                float? activeWaypointY = activeWaypoint.HasValue ? activeWaypoint.Value.Position.Y : null;
                float? activeWaypointZ = activeWaypoint.HasValue ? activeWaypoint.Value.Position.Z : null;
                string? lastTrapTriggeredExecutionKind = lastTrapTriggered?.ExecutionKind.ToString();
                string? lastTrapCompletedExecutionKind = lastTrapCompleted?.ExecutionKind.ToString();
                float? lastTrapTriggeredX = lastTrapTriggered?.Position.X;
                float? lastTrapTriggeredY = lastTrapTriggered?.Position.Y;
                float? lastTrapTriggeredZ = lastTrapTriggered?.Position.Z;
                float? lastTrapCompletedX = lastTrapCompleted?.Position.X;
                float? lastTrapCompletedY = lastTrapCompleted?.Position.Y;
                float? lastTrapCompletedZ = lastTrapCompleted?.Position.Z;
                RecordReplayEvent("hoard-indicator-visible", new
                {
                    floor = dd != null ? dd->Floor : (byte)0,
                    playerRoom,
                    roomIndex = _executor?.RoomContext?.RoomIndex ?? -1,
                    waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
                    activeWaypointType,
                    activeWaypointAtObservationX = activeWaypointX,
                    activeWaypointAtObservationY = activeWaypointY,
                    activeWaypointAtObservationZ = activeWaypointZ,
                    executionKind = _searchExecutionKind.ToString(),
                    duringTrapSearch = activeWaypoint.HasValue && activeWaypoint.Value.Type == RoomObjectiveType.Trap,
                    lastTrapTriggeredAtUtc = lastTrapTriggered?.TimestampUtc,
                    lastTrapTriggeredRoom = lastTrapTriggered?.RoomIndex,
                    lastTrapTriggeredWaypoint = lastTrapTriggered?.WaypointIndex,
                    lastTrapTriggeredExecutionKind,
                    lastTrapTriggeredX,
                    lastTrapTriggeredY,
                    lastTrapTriggeredZ,
                    elapsedSinceLastTrapTriggeredMs,
                    lastTrapCompletedAtUtc = lastTrapCompleted?.TimestampUtc,
                    lastTrapCompletedRoom = lastTrapCompleted?.RoomIndex,
                    lastTrapCompletedWaypoint = lastTrapCompleted?.WaypointIndex,
                    lastTrapCompletedExecutionKind,
                    lastTrapCompletedX,
                    lastTrapCompletedY,
                    lastTrapCompletedZ,
                    elapsedSinceLastTrapCompletedMs,
                    distanceToPlayer,
                    matchedObjectBaseId = match.BaseId,
                    matchedObjectGameObjectId = match.GameObjectId,
                    matchedObjectEntityId = match.EntityId,
                    matchedObjectIndex = match.ObjectIndex,
                    matchedObjectName = match.Name,
                    matchedObjectKind = match.ObjectKind,
                    matchedObjectSubKind = match.SubKind,
                    matchedObjectIsTargetable = match.IsTargetable,
                    matchedObjectIsBandedChest = match.IsBandedChest,
                    matchedObjectAddress = match.Address,
                    x = after.Value.X,
                    y = after.Value.Y,
                    z = after.Value.Z
                });
            }
        }

        return true;
    }

    private void RecordWaypointStarted(RoomWaypoint waypoint)
    {
        int roomIndex = _executor?.RoomContext?.RoomIndex ?? -1;
        int waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1;
        RecordReplayEvent("waypoint-started", new
        {
            roomIndex,
            waypointIndex,
            executionKind = _searchExecutionKind.ToString(),
            objectiveType = waypoint.Type.ToString(),
            arrivalRadius = waypoint.ArrivalRadius,
            x = waypoint.Position.X,
            y = waypoint.Position.Y,
            z = waypoint.Position.Z
        });
    }

    private void RecordWaypointResolved(
        RoomWaypoint waypoint,
        WaypointOutcomeKind outcome,
        string reason,
        double elapsedSeconds,
        TaskPhase taskPhaseBefore)
    {
        int roomIndex = _executor?.RoomContext?.RoomIndex ?? -1;
        int waypointIndex = _executor?.RoomContext?.CurrentWaypointIndex ?? -1;
        string recordedOutcome = outcome switch
        {
            WaypointOutcomeKind.Completed => ObjectiveOutcomeKind.Succeeded.ToString(),
            WaypointOutcomeKind.PolicySkipped => "Skipped",
            _ => outcome.ToString()
        };
        var payload = new
        {
            roomIndex,
            waypointIndex,
            executionKind = _searchExecutionKind.ToString(),
            objectiveType = waypoint.Type.ToString(),
            outcome = recordedOutcome,
            taskPhase = taskPhaseBefore.ToString(),
            reason,
            elapsedSeconds,
            x = waypoint.Position.X,
            y = waypoint.Position.Y,
            z = waypoint.Position.Z
        };

        RecordReplayEvent(outcome == WaypointOutcomeKind.Completed ? "waypoint-completed" : "waypoint-skipped", payload);

        if (waypoint.Type == RoomObjectiveType.Trap)
        {
            if (outcome == WaypointOutcomeKind.Completed)
            {
                SearchState.LastTrapCompleted = new TrapObservation(
                    DateTime.UtcNow,
                    roomIndex,
                    waypointIndex,
                    _searchExecutionKind,
                    waypoint.Position);
                this.ObjectEvidence.Invalidate();
            }
        }
        else
        {
            var snapshot = _ctx?.ChestInteraction.Observe(ActiveChestAttempt, this.ObjectEvidence.Current) ?? default;
            var nativeChest = snapshot.Evidence;
            RecordReplayEvent("chest-resolved", new
            {
                roomIndex,
                waypointIndex,
                executionKind = _searchExecutionKind.ToString(),
                chestType = waypoint.Type.ToString(),
                outcome = recordedOutcome,
                reason,
                taskPhase = taskPhaseBefore.ToString(),
                elapsedSeconds,
                elapsedSinceInteractionStartedMilliseconds = snapshot.InteractionStartedAtUtc == DateTime.MinValue
                    ? (double?)null
                    : (DateTime.UtcNow - snapshot.InteractionStartedAtUtc).TotalMilliseconds,
                completionSource = outcome == WaypointOutcomeKind.Completed
                    ? DescribeNativeChestCompletionSource(nativeChest.NativeCompletionKind)
                    : "None",
                expectedEntityId = snapshot.ExpectedEntityId,
                evidenceEntityId = nativeChest.Object.EntityId,
                nativeStateAvailable = nativeChest.NativeStateAvailable,
                nativeCompletionKind = nativeChest.NativeCompletionKind.ToString(),
                nativeIsTargetable = nativeChest.Object.IsTargetable,
                nativeTreasureState = nativeChest.State.ToString(),
                nativeTreasureFlags = nativeChest.Flags.ToString(),
                evidenceSequenceAtStart = snapshot.EvidenceSequenceAtStart,
                evidenceSequence = snapshot.EvidenceSequence,
                completionStatus = snapshot.Decision.Status.ToString(),
                nativeAcceptanceRecorded = snapshot.AcceptanceRecorded,
                x = waypoint.Position.X,
                y = waypoint.Position.Y,
                z = waypoint.Position.Z
            });
        }
    }

    private void StartBandedRevealExpectation(int sourceRoomIndex)
    {
        if (this.IsDisposed)
            return;

        var expectation = new BandedRevealPending(
            DateTime.UtcNow.AddSeconds(BandedRevealExpectationSeconds),
            this.ObjectEvidence.Current?.RefreshSequence ?? 0);
        this.BandedRevealExpectation = expectation;
        RecordReplayEvent("banded-reveal-expectation-started", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            sourceRoomIndex,
            evidenceSequence = expectation.EvidenceSequence,
            durationSeconds = BandedRevealExpectationSeconds,
            reason = "hoard-room-search-finished"
        });
    }

    private bool IsBandedRevealExpectationPending()
    {
        if (this.IsDisposed || this.BandedRevealExpectation is not { } expectation)
            return false;

        if (_executor?.HasOpenedHoardThisFloor == true)
        {
            ClearBandedRevealExpectation("hoard-opened");
            return false;
        }
        if (_executor?.ConfigSnapshot.BandedEnabled != true)
        {
            ClearBandedRevealExpectation("banded-disabled");
            return false;
        }
        if (DateTime.UtcNow >= expectation.ExpiresAtUtc &&
            this.ObjectEvidence.Current is { Available: true } evidence &&
            evidence.RefreshSequence > expectation.EvidenceSequence)
        {
            ClearBandedRevealExpectation("expired");
            return false;
        }

        return true;
    }

    private void ClearBandedRevealExpectation(string reason)
    {
        if (this.BandedRevealExpectation is not { } expectation)
            return;

        RecordReplayEvent("banded-reveal-expectation-ended", new
        {
            floor = this.Floor,
            floorGeneration = this.Generation,
            evidenceSequence = this.ObjectEvidence.Current?.RefreshSequence ?? 0,
            startedAtEvidenceSequence = expectation.EvidenceSequence,
            reason
        });
        this.BandedRevealExpectation = null;
    }

    private void StartPostRoomPomanderRetry(int roomIndex, bool searchCompletePending)
    {
        SearchState.PostRoomPomanderRetry = new PostRoomPomanderRetry
        {
            FinishedRoomIndex = roomIndex,
            ExpiresAt = DateTime.UtcNow.AddSeconds(PostRoomPomanderRetrySeconds)
        };
        RecordReplayEvent("post-room-pomander-retry-started", new
        {
            roomIndex,
            searchCompletePending,
            reason = "optional-pomander-retry"
        });
        Service.Log.Info(searchCompletePending
            ? $"[FloorPhase] Room {roomIndex} complete, starting {PostRoomPomanderRetrySeconds:F1}s post-room pomander retry alongside search-complete transition"
            : $"[FloorPhase] Room {roomIndex} complete, starting {PostRoomPomanderRetrySeconds:F1}s post-room pomander retry");
    }

    private void ClearPostRoomPomanderRetry()
    {
        SearchState.PostRoomPomanderRetry = null;
    }

    private void BeginRoomIntelSettle(int roomIndex)
    {
        SearchState.IntelSettleRoomIndex = roomIndex;
        SearchState.IntelSettleUntil = DateTime.UtcNow.AddSeconds(IntelSettleDurationSeconds);
        _status = $"Waiting for room {roomIndex} hoard evidence";
        RecordReplayEvent("room-intel-settle-started", new
        {
            roomIndex,
            durationSeconds = IntelSettleDurationSeconds
        });
    }

    private unsafe bool UpdateRoomIntelSettle(InstanceContentDeepDungeon* dd, IPlayerCharacter player, int roomIndex)
    {
        if (SearchState.IntelSettleRoomIndex != roomIndex)
            return false;

        if (!RefreshCachedHoardIndicator(dd))
        {
            _status = "Waiting for hoard indicator evidence...";
            return true;
        }
        if (PlanningState.RefreshRequested)
        {
            var normalGraph = this.NormalGraph;
            if (normalGraph == null)
            {
                _status = "Waiting for room graph...";
                return true;
            }

            long evidenceVersion = PlanningState.PendingEvidenceVersion;
            var roomContext = _executor!.RoomContext;
            roomContext?.MarkIntelCompleted();
            bool authoritativeHoardResolved = _executor.HasOpenedHoardThisFloor || dd->HoardCount > PlanningState.LastKnownHoardCount;
            var committedOutcome = RoomObjectiveOutcomePlanner.Decide(
                roomContext?.BuildOutcomeSnapshot(authoritativeHoardResolved) ?? default);
            if (!TryApplyActiveObjectiveOutcomes(roomIndex, committedOutcome, "RoomIntelSettleEvidenceRefresh"))
                return true;
            ClearRoomIntelSettle();
            _executor.ClearRoomContext();
            _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
            MarkPlanRefreshConsumed(evidenceVersion);
            RecordReplayEvent("room-intel-settle-committed", new
            {
                roomIndex,
                intel = committedOutcome.Intel.ToString(),
                markIntelVisited = committedOutcome.MarkIntelVisited,
                evidenceVersion
            });
            RecordReplayEvent("room-intel-settle-replanned", BuildPlanReplayPayload(dd->Floor, "room-intel-settle-evidence-refresh", roomIndex));
            return true;
        }

        if (DateTime.UtcNow < SearchState.IntelSettleUntil)
        {
            _status = $"Waiting for room {roomIndex} hoard evidence";
            return true;
        }

        _executor?.RoomContext?.MarkIntelCompleted();
        ClearRoomIntelSettle();
        return false;
    }

    private void ClearRoomIntelSettle()
    {
        SearchState.IntelSettleRoomIndex = -1;
        SearchState.IntelSettleUntil = DateTime.MinValue;
    }

    private void ExpirePostRoomPomanderRetry()
    {
        if (SearchState.PostRoomPomanderRetry != null)
        {
            Service.Log.Info($"[FloorPhase] Post-room pomander retry expired for room {SearchState.PostRoomPomanderRetry.FinishedRoomIndex}");
        }

        ClearPostRoomPomanderRetry();
    }

    private bool IsSightUseBlocked()
    {
        return SightUseStateMachine.PreventsAutomaticUse(
            _chatWatchers?.SightState ?? SightUseState.None);
    }

    private unsafe void TryUseFloorInitPomander(InstanceContentDeepDungeon* dd)
    {
        if (_chatWatchers == null)
            return;
        if (_ctx?.ControlledPtSurvey != null)
            return;

        var snapshot = BuildFloorInitSnapshot(dd, HasHarmfulFloorEffect(dd));
        if (DungeonCatalog.SupportsNaturalPtStones(dd->DeepDungeonId) &&
            ShouldUseNaturalMazerootForHoardExploration(snapshot) &&
            this.Survey.TryUseNaturalMazeroot(dd, "S1 Sight fallback with 敏慧") == true)
        {
            return;
        }

        var decision = FloorInitPlanner.Decide(snapshot);
        if (decision.ShouldUse)
        {
            TryUsePomander(decision.SlotIndex!.Value, dd, decision.Reason!);
        }
    }

    private bool ShouldUseNaturalMazerootForHoardExploration(
        in FloorInitSnapshot snapshot)
    {
        // Preference over Sight when both effects are needed: with the
        // passage still closed a Mazeroot is worthwhile even while a
        // Sight pomander could serve the same reveal.
        var passageOpen = _ctx?.Duty.PassageOpen == true;
        return snapshot.CanAttemptPomanderUse &&
               snapshot.BandedEnabled &&
               !snapshot.HasOpenedHoardThisFloor &&
               FloorsetHoardDistributionPolicy.AllowsHoardPomander(
                   snapshot.HoardOpportunity) &&
               !snapshot.IntuitionActive &&
               !snapshot.IntuitionUsable &&
               !snapshot.SightUseBlocked &&
               (!passageOpen || !snapshot.SightUsable) &&
               GetStoneCountAvailableForFloorUse(2) > 0;
    }

    private unsafe void TryUseGeneralAutoPomander(InstanceContentDeepDungeon* dd)
    {
        if (_ctx?.ControlledPtSurvey != null)
            return;
        if (CurrentInteractionAttempt is { PendingGoldOvercapSlotIndex: not null } or { PendingSilverOvercapStoneRowId: not null })
            return;

        var decision = GeneralAutoPomanderPlanner.Decide(BuildGeneralAutoPomanderSnapshot(allowStatusOverlap: false, HasHarmfulFloorEffect(dd)));
        if (decision.ShouldUse &&
            TryUsePomander(decision.SlotIndex!.Value, dd, decision.Reason!))
        {
            return;
        }
        // Coord floors never run the old room-finish executor — the
        // same RoomFinish planner re-evaluates newly available Intuition
        // here, guarded by an active hoard-search policy and no factual
        // terminal state (coverage exhaustion alone is NOT absence).
        if (!CoordOwnsWork)
            return;
        var cfk = _ctx?.Knowledge?.Floor;
        // Known exact / known-present intelligence and already observed
        // Banded coffers are useful facts — never reasons to consume
        // another Intuition/Sight. Coverage exhaustion stays eligible.
        if (cfk == null || cfk.HoardAcquired || cfk.ChatSaysNoHoard ||
            cfk.InheritedNoHoardInferred ||
            cfk.ObservedBandedPositions.Count > 0 ||
            cfk.EvidenceState is HoardEvidenceState.IntuitionNoHoard
                or HoardEvidenceState.Disabled
                or HoardEvidenceState.AlreadyOpened
                or HoardEvidenceState.FloorsetMaxed
                or HoardEvidenceState.FloorsetDistributionExcluded
                or HoardEvidenceState.IntuitionDirect
                or HoardEvidenceState.IntuitionWaitingForIndicator
                or HoardEvidenceState.IntuitionPending
                or HoardEvidenceState.IntuitionActiveUnconfirmed)
            return;
        var roomFinish = RoomFinishPomanderPlanner.Decide(
            BuildRoomFinishPomanderSnapshot(dd));
        if (roomFinish.Kind == RoomFinishPomanderDecisionKind.Use &&
            roomFinish.SlotIndex.HasValue)
            TryUsePomander(roomFinish.SlotIndex.Value, dd, roomFinish.Reason!);
    }

    private unsafe FloorInitSnapshot BuildFloorInitSnapshot(
        InstanceContentDeepDungeon* dd,
        bool hasHarmfulFloorEffect)
    {
        bool pomandersUsableThisFloor =
            DeepDungeonFloorItemUsePolicy.CanUsePomanders(
                dd->DeepDungeonBanId);
        // Coord floors read the shared knowledge/run options — the
        // legacy executor's room state is not authoritative there.
        var cfk = _ctx?.Knowledge?.Floor;
        return new FloorInitSnapshot
        {
            CanAttemptPomanderUse = CanAttemptPomanderUse(),
            BandedEnabled = CoordOwnsWork
                ? SnapshotRunOptions().BandedEnabled
                : (_executor?.ConfigSnapshot.BandedEnabled ?? false),
            HasOpenedHoardThisFloor = CoordOwnsWork
                ? (cfk?.HoardAcquired == true)
                : (_executor?.HasOpenedHoardThisFloor ?? false),
            HoardOpportunity = DeepDungeonFloorsetTracker.GetCurrentOpportunity(dd->Floor),
            IntuitionActive = _nativeIntuitionActive,
            UsedIntuitionThisFloor = CoordOwnsWork
                ? (cfk?.HasCurrentFloorIntuitionUse == true)
                : (_chatWatchers?.UsedIntuitionThisFloor == true),
            SightUseBlocked = IsSightUseBlocked(),
            IntuitionUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.IntuitionPomanderSlotIndex),
            SightUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.SightPomanderSlotIndex),
            AffluenceUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.AffluencePomanderSlotIndex),
            StrengthUsable = pomandersUsableThisFloor && CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex),
            SteelUsable = pomandersUsableThisFloor && CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex),
            PurityUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.PurityPomanderSlotIndex),
            SerenityUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.SerenityPomanderSlotIndex),
            RaisingUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.RaisingPomanderSlotIndex),
            AffluenceActive = _pomanderManager.IsActive(FloorInitPlanner.AffluencePomanderSlotIndex),
            RaisingActive = _pomanderManager.IsActive(FloorInitPlanner.RaisingPomanderSlotIndex),
            HasStrengthStatus = HasLocalPlayerStatus(StrengthStatusId),
            HasSteelStatus = HasLocalPlayerStatus(SteelStatusId),
            HasCurseStatus = HasLocalPlayerStatus(DeepDungeonCurseStatusId),
            HasHarmfulFloorEffect = hasHarmfulFloorEffect,
            NextFloorIsMob = !DeepDungeonHelper.IsBossFloor(dd->DeepDungeonId,
                (byte)(dd->Floor + 1))
        };
    }

    private unsafe GeneralAutoPomanderSnapshot BuildGeneralAutoPomanderSnapshot(bool allowStatusOverlap, bool hasHarmfulFloorEffect = false)
    {
        var efw = EventFramework.Instance();
        var dd = efw != null ? efw->GetInstanceContentDeepDungeon() : null;
        return new GeneralAutoPomanderSnapshot
        {
            CanAttemptPomanderUse = CanAttemptPomanderUse(),
            AffluenceUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.AffluencePomanderSlotIndex),
            StrengthUsable = CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex, allowStatusOverlap),
            SteelUsable = CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex, allowStatusOverlap),
            PurityUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.PurityPomanderSlotIndex),
            SerenityUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.SerenityPomanderSlotIndex),
            RaisingUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.RaisingPomanderSlotIndex),
            AffluenceActive = _pomanderManager.IsActive(FloorInitPlanner.AffluencePomanderSlotIndex),
            RaisingActive = _pomanderManager.IsActive(FloorInitPlanner.RaisingPomanderSlotIndex),
            HasStrengthStatus = HasLocalPlayerStatus(StrengthStatusId),
            HasSteelStatus = HasLocalPlayerStatus(SteelStatusId),
            HasCurseStatus = HasLocalPlayerStatus(DeepDungeonCurseStatusId),
            HasHarmfulFloorEffect = hasHarmfulFloorEffect,
            AllowStatusOverlap = allowStatusOverlap,
            FlightUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.FlightPomanderSlotIndex),
            FortuneUsable = IsPomanderAvailableForFloorUse(FloorInitPlanner.FortunePomanderSlotIndex),
            HasteUsable = dd != null && dd->DeepDungeonId == 4 && CombatBuffUsable(FloorInitPlanner.HastePomanderSlotIndex, allowStatusOverlap),
            FlightActive = _pomanderManager.IsActive(FloorInitPlanner.FlightPomanderSlotIndex),
            FortuneActive = _pomanderManager.IsActive(FloorInitPlanner.FortunePomanderSlotIndex),
            HasHasteStatus = _pomanderManager.IsActive(FloorInitPlanner.HastePomanderSlotIndex) || HasLocalPlayerStatus(PtHasteStatusId),
            NextFloorIsMob = dd != null && !DeepDungeonHelper.IsBossFloor(dd->DeepDungeonId, (byte)(dd->Floor + 1))
        };
    }

    private unsafe RoomFinishPomanderOutcome ProcessRoomFinishPomander(InstanceContentDeepDungeon* dd)
    {
        if (_ctx?.ControlledPtSurvey != null)
            return RoomFinishPomanderOutcome.NotNeeded;

        if (_chatWatchers == null)
        {
            return RoomFinishPomanderOutcome.NotNeeded;
        }
        var decision = RoomFinishPomanderPlanner.Decide(BuildRoomFinishPomanderSnapshot(dd));
        switch (decision.Kind)
        {
            case RoomFinishPomanderDecisionKind.NotNeeded:
                return RoomFinishPomanderOutcome.NotNeeded;
            case RoomFinishPomanderDecisionKind.PendingRetry:
                return RoomFinishPomanderOutcome.PendingRetry;
            case RoomFinishPomanderDecisionKind.Use:
                TryUsePomander(decision.SlotIndex!.Value, dd, decision.Reason!);
                return RoomFinishPomanderOutcome.PendingRetry;
            default:
                return RoomFinishPomanderOutcome.NotNeeded;
        }
    }

    private unsafe bool ShouldUseGoldChestOvercapPomander(uint slotIndex)
    {
        if (_chatWatchers == null || _executor == null)
        {
            return false;
        }

        return slotIndex switch
        {
            _ when GeneralAutoPomanderPlanner.ShouldUseSlot(BuildGeneralAutoPomanderSnapshot(allowStatusOverlap: true), slotIndex) => true,
            FloorInitPlanner.IntuitionPomanderSlotIndex => ShouldUseGoldChestOvercapHoardPomander(slotIndex),
            FloorInitPlanner.SightPomanderSlotIndex => ShouldUseGoldChestOvercapHoardPomander(slotIndex),
            _ => false
        };
    }

    private bool ShouldUseGoldChestOvercapHoardPomander(uint slotIndex)
    {
        if (_chatWatchers == null ||
            _executor == null ||
            IsPomanderBlockedForFloor(slotIndex) ||
            !_executor.ConfigSnapshot.BandedEnabled ||
            _executor.HasOpenedHoardThisFloor ||
            !FloorsetHoardDistributionPolicy.AllowsHoardPomander(
                DeepDungeonFloorsetTracker.GetCurrentOpportunity(
                    _ctx?.Duty.Floor ?? 0)))
        {
            return false;
        }

        return slotIndex switch
        {
            FloorInitPlanner.IntuitionPomanderSlotIndex => !_nativeIntuitionActive,
            FloorInitPlanner.SightPomanderSlotIndex => !_nativeIntuitionActive && !IsSightUseBlocked(),
            _ => false
        };
    }

    private unsafe RoomFinishPomanderSnapshot BuildRoomFinishPomanderSnapshot(
        InstanceContentDeepDungeon* dd)
    {
        bool pomandersUsableThisFloor =
            DeepDungeonFloorItemUsePolicy.CanUsePomanders(
                dd->DeepDungeonBanId);
        DeepDungeonFloorsetTracker.TryGetCurrentFloorsetState(
            dd->Floor,
            out FloorsetHoardDistributionState floorsetState);
        // Coord floors read the shared knowledge/run options — the
        // legacy executor's room state is not authoritative there.
        var cfk = _ctx?.Knowledge?.Floor;
        return new RoomFinishPomanderSnapshot
        {
            CanAttemptPomanderUse = CanAttemptPomanderUse(),
            BandedEnabled = CoordOwnsWork
                ? SnapshotRunOptions().BandedEnabled
                : (_executor?.ConfigSnapshot.BandedEnabled ?? false),
            HasOpenedHoardThisFloor = CoordOwnsWork
                ? (cfk?.HoardAcquired == true)
                : (_executor?.HasOpenedHoardThisFloor ?? false),
            FloorsetBandedCount = floorsetState.TotalHoardCount,
            HoardOpportunity = FloorsetHoardDistributionPolicy.Decide(
                floorsetState,
                dd->Floor),
            IntuitionActive = _nativeIntuitionActive,
            UsedIntuitionThisFloor = CoordOwnsWork
                ? (cfk?.HasCurrentFloorIntuitionUse == true)
                : (_chatWatchers?.UsedIntuitionThisFloor == true),
            SightUseBlocked = IsSightUseBlocked(),
            IntuitionUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.IntuitionPomanderSlotIndex),
            SightUsable = pomandersUsableThisFloor && IsPomanderAvailableForFloorUse(FloorInitPlanner.SightPomanderSlotIndex),
            IntuitionCount = _pomanderManager.GetCount(FloorInitPlanner.IntuitionPomanderSlotIndex),
            RemainingMobFloors = GetRemainingMobFloorCount(dd)
        };
    }

    private static unsafe bool HasHarmfulFloorEffect(InstanceContentDeepDungeon* dd)
    {
        if (dd == null)
            return false;

        return DeepDungeonFloorEffectPolicy.HasHarmfulSerenityRemovableEffect(
            dd->DeepDungeonStatusId,
            dd->DeepDungeonBanId,
            dd->DeepDungeonDangerId);
    }

    internal static bool HasLocalPlayerStatus(uint statusId)
    {
        var player = Service.LocalPlayer;
        if (player == null)
            return false;

        foreach (var status in player.StatusList)
        {
            if (status.StatusId == statusId)
                return true;
        }

        return false;
    }

    private bool CanAttemptPomanderUse(bool allowCombat = false) =>
        FloorItemExecutor.CanAttemptPomanderUse(this.Items, allowCombat);
    private bool IsPomanderAvailableForFloorUse(uint slotIndex) =>
        this.Items.IsPomanderAvailableForFloorUse(slotIndex);
    private bool IsPomanderBlockedForFloor(uint slotIndex) =>
        this.Items.IsPomanderBlockedForFloor(slotIndex) == true;
    private bool IsStoneBlockedForFloor(byte stoneId) =>
        this.Items.IsStoneBlockedForFloor(stoneId) == true;
    private int GetStoneCountAvailableForFloorUse(byte stoneId) =>
        this.Items.GetStoneCountAvailableForFloorUse(stoneId);
    private unsafe bool TryUsePomander(uint slotIndex, InstanceContentDeepDungeon* dd, string reason,
        FloorItemUsePurpose purpose = FloorItemUsePurpose.Automatic) =>
        this.Items.TryUsePomander(slotIndex, dd, reason, purpose) == true;
    private unsafe bool TryDispatchFloorStone(byte stoneId, InstanceContentDeepDungeon* dd, string reason,
        FloorItemUsePurpose purpose) => this.Items.TryDispatchFloorStone(stoneId, dd, reason, purpose) == true;

    internal unsafe void ResolvePendingFloorItemUse(InstanceContentDeepDungeon* dd)
    {
        if (this.Items.ResolvePending(dd) is not { } resolution) return;
        // Coord overcap relief resolves against the attempt that REQUESTED
        // it — never whichever chest head happens to be current now.
        if (ReferenceEquals(resolution.Pending, _coordOvercapRequest))
        {
            if (resolution.Decision == FloorItemUseConfirmationDecisionKind.Confirmed &&
                _coordOvercapAttempt is { } owner)
            {
                if (resolution.Pending.Purpose == FloorItemUsePurpose.GoldChestOvercap &&
                    owner.PendingGoldOvercapSlotIndex == resolution.Pending.Key.ItemId)
                    owner.PendingGoldOvercapSlotIndex = null;
                else if (resolution.Pending.Purpose == FloorItemUsePurpose.SilverChestOvercap)
                {
                    owner.PendingSilverOvercapStoneRowId = null;
                    owner.NextInteractAt = DateTime.MinValue;
                }
            }
            if (resolution.Decision is FloorItemUseConfirmationDecisionKind.Confirmed
                or FloorItemUseConfirmationDecisionKind.Exhausted)
            {
                _coordOvercapRequest = null;
                _coordOvercapAttempt = null;
            }
        }
        if (resolution.Decision == FloorItemUseConfirmationDecisionKind.Confirmed)
            ApplyConfirmedFloorItemUse(dd, resolution.Pending, resolution.CurrentCount);
        else if (resolution.Decision == FloorItemUseConfirmationDecisionKind.Exhausted)
            ApplyExhaustedFloorItemUse(resolution.Pending);
    }

    private unsafe void ApplyConfirmedFloorItemUse(
        InstanceContentDeepDungeon* dd,
        PendingFloorItemUse pending,
        int currentCount)
    {
        if (pending.Key.Kind == FloorItemUseKind.Pomander)
        {
            uint slotIndex = pending.Key.ItemId;
            _ctx?.Knowledge.Floor?.RecordConfirmedPomanderUse(slotIndex);
            if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
            {
                _chatWatchers?.MarkIntuitionUsedThisFloor();
                if (_ctx?.ControlledPtSurvey != null)
                {
                    this.Survey.ObserveConfirmedIntuition(pending);
                }
                RecordNativeIntuitionState(
                    $"after-intuition-use:{pending.Reason}:confirmed",
                    force: true);
            }
            else if (slotIndex == FloorInitPlanner.SightPomanderSlotIndex)
            {
                _chatWatchers?.MarkSightAttemptedThisFloor();
                if (pending.Purpose == FloorItemUsePurpose.ControlledReveal)
                    this.Survey.ApplyConfirmedControlledReveal(pending);
                else
                    this.Survey.ApplyConfirmedNaturalReveal(pending,
                        SightResearchRevealResource.Sight);
            }

            if (pending.Purpose == FloorItemUsePurpose.ControlledStrength)
                this.Survey.MarkControlledStrengthHandled();
            // Legacy direct-controller clearing only — coord relief is
            // resolved against the captured requester's attempt above.
            if (!CoordOwnsWork &&
                pending.Purpose == FloorItemUsePurpose.GoldChestOvercap &&
                ActiveChestAttempt?.PendingGoldOvercapSlotIndex == slotIndex)
            {
                ActiveChestAttempt.PendingGoldOvercapSlotIndex = null;
                Service.Log.Info(
                    $"[FloorPhase] Used {DescribePomanderSlot(slotIndex)} to resolve capped gold chest");
            }

            Service.Log.Info(
                $"[FloorPhase] Confirmed pomander slot {slotIndex} use ({pending.Reason}) on floor {dd->Floor}");
            RecordReplayEvent("pomander-used", new
            {
                floor = dd->Floor,
                slotIndex,
                reason = pending.Reason,
                attempt = pending.AttemptNumber,
                countBeforeDispatch = pending.CountBeforeDispatch,
                currentCount,
                intuitionAttemptId = pending.IntuitionAttemptId
            });
        }
        else
        {
            _ctx?.Knowledge.Floor?.RecordConfirmedStoneUse(pending.Key.ItemId);
            byte stoneId = (byte)pending.Key.ItemId;
            if (dd->DeepDungeonId == 4 && stoneId is 1 or 2) this.FarmingPassageItemConfirmed = true;
            // A confirmed Mazeroot IS this floor's natural reveal — mark
            // Sight confirmed regardless of purpose, without touching
            // synthetic native log sequences.
            if (dd->DeepDungeonId == 4 && stoneId == 2)
                _chatWatchers?.ConfirmSightThisFloor("MazerootUseConfirmed");
            if (!CoordOwnsWork &&
                pending.Purpose == FloorItemUsePurpose.SilverChestOvercap &&
                ActiveChestAttempt is { PendingSilverOvercapStoneRowId: not null } chestAttempt)
            {
                chestAttempt.PendingSilverOvercapStoneRowId = null;
                chestAttempt.NextInteractAt = DateTime.MinValue;
            }
            switch (pending.Purpose)
            {
                case FloorItemUsePurpose.NaturalReveal:
                    _chatWatchers?.MarkSightAttemptedThisFloor();
                    this.Survey.ApplyConfirmedNaturalReveal(pending,
                        SightResearchRevealResource.Mazeroot);
                    break;
                case FloorItemUsePurpose.ControlledReveal:
                    this.Survey.ApplyConfirmedControlledReveal(pending);
                    break;
                case FloorItemUsePurpose.NaturalPoisonfruit:
                    this.Survey.ObserveConfirmedStone(pending.Purpose);
                    break;
                case FloorItemUsePurpose.NaturalPassageMazeroot:
                    this.Survey.ObserveConfirmedStone(pending.Purpose);
                    this.Survey.ApplyConfirmedNaturalReveal(pending,
                        SightResearchRevealResource.Mazeroot);
                    break;
                case FloorItemUsePurpose.BossSerenity:
                    this.Boss.OnSerenityConfirmed();
                    break;
                case FloorItemUsePurpose.ControlledPoisonfruit:
                    this.Survey.ObserveConfirmedStone(pending.Purpose);
                    break;
            }

            string eventType = dd->DeepDungeonId == 2 ? "magicite-used" : pending.Purpose is
                FloorItemUsePurpose.ControlledReveal or
                FloorItemUsePurpose.ControlledPoisonfruit
                    ? "controlled-incense-used"
                    : "incense-used";
            Service.Log.Info(
                $"[FloorPhase] Confirmed stone {stoneId} use ({pending.Reason}) in dungeon {dd->DeepDungeonId} floor {dd->Floor}");
            RecordReplayEvent(eventType, new
            {
                floor = dd->Floor,
                stoneId,
                reason = pending.Reason,
                attempt = pending.AttemptNumber,
                countBeforeDispatch = pending.CountBeforeDispatch,
                currentCount
            });
        }

        RecordReplayEvent("floor-item-use-confirmed", new
        {
            floor = dd->Floor,
            kind = pending.Key.Kind.ToString(),
            itemId = pending.Key.ItemId,
            purpose = pending.Purpose.ToString(),
            attempt = pending.AttemptNumber,
            countBeforeDispatch = pending.CountBeforeDispatch,
            currentCount
        });
    }

    private void ApplyExhaustedFloorItemUse(
        PendingFloorItemUse pending)
    {
        if (pending.Key is
            {
                Kind: FloorItemUseKind.Pomander,
                ItemId: FloorInitPlanner.IntuitionPomanderSlotIndex or
                    FloorInitPlanner.SightPomanderSlotIndex
            } ||
            pending.Purpose == FloorItemUsePurpose.NaturalReveal)
        {
            RequestPlanRefresh("floor-item-use-exhausted");
        }

        this.Survey.ObserveExhaustedItem(pending.Purpose);
    }

    private unsafe void ObserveHoardCount(InstanceContentDeepDungeon* dd, string checkpoint)
    {
        int current = dd->HoardCount;
        int previous = PlanningState.LastKnownHoardCount;
        _executor!.ObserveHoardCount(current);
        PlanningState.ObserveHoardCount(current);
        if (current > previous)
            RecordNativeIntuitionState($"hoard-completed:{checkpoint}", force: true);
    }

    private static string DescribePomanderSlot(uint slotIndex)
    {
        return slotIndex switch
        {
            0 => "safety",
            1 => "sight",
            2 => "strength",
            3 => "steel",
            4 => "affluence",
            5 => "flight",
            6 => "alteration",
            7 => "purity",
            8 => "fortune",
            9 => "witching",
            10 => "serenity",
            11 => "unique-1",
            12 => "unique-2",
            13 => "intuition",
            14 => "raising",
            15 => "unique-3",
            _ => $"slot {slotIndex}"
        };
    }

    private unsafe int GetRemainingMobFloorCount(InstanceContentDeepDungeon* dd)
    {
        if (dd == null || !DungeonCatalog.TryGetByDungeonId(dd->DeepDungeonId, out var dungeon))
            return 0;

        int endFloor = dungeon.TerritoryFloorRanges.Count > 0
            ? dungeon.TerritoryFloorRanges.Values.Max(x => x.endFloor)
            : 0;

        int remaining = 0;
        for (int floor = dd->Floor + 1; floor <= endFloor; floor++)
        {
            if (!DeepDungeonHelper.IsBossFloor(dd->DeepDungeonId, (byte)floor))
            {
                remaining++;
            }
        }

        return remaining;
    }

    private unsafe bool UpdatePostRoomPomanderRetry(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var retry = SearchState.PostRoomPomanderRetry;
        if (retry == null)
            return false;
        if (_activeWaypoint is { } activeWaypoint && IsChestWaypoint(activeWaypoint))
            return false;

        if (_ctx?.Duty.PassageOpen == true && !Service.Condition[ConditionFlag.InCombat])
        {
            RecordReplayEvent("post-room-pomander-retry-discarded", new
            {
                reason = "passage-open-optional-pomander-only",
                finishedRoomIndex = retry.FinishedRoomIndex
            });
            ClearPostRoomPomanderRetry();
            return false;
        }

        if (DateTime.UtcNow >= retry.ExpiresAt)
        {
            ExpirePostRoomPomanderRetry();
            return false;
        }

        switch (ProcessRoomFinishPomander(dd))
        {
            case RoomFinishPomanderOutcome.NotNeeded:
                ClearPostRoomPomanderRetry();
                break;
            case RoomFinishPomanderOutcome.PendingRetry:
                break;
        }

        return false;
    }

    private unsafe void SyncLiveRunOptions(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (_executor == null)
            return;

        var options = SnapshotRunOptions();
        if (!_executor.ApplyRunOptions(options))
            return;

        RecordReplayEvent("run-options-synchronized", new
        {
            floor = dd->Floor,
            phase = _phase.ToString(),
            openGold = options.OpenGold,
            openSilver = options.OpenSilver,
            openBronze = options.OpenBronze,
            bandedEnabled = options.BandedEnabled
        });

        if (UsesSiteCoordination)
        {
            // Option changes mark the route dirty — the scheduler rebuilds
            // on its next decision tick; no old planner call here.
            _siteDriver?.MarkRouteDirty();
            return;
        }

        var normalGraph = this.NormalGraph;
        if (_phase != FloorPhase.FloorActive ||
            normalGraph == null ||
            _taskRunner?.Phase != TaskPhase.Idle ||
            _executor.RoomContext != null)
        {
            return;
        }

        _executor.GeneratePlan(dd, normalGraph, _chatWatchers, player.Position, _nativeIntuitionActive);
        RecordReplayEvent("floor-plan-regenerated-options", BuildPlanReplayPayload(dd->Floor, "run-options-changed"));
    }

    private bool ShouldRunGeneralTick() =>
        PlanningState.TryBeginGeneralTick(DateTime.UtcNow, GeneralTickInterval);
    internal DateTime? AttackWindowStartedAt => _coordRecoveryController?.AttackWindowStartedAt;
    private ObjectiveExecution PatrolExecution =>
        this.ActiveExecution ?? throw new InvalidOperationException("No active patrol objective execution.");
    private List<int> _patrolRooms => PatrolExecution.PatrolRooms;
    private int _patrolIndex { get => PatrolExecution.PatrolIndex; set => PatrolExecution.PatrolIndex = value; }
    private int _currentPatrolRoom { get => PatrolExecution.CurrentPatrolRoom; set => PatrolExecution.CurrentPatrolRoom = value; }

    private static bool IsWithinLiveTargetHoldRange(EnemyChaseTarget target, Vector3 playerPosition)
    {
        float dx = target.LivePosition.X - playerPosition.X;
        float dz = target.LivePosition.Z - playerPosition.Z;
        float maxCenterDistance =
            Math.Max(0f, target.HitboxRadius) + EnemyChaseHelper.LiveTargetHoldRange;
        return dx * dx + dz * dz <= maxCenterDistance * maxCenterDistance;
    }

    internal void ResetPatrolPlan()
    {
        var execution = this.ActiveExecution;
        if (execution == null)
            return;
        execution.PatrolRooms.Clear();
        execution.PatrolIndex = 0;
        execution.CurrentPatrolRoom = -1;
    }

    private unsafe void EnsurePatrolPlan(InstanceContentDeepDungeon* dd)
    {
        if (dd == null || _patrolRooms.Count > 0)
            return;

        int startRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if (startRoom < 0)
            startRoom = RoomGraph.GetHomeRoomIndex(dd);
        if (startRoom < 0)
            startRoom = 0;

        var order = RoomGraph.BuildReachableRoomOrder(dd, startRoom);
        if (order.Count == 0 && startRoom >= 0)
            order.Add(startRoom);

        _patrolRooms.AddRange(order);
        _patrolIndex = 0;
        _currentPatrolRoom = _patrolRooms.Count > 0 ? _patrolRooms[0] : -1;
    }

    private void AdvancePatrol()
    {
        if (_patrolRooms.Count == 0)
        {
            _currentPatrolRoom = -1;
            return;
        }

        _patrolIndex = (_patrolIndex + 1) % _patrolRooms.Count;
        _currentPatrolRoom = _patrolRooms[_patrolIndex];
    }


    internal void ResetEngagedTargetProgress() => _coordRecoveryController = null;
    internal unsafe void UpdatePassageNavigation(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        if (player == null) return;

        _chaseHelper.Reset();
        ResetPatrolPlan();
        _ctx?.ClearPreferredAggroTarget();
        if (!RequireTransitionPermission("passage navigation", FloorObjectiveKind.EnterPassage))
            return;
        NavigateToPassage(dd, player);
    }

    private CoordPipeline? _coordPipeline;
    private readonly ShadowNativeBoundary _coordShadow = new();
    private RealNativeBoundary? _coordReal;
    private ObjectiveArbiterSnapshot _coordSnapshot;
    // The once-per-decision-tick activity pair — driver, pipeline tick and
    // channel permissions all read THIS sample, never Service.Condition.
    private Coord.FloorActivity _normalActivity;
    // Coord-owned recovery plumbing: the combat controller is shared
    // floor-scoped (existing RecoveryBudget) and runs claim-only;
    // the geometry adapter is reused across chest sessions.
    private CombatPositionRecoveryController? _coordRecoveryController;
    // First no-progress failure per floor+work target gets one bounded
    // replay snapshot — dedup key set.
    private readonly HashSet<string> _navFailureReported = new();
    private bool _coordTookOver;
    // Overcap relief request bound to the attempt that asked for it — the
    // pending-item channel is one-at-a-time so a reference pair suffices;
    // the resolution clears THIS attempt, never whichever head is current.
    private PendingFloorItemUse? _coordOvercapRequest;
    private ChestInteractionAttempt? _coordOvercapAttempt;
    private Coord.NavBitmap? _navBitmap;
    private Coord.CoverageSet? _coverage;
    private Coord.NavigationSurface? _navigation;
    private Coord.SiteSearchBoundary? _siteSearch;
    private Coord.ObjectiveSiteDriver? _siteDriver;
    private unsafe InstanceContentDeepDungeon* _scopeDd;
    private string _navBackend = "navigation";
    private int _navBitmapFloor = -1;
    private int _passageNavFailStreak;
    private DateTime _passageRepositionUntilUtc = DateTime.MinValue;
    private int _passageNavRoomContext = -1;
    private const int PassageNavFailsBeforeReposition = 3;
    private const int PassageNavFailExhausted = 8;
    private static readonly TimeSpan PassageRepositionWindow = TimeSpan.FromSeconds(8);

    private unsafe void NavigateToPassage(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var objectEvidence = this.ObjectEvidence.Current;
        if (objectEvidence?.Available != true ||
            !PassageLocator.TryResolvePassageDestination(dd, objectEvidence, out var dest, out bool usedActor, out var passageRoomIndex))
        {
            _status = "Passage position unknown";
            RecordPassageNavigationEvent("passage-navigation", "Unknown", usedActor: false, passageRoomIndex: -1, playerRoom: RoomGraph.GetLocalPlayerRoomIndex(dd));
            _navDriver!.Reset();
            return;
        }

        var now = DateTime.UtcNow;
        int playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        int roomContext = passageRoomIndex * 1000 + Math.Max(0, playerRoom);
        if (roomContext != _passageNavRoomContext)
        {
            _passageNavRoomContext = roomContext;
            _passageNavFailStreak = 0;
            _passageRepositionUntilUtc = DateTime.MinValue;
        }
        // A repeated dead end usually means the projected walking point sits on a
        // mesh island the player cannot reach (multi-level room). Spend a window
        // driving to the room center — always mesh-connected — then retry; after
        // too many failures stop driving and let the scenario bound handle it.
        var hasRoomCenter = MapPos.TryGetRoomCenter(dd, passageRoomIndex, out var roomCenter);
        bool repositioning = _passageNavFailStreak >= PassageNavFailsBeforeReposition &&
            _passageNavFailStreak < PassageNavFailExhausted &&
            now < _passageRepositionUntilUtc &&
            hasRoomCenter &&
            Vector3.DistanceSquared(player.Position, roomCenter) > 4f;
        if (_passageNavFailStreak >= PassageNavFailExhausted)
        {
            RecordPassageNavigationEvent("passage-navigation-exhausted", "Failed", usedActor, passageRoomIndex, playerRoom);
            _status = "Passage unreachable; holding for scenario bound";
            return;
        }
        if (repositioning)
        {
            dest = roomCenter;
            _status = "Passage unreachable, repositioning via room center";
        }
        // Knowing the actor does not make a failing cross-room path reachable.
        // Keep the room graph fallback until inside its room, then approach the exact walking point.
        int? targetRoom = repositioning ? null : PassageNavigationPolicy.RoutingRoom(usedActor, passageRoomIndex, playerRoom);
        if (usedActor && !repositioning) dest = ResolvePassageWalkingPosition(dest);

        var result = _navDriver!.Drive(dest, player.Position, 0.5f, dd, playerRoom, targetRoom);

        switch (result)
        {
            case NavDriveResult.Moving:
                RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                _status = _navDriver.IsStaging
                    ? $"Navigating to passage ({_navDriver.StageLabel})"
                    : "Navigating to passage...";
                break;
            case NavDriveResult.Staging:
                RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                _status = $"Navigating to passage ({_navDriver.StageLabel})";
                break;
            case NavDriveResult.Arrived:
                if (repositioning)
                {
                    _passageRepositionUntilUtc = DateTime.MinValue;
                    _navDriver.Reset();
                    _status = "Repositioned, retrying passage approach";
                    RecordPassageNavigationEvent("passage-repositioned", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                    break;
                }
                if (!usedActor)
                {
                    _status = "At passage room, waiting for passage actor";
                    RecordPassageNavigationEvent("passage-room-center-arrived", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                    _navDriver.Reset();
                    break;
                }
                this.RunTelemetry?.ObservePassageCommit(DateTime.UtcNow);
                bool newlyPositioned = _status != "At passage, waiting for transition";
                _status = "At passage, waiting for transition";
                if (newlyPositioned)
                {
                    Service.Log.Info("[FloorPhase] Positioned at passage, waiting for transition");
                    RecordPassageNavigationEvent("passage-arrived", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                }
                break;
            case NavDriveResult.StuckRetrying:
                this.RunTelemetry?.ObserveNavigationIssue();
                RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                _status = $"Stuck, repathing ({_navDriver.StuckRetryCount}/3)";
                break;
            case NavDriveResult.Failed:
                this.RunTelemetry?.ObserveNavigationIssue();
                RecordPassageNavigationEvent("passage-navigation", result.ToString(), usedActor, passageRoomIndex, playerRoom);
                if (++_passageNavFailStreak >= PassageNavFailsBeforeReposition)
                    _passageRepositionUntilUtc = now + PassageRepositionWindow;
                _status = _passageNavFailStreak >= PassageNavFailsBeforeReposition
                    ? $"Passage navigation failed ({_passageNavFailStreak}/{PassageNavFailExhausted})"
                    : "Passage navigation failed";
                break;
        }
    }
    private void StartActiveWaypointTelemetry(RoomWaypoint waypoint)
    {
        if (_runTelemetryObserver == null ||
            IsDisposed ||
            this.ActiveExecution == null ||
            Service.LocalPlayer is not { } player)
            return;

        if (this.ActiveExecution.WaypointTelemetry != null)
            EndActiveWaypointTelemetry(RunWaypointTerminalOutcome.Aborted, "WaypointReplaced");

        this.ActiveExecution.WaypointTelemetry = new RunWaypointTelemetryTrace(
            DateTime.UtcNow,
            player.ClassJob.RowId,
            this.DungeonId,
            ((this.Floor - 1) / 10) * 10 + 1,
            this.Floor,
            this.Generation,
            _ctx?.ControlledPtSurvey != null,
            _executor?.RoomContext?.RoomIndex ?? -1,
            _executor?.RoomContext?.CurrentWaypointIndex ?? -1,
            _searchExecutionKind.ToString(),
            waypoint.Type.ToString(),
            player.Position,
            waypoint.Position);
    }

    private void SampleActiveWaypointTelemetry(TaskPhase phase, Vector3 position)
    {
        this.ActiveExecution?.WaypointTelemetry?.Sample(
            DateTime.UtcNow,
            position,
            phase,
            Service.Condition[ConditionFlag.InCombat],
            Service.Condition[ConditionFlag.Casting]);
    }

    internal void EndActiveWaypointTelemetry(
        RunWaypointTerminalOutcome outcome,
        string reason,
        TaskPhase? observedPhase = null)
    {
        var execution = this.ActiveExecution;
        var trace = execution?.WaypointTelemetry;
        if (execution == null || trace == null)
            return;

        execution.WaypointTelemetry = null;
        Vector3 position = Service.LocalPlayer?.Position ?? trace.From;
        TaskPhase phase = observedPhase ?? execution.TaskRunner.Phase;
        var terminal = trace.Finish(
            DateTime.UtcNow,
            position,
            phase,
            Service.Condition[ConditionFlag.InCombat],
            Service.Condition[ConditionFlag.Casting],
            execution.TaskRunner.NavigationIssueCount,
            outcome,
            reason);
        this.RunTelemetry?.ObserveNavigationIssue(
            terminal.NavigationIssueCount);
        try
        {
            _runTelemetryObserver?.ObserveWaypointTerminal(terminal);
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[RunTelemetry] Host waypoint observer failed: {ex}");
        }
    }


}
internal enum EnemyChaseRecoveryDecision
{
    None,
    Start,
    Continue,
    TargetProgress
}

internal static class EnemyChaseRecoveryPolicy
{

    public static EnemyChaseRecoveryDecision Decide(
        bool targetAvailable,
        bool targetDead,
        bool targetSpecificAggro,
        bool targetHpDecreased,
        bool attackAttemptWindow,
        TimeSpan noProgress,
        bool recoveryActive)
    {
        if (!targetAvailable || targetDead || targetSpecificAggro || targetHpDecreased)
            return EnemyChaseRecoveryDecision.TargetProgress;
        if (recoveryActive)
            return EnemyChaseRecoveryDecision.Continue;
        if (!attackAttemptWindow)
            return EnemyChaseRecoveryDecision.None;
        return noProgress >= CombatPositionPolicy.UnknownNoProgressLimit
            ? EnemyChaseRecoveryDecision.Start
            : EnemyChaseRecoveryDecision.None;
    }
}
