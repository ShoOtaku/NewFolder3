using System.Numerics;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Coord;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Combat policy for a persistent attack goal. Navigation owns the region,
/// path and hold state. This policy observes damage, requests another stand, or releases a target.</summary>
internal sealed class CombatPositionRecoveryController(RunContext context, EnemyChaseHelper chase,
    CombatRecoveryBudget budget, RunFloorTelemetryTrace? telemetry, Action<string> setStatus,
    Action<string, object> record)
{
    private ulong _id;
    private uint _hp;
    private TimeSpan _approachTime;
    private DateTime _lastUpdate;
    private bool _walking;
    private Vector3 _lastPosition;
    private DateTime _lastDamage, _lastMove, _recovery, _verify, _pause;
    public DateTime AttackWindowStartedAt { get; private set; }

    internal void Suspend()
    {
        if (_id != 0 && _pause == default) _pause = DateTime.UtcNow;
    }

    internal NavOutcome Navigate(NavigationSurface surface, IBattleChara target, Vector3 player,
        Vector3 targetPosition, float range, DateTime now)
    {
        var id = target.GameObjectId;
        if (id != _id)
        {
            _id = id;
            _hp = target.CurrentHp;
            _lastDamage = _lastMove = _lastUpdate = now;
            _approachTime = default; _walking = false;
            _lastPosition = player;
            _recovery = _verify = _pause = AttackWindowStartedAt = default;
        }
        if (_pause != default)
        {
            var gap = now - _pause;
            _lastUpdate = now;
            _lastDamage += gap; _lastMove += gap;
            if (_recovery != default) _recovery += gap;
            if (_verify != default) _verify += gap;
            if (AttackWindowStartedAt != default) AttackWindowStartedAt += gap;
            _pause = default;
        }
        if (budget.Exhausted)
            return Stop(surface, "Combat position recovery exhausted the floor retry limit.");
        var state = surface.NavigateToAttack(id, targetPosition, player, range);
        var region = surface.AttackRegion;
        if (_walking) _approachTime += now - _lastUpdate;
        _lastUpdate = now;
        _walking = state == NavigationState.Moving && !region.Holding && !surface.NeedsSafetyPlan;

        if (target.CurrentHp < _hp)
        {
            _lastDamage = now;
            if (_recovery != default)
            {
                record("sight-recovery-completed", new { targetId = id, reason = "target-hp-progress" });
                _recovery = _verify = default;
                region.ClearRejected();
            }
        }
        _hp = target.CurrentHp;
        if (region.Holding || Vector3.DistanceSquared(player, _lastPosition) > .04f)
        {
            _lastMove = now;
            _lastPosition = player;
        }
        if (region.Holding && AttackWindowStartedAt == default) AttackWindowStartedAt = now;
        var stalled = AttackWindowStartedAt != default
            && now - AttackWindowStartedAt >= CombatPositionPolicy.UnknownNoProgressLimit
            && now - _lastDamage >= CombatPositionPolicy.UnknownNoProgressLimit;
        if (_recovery == default && stalled)
        {
            if (budget.IsTargetExhausted(id))
                return Stop(surface, "Combat position recovery exhausted this target's retry budget.");
            _recovery = now;
            if (region.Path is { } path) region.Reject(path.Points[^1]);
            region.Reject(player);
            telemetry?.ObserveStalledEngageRecoveryStarted();
            record("sight-recovery-started", new { targetId = id, reason = "no-target-progress", failures = budget.TotalFailures });
        }
        else if (_recovery != default && region.Holding)
        {
            if (_verify == default) _verify = now;
            if (now - _verify >= TimeSpan.FromSeconds(6))
            {
                region.Reject(player);
                _verify = default;
                record("sight-recovery-position-rejected", new { targetId = id, reason = "attack-no-progress" });
            }
        }
        else _verify = default;

        // Preserve the former four held 30-second navigation attempts for one target.
        var approachExhausted = _approachTime >= 4 * CombatPositionPolicy.AttemptLimit;
        if (region.Unreachable || approachExhausted || state == NavigationState.Failed ||
            now - _lastMove >= CombatPositionPolicy.AttemptLimit ||
            (_recovery != default && now - _recovery >= CombatPositionPolicy.AttemptLimit))
        {
            var reason = region.Unreachable ? "no-reachable-standing-position"
                : approachExhausted ? "chase-navigation-exhausted" : "combat-position-no-progress";
            budget.RecordFailure(id);
            record("clearing-target-deprioritized", new { targetId = id, reason, hp = _hp,
                failures = budget.TotalFailures, targetExhausted = budget.IsTargetExhausted(id) });
            chase.DeprioritizeCurrentTarget();
            context.SuppressCombatTarget(id, TimeSpan.FromSeconds(20));
            context.ClearPreferredAggroTarget();
            surface.Cancel();
            _id = 0;
            AttackWindowStartedAt = default;
            if (budget.Exhausted) return Stop(surface, "Combat position recovery exhausted the floor retry limit.");
            setStatus("Switching target after combat position recovery failed");
            return NavOutcome.Pending;
        }
        return state == NavigationState.Arrived ? NavOutcome.Arrived : NavOutcome.Moving;
    }

    private NavOutcome Stop(NavigationSurface surface, string reason)
    {
        surface.Cancel();
        context.StatusIsError = true;
        context.StatusLine = reason;
        setStatus(reason);
        record("sight-recovery-stopped", new { reason, targetId = _id, failures = budget.TotalFailures });
        return NavOutcome.Failed;
    }
}
