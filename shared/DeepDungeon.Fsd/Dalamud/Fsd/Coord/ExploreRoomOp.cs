using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// Search-objective driver: the domain planner (AutoPilotExecutor via
/// ISearchBoundary) owns room plans and evidence truth; this op owns bounded
/// movement and typed failure. Room entry uses the sustained room index —
/// boundary flap never triggers a re-entry reset. Waypoint navigation runs
/// through NavigateOp; an unreachable waypoint is skipped (advance) rather
/// than retried forever; a room that can't be re-entered after K attempts is
/// cleared so the plan moves on.
/// </summary>
internal sealed class ExploreRoomOp : FloorOperation
{
    private const int OpPriority = 10;
    private const int NavBudgetPerWaypoint = 4;
    private const int MaxRoomReentries = 3;
    private static readonly TimeSpan ReentryWindow = TimeSpan.FromSeconds(8);

    private NavigateOp? _nav;
    private Vector3? _navDest;
    // Chest work is the shared OpenChestOp, bound to the waypoint AND the
    // head's stable work key — a head change discards the whole child so
    // a stale attempt can never resolve a newer head's chest.
    private OpenChestOp? _chestOp;
    private RoomWaypoint? _chestWaypoint;
    private string? _chestWorkKey;
    private TimeSpan _outsideAccum;
    private int _outsideCtx = -1;
    private DateTime _lastTick = DateTime.MinValue;
    private int _reentries;
    private int? _ctxRoom;
    private Vector3? _wpPos;
    private string _phase = "init";
    private DateTime _finishDeferredSince = DateTime.MinValue;
    private int _forcedFinishes;
    private static readonly TimeSpan FinishDeferredLimit = TimeSpan.FromSeconds(20);

    internal override string Describe() =>
        $"ctx={_ctxRoom?.ToString() ?? "-"} wp={_wpPos} nav={_nav?.Attempts}/{_nav?.MaxAttempts} re={_reentries} ph={_phase} forced={_forcedFinishes}";

    public ExploreRoomOp(FloorObjectiveKind objective)
        : base(objective, OpPriority, maxAttempts: MaxRoomReentries)
    {
    }

    private static bool IsChest(RoomObjectiveType t) =>
        t is RoomObjectiveType.ChestBronze or RoomObjectiveType.ChestSilver
            or RoomObjectiveType.ChestGold or RoomObjectiveType.ChestBanded;

    /// <summary>Release the chest child — it is never allowed to resolve
    /// work it did not own, and its identity is discarded, not reused.</summary>
    private void ClearChestWork()
    {
        _chestOp?.OnRetired();
        _chestOp = null;
        _chestWaypoint = null;
        _chestWorkKey = null;
    }

    internal override OpStatus Tick(in FloorFacts facts, INativeBoundary native, DateTime now,
        in OpAuthority authority)
    {
        // Tick-delta for the cumulative reentry clock — computed at the top
        // so early-return branches can't sneak a giant delta into the next
        // reentry window.
        var dt = _lastTick == DateTime.MinValue ? TimeSpan.Zero : now - _lastTick;
        _lastTick = now;
        Claim = MoveClaim.None;

        var search = native.Search;
        if (search == null)
        {
            Failure = FailureKind.Terminal;
            return OpStatus.Failed;
        }
        if (search.ExecutorComplete)
        {
            ClearChestWork();
            return OpStatus.Succeeded;
        }

        // Telemetry context for Describe before any branch.
        _ctxRoom = search.ContextRoomIndex;
        var wp = search.CurrentWaypoint;
        _wpPos = wp?.Position;

        // Chest work runs FIRST and owns its whole approach — the shared
        // OpenChestOp navigates from anywhere; the room-context/reentry
        // machinery below only serves non-chest waypoints.
        if (wp is { } wpv && IsChest(wpv.Type))
        {
            _phase = "chest";
            var headKey = search.CurrentWorkKey;
            if (_chestOp != null &&
                (_chestWaypoint != wpv || _chestWorkKey != headKey))
                ClearChestWork();   // identity changed → discard stale child
            if (_chestOp == null)
            {
                _nav = null;
                _navDest = null;
                _chestOp = new OpenChestOp(wpv, maxAttempts: NavBudgetPerWaypoint);
                _chestWaypoint = wpv;
                _chestWorkKey = headKey;
            }
            var chestStatus = _chestOp.Tick(facts, native, now, authority);
            if (chestStatus == OpStatus.Running)
            {
                Claim = _chestOp.Claim;
                return OpStatus.Running;
            }
            // Terminal → advance the CAPTURED waypoint+key only; the guarded
            // advance ignores us if the head has already moved on.
            var chestOutcome = chestStatus switch
            {
                OpStatus.Succeeded => WaypointOutcomeKind.Completed,
                OpStatus.PolicySkipped => WaypointOutcomeKind.PolicySkipped,
                _ => WaypointOutcomeKind.Failed,
            };
            var chestReason = chestStatus switch
            {
                OpStatus.Succeeded => "chest-opened",
                OpStatus.PolicySkipped => "chest-overcap-policy",
                _ => _chestOp.Failure?.ToString() ?? "chest-failed",
            };
            search.AdvanceWaypoint(_chestWorkKey, _chestWaypoint!.Value,
                chestOutcome, chestReason);
            ClearChestWork();
            return OpStatus.Running;
        }
        ClearChestWork();   // non-chest or absent waypoint → no stale child

        // No active room context → let the planner build one for the current
        // plan entry; a null context afterward means the plan is done.
        var ctxRoom = _ctxRoom;
        if (ctxRoom == null)
        {
            _phase = "start-search";
            search.TryStartNextRoomSearch(facts.PlayerPosition, facts.Evidence);
            if (search.ExecutorComplete)
                return OpStatus.Succeeded;
            Claim = MoveClaim.Hold(Priority);
            return OpStatus.Running;
        }

        // Outside the context room — facts.RoomIndex is already the sustained
        // value, so being here means we genuinely left, not a boundary flap.
        // The out-of-room clock ACCUMULATES per context: a boundary hover that
        // flickers room==ctx must not reset it, or the window never expires.
        if (facts.RoomIndex != ctxRoom.Value)
        {
            _phase = "reentry";
            if (_outsideCtx != ctxRoom.Value)
            {
                _outsideCtx = ctxRoom.Value;
                _outsideAccum = TimeSpan.Zero;
            }
            _outsideAccum += dt;
            if (_outsideAccum > ReentryWindow)
            {
                _outsideAccum = TimeSpan.Zero;
                if (++_reentries >= MaxRoomReentries)
                {
                    _reentries = 0;
                    _outsideCtx = -1;
                    // Domain finish decides the outcome (Deferred/Failed per
                    // policy) and regenerates the plan — never bare-clear,
                    // or the same room rebuilds forever.
                    search.FinishRoom();
                    return OpStatus.Running;
                }
            }
            var center = search.RoomCenter(ctxRoom.Value);
            Claim = center.HasValue ? MoveClaim.To(Priority, center.Value, 2.5f) : MoveClaim.Hold(Priority);
            return OpStatus.Running;
        }
        _reentries = 0;
        _phase = "in-room";

        if (wp == null)
        {
            _phase = "finish-room";
            _nav = null;
            _navDest = null;
            // Waypoints exhausted → room finish through the domain path
            // (outcome apply + progress marks + plan regeneration). Deferred
            // finishes are bounded: waiting forever for indicator evidence is
            // the same livelock the legacy runner had — force-advance past
            // the window so the plan moves on.
            var finish = search.FinishRoom();
            if (finish == RoomFinishResult.Done)
            {
                _finishDeferredSince = DateTime.MinValue;
            }
            else if (finish == RoomFinishResult.Rejected)
            {
                // No active objective execution — the finish can never apply
                // outcomes; force-advance now instead of waiting out the
                // deferred window (also stops the reject-log spam).
                _finishDeferredSince = DateTime.MinValue;
                ++_forcedFinishes;
                search.SkipPlanHead();
                search.ClearRoomContext();
            }
            else
            {
                if (_finishDeferredSince == DateTime.MinValue)
                    _finishDeferredSince = now;
                if (now - _finishDeferredSince > FinishDeferredLimit)
                {
                    _finishDeferredSince = DateTime.MinValue;
                    ++_forcedFinishes;
                    // Drop the plan head AND the context — clearing alone would
                    // rebuild the same room and loop the deferred finish.
                    search.SkipPlanHead();
                    search.ClearRoomContext();
                }
                // Deferred on pending evidence (e.g. hoard indicator): keep
                // covering ground toward the NEXT planned room instead of
                // holding — standing still guarantees the indicator never
                // resolves, which is the same livelock by another name.
                var nextCenter = search.NextRoomCenter();
                Claim = nextCenter.HasValue
                    ? MoveClaim.To(Priority, nextCenter.Value, 2.5f)
                    : MoveClaim.Hold(Priority);
                return OpStatus.Running;
            }
            Claim = MoveClaim.Hold(Priority);
            return OpStatus.Running;
        }
        _finishDeferredSince = DateTime.MinValue;

        // Navigate to the current waypoint — per-waypoint bounded budget.
        if (_navDest != wp.Value.Position || _nav == null)
        {
            _nav = new NavigateOp(wp.Value.Position, OpPriority, NavBudgetPerWaypoint,
                wp.Value.ArrivalRadius > 0 ? wp.Value.ArrivalRadius : NavigationRange.MovementTolerance);
            _navDest = wp.Value.Position;
        }
        var navStatus = _nav.Tick(facts, native, now, authority);
        Claim = _nav.Claim;
        if (navStatus == OpStatus.Failed)
        {
            // Unreachable waypoint → skip it like the legacy executor did,
            // not a floor failure.
            _nav = null;
            _navDest = null;
            search.AdvanceWaypoint(wp.Value, WaypointOutcomeKind.Failed, "unreachable");
            return OpStatus.Running;
        }
        if (navStatus != OpStatus.Succeeded)
            return OpStatus.Running;

        _nav = null;
        _navDest = null;
        search.AdvanceWaypoint(wp.Value, WaypointOutcomeKind.Completed, "arrived");
        return OpStatus.Running;
    }

    /// <summary>The chest child's attempt is the floor's authoritative
    /// interaction while one is bound — overcap/item relief binds to it.</summary>
    internal override ChestInteractionAttempt? ActiveChestAttempt =>
        _chestOp?.ActiveChestAttempt;
    internal override bool ProtectsCommit => _chestOp?.ProtectsCommit == true;
    internal override void OnNavArrived() => _chestOp?.OnNavArrived();
    internal override void OnNavFailed(DateTime now)
    {
        if (_chestOp != null)
            _chestOp.OnNavFailed(now);
        else
            _nav?.OnNavFailed(now);
    }
    internal override void OnRetired() => ClearChestWork();
}
