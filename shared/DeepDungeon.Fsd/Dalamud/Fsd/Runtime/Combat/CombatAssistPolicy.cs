using System;
using System.Numerics;
using MasterPlayer.MechanicRuntime;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

/// <summary>
/// Shared combat-assist policy used by both manual banded farming and FullSelfDelving controllers.
/// Encapsulates target selection. Attacks are owned by the selected rotation plugin.
/// Run-scoped state is maintained between calls.
/// </summary>
internal sealed class CombatAssistPolicy
{
    private readonly ICombatTargetProvider _encounter = new EncounterTargetProvider();
    private readonly ICombatTargetProvider _default = new DefaultTargetProvider();
    private CombatTargetChoice _decision;
    private DateTime _decisionFrame;
    private DateTime _chaseTargetFrame;
    private ulong _chaseTargetId;
    internal string? TargetSource => _decision.Source;
    internal ulong SelectedId { get; private set; }

    // Cache the navigation decision once per frame. Attack-side sight filtering
    // must not replace it, so navigation can still approach an obstructed target.
    private CombatTargetChoice Select(in CombatTargetRequest request)
    {
        var frame = Service.Framework.LastUpdateUTC;
        if (_decisionFrame == frame) return _decision;
        _decisionFrame = frame;
        _decision = Choose(request);
        SelectedId = _decision.Target?.GameObjectId ?? 0;
        return _decision;
    }

    private CombatTargetChoice Choose(in CombatTargetRequest request) =>
        _encounter.TrySelect(request, out var choice) || _default.TrySelect(request, out choice)
            ? choice
            : default;

    // Filter the attack sidecar only. Navigation can still approach its
    // independently selected target when that target is behind a wall.
    private CombatTargetChoice SelectAttack(in CombatTargetRequest request, Vector3 position)
    {
        var choice = Select(request);
        if (choice.Target is not { } target || !CombatTargetRequest.IsSightBlocked(position, target.Position))
            return choice;
        choice = Choose(request with { SightOrigin = position });
        return choice.Target == null ? choice with { HoldFire = true } : choice;
    }

    internal void OfferChaseTarget(ulong targetId)
    {
        _chaseTargetFrame = Service.Framework.LastUpdateUTC;
        _chaseTargetId = targetId;
    }

    private static CombatTargetChoice UseChaseTarget(in CombatTargetRequest request, Vector3 position,
        ulong chaseTargetId, CombatTargetChoice choice)
    {
        if (choice.Target != null || choice.Source != "default" || request.BossFloor || request.PassageOpen ||
            chaseTargetId == 0 || request.Context is not { } context || context.IsCombatTargetSuppressed(chaseTargetId))
            return choice;

        if (CombatTargetingHelpers.GetBattleCharaByGameObjectId(chaseTargetId) is not
                IBattleNpc { IsDead: false, IsTargetable: true, CurrentHp: > 0 } target ||
            !context.IsNewCombatTargetAllowed(target) ||
            !CombatPositionNavigation.CheckAttack(position, target.Position, request.Range).Allowed)
            return choice;

        return new(target, "chase");
    }

    private static void Apply(in CombatTargetChoice decision)
    {
        if (decision.Target is { IsDead: false, IsTargetable: true } target)
        {
            if (Service.TargetManager.Target?.GameObjectId != target.GameObjectId)
                Service.TargetManager.Target = target;
        }
        else if (decision.HoldFire && Service.TargetManager.Target is IBattleNpc)
            Service.TargetManager.Target = null;
    }

    internal (Vector3 Position, float Radius)? SelectForNavigation(RunContext ctx, EncounterPlanningSnapshot snapshot)
    {
        var choice = Select(new(ctx, true, Service.Condition[ConditionFlag.InCombat], ctx.Duty.PassageOpen,
            GetEngageRange(), true, snapshot));
        Apply(choice);
        return choice.Target is { } target ? (target.Position, target.HitboxRadius) : null;
    }

    private uint _rangeJob;
    private float _engageRange;
    private DateTime _nextDiagnosticAt;
    private string _lastDiagnosticStatus = string.Empty;
    private ulong _lastDiagnosticTarget;

    public void Tick(RunContext context, bool isBossFloor, bool passageOpen, out string status)
    {
        status = "Select: idle";
        try
        {
            var player = Service.LocalPlayer;
            if (player == null || player.IsDead) return;
            bool inCombat = Service.Condition[ConditionFlag.InCombat];
            float range = GetEngageRange();
            var request = new CombatTargetRequest(context, isBossFloor, inCombat, passageOpen, range, true,
                EncounterPlanningSnapshotRegistry.Read());
            ulong chaseTargetId = _chaseTargetFrame == Service.Framework.LastUpdateUTC ? _chaseTargetId : 0;
            var choice = UseChaseTarget(request, player.Position, chaseTargetId, SelectAttack(request, player.Position));
            var target = choice.Target;
            SelectedId = target?.GameObjectId ?? 0;
            Apply(choice);
            if (target == null)
            {
                if (!inCombat && !passageOpen && Service.TargetManager.Target is IBattleChara previous &&
                    (!context.IsNewCombatTargetAllowed(previous) || context.IsCombatTargetSuppressed(previous.GameObjectId)))
                    Service.TargetManager.Target = null;
                status = $"{choice.Source}: {(choice.HoldFire ? "hold fire" : "no target")}";
            }
            else
            {
                status = $"{choice.Source} →{target.Name.TextValue}";
                if (!inCombat && !passageOpen && Vector2.DistanceSquared(new(player.Position.X, player.Position.Z),
                    new(target.Position.X, target.Position.Z)) <= range * range)
                    context.PrepareCombat(target.GameObjectId);
            }
            var now = DateTime.UtcNow;
            if (now < _nextDiagnosticAt && status == _lastDiagnosticStatus && SelectedId == _lastDiagnosticTarget) return;
            _nextDiagnosticAt = now.AddSeconds(5);
            _lastDiagnosticStatus = status;
            _lastDiagnosticTarget = SelectedId;
            context.RecordDiagnostic?.Invoke("combat-assist-state", new {
                floor = context.Duty.Floor, status, autoSelect = true,
                inCombat, passageOpen, engageRange = range,
                currentTargetId = Service.TargetManager.Target?.GameObjectId,
                selectedTargetId = SelectedId, targetHp = target?.CurrentHp, chaseTargetId,
                preferredTargetId = context.TryGetPreferredAggroTarget(out var preferred) ? preferred : 0
            });
        }
        catch (Exception ex)
        {
            status = "Select: error";
            context.RecordDiagnostic?.Invoke("combat-assist-error", new { error = ex.ToString() });
            Service.Log.Error($"[CombatAssistPolicy] Tick error: {ex}");
        }
    }

    internal float GetEngageRange()
    {
        var job = Service.LocalPlayer?.ClassJob;
        uint id = job?.RowId ?? 0;
        if (id != _rangeJob || _engageRange == 0)
        {
            _rangeJob = id;
            _engageRange = job?.Value.Role is 3 or 4 or 5 ? 25f : 3f;
        }
        return _engageRange;
    }
}
