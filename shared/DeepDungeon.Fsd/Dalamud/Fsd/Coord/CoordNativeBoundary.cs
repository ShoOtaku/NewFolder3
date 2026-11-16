using System;
using System.Collections.Generic;
using DeepDungeon.Fsd.Core;
using System.Numerics;
using MasterPlayer.MechanicRuntime;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>Unsafe pointer accessors can't be generic args — plain delegate.</summary>
internal unsafe delegate InstanceContentDeepDungeon* DdAccessor();

/// <summary>Search-boundary facade over AutoPilotExecutor + room-center lookup.</summary>

/// <summary>
/// Real boundary: routes claims through the existing NavigationHelper so the
/// pipeline exercises the same IPC path the legacy engine used.
/// </summary>
internal sealed class RealNativeBoundary : INativeBoundary
{
    private readonly Func<NavigationSurface> _surface;
    private readonly Func<Vector3> _playerPos;
    private readonly Func<FsdChestInteraction?> _chest;
    private readonly Func<ISearchBoundary?> _search;
    private readonly Func<Vector3, bool, EnemyChaseTarget?> _chase;
    private readonly Action<ulong>? _completeChaseLeg;
    private readonly Func<ChestInteractionAttempt, ChestOvercapProgress>? _chestOvercap;
    private readonly Func<EncounterPlanningSnapshot, (Vector3 Position, float Radius)?>? _selectEncounter;
    private readonly Func<AttackTargetVerb, NavOutcome> _attack;

    public RealNativeBoundary(
        Func<NavigationSurface> surface,
        Func<Vector3> playerPos,
        Func<FsdChestInteraction?> chest,
        Func<ISearchBoundary?> search,
        Func<Vector3, bool, EnemyChaseTarget?> chase,
        Func<AttackTargetVerb, NavOutcome> attack,
        Action<ulong>? completeChaseLeg = null,
        Func<ChestInteractionAttempt, ChestOvercapProgress>? chestOvercap = null,
        Func<EncounterPlanningSnapshot, (Vector3 Position, float Radius)?>? selectEncounter = null)
    {
        _surface = surface;
        _playerPos = playerPos;
        _chest = chest;
        _search = search;
        _chase = chase;
        _completeChaseLeg = completeChaseLeg;
        _chestOvercap = chestOvercap;
        _selectEncounter = selectEncounter;
        _attack = attack;
    }

    public (Vector3 Position, float Radius)? SelectEncounterTarget(EncounterPlanningSnapshot snapshot) => _selectEncounter?.Invoke(snapshot);
    public float EncounterAttackRange => CombatPositionPolicy.DefaultAttackRange(Service.LocalPlayer?.ClassJob.RowId ?? 0);

    public EnemyChaseTarget? ChaseTarget(Vector3 playerPos, bool allowNewTargets) =>
        _chase(playerPos, allowNewTargets);

    public void CompleteChaseLeg(ulong expectedTargetId) =>
        _completeChaseLeg?.Invoke(expectedTargetId);


    public ChestPositionObservation ObserveChestPosition(
        ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence) =>
        _chest() is { } chest
            ? chest.ObserveChestPosition(attempt, evidence)
            : default;

    public ISearchBoundary? Search => _search();

    // Claim-dispatch channel: shared verbs interpreted here; anything else
    // is a profile verb this boundary doesn't speak — fail, never pretend.
    public NavOutcome Execute(IClaimVerb? verb) => verb switch
    {
        null => CancelMovementOutcome(),
        ChestTargetVerb chest => ToOutcome(_surface().NavigateToChest(chest, _playerPos())),
        AttackTargetVerb attack => _attack(attack),
        GoToVerb g => Navigate(g.Dest, g.Radius, g.Path),
        HoldVerb => CancelMovementOutcome(),
        _ => NavOutcome.Failed
    };

    private NavOutcome CancelMovementOutcome()
    {
        _surface().Cancel();
        return NavOutcome.Pending;
    }

    // The surface impl owns data+pathfinder+follower — which impl serves a
    // floor is decided once at assembly; the func reads that decision, ops
    // never see or choose the backend themselves.
    public NavOutcome Navigate(Vector3 dest, float arrivalRadius) => Navigate(dest, arrivalRadius, null);
    private NavOutcome Navigate(Vector3 dest, float arrivalRadius, NavigationPath? path) =>
        ToOutcome(_surface().Navigate(dest, _playerPos(), arrivalRadius, path));

    private static NavOutcome ToOutcome(NavigationState state) => state switch
        {
            NavigationState.Arrived => NavOutcome.Arrived,
            NavigationState.Failed or NavigationState.StuckGiveUp => NavOutcome.Failed,
            NavigationState.StuckRepathing => NavOutcome.Pending,
            _ => NavOutcome.Moving
        };

    public void CancelMovement() => _surface().Cancel();

    public string? LastNavFailure => _surface().Failure;

    public bool IsChestAccepted(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence,
        out ChestLifecycleSnapshot snapshot)
    {
        snapshot = default;
        return _chest()?.IsAccepted(attempt, attempt.Waypoint, evidence, out snapshot, out _) == true;
    }

    public bool IsChestTargetMissing(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence) =>
        evidence != null && _chest()?.IsTargetMissing(attempt, evidence) == true;

    public bool TryChestInteract(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot evidence,
        out ChestLifecycleSnapshot snapshot, out bool retry)
    {
        snapshot = default;
        retry = false;
        return _chest()?.TryInteract(attempt, evidence, out snapshot, out retry) == true;
    }

    public float ChestOpenTimeoutSeconds(RoomWaypoint waypoint) =>
        _chest()?.GetOpenTimeoutSeconds(waypoint) ?? 30f;

    public bool CanStartChest(ChestInteractionAttempt attempt) =>
        _chest()?.CanStart(attempt, attempt.Waypoint) == true;

    /// <summary>Overcap relief lives on the controller — the injected
    /// delegate supplies the existing item-policy decision; without it the
    /// chest proceeds as Ready (legacy callers unchanged).</summary>
    public ChestOvercapProgress ResolveChestOvercap(ChestInteractionAttempt attempt) =>
        _chestOvercap?.Invoke(attempt) ?? ChestOvercapProgress.Ready;
}

/// <summary>
/// Shadow boundary: records what the pipeline would do without touching the
/// native layer — the legacy path keeps real movement control during the
/// comparison window.
/// </summary>
internal sealed class ShadowNativeBoundary : INativeBoundary
{
    public Vector3? LastDestination { get; private set; }
    public float LastRadius { get; private set; }
    public int NavigateCalls { get; private set; }
    public int CancelCalls { get; private set; }
    public string? LastVerb { get; private set; }

    public NavOutcome Execute(IClaimVerb? verb)
    {
        LastVerb = verb?.Describe();
        switch (verb)
        {
            case null:
            case HoldVerb:
                ++CancelCalls;
                return NavOutcome.Pending;
            case GoToVerb g:
                return Navigate(g.Dest, g.Radius);
            default:
                return NavOutcome.Failed;
        }
    }

    public NavOutcome Navigate(Vector3 dest, float arrivalRadius)
    {
        LastDestination = dest;
        LastRadius = arrivalRadius;
        ++NavigateCalls;
        return NavOutcome.Moving;
    }

    public void CancelMovement() => ++CancelCalls;

    public bool IsChestAccepted(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence,
        out ChestLifecycleSnapshot snapshot) { snapshot = default; return false; }
    public bool IsChestTargetMissing(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence) => false;
    public bool TryChestInteract(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot evidence,
        out ChestLifecycleSnapshot snapshot, out bool retry) { snapshot = default; retry = false; return false; }


    public float ChestOpenTimeoutSeconds(RoomWaypoint waypoint) => 30f;
    public ISearchBoundary? Search => null;
    public EnemyChaseTarget? ChaseTarget(Vector3 playerPos, bool allowNewTargets) => null;
}
