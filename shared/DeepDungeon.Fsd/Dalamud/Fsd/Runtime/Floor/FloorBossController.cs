using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Utilities;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Owns one floor's boss navigation, combat buffs and diagnostic cadence.</summary>
internal sealed class FloorBossController : IDisposable
{
    private const float BossNavigationArrivalTolerance = 3f;
    private const uint SteelStatusId = 1100;
    private const uint StrengthStatusId = 687;
    private const uint PtHasteStatusId = 4718;
    private readonly byte _floor;
    private readonly RunContext _ctx;
    private readonly NavigationHelper _navHelper;
    private readonly PomanderManager _pomanderManager;
    private readonly FloorItemExecutor _items;
    private readonly DirectMovementController _preparationHold = new("boss preparation");
    private bool _preparing;
    // These helpers retain their run lifetime; this floor owner does not dispose or recreate them.
    private readonly Func<string, FloorObjectiveKind, bool> _requireMovementPermission;
    private readonly Action<string> _setStatus;
    private readonly Action<string, object> _recordReplayEvent;
    private readonly Action<byte, IPlayerCharacter> _recordBossSnapshot;
    private DateTime _nextBossDiagnosticAtUtc = DateTime.MinValue;
    private readonly Func<bool>? _coordEscapeActive;
    private readonly IBossMovementStrategy _movementStrategy;
    private bool _movementStarted;
    internal bool UsesSpinStrategy { get; }
    internal string MovementStrategyName => _movementStrategy.Name;
    internal bool MovementStarted => _movementStarted;
    internal bool RequestsNorthCamera => _movementStarted && _movementStrategy.RequestsNorthCamera;
    private bool BossNavigationResolved { get; set; }
    public bool BossSerenityAwaitingBuff { get; private set; }
    private string Status { set => _setStatus(value); }
    private FarmingItemPolicy ItemPolicy => FarmingItemPolicy.For(_ctx.FarmingPlan?.Mode);

    public FloorBossController(byte floor, RunContext context, NavigationHelper navigation,
        PomanderManager pomanders, FloorItemExecutor items,
        Func<string, FloorObjectiveKind, bool> requireMovementPermission, Action<string> setStatus,
        Action<string, object> recordReplayEvent, Action<byte, IPlayerCharacter> recordBossSnapshot,
        FloorExplorationController floorRuntime,
        Func<IBossMovementStrategy>? spinBossMovementFactory = null,
        Func<bool>? coordEscapeActive = null)
    {
        _floor = floor;
        _ctx = context;
        _navHelper = navigation;
        _coordEscapeActive = coordEscapeActive;
        _pomanderManager = pomanders;
        _items = items;
        _requireMovementPermission = requireMovementPermission;
        _setStatus = setStatus;
        _recordReplayEvent = recordReplayEvent;
        _recordBossSnapshot = recordBossSnapshot;
        UsesSpinStrategy = floorRuntime.Kind == FloorRuntimeKind.Boss &&
            DeepDungeonBossPolicy.UsesSpin(floorRuntime.DungeonId, floor) && spinBossMovementFactory != null;
        _movementStrategy = UsesSpinStrategy
            ? spinBossMovementFactory!()
            : new NormalBossMovementStrategy(floorRuntime);
        if (floorRuntime.DungeonId == 4 && floor == 30)
            _movementStrategy = new PromeRadarBossMovementStrategy(floorRuntime, context, recordReplayEvent);
    }

    public void OnSerenityConfirmed() => BossSerenityAwaitingBuff = true;

    public void Dispose()
    {
        try { SuspendMovement(); }
        finally { _preparationHold.Dispose(); }
    }

    private void StopMovement()
    {
        if (!_movementStarted) return;
        _movementStarted = false;
        try { _movementStrategy.Stop(); }
        finally { _navHelper.Cancel(); }
        _recordReplayEvent("boss-movement-stopped", new { floor = _floor, strategy = _movementStrategy.Name });
    }

    internal void SuspendMovement()
    {
        try { StopMovement(); }
        finally
        {
            _preparing = false;
            _preparationHold.SuppressInput = false;
        }
    }

    public unsafe void Update(InstanceContentDeepDungeon* dd)
    {
        if (Service.LocalPlayer is not { IsDead: false } || _ctx.Duty.IsTransitioning ||
            Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            SuspendMovement();
            Status = "Boss floor - waiting for player or floor";
            return;
        }
        if (_ctx.Duty.PassageOpen)
        {
            SuspendMovement();
            return;
        }
        if (!_requireMovementPermission("boss objective", FloorObjectiveKind.DefeatBoss))
        {
            SuspendMovement();
            return;
        }
        // Preparation owns the hold before either movement strategy can start.
        if (TryPrepareApproach(dd))
        {
            StopMovement();
            return;
        }
        if (!_movementStarted)
        {
            _navHelper.Cancel();
            _movementStarted = true;
            _movementStrategy.Start();
            _recordReplayEvent("boss-movement-started", new { floor = _floor, strategy = _movementStrategy.Name });
        }
        var status = _movementStrategy.Update(dd);
        if (status != null) Status = status;
        if (UsesSpinStrategy || _movementStrategy is PromeRadarBossMovementStrategy)
            UpdateManagedEncounter(dd, status, liveEncounter: true);
    }

    // Finish items before approaching: a cast/animation lock after spin has
    // established its phase can let the boss face the player and start a skill.
    internal unsafe bool TryPrepareApproach(InstanceContentDeepDungeon* dd)
    {
        var actions = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        bool beforeCombat = !_ctx.Duty.PassageOpen && !Service.Condition[ConditionFlag.InCombat] &&
            Service.LocalPlayer is { IsDead: false };
        bool needed = beforeCombat && (_items.Session.Pending != null ||
            Service.LocalPlayer?.IsCasting == true || Service.Condition[ConditionFlag.Casting] ||
            actions != null && actions->AnimationLock > 0 ||
            CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex) &&
                FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(SteelStatusId)) ||
            CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex) &&
                FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(StrengthStatusId)));
        if (_preparing != needed)
        {
            _preparing = needed;
            _preparationHold.SuppressInput = needed;
            _recordReplayEvent("boss-preparation", new { floor = _floor, waiting = needed });
        }
        if (!needed) return false;
        TryMaintainBossBuffs(dd);
        Status = "Boss floor - preparing items before approach";
        return true;
    }

    // Keep the existing one-second diagnostic cadence for both strategies.
    private void RecordBossCombatSnapshot()
    {
        var player = Service.LocalPlayer;
        var now = DateTime.UtcNow;
        if (player == null || now < _nextBossDiagnosticAtUtc) return;
        _nextBossDiagnosticAtUtc = now.AddSeconds(1);
        _recordBossSnapshot(_floor, player);
    }

    /// <summary>Maintenance-only update for the selected boss movement
    /// path: keeps the combat snapshot and buff maintenance while a live
    /// observed boss exists, sets status — NO Navigate/Cancel on the
    /// shared travel helper.</summary>
    public unsafe void UpdateManagedEncounter(InstanceContentDeepDungeon* dd,
        string? status, bool liveEncounter)
    {
        Status = status ?? "Boss floor - managed encounter";
        if (liveEncounter)
        {
            RecordBossCombatSnapshot();
            TryMaintainBossBuffs(dd);
        }
    }

    internal unsafe void UpdateApproach(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        if (player == null)
        {
            Status = "Boss floor - waiting for player";
            return;
        }

        if (Service.Condition[ConditionFlag.BetweenAreas] ||
            Service.Condition[ConditionFlag.BetweenAreas51])
        {
            Status = "Boss floor - waiting for floor load to complete";
            _navHelper?.Cancel();
            return;
        }
        if (!_requireMovementPermission("boss objective", FloorObjectiveKind.DefeatBoss))
            return;

        RecordBossCombatSnapshot();
        // Coord escape owns movement through the shared boundary — every
        // _navHelper write below (Navigate AND Cancel, they share the helper)
        // must stay silent while it holds.
        if (_coordEscapeActive?.Invoke() == true)
        {
            Status = "Boss floor - internal mechanic escape";
            return;
        }

        if (Service.Condition[ConditionFlag.InCombat])
        {
            TryMaintainBossBuffs(dd);
            if (!BossNavigationResolved)
            {
                _navHelper?.Cancel();
                BossNavigationResolved = true;
                Service.Log.Info("[FloorPhase] Boss combat started -> canceling boss navigation");
            }

            Status = "Boss floor - combat assist active";
            return;
        }

        var boss = Runtime.Helpers.CombatTargetingHelpers.PickNearestHostile(60f, out _);
        if (boss == null)
        {
            Status = "Boss floor - waiting for boss target";
            return;
        }

        TryMaintainBossBuffs(dd);
        if (BossNavigationResolved)
        {
            var currentTarget = Service.TargetManager.Target as IBattleChara;
            if (currentTarget != null &&
                currentTarget.GameObjectId == boss.GameObjectId &&
                !currentTarget.IsDead &&
                IsWithinBossEngageRadius(player.Position, boss.Position))
            {
                Status = "Boss floor - waiting for boss combat";
                return;
            }

            BossNavigationResolved = false;
            _recordReplayEvent("boss-navigation-reset", new
            {
                floor = dd->Floor,
                reason = "combat-ended-or-target-lost",
                bossId = boss.GameObjectId,
                currentTargetId = currentTarget?.GameObjectId ?? 0
            });
        }

        var withinRange = IsWithinBossEngageRadius(player.Position, boss.Position);
        if (withinRange)
        {
            _navHelper?.Cancel();
            BossNavigationResolved = true;
            Status = "Boss floor - at boss";
            Service.Log.Info("[FloorPhase] Boss navigation complete -> in engage range");
            return;
        }

        var state = _navHelper!.Navigate(boss.Position, player.Position, BossNavigationArrivalTolerance, retryIntervalSeconds: 5.0);
        switch (state)
        {
            case NavigationState.Moving:
                Status = "Boss floor - navigating to boss";
                break;
            case NavigationState.Arrived:
                BossNavigationResolved = true;
                Status = "Boss floor - at boss";
                Service.Log.Info("[FloorPhase] Boss navigation complete -> arrived");
                break;
            case NavigationState.StuckRepathing:
                Status = $"Boss floor - repathing to boss ({_navHelper.StuckRetryCount}/3)";
                break;
            case NavigationState.StuckGiveUp:
                BossNavigationResolved = true;
                Status = "Boss floor - boss navigation failed";
                Service.Log.Warning("[FloorPhase] Boss navigation gave up");
                break;
            case NavigationState.Failed:
                BossNavigationResolved = true;
                Status = "Boss floor - boss navigation unavailable";
                Service.Log.Warning("[FloorPhase] Boss navigation failed to start");
                break;
        }
    }

    private static bool IsWithinBossEngageRadius(Vector3 playerPosition, Vector3 bossPosition)
    {
        var dx = playerPosition.X - bossPosition.X;
        var dz = playerPosition.Z - bossPosition.Z;
        return dx * dx + dz * dz <=
               BossNavigationArrivalTolerance * BossNavigationArrivalTolerance;
    }

    private bool CombatBuffUsable(uint slot, bool overcapRelief = false) =>
        _items.IsPomanderAvailableForFloorUse(slot) && ItemPolicy.AllowsCombatBuff(
            _pomanderManager.GetCount(slot), _ctx?.Duty.IsBossFloor == true, overcapRelief);

    private static float LocalStatusRemaining(uint statusId, bool zeroMeansPermanent = false)
    {
        var player = Service.LocalPlayer;
        if (player != null)
            foreach (var status in player.StatusList)
                if (status.StatusId == statusId)
                    return zeroMeansPermanent && status.RemainingTime == 0 ? float.PositiveInfinity : Math.Abs(status.RemainingTime);
        return 0;
    }

    private unsafe void TryMaintainBossBuffs(InstanceContentDeepDungeon* dd)
    {
        if (_movementStrategy.SuppressCombatItems && Service.Condition[ConditionFlag.InCombat]) return;
        if (_ctx?.ControlledPtSurvey != null || !_items.CanAttemptPomanderUse(allowCombat: true)) return;
        // Defence first. Refresh shortly before expiry rather than repeatedly overwriting active buffs.
        if (CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(SteelStatusId)) &&
            _items.TryUsePomander(FloorInitPlanner.SteelPomanderSlotIndex, dd, "boss steel refresh")) return;
        if (CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(StrengthStatusId)))
            if (_items.TryUsePomander(FloorInitPlanner.StrengthPomanderSlotIndex, dd, "boss strength refresh")) return;
        if (dd->DeepDungeonId == 4 && CombatBuffUsable(FloorInitPlanner.HastePomanderSlotIndex) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(PtHasteStatusId)))
            if (_items.TryUsePomander(FloorInitPlanner.HastePomanderSlotIndex, dd, "boss haste refresh")) return;
        if (dd->DeepDungeonId == 4)
        {
            // Demiclone row 5/native stone 3, not the floor-effect removal pomander.
            float remaining = LocalStatusRemaining(4584, zeroMeansPermanent: true); // Tranquil Moonlight: maximum HP.
            if (!FarmingItemPolicy.NeedsBossRefresh(remaining)) BossSerenityAwaitingBuff = false;
            if (ItemPolicy.AllowsBossSerenity(_items.GetStoneCountAvailableForFloorUse(3), _ctx?.Duty.IsBossFloor == true,
                remaining, BossSerenityAwaitingBuff))
                _items.TryDispatchFloorStone(3, dd, "boss serenity incense", FloorItemUsePurpose.BossSerenity);
        }
    }
}
