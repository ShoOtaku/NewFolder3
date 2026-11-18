using System.Text.Json;
using Dalamud.Plugin.Ipc;
using DeepDungeon.Fsd.Core;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>PT30 has one movement owner: PromeRadar. FSD retains entry, combat items and exit.</summary>
internal sealed unsafe class PromeRadarBossMovementStrategy(
    FloorExplorationController floor, RunContext context, Action<string, object> record) : IBossMovementStrategy
{
    private readonly ICallGateSubscriber<string> state = Service.PluginInterface.GetIpcSubscriber<string>("PromeRadar.GetState");
    private readonly ICallGateSubscriber<bool, object> setSession = Service.PluginInterface.GetIpcSubscriber<bool, object>("PromeRadar.SetFsdSessionActive");
    private bool enableRequested;
    private bool handedOver;
    private DateTime startedAtUtc;
    private DateTime nextRecordAtUtc;
    public string Name => "PromeRadar-PT30";
    public bool SuppressCombatItems => false;
    public bool RequestsNorthCamera => false;
    internal PromeRadarMovementState? Diagnostics { get; private set; }

    public void Start()
    {
        startedAtUtc = DateTime.UtcNow;
        enableRequested = false;
        handedOver = false;
        floor.CancelCoordMovement();
    }

    public string? Update(InstanceContentDeepDungeon* dungeon)
    {
        var now = DateTime.UtcNow;
        string reason;
        try
        {
            Diagnostics = JsonSerializer.Deserialize<PromeRadarMovementState>(state.InvokeFunc());
            if (Diagnostics is { } current && current.IsCurrent(Service.ClientState.TerritoryType, now))
            {
                if (!current.FsdSessionActive && !enableRequested)
                {
                    setSession.InvokeAction(true);
                    enableRequested = true;
                    record("mp-boss-movement-requested", new { floor = 30 });
                }
                if (current.OwnsPt30(Service.ClientState.TerritoryType, now))
                {
                    if (!handedOver)
                    {
                        floor.CancelCoordMovement();
                        handedOver = true;
                        record("mp-boss-movement-acquired", current);
                    }
                    if (now >= nextRecordAtUtc)
                    {
                        nextRecordAtUtc = now.AddSeconds(1);
                        record("mp-boss-movement-state", current);
                    }
                    startedAtUtc = now;
                    return "Boss floor - PromeRadar MP script owns PT30 mechanics";
                }
                if (current.CanMove && !Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]
                    && !current.Pt30ScriptReady)
                {
                    floor.Boss.UpdateApproach(dungeon);
                    startedAtUtc = now;
                    return "Boss floor - approaching before PromeRadar PT30 script activation";
                }
                reason = $"script={current.Pt30ScriptVersion}, ready={current.Pt30ScriptReady}, session={current.FsdSessionActive}, enabled={current.SmartMovementEnabled}, canMove={current.CanMove}, nav={current.NavigationActive}";
            }
            else reason = "PromeRadar state missing, stale, replaying or from another territory";
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }
        if ((now - startedAtUtc).TotalSeconds >= 10)
        {
            context.StatusLine = $"PT30 PromeRadar 移動未就緒；停止並保留現場：{reason}";
            context.StatusIsError = true;
            record("mp-boss-movement-unavailable", new { reason, state = Diagnostics });
        }
        return $"Boss floor - waiting for PromeRadar: {reason}";
    }

    public void Stop()
    {
        enableRequested = false;
        handedOver = false;
    }
}
