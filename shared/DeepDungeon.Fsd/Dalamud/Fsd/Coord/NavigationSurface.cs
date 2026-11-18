using System.Numerics;
using System.Threading;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Utilities;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>One observation, planner and follower for every navigation request.</summary>
internal sealed partial class NavigationSurface : INavSurface, IDisposable
{
    private static readonly TimeSpan ReplanInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromMilliseconds(500);
    private readonly Func<NavBitmap?> _mapProvider;
    private readonly Func<EncounterPlanningSnapshot?> _snapshotProvider;
    private readonly Func<ulong> _playerId;
    private readonly Func<float> _movementSpeed;
    private readonly Action<string, object> _record;
    private readonly DirectMovementController _direct = new("navigation");
    private readonly NavigationPlanner _planner = new();
    private readonly NavigationFollower _follower = new();
    private readonly NativeNavigationObservation _observation = new();
    private readonly List<Vector3> _displayPath = new();
    private NavigationObservation _observed;
    private EncounterPlanningSnapshot? _snapshot;
    private EncounterPlanningSnapshot? _completedTerrain;
    private EncounterRoute? _route;
    private NavigationPath? _preparedPath;
    private DateTime _nextReplan;
    private int _generation, _routeIndex;
    private Vector2 _lastDirection;
    private DateTime _lastDiag;
    private string _lastDiagState = "";
    private string? _failure;
    private bool _disposed, _movementHeld;
    private Vector3 _goal, _commandedTarget;
    private float _arrivalRadius;

    internal NavigationSurface(Func<NavBitmap?> mapProvider, Action<string, object>? record = null,
        Func<EncounterPlanningSnapshot?>? snapshotProvider = null,
        Func<ulong>? playerId = null, Func<float>? movementSpeed = null)
    {
        _mapProvider = mapProvider;
        _record = record ?? ((_, _) => { });
        _snapshotProvider = snapshotProvider ?? EncounterPlanningSnapshotRegistry.Read;
        _playerId = playerId ?? (() => (ulong)(Service.LocalPlayer?.GameObjectId ?? 0));
        _direct.ObserveUserInput = true;
        _movementSpeed = movementSpeed ?? (() => DirectMovementController.TryReadNativeMotion(out var m)
            ? m.TargetSpeed > 0 ? m.TargetSpeed : m.BaseSpeed > 0 ? m.BaseSpeed : 6 : 6);
        _direct.ClampFinalStep = false;
        _direct.AfterWalkInput = OnNativeWrite;
        _direct.CanWriteWalkInput = () => !CastMovementGuard.ShouldWait();
    }
    internal bool NeedsSafetyPlan => MechanicExecutionState.MovementBlocked
        || _snapshotProvider() is { Hazards.Length: > 0, BossDead: false } snap
        && snap.Territory == (ushort)Service.ClientState.TerritoryType;

    internal string? Failure => _failure;
    internal string Status => _lastDiagState;
    internal string Solver => _planner.Solver;
    internal NavBitmap? CurrentBitmap => _mapProvider();
    internal INavigationGrid? CurrentTerrain => _planner.CurrentTerrain;
    internal IReadOnlyList<Vector3> RawPath => _planner.RawPath;
    internal IReadOnlyList<Vector3> SmoothedPath => _displayPath;
    internal int LegIndex => Math.Max(0, _follower.Index - 1);
    internal Vector3 CommandedTarget => _commandedTarget;
    internal Vector3 FinalDestination => _goal;
    internal float ArrivalRadius => _arrivalRadius;
    internal bool InputEnabled => _direct.Enabled;

    internal Action? AttackSuspended { get; init; }
    private NavigationRegionGoal? _attackRegion;
    internal NavigationRegionGoal AttackRegion => _attackRegion ??= AttackRegionGoal.Create(
        (map, seed) => CombatPositionNavigation.BitmapGroundPoint(map, seed),
        (stand, target, range, clearance) => CombatPositionNavigation.CheckAttack(
            stand, target, range, clearance, clearance).AvailableResult);

    private NavigationRegionGoal? _activeRegion;
    private NavigationRegionGoal? _chestRegion;

    internal NavigationState NavigateToAttack(ulong id, Vector3 target, Vector3 player, float range)
    {
        var ranges = DeepDungeon.Fsd.Core.CombatChaseRanges.ForAction(range);
        return NavigateRegion(AttackRegion, id, target, player, ranges.Approach, ranges.Hold);
    }

    internal NavigationState NavigateToChest(ChestTargetVerb chest, Vector3 player)
    {
        _chestRegion ??= new((map, seed) => CombatPositionNavigation.BitmapGroundPoint(map, seed),
            CanInteractFrom);
        var result = NavigateRegion(_chestRegion, chest.TargetId, chest.Position, player,
            chest.Range.Radius, chest.Range.Radius);
        return _chestRegion.Unreachable ? NavigationState.Failed : result;
    }

    private static bool? CanInteractFrom(Vector3 stand, Vector3 target, float radius, float clearance)
    {
        var range = new DeepDungeon.Fsd.Core.NavigationRange(radius);
        if (!(clearance == 0 ? range.Contains(stand, target) : range.CanStandAt(stand, target)))
            return false;
        return CombatPositionNavigation.Sight(stand, target, out _) switch
        {
            CombatSight.Clear => true,
            CombatSight.Blocked => false,
            _ => null
        };
    }

    private NavigationState NavigateRegion(NavigationRegionGoal region, ulong id, Vector3 target,
        Vector3 player, float approach, float hold)
    {
        if (_activeRegion != region || region.TargetId != id) Cancel();
        _activeRegion = region;
        region.Observe(id, target, approach, hold);
        return NavigateCore(target, player, approach, null, region);
    }

    public NavigationState Navigate(Vector3 dest, Vector3 playerPos, float arrivalRadius, NavigationPath? path = null)
    {
        if (_activeRegion != null) Cancel();
        return NavigateCore(dest, playerPos, arrivalRadius, path, null);
    }

    private NavigationState NavigateCore(Vector3 dest, Vector3 playerPos, float arrivalRadius,
        NavigationPath? path, NavigationRegionGoal? region)
    {
        if (_disposed) return NavigationState.Failed;
        var now = DateTime.UtcNow;
        var speed = _movementSpeed();
        CaptureDiagnosticContext(now, dest, playerPos, arrivalRadius, speed);
        var snap = _snapshotProvider();
        if (snap?.Territory != (ushort)Service.ClientState.TerritoryType) snap = null;
        if (snap is { BossDead: true })
        {
            // The encounter has ended, but its terrain still connects the player to the exit.
            if (!snap.Arena.Contains(new BossMod.WPos(playerPos.X, playerPos.Z) - snap.ArenaCenter)) snap = null;
            else
            {
                if (_completedTerrain?.EncounterId != snap.EncounterId)
                    _completedTerrain = snap with { Hazards = [], MovingObstacles = [], Chasers = [],
                        ChasingHazards = [], Knockbacks = [], TemporaryObstacles = [], Residence = null, Hints = null };
                snap = _completedTerrain;
            }
        }
        else if (snap != null) _completedTerrain = null;
        _diagnosticSnapshot = snap;
        if (CastMovementGuard.ShouldWait())
        {
            ReleaseDirect();
            DropRoute();
            _nextReplan = now;
            Diag(now, "waiting-cast");
            return NavigationState.Moving;
        }
        if (snap is { BossDead: false } && now - snap.CapturedAt > MaxSnapshotAge)
        { ReleaseDirect(); Diag(now, "waiting-snapshot"); return NavigationState.StuckRepathing; }
        if (snap?.EncounterId != _snapshot?.EncounterId || snap?.Territory != _snapshot?.Territory)
        {
            ++_generation;
            // On the first hazard frame the timed solver can assess the remaining
            // spatial route against the new hazards before searching for a detour.
            if (!(snap is { UsesDutyTerrain: true } && _snapshot == null && _route is { Reason: "astar" }))
                DropRoute();
            _planner.Reset();
            _nextReplan = now;
        }
        // Recheck each new ordinary-floor hint frame immediately, even when
        // the objective, zone count and destination all stayed the same.
        if (snap is { UsesDutyTerrain: true } && !ReferenceEquals(snap, _snapshot))
            _nextReplan = now;
        _snapshot = snap;
        var held = MechanicExecutionState.MovementBlocked || snap?.Hints?.HoldMovement(now) == true;
        _observed = _observation.Capture(playerPos, now, held, snap?.Hints?.PendingKnockback == true);
        if (_observed.MovementLocked != _movementHeld) _nextReplan = now;
        _movementHeld = _observed.MovementLocked;
        var targetChanged = Vector3.DistanceSquared(dest, _goal) > .01f || arrivalRadius != _arrivalRadius;
        _goal = dest; _arrivalRadius = arrivalRadius;
        if ((targetChanged && region == null) || !ReferenceEquals(path, _preparedPath)) _nextReplan = now;
        _preparedPath = path;
        if (now >= _nextReplan || (region is { Searching: true } && snap == null))
        {
            _nextReplan = now + ReplanInterval;
            var map = _mapProvider();
            // Do not use a bitmap belonging to another region of this territory.
            if (map != null && !map.ContainsWorld(playerPos)) map = null;
            var request = new NavigationRequest(now, new(playerPos.X, playerPos.Z), new(dest.X, dest.Z),
                arrivalRadius, speed, map, snap)
            { Region = region, PlayerHeight = playerPos.Y, PlayerId = _playerId(), Observation = _observed, Continuation = _route, ContinuationIndex = _follower.Index, Follower = _follower, PreparedPath = path };
            var planId = ++_planSequence;
            try
            {
                var completion = MeasurePlan(request, CancellationToken.None);
                var next = completion.Route;
                var decision = next.Kind == EncounterRouteKind.NoRoute ? "no-route" : "adopted";
                if (snap != null)
                {
                    var timed = new EncounterRouteRequest(snap, now, request.PlayerId, request.Position,
                        request.Goal, arrivalRadius, speed, _lastDirection) { Observation = _observed };
                    _record("coord-encounter-plan", BuildPlanRecord(planId, _generation, _generation,
                        timed, completion, decision, DateTime.UtcNow, null));
                }
                if (next.Kind == EncounterRouteKind.Ready)
                {
                    _failure = null;
                    if (!ReferenceEquals(_route, next))
                    {
                        _route = next; _follower.Adopt(next); _routeIndex = 0;
                        _displayPath.Clear();
                        foreach (var point in next.Points) _displayPath.Add(new(point.Position.X, playerPos.Y, point.Position.Y));
                    }
                    _adoptedPlanId = planId;
                }
                else
                {
                    // A region path must still lead to an accepted stand. Local hazard
                    // paths also need a fresh safety check before further movement.
                    _failure = next.Reason;
                    if (region != null || snap is { UsesDutyTerrain: true }) DropRoute();
                }
            }
            catch (Exception ex)
            {
                _failure = ex.Message;
                if (region != null || snap is { UsesDutyTerrain: true }) DropRoute();
                _record("navigation-plan-fault", new { planId, error = ex.ToString() });
            }
        }
        if (_route == null)
        { ReleaseDirect(); Diag(now, _failure ?? "planning"); return NavigationState.StuckRepathing; }
        var command = _follower.Step(_observed);
        _routeIndex = _follower.Index;
        _commandedTarget = new(command.Target.X, playerPos.Y, command.Target.Y);
        try
        {
            NativeNavigationOutput.Apply(_direct, command, playerPos);
            _issuedPlanId = _adoptedPlanId;
        }
        catch (Exception ex)
        {
            _failure = ex.Message; ReleaseDirect(); _nextReplan = now;
            Diag(now, "controller-unavailable"); return NavigationState.StuckRepathing;
        }
        if (command.Kind == NavigationCommandKind.Replan)
        { _nextReplan = now; Diag(now, "game-driven-handoff"); return NavigationState.Moving; }
        if (command.Correction is { } corrected)
        {
            _observation.CorrectPosition(corrected);
            _record("coord-endpoint-correction", new { planId = _adoptedPlanId,
                fromX = playerPos.X, fromZ = playerPos.Z, toX = corrected.X, toZ = corrected.Y });
        }
        if (command.Kind == NavigationCommandKind.Move)
        {
            var direction = command.Target - _observed.Position;
            if (direction.LengthSquared() > 1e-6f) _lastDirection = Vector2.Normalize(direction);
            Diag(now, "moving"); return NavigationState.Moving;
        }
        if (_observed.MovementLocked)
        { Diag(now, "movement-locked"); return NavigationState.Moving; }
        var inside = snap == null || snap.Arena.Contains(new BossMod.WPos(playerPos.X, playerPos.Z) - snap.ArenaCenter);
        var arrived = region?.Holding ?? (inside && new DeepDungeon.Fsd.Core.NavigationRange(arrivalRadius).HasArrived(playerPos, dest));
        Diag(now, arrived ? "holding-in-range" : command.Kind == NavigationCommandKind.Complete ? "replanning" : "waiting-seg");
        if (command.Kind == NavigationCommandKind.Complete && !arrived)
        { _nextReplan = now; return NavigationState.StuckRepathing; }
        return arrived ? NavigationState.Arrived : NavigationState.Moving;
    }

    private void DropRoute()
    {
        _route = null;
        _follower.Reset();
        _routeIndex = 0;
        _displayPath.Clear();
    }

    private void ReleaseDirect() { if (_direct.Enabled) _direct.Release(); }
    private void Diag(DateTime now, string state) => RecordDiagnosticState(now, state);
    public void Cancel()
    {
        if (_activeRegion is { } active)
        {
            active.Reset();
            if (active == _attackRegion) AttackSuspended?.Invoke();
            _activeRegion = null;
        }
        if (_route != null || _direct.Enabled) Diag(DateTime.UtcNow, "cancel-requested");
        ++_generation; _route = null; _preparedPath = null; _routeIndex = 0; _failure = null;
        _follower.Reset(); _planner.Reset(); _displayPath.Clear(); _nextReplan = default;
        // Keep observation history through claim changes and pending-effect gaps.
        ReleaseDirect();
    }
    public void Dispose()
    {
        if (_disposed) return;
        Cancel(); _observation.Reset(); _disposed = true;
        _direct.AfterWalkInput = null; _direct.CanWriteWalkInput = null; _direct.Dispose();
    }
}
