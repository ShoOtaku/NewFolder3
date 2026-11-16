using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// Per-floor coordination pipeline: observation -> arbiter decision ->
/// objective operation -> movement arbitration -> native boundary.
/// Handles only the v1 slice (ordinary-floor loop); everything else falls
/// back to the legacy path at the caller's seam.
/// </summary>
internal sealed class CoordPipeline
{
    private readonly FloorObservation _obs = new();
    private readonly MovementArbiter _arbiter = new();
    private Vector3? _idleAnchor;
    private readonly EncounterDriver _encounter = new();
    private readonly List<(FloorOperation op, MoveClaim claim)> _claims = new(8);
    private readonly Action<string, object> _record;
    private FloorOperation? _objectiveOp;

    // Held/masked authority accounting — last frame's arbitration result,
    // handed to ops as OpAuthority so effort budgets burn only while the
    // op's claim actually won. _objectiveMaskedSec is starvation
    // telemetry: an op that never wins must not fail itself, but a
    // starved head must stay visible in coord-tick.
    private DateTime _lastTickUtc = DateTime.MinValue;
    private float _lastDt;
    private FloorOperation? _lastWinner;
    private readonly HashSet<FloorOperation> _lastMasked = new(4);
    private float _objectiveMaskedSec;
    private FloorObjectiveKind _lastObjective = FloorObjectiveKind.None;
    private readonly Dictionary<FloorObjectiveKind, int> _objectiveFails = new();
    private int _lastFloor = -1;
    private INativeBoundary? _lastBoundary;
    private SiteWorkToken _boundToken;
    private ObjectiveSiteKind? _boundKind;
    private bool _lastWorkDriven;
    // Floor-scoped work-op cache: suspension rebinds the SAME attempt;
    // completion, failure, explicit policy skip or teardown retires it.
    private readonly Dictionary<SiteWorkToken, FloorOperation> _workOperations = new();
    private const int MaxObjectiveFails = 3;
    // Latest assembled facts — ops' late-bound providers read this snapshot
    // rather than stashing per-field copies of what facts already carries.
    private FloorFacts _lastFacts;

    /// <summary>An objective that keeps failing stops taking over — the
    /// legacy path resumes it for the rest of the floor (graceful
    /// degradation instead of infinite op recreation).</summary>
    internal bool IsObjectiveBlocked(FloorObjectiveKind objective) =>
        _objectiveFails.TryGetValue(objective, out var n) && n >= MaxObjectiveFails;


    internal CoordPipeline(Action<string, object> recordEvent)
    {
        _record = recordEvent;
    }

    /// <summary>The v1 slice: ordinary (non-boss, non-terminal) floors only.</summary>
    internal bool Handles(bool isBossFloor, bool isTerminalFloor) => !isBossFloor && !isTerminalFloor;

    internal FloorObjectiveKind CurrentObjective => _objectiveOp?.Objective ?? FloorObjectiveKind.None;

    /// <summary>Last fact-selected objective kind — for ledger/reporting,
    /// not for control flow.</summary>
    internal FloorObjectiveKind SelectedObjective => _selectedObjective;
    /// <summary>The chest attempt the bound work op owns, if any — the
    /// controller's item/overcap channel binds to this, not a sidecar.</summary>
    internal ChestInteractionAttempt? ActiveChestAttempt => _objectiveOp?.ActiveChestAttempt;
    private FloorObjectiveKind _selectedObjective = FloorObjectiveKind.None;

    /// <summary>
    /// Entity-dynamic objective selection, in local-opportunity order:
    /// visible chest → open passage → coverage traversal → leftover-hunt.
    /// No domain decision is consulted.
    /// </summary>
    private static FloorObjectiveKind SelectObjective(in FloorFacts facts, CoverageSet? coverage)
    {
        if (facts.ChestWaypoint != null)
            return FloorObjectiveKind.OpenVisibleBandedChest;
        if (facts.PassageOpen)
            return FloorObjectiveKind.EnterPassage;
        if (coverage != null && !coverage.AllVisited)
            return FloorObjectiveKind.DiscoverHoard;
        if (coverage != null)
            return FloorObjectiveKind.FinishCombatBeforePassage;
        return FloorObjectiveKind.None;
    }

    internal void Tick(
        int floor,
        int rawRoomIndex,
        DateTime now,
        Vector3 playerPos,
        bool inCombat,
        bool playerDead,
        bool passageOpen,
        bool planForHazards,
        ObjectiveArbiterSnapshot snapshot,
        Vector3? passageDestination,
        Vector3? passageRoomCenter,
        int passageRoomIndex,
        FloorObjectEvidenceSnapshot? evidence,
        RoomWaypoint? chestWaypoint,
        CoverageSet? coverage,
        INativeBoundary native,
        Vector3? passageActorPosition = null)
    {
        _lastBoundary = native;
        if (floor != _lastFloor)
        {
            _objectiveFails.Clear();
            // A floor change retires every cached work attempt AND a
            // bound op that was never cached (legacy boundary binding) —
            // cached ops are floor-scoped like the knowledge they serve.
            var boundCached = _objectiveOp != null &&
                !string.IsNullOrEmpty(_boundToken.Key) &&
                _workOperations.TryGetValue(_boundToken, out var cached) &&
                ReferenceEquals(cached, _objectiveOp);
            foreach (var op in _workOperations.Values)
                op.OnRetired();
            _workOperations.Clear();
            if (!boundCached)
                _objectiveOp?.OnRetired();
            _objectiveOp = null;
            _boundToken = default;
            _boundKind = null;
            _claims.Clear();
            _lastWinner = null;
            _lastMasked.Clear();
            _objectiveMaskedSec = 0;
            _idleAnchor = null;
            _lastFloor = floor;
        }
        var facts = _obs.Assemble(floor, rawRoomIndex, now, playerPos, inCombat,
            playerDead, passageOpen, snapshot, evidence, chestWaypoint,
            passageDestination, passageRoomCenter, passageRoomIndex,
            passageActorPosition);
        _lastFacts = facts;

        // Frame delta feeding the authority accounting — clamped so a
        // hitch can't dump a huge dt into effort budgets.
        var dt = _lastTickUtc == DateTime.MinValue ? 0f
            : Math.Clamp((float)(now - _lastTickUtc).TotalSeconds, 0f, 0.5f);
        _lastTickUtc = now;
        OpAuthority AuthorityFor(FloorOperation? op) => new(
            op != null && _lastWinner == op,
            op != null && _lastWinner == op ? _lastDt : 0f,
            op != null && _lastMasked.Contains(op) ? _lastDt : 0f);

        _claims.Clear();
        // Objective dispatch — knowledge-driven mode executes the
        // committed work only (typed token binding); the legacy path keeps
        // its fact-order selection for non-coord floors.
        if (native.Search is SiteSearchBoundary workSb && workSb.KnowledgeDriven)
        {
            _lastWorkDriven = true;
            EnsureWorkOp(workSb, facts);
            _selectedObjective = _objectiveOp?.Objective ?? FloorObjectiveKind.None;
        }
        else
        {
            _lastWorkDriven = false;
            var objective = SelectObjective(in facts, coverage);
            // Head-kind dispatch — the site pool's committed head
            // overrides the fact-order objective for execution shape.
            if (native.Search is SiteSearchBoundary sb)
                objective = sb.HeadKind switch
                {
                    ObjectiveSiteKind.Quota => FloorObjectiveKind.FinishCombatBeforePassage,
                    ObjectiveSiteKind.Passage => FloorObjectiveKind.EnterPassage,
                    _ => objective,
                };
            _selectedObjective = objective;
            EnsureObjectiveOp(objective, facts);
        }
        // Only a RUNNING operation protects the commit — a retained
        // Failed op must never pin the head through ProtectsCommit.
        var protectsCommit = false;
        if (_objectiveOp != null)
        {
            var status = _objectiveOp.Tick(facts, native, now, AuthorityFor(_objectiveOp));
            if (status == OpStatus.Running)
            {
                protectsCommit = _objectiveOp.ProtectsCommit;
                _claims.Add((_objectiveOp, _objectiveOp.Claim));
            }
            else
                RetireObjectiveOp(status, native);
        }
        // Publish the bound op's actual in-progress state AFTER ticking —
        // the next scheduler decision sees the real dwell/interaction
        // protection, not the pre-tick snapshot.
        if (native.Search is SiteSearchBoundary postSb && postSb.KnowledgeDriven)
            postSb.CommitProtected = protectsCommit;

        // Arbitration resolves the winner; the pipeline dispatches the
        // winning verb through the boundary — the arbiter never sees what
        // the claim does, the boundary never sees who lost.
        var winner = _arbiter.Resolve(_claims);
        var outcome = ExecuteFloorMovement(native, winner?.Claim.Verb, planForHazards && !playerDead, playerPos);
        if (outcome == NavOutcome.Arrived && winner?.Claim.Verb is ChestTargetVerb)
            winner.OnNavArrived();
        if (outcome == NavOutcome.Failed)
        {
            winner?.OnNavFailed(now);
            // Every failure episode gets its own record — LastNavFailure
            // only keeps the latest string, and consecutive episodes with
            // different destinations used to overwrite each other.
            _record("nav-fail", new
            {
                op = winner?.GetType().Name,
                dest = winner?.Claim.Verb is GoToVerb g
                    ? $"{g.Dest.X:0.0},{g.Dest.Z:0.0}"
                    : winner?.Claim.Verb?.GetType().Name,
                reason = native.LastNavFailure,
                pos = $"{facts.PlayerPosition.X:0.0},{facts.PlayerPosition.Z:0.0}",
                head = native.Search is SiteSearchBoundary hf && hf.Head is { } hh
                    ? $"{hh.Kind}:{hh.Key} @{hh.Position.X:0.0},{hh.Position.Z:0.0}"
                    : null,
            });
        }

        // Authority accounting for the NEXT tick: who held, who was masked.
        _lastWinner = winner;
        _lastDt = dt;
        _lastMasked.Clear();
        foreach (var (op, claim) in _claims)
            if (op != winner && claim.Verb != null)
                _lastMasked.Add(op);
        if (_objectiveOp != null && _lastMasked.Contains(_objectiveOp))
            _objectiveMaskedSec += dt;

        _record("coord-tick", new
        {
            floor,
            objective = _selectedObjective.ToString(),
            op = _objectiveOp?.GetType().Name,
            opState = _objectiveOp?.Describe(),
            pos = $"{facts.PlayerPosition.X:0.0},{facts.PlayerPosition.Z:0.0}",
            head = native.Search is SiteSearchBoundary hb && hb.Head is { } h
                ? $"{h.Kind}:{h.Key} @{h.Position.X:0.0},{h.Position.Z:0.0} " +
                  $"d={Vector3.Distance(facts.PlayerPosition, h.Position):0.0}"
                : null,
            navFailure = native.LastNavFailure,
            mechanicEngaged = planForHazards,
            maskedSec = _objectiveMaskedSec,
            room = facts.RoomIndex,
            inCombat
        });
    }

    /// <summary>The last arbitrated movement owner — diagnostics only.</summary>
    internal string MovementOwner => _lastWinner?.Describe() ?? "none";

    // A stationary objective supplies a preference to stay where it was when
    // the hazards appeared. Keep the anchor stable as the follower moves.
    internal NavOutcome ExecuteFloorMovement(INativeBoundary native, IClaimVerb? verb,
        bool planForHazards, Vector3 playerPos)
    {
        if (planForHazards && verb is null or HoldVerb)
        {
            _idleAnchor ??= playerPos;
            return native.Navigate(_idleAnchor.Value, NavigationRange.MovementTolerance);
        }
        _idleAnchor = null;
        return native.Execute(verb);
    }

    /// <summary>Encounter path (internal PT30): the driver binds ONE
    /// stable op to the real EncounterId; its claim goes through the same
    /// arbiter + boundary.Execute. NavigationSurface applies the encounter
    /// constraints. The FEC already
    /// validated snapshot identity/territory — this takes THE filtered
    /// reference, never a second registry read.</summary>
    internal void TickEncounter(
        int floor, int rawRoomIndex, DateTime now, Vector3 playerPos,
        bool inCombat, bool playerDead,
        MasterPlayer.MechanicRuntime.EncounterPlanningSnapshot? snapshot,
        ObjectiveArbiterSnapshot snapshot2,
        FloorObjectEvidenceSnapshot? evidence, INativeBoundary native)
    {
        var facts = _obs.Assemble(floor, rawRoomIndex, now, playerPos, inCombat,
            playerDead, false, snapshot2, evidence, null);
        _lastFacts = facts;
        _lastBoundary = native;
        var dt = _lastTickUtc == DateTime.MinValue ? 0f
            : Math.Clamp((float)(now - _lastTickUtc).TotalSeconds, 0f, 0.5f);
        _lastTickUtc = now;

        // A different real encounter id is NEW work — the stale bound op
        // was already retired by the driver; cancel any leftover boundary
        // movement before executing the fresh claim.
        var op = _encounter.EnsureOp(snapshot, out var newIdentity) as EngageBossOp;
        if (newIdentity)
            native.CancelMovement();
        op?.Observe(snapshot);
        var authority = new OpAuthority(
            op != null && _lastWinner == op,
            op != null && _lastWinner == op ? _lastDt : 0f,
            op != null && _lastMasked.Contains(op) ? _lastDt : 0f);
        var status = op?.Tick(facts, native, now, authority) ?? OpStatus.Running;
        if (_encounter.Observe(status))
            _record("coord-encounter-state", new
            {
                encounter = _encounter.EncounterId,
                status = status.ToString(),
                navFailure = native.LastNavFailure,
            });
        _claims.Clear();
        if (op != null && status == OpStatus.Running && op.Claim.Verb != null)
            _claims.Add((op, op.Claim));
        var winner = _arbiter.Resolve(_claims);
        _lastWinner = winner;
        _lastMasked.Clear();
        if (op != null && winner != op && op.Claim.Verb != null)
            _lastMasked.Add(op);
        var outcome = native.Execute(winner?.Claim.Verb);
        if (outcome == NavOutcome.Failed)
            winner?.OnNavFailed(now);
        // Held interval recorded AFTER dispatch, matching ordinary Tick's
        // previous-winner authority convention.
        _lastDt = dt;
    }

    /// <summary>Suspend the encounter path while movement permission is
    /// denied: clears pending claims/last winner and suspends the bound
    /// op; timing fields are ZEROED so pause time is never counted
    /// toward authority.</summary>
    internal void SuspendEncounter()
    {
        _claims.Clear();
        _lastWinner = null;
        _lastMasked.Clear();
        _lastDt = 0f;
        _lastTickUtc = DateTime.MinValue;
        _encounter.Current?.OnSuspended();
    }

    /// <summary>Retire the encounter driver before cancelling the boundary
    /// — a floor change/dispose must not leave a stale bound op. Suspend
    /// first so its input/claims clear; ordinary-floor authority is not
    /// touched for a nonexistent encounter.</summary>
    internal void RetireEncounter()
    {
        if (_encounter.Current != null)
            SuspendEncounter();
        _encounter.Retire();
    }
    /// <summary>The encounter driver's bound op, if any — the FEC suspend
    /// path calls OnSuspended so a pause keeps its identity/attempt.</summary>
    internal FloorOperation? CurrentEncounterOp => _encounter.Current;
    /// <summary>Latest encounter status for FEC status publishing.</summary>
    internal OpStatus EncounterStatus => _encounter.LastReported;
    /// <summary>Bound encounter identity, if any.</summary>
    internal ulong EncounterId => _encounter.EncounterId;

    private void EnsureObjectiveOp(FloorObjectiveKind objective, in FloorFacts facts)
    {
        if (objective == _lastObjective && _objectiveOp != null)
            return;
        _objectiveOp?.OnRetired();
        _objectiveMaskedSec = 0;
        _lastObjective = objective;
        _objectiveOp = objective switch
        {
            FloorObjectiveKind.EnterPassage when facts.PassageDestination is { } dest
                => new ExitPassageOp(dest, facts.Floor, facts.PassageRoomIndex,
                    () => _lastFacts.PassageRoomCenter, () => _lastFacts.PassageDestination,
                    () => _lastFacts.PassageActorPosition),
            FloorObjectiveKind.OpenVisibleBandedChest when facts.ChestWaypoint is { } wp
                => new OpenChestOp(wp),
            FloorObjectiveKind.CompleteKnownHoard or FloorObjectiveKind.DiscoverHoard
                or FloorObjectiveKind.OpenPlannedChest
                => new ExploreRoomOp(objective),
            FloorObjectiveKind.ActivatePassage or FloorObjectiveKind.FinishCombatBeforePassage
                => new ChaseMobsOp(objective),
            // Unhandled kinds supply no movement goal; the
            // legacy path stays the real mover for those objectives.
            _ => null
        };
    }

    /// <summary>Bind the objective op to the committed work token. Work
    /// ops are CACHED per token — a route rebuild/suspension rebinds the
    /// SAME execution attempt; only real retirement (completion, failure,
    /// policy skip, teardown) removes it from the cache.</summary>
    private void EnsureWorkOp(SiteSearchBoundary sb, in FloorFacts facts)
    {
        var head = sb.Head;
        var token = sb.HeadToken;
        if (!head.HasValue || token.Key.Length == 0)
        {
            // No committed work — suspend the current op (it keeps its
            // state in the cache); suspension is never an outcome.
            if (_objectiveOp != null)
            {
                _objectiveOp.OnSuspended();
                _objectiveOp = null;
            }
            _boundToken = default;
            _boundKind = null;
            sb.CommitProtected = false;
            return;
        }
        if (_objectiveOp != null && _boundToken == token &&
            _boundKind == head.Value.Kind)
        {
            sb.CommitProtected = _objectiveOp.ProtectsCommit;
            return;
        }
        if (_objectiveOp != null)
        {
            _objectiveOp.OnSuspended();
            _objectiveOp = null;
            // One transport owner — explicitly release the replaced
            // head's movement before the new binding resumes.
            _lastBoundary?.CancelMovement();
        }
        _objectiveMaskedSec = 0;
        if (!_workOperations.TryGetValue(token, out var op) || op == null)
        {
            op = head.Value.Kind switch
            {
                ObjectiveSiteKind.ConfirmedChest
                    => new OpenChestOp(new RoomWaypoint(head.Value.Position,
                        WaypointTypeFor(head.Value)) , token),
                ObjectiveSiteKind.Passage
                    => new ExitPassageOp(
                        facts.PassageDestination ?? head.Value.Position,
                        facts.Floor,
                        facts.PassageRoomIndex >= 0
                            ? facts.PassageRoomIndex : head.Value.RoomIndex,
                        () => _lastFacts.PassageRoomCenter,
                        () => _lastFacts.PassageDestination,
                        () => _lastFacts.PassageActorPosition),
                ObjectiveSiteKind.Quota
                    => new ChaseMobsOp(FloorObjectiveKind.FinishCombatBeforePassage),
                _ => new SiteWorkOp(head.Value, token, () => sb.KnowledgeHoardAcquired),
            };
            _workOperations[token] = op;
        }
        _objectiveOp = op;
        _objectiveOp.OnResumed(DateTime.UtcNow);
        _boundToken = token;
        _boundKind = head.Value.Kind;
        _lastObjective = _objectiveOp?.Objective ?? FloorObjectiveKind.None;
        sb.CommitProtected = _objectiveOp?.ProtectsCommit == true;
    }

    private static RoomObjectiveType WaypointTypeFor(in ObjectiveSite s) =>
        s.Chest switch
        {
            Runtime.Floor.FloorChestKind.Silver => RoomObjectiveType.ChestSilver,
            Runtime.Floor.FloorChestKind.Gold => RoomObjectiveType.ChestGold,
            Runtime.Floor.FloorChestKind.Banded => RoomObjectiveType.ChestBanded,
            _ => RoomObjectiveType.ChestBronze,
        };

    /// <summary>Floor teardown: retire EVERY operation — cached work ops
    /// AND a bound op that was never cached (legacy boundary binding) —
    /// cancel their queries and release movement through the owner.</summary>
    internal void Reset()
    {
        var boundCached = _objectiveOp != null &&
            !string.IsNullOrEmpty(_boundToken.Key) &&
            _workOperations.TryGetValue(_boundToken, out var cached) &&
            ReferenceEquals(cached, _objectiveOp);
        foreach (var op in _workOperations.Values)
            op.OnRetired();
        _workOperations.Clear();
        if (!boundCached)
            _objectiveOp?.OnRetired();
        _objectiveOp = null;
        _boundToken = default;
        _boundKind = null;
        _lastObjective = FloorObjectiveKind.None;
        _claims.Clear();
        _lastWinner = null;
        _lastMasked.Clear();
        _objectiveMaskedSec = 0;
        _lastTickUtc = DateTime.MinValue;
        _encounter.Retire();
        _lastBoundary?.CancelMovement();
    }

    private void RetireObjectiveOp(OpStatus status, INativeBoundary? native)
    {
        var retiredToken = _boundToken;
        var boundary = native ?? _lastBoundary;
        if (_lastWorkDriven && !string.IsNullOrEmpty(_boundToken.Key) &&
            boundary?.Search is SiteSearchBoundary wsb && wsb.KnowledgeDriven)
        {
            var outcome = status switch
            {
                OpStatus.Succeeded => SiteWorkOutcome.Completed,
                OpStatus.Failed => SiteWorkOutcome.Failed,
                _ => SiteWorkOutcome.PolicySkipped,
            };
            // "chest-overcap-policy" identifies the explicit policy skip —
            // cancellations report PolicySkipped without it and must not
            // suppress future selection of the work.
            var reason = status == OpStatus.PolicySkipped
                ? "chest-overcap-policy"
                : _objectiveOp?.Failure?.ToString();
            wsb.ReportWorkOutcome(_boundToken, outcome, reason);
        }
        // Terminal outcome (success / failure / skip / cancel): the
        // execution attempt is done — release the cached op. Failed work
        // is excluded by the driver's failed-key set, not retained here.
        if (!string.IsNullOrEmpty(_boundToken.Key))
            _workOperations.Remove(_boundToken);
        _boundToken = default;
        _boundKind = null;
        // Policy skips are policy conclusions, not failures — only real
        // failures count toward the objective block counter. Work-driven
        // failures were already reported above; this covers legacy ops.
        if (status == OpStatus.Failed && _objectiveOp != null && !_lastWorkDriven)
        {
            var kind = _objectiveOp.Objective;
            _objectiveFails[kind] = _objectiveFails.TryGetValue(kind, out var n) ? n + 1 : 1;
            if (_objectiveFails[kind] >= MaxObjectiveFails)
                _record("coord-objective-blocked", new { objective = kind.ToString(), fails = _objectiveFails[kind] });
        }
        _record("coord-op-retired", new
        {
            op = _objectiveOp?.GetType().Name,
            objective = _objectiveOp?.Objective.ToString(),
            status = status.ToString(),
            failure = _objectiveOp?.Failure?.ToString(),
            attempts = _objectiveOp?.Attempts,
            key = retiredToken.Key,
            attempt = retiredToken.Attempt,
            state = _objectiveOp?.Describe(),
            interactionCalls = _objectiveOp?.ActiveChestAttempt?.InteractionCallCount,
            nativeDispatches = _objectiveOp?.ActiveChestAttempt?.NativeDispatchCount,
            lastNativeReturn = _objectiveOp?.ActiveChestAttempt?.LastNativeReturn,
            lastInteractionRejection = _objectiveOp?.ActiveChestAttempt?.LastRejectionReason,
        });
        _objectiveOp?.OnRetired();
        _objectiveOp = null;
        _lastObjective = FloorObjectiveKind.None;
    }
}
