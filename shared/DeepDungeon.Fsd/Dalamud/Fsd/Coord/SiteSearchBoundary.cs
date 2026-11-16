using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// ISearchBoundary over the objective site pool — the third boundary
/// implementation, after the executor's room contexts and the coverage
/// anchor set. "The next thing to do" is the driver's committed head
/// site; room finish/skip collapse open sites attributed to the room.
/// There is no plan head and no deferred finish: the gate owns
/// commitment stability, the ledger owns what remains.
/// </summary>
internal sealed unsafe class SiteSearchBoundary : ISearchBoundary
{
    private readonly ObjectiveSiteDriver _driver;
    private readonly DdAccessor _dd;
    private readonly Func<Vector3> _playerPos;
    private readonly Func<FloorObjectEvidenceSnapshot?> _evidence;
    private readonly HashSet<int> _quotaSearchedRooms = new();
    private readonly HashSet<int> _quotaFailedRooms = new();

    public QuotaSearchResult NextQuotaSearchRoom(Vector3 playerPosition)
    {
        var dd = _dd();
        if (dd == null) return default;
        var current = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if ((uint)current >= RoomGraph.MaxRooms) return default;
        var rooms = RoomGraph.BuildReachableRoomOrder(dd, current);
        var route = new List<int>();
        QuotaSearchRoom? best = null;
        var bestHops = int.MaxValue;
        var bestDistance = float.PositiveInfinity;
        foreach (var room in rooms)
        {
            if (_quotaSearchedRooms.Contains(room) || _quotaFailedRooms.Contains(room)
                || RoomCenter(room) is not { } center
                || !RoomGraph.TryBuildRoomRoute(dd, current, room, route)) continue;
            var distance = Vector3.DistanceSquared(playerPosition, center);
            if (route.Count > bestHops || route.Count == bestHops && distance >= bestDistance) continue;
            best = new(room, center);
            bestHops = route.Count;
            bestDistance = distance;
        }
        if (best == null)
        {
            // Exhausting one patrol does not complete the kill quota. Repeat
            // the reachable rooms, including rooms whose last approach failed.
            _quotaSearchedRooms.Clear();
            _quotaFailedRooms.Clear();
        }
        return new(true, best);
    }

    public void CompleteQuotaSearchRoom(int roomIndex, bool reached)
    {
        if (reached) _quotaSearchedRooms.Add(roomIndex);
        else _quotaFailedRooms.Add(roomIndex);
    }

    internal SiteSearchBoundary(
        ObjectiveSiteDriver driver, DdAccessor dd, Func<Vector3> playerPos,
        Func<FloorObjectEvidenceSnapshot?>? evidence = null)
    {
        _driver = driver;
        _dd = dd;
        _playerPos = playerPos;
        _evidence = evidence ?? (() => null);
    }

    /// <summary>The controller's once-per-tick activity sample — forwarded
    /// to the driver so passage/combat transitions dirty the route now.</summary>
    internal void SetActivity(in FloorActivity activity) =>
        _driver.SetActivity(activity);

    /// <summary>Per-frame decision tick — driven from the floor update next
    /// to the coverage update, before the coord pipeline ticks.</summary>
    internal void Tick()
    {
        var dd = _dd();
        if (dd != null)
            _driver.TickDecision(dd, _playerPos(), _evidence());
    }

    public bool ExecutorComplete => _driver.Sites.Count > 0 && _driver.OpenCount == 0;

    /// <summary>The committed head site as the search waypoint. Mob
    /// sites carry the engage radius while approaching; once the engage
    /// latch is on, the radius goes effectively infinite — nav reports
    /// arrived every tick and pushes nothing, so the combat channel
    /// owns the player until the latch releases.</summary>
    public RoomWaypoint? CurrentWaypoint =>
        _driver.Head is { } h
            ? new RoomWaypoint(h.Position, WaypointType(h))
            : null;

    /// <summary>The committed head's kind — the pipeline dispatches
    /// execution by it (Quota→ChaseMobsOp, Passage→ExitPassageOp).</summary>
    internal ObjectiveSiteKind? HeadKind => _driver.Head?.Kind;

    /// <summary>The committed work's identity — the op binds to this token
    /// and reports its outcome back through it, never to a newer head.</summary>
    internal SiteWorkToken HeadToken => _driver.HeadToken;

    /// <summary>True when the driver runs the knowledge-driven normal
    /// path (typed work ops) rather than the legacy fact-order path.</summary>
    internal bool KnowledgeDriven => _driver.KnowledgeDriven;

    /// <summary>Floor's native hoard acquisition as knowledge sees it —
    /// fed to reveal ops whose postcondition includes acquisition.</summary>
    internal bool KnowledgeHoardAcquired => _driver.KnowledgeHoardAcquired;

    /// <summary>Pipeline→scheduler channel: the bound op reports whether
    /// a meaningful action is in progress; the scheduler preserves the
    /// committed head only while true.</summary>
    internal bool CommitProtected
    {
        set => _driver.CommitProtected = value;
    }

    /// <summary>Typed outcome for a captured work token — updates the
    /// original floor work, never a different head.</summary>
    internal void ReportWorkOutcome(SiteWorkToken token, SiteWorkOutcome outcome, string? reason) =>
        _driver.ReportWorkOutcome(token, outcome, reason);

    /// <summary>The committed head itself — telemetry reads kind/key/pos.</summary>
    internal ObjectiveSite? Head => _driver.Head;

    /// <summary>The committed head's stable work key — a legacy adapter
    /// binds child work to this identity, not to a mutable head.</summary>
    public string? CurrentWorkKey => _driver.Head?.Key;

    private static RoomObjectiveType WaypointType(in ObjectiveSite s) =>
        s.Kind switch
        {
            ObjectiveSiteKind.ConfirmedChest => s.Chest switch
            {
                FloorChestKind.Silver => RoomObjectiveType.ChestSilver,
                FloorChestKind.Gold => RoomObjectiveType.ChestGold,
                FloorChestKind.Banded => RoomObjectiveType.ChestBanded,
                _ => RoomObjectiveType.ChestBronze,
            },
            // Hoard is a walk-over pickup, not a chest entity — the chest
            // interaction protocol's target-missing probe can never find
            // it and deferred the site forever (observed live: parked on
            // the hoard for 20s+ in an infinite deferral loop).
            ObjectiveSiteKind.Hoard => RoomObjectiveType.Coverage,
            ObjectiveSiteKind.HoardIndicator => RoomObjectiveType.Trap,
            _ => RoomObjectiveType.Coverage,
        };

    public int? ContextRoomIndex
    {
        get
        {
            // The committed head's room — never the player's room: falling
            // back to the player position attributed the head's outcome
            // to whatever room the player happened to stand in.
            if (_driver.Head is { } h && h.RoomIndex >= 0)
                return h.RoomIndex;
            return null;
        }
    }

    /// <summary>The driver is always ready once the pool produced sites —
    /// there is no per-room setup step.</summary>
    public bool TryStartNextRoomSearch(Vector3 playerPos,
        FloorObjectEvidenceSnapshot? evidence) => _driver.Head != null || _driver.OpenCount > 0;

    /// <summary>The op reports the head's outcome — Completed retires the
    /// site; Failed marks it unreachable; Deferred keeps it open for the
    /// next decision tick; PolicySkipped is the driver's explicit policy
    /// conclusion, NOT completion. A waypoint that no longer matches the
    /// current head belongs to a discarded child and is ignored.</summary>
    public void AdvanceWaypoint(RoomWaypoint waypoint, WaypointOutcomeKind outcome,
        string reason)
    {
        if (CurrentWaypoint is not { } current || current != waypoint)
            return;
        switch (outcome)
        {
            case WaypointOutcomeKind.Completed:
                _driver.ReportHeadOutcome(true, null);
                break;
            case WaypointOutcomeKind.PolicySkipped:
                _driver.ReportHeadPolicySkipped(reason);
                break;
            case WaypointOutcomeKind.Failed:
                _driver.ReportHeadOutcome(false, reason);
                break;
            case WaypointOutcomeKind.Deferred:
                break;
        }
    }

    public Vector3? RoomCenter(int roomIndex)
    {
        var dd = _dd();
        return dd != null && MapPos.TryGetRoomCenter(dd, roomIndex, out var center)
            ? center
            : null;
    }

    public void ClearRoomContext() => _driver.ClearCommit();

    /// <summary>Room finish under site semantics: the head's room proved
    /// unworkable — fail only the head site (Retryable via driver), not
    /// the whole room's sites. Rooms are cost indices, not kill units.</summary>
    public RoomFinishResult FinishRoom()
    {
        _driver.ReportHeadOutcome(false, "room-abandon");
        return RoomFinishResult.Done;
    }

    /// <summary>Force-skip = abandon the current head.</summary>
    public bool SkipPlanHead()
    {
        _driver.ReportHeadOutcome(false, "plan-skip");
        return true;
    }

    /// <summary>The advisory tail's next site outside the current room —
    /// the productive destination while the head is finishing.</summary>
    public Vector3? NextRoomCenter()
    {
        var room = ContextRoomIndex;
        foreach (var id in _driver.Tail)
            foreach (var s in _driver.Sites)
                if (s.Id == id && s.RoomIndex != room)
                    return s.RoomIndex >= 0 && RoomCenter(s.RoomIndex) is { } c ? c : s.Position;
        return null;
    }

    /// <summary>Center of the next-hop room on the flag-verified room-graph
    /// route — identical to the coverage boundary's honest cross-room
    /// step.</summary>
    public Vector3? NextHopRoomCenter(int fromRoom, int toRoom)
    {
        if (fromRoom < 0 || toRoom < 0 || fromRoom == toRoom)
            return null;
        var dd = _dd();
        if (dd == null)
            return null;
        var route = new List<int>();
        if (!RoomGraph.TryBuildRoomRoute(dd, fromRoom, toRoom, route) || route.Count < 2)
            return null;
        return RoomCenter(route[1]);
    }
}
