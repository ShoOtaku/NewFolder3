using System;
using System.Numerics;
using MasterPlayer.MechanicRuntime;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;

namespace DeepDungeon.Fsd.Dalamud.Coord;

internal enum OpStatus { Running, Succeeded, Failed, Cancelled, PolicySkipped }

/// <summary>Result of the controller-side chest-overcap relief decision:
/// Ready = proceed with interaction dispatch; Waiting = an item relief is
/// pending/in progress, hold; PolicySkipped = existing policy denies the
/// chest this floor — an explicit policy conclusion, not a failure.</summary>
internal enum ChestOvercapProgress { Ready, Waiting, PolicySkipped }

/// <summary>
/// Movement-authority accounting handed to an op each tick, computed by
/// the pipeline after arbitration resolves the previous frame's claims.
/// HeldDt = seconds the op's claim won and was dispatched; MaskedDt =
/// seconds the op produced a claim that lost to another claim. Effort
/// budgets (nav deadlines, pursuit budgets) burn HeldDt only — being
/// masked by escape/danger must not consume an op's own effort budget.
/// Real world deadlines (a chest's despawn window) stay on wall-clock.
/// </summary>
internal readonly record struct OpAuthority(bool Held, float HeldDt, float MaskedDt)
{
    public static readonly OpAuthority None = new(false, 0f, 0f);
}

/// <summary>
/// Typed failure terminal so callers route outcomes instead of retrying
/// forever: Retryable consumes budget, Unreachable/Blocked end the attempt
/// differently, Terminal kills the operation outright.
/// </summary>
internal enum FailureKind { Retryable, Unreachable, Blocked, Terminal }

/// <summary>
/// Movement request an operation hands to the arbiter each tick — an
/// arbitration envelope only. The arbiter reads Priority; the verb type is
/// the intent and stays opaque until the winning boundary dispatches it.
/// RevalidateAt records which facts revision the claim was computed from —
/// diagnostic metadata, never consulted by arbitration.
/// </summary>
public readonly record struct MoveClaim(
    int Priority,
    IClaimVerb? Verb,
    long RevalidateAt = -1)
{
    public static readonly MoveClaim None = new(int.MaxValue, null);
    public static MoveClaim Hold(int priority) => new(priority, new HoldVerb());
    public static MoveClaim To(int priority, Vector3 dest, float radius = NavigationRange.MovementTolerance,
        NavigationPath? path = null) => new(priority, new GoToVerb(dest, radius) { Path = path });
}

/// <summary>Outcome of a navigation request at the native boundary.</summary>
public enum NavOutcome { Moving, Arrived, Pending, Failed }

/// <summary>
/// Thin shell over the legacy/native boundary. Operations only see this —
/// they never call Service.*, nav helpers, or game structs directly.
/// </summary>
/// <summary>Result of a domain room finish.</summary>
internal enum RoomFinishResult { Done, Deferred, Rejected }

internal interface INativeBoundary
{
    /// <summary>
    /// The claim-dispatch channel: the winning claim's verb lands here.
    /// Shared verbs are interpreted by the boundary itself; unknown/profile
    /// verbs must return Failed — never silently drop or reinterpret.
    /// </summary>
    NavOutcome Execute(IClaimVerb? verb);
    NavOutcome Navigate(Vector3 dest, float arrivalRadius);
    void CancelMovement();

    /// <summary>Why the surface's last Navigate reported Failed, when the
    /// backend tracks a reason (bitmap: unreachable/retargeted/no-progress/
    /// owned-elsewhere). Diagnostics only — never a control signal.</summary>
    string? LastNavFailure => null;

    // Chest surface — the AR-1.2 verified interaction lifecycle lives here.
    bool IsChestAccepted(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence,
        out ChestLifecycleSnapshot snapshot);
    bool IsChestTargetMissing(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence);
    bool TryChestInteract(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot evidence,
        out ChestLifecycleSnapshot snapshot, out bool retry);
    float ChestOpenTimeoutSeconds(RoomWaypoint waypoint);

    /// <summary>Existing silver precondition (e.g. player HP threshold)
    /// behind native acceptance — the op waits, the service decides.
    /// Default true so test fakes keep compiling unchanged.</summary>
    bool CanStartChest(ChestInteractionAttempt attempt) => true;
    /// <summary>Native chest position facts for recovery decisions —
    /// the default is appropriate for fakes that don't support it.</summary>
    ChestPositionObservation ObserveChestPosition(
        ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence)
        => default;

    /// <summary>Existing overcap relief decision for this chest attempt —
    /// item policy lives on the controller, never in the op. Default Ready
    /// so test fakes keep compiling unchanged.</summary>
    ChestOvercapProgress ResolveChestOvercap(ChestInteractionAttempt attempt)
        => ChestOvercapProgress.Ready;

        /// <summary>Domain search planner surface; null when no executor is live.</summary>
    ISearchBoundary? Search { get; }

    /// <summary>Current clearing-chase destination, if any — wraps the
    /// legacy chase helper + preferred-aggro bookkeeping inside the real
    /// boundary so ops only see a position.</summary>
    /// <summary>The full chase target — immutable leg endpoint, live
    /// position, hitbox and identity — never a frozen position copy.</summary>
    EnemyChaseTarget? ChaseTarget(Vector3 playerPos, bool allowNewTargets);

    (Vector3 Position, float Radius)? SelectEncounterTarget(EncounterPlanningSnapshot snapshot) =>
        snapshot.Hints is { HasTargetDirective: true, AttackTarget: { } target } ? (target.Position, target.Radius) : null;
    float EncounterAttackRange => 3f;
    /// <summary>Complete the current navigation leg ONLY for the expected
    /// target — a stale op never consumes a newer target's leg.</summary>
    void CompleteChaseLeg(ulong expectedTargetId) { }

}

/// <summary>Native-side chest position facts for a claim-only op —
/// computed by the boundary so ops never touch Service/BGCollision
/// directly.</summary>
internal readonly record struct ChestPositionObservation(
    bool Available, bool WaitingForReply,
    bool InRange, bool SightBlocked, ulong GameObjectId, Vector3 Position, NavigationRange Range = default);

internal readonly record struct QuotaSearchRoom(int RoomIndex, Vector3 Center);
internal readonly record struct QuotaSearchResult(bool Available, QuotaSearchRoom? Room);

internal interface ISearchBoundary
{
    // Independent of productive site work; progress belongs to this floor.
    QuotaSearchResult NextQuotaSearchRoom(Vector3 playerPosition) => default;
    void CompleteQuotaSearchRoom(int roomIndex, bool reached) { }
    bool ExecutorComplete { get; }
    RoomWaypoint? CurrentWaypoint { get; }
    int? ContextRoomIndex { get; }
    bool TryStartNextRoomSearch(Vector3 playerPos, FloorObjectEvidenceSnapshot? evidence);

    /// <summary>Stable key of the search driver's current work head —
    /// lets a legacy adapter bind child work to identity, not to whatever
    /// the head happens to be at retire time. Default null for fakes.</summary>
    string? CurrentWorkKey => null;

    /// <summary>Record the waypoint's outcome (feeds the room outcome
    /// snapshot — bare advancing leaves hoard/chest outcomes unresolved and
    /// defers every room finish), then advance to the next waypoint.</summary>
    void AdvanceWaypoint(RoomWaypoint waypoint, WaypointOutcomeKind outcome, string reason);

    /// <summary>Identity-guarded advance: only applies the outcome when
    /// the caller's captured key+waypoint still matches the current head —
    /// a discarded child can never advance a newer head's work. Default
    /// implementation keeps fakes compiling.</summary>
    void AdvanceWaypoint(string? expectedWorkKey, RoomWaypoint waypoint, WaypointOutcomeKind outcome, string reason)
    {
        if (CurrentWorkKey == expectedWorkKey && CurrentWaypoint is { } current && current == waypoint)
            AdvanceWaypoint(waypoint, outcome, reason);
    }
    Vector3? RoomCenter(int roomIndex);
    void ClearRoomContext();

    /// <summary>
    /// Domain room finish: outcome snapshot → progress marks → plan
    /// regeneration → context clear (the legacy FinalizeRoomSearch path).
    /// Deferred = pending evidence, keep exploring; Rejected = no active
    /// objective execution to apply outcomes to (terminal for this room —
    /// force-advance, not a wait).
    /// </summary>
    RoomFinishResult FinishRoom();

    /// <summary>Drop the current plan head so a forced finish can't rebuild
    /// the same room — the bounded escape when FinishRoom stays deferred.</summary>
    bool SkipPlanHead();

    /// <summary>Center of the NEXT planned room after the current one — the
    /// productive destination while a room finish defers on pending hoard
    /// evidence (keep exploring, don't hold).</summary>
    Vector3? NextRoomCenter();

    /// <summary>Center of the next-hop room on the room-graph route from
    /// <paramref name="fromRoom"/> toward <paramref name="toRoom"/> — the
    /// honest cross-room step instead of a straight line through walls.
    /// Null when no route exists or the rooms are already adjacent-same.</summary>
    Vector3? NextHopRoomCenter(int fromRoom, int toRoom);
}

/// <summary>
/// One typed unit of floor work bound to an objective. Lifecycle is owned by
/// the pipeline; retry budgets are per-operation constructor data, not global
/// constants sprinkled through call sites.
/// </summary>
internal abstract class FloorOperation
{
    protected FloorOperation(FloorObjectiveKind objective, int priority, int maxAttempts)
    {
        Objective = objective;
        Priority = priority;
        MaxAttempts = maxAttempts;
    }

    public FloorObjectiveKind Objective { get; }
    public int Priority { get; }
    public int MaxAttempts { get; }
    public int Attempts { get; protected set; }
    public MoveClaim Claim { get; protected set; } = MoveClaim.None;
    public FailureKind? Failure { get; protected set; }

    /// <summary>Compact op state for diagnostics (recorded in coord-tick).</summary>
    internal virtual string Describe() => GetType().Name;

    internal abstract OpStatus Tick(in FloorFacts facts, INativeBoundary native, DateTime now,
        in OpAuthority authority);

    /// <summary>Invoked when the arbiter preempts this operation's claim.</summary>
    internal virtual void OnPreempted() { }
    /// <summary>Suspended while the route head moved elsewhere — releases
    /// the movement claim but keeps all execution state; rebinding the
    /// same token resumes THIS attempt, never a fresh one.</summary>
    internal virtual void OnSuspended() => OnPreempted();
    /// <summary>Rebound after a suspension — ops may refresh transient
    /// state (last-tick timestamps), never reset their work clocks.</summary>
    internal virtual void OnResumed(DateTime now) { }

    /// <summary>True while a meaningful action is in progress whose
    /// interruption would waste real work — a partial/awaited probe dwell
    /// or a dispatched chest interaction. The scheduler preserves the
    /// committed head only while this holds; plain travel does NOT
    /// protect a commit.</summary>
    internal virtual bool ProtectsCommit => false;

    /// <summary>The chest interaction attempt this op currently owns, if
    /// any — read-only exposure so the controller's native overcap/item
    /// channel can bind to the REAL attempt, not a stale sidecar.</summary>
    internal virtual ChestInteractionAttempt? ActiveChestAttempt => null;

    internal virtual void OnNavArrived() { }

    /// <summary>Invoked when this operation's winning claim came back Failed.</summary>
    internal virtual void OnNavFailed(DateTime now)
    {
        ++Attempts;
        if (Attempts >= MaxAttempts)
            Failure = FailureKind.Unreachable;
    }

    /// <summary>Invoked when the pipeline retires this operation (any status).</summary>
    internal virtual void OnRetired() { }
}
