using System.Text.Json;
using Dalamud.Plugin.Ipc;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Helpers;

/// <summary>Keep MP enabled for the whole run; FSD yields its travel input while MP actively dodges.</summary>
internal sealed class PromeRadarFarmingMovement : IDisposable
{
    private readonly ICallGateSubscriber<string> getState = Service.PluginInterface.GetIpcSubscriber<string>("PromeRadar.GetState");
    private readonly ICallGateSubscriber<bool, object> setSession = Service.PluginInterface.GetIpcSubscriber<bool, object>("PromeRadar.SetFsdSessionActive");
    private PromeRadarMovementState? lastState;
    private DateTime nextReadAtUtc;
    private bool disposed;

    public PromeRadarFarmingMovement(RunContext context)
    {
        var state = ReadState();
        if (state == null || !state.IsCurrent(Service.ClientState.TerritoryType, DateTime.UtcNow) || !state.Pt30ScriptAvailable)
            throw new InvalidOperationException("PromeRadar 未就緒或缺少 MP 自有 PT30 腳本；停止 PT21–30 入場。");
        setSession.InvokeAction(true);
        context.RecordDiagnostic?.Invoke("mp-farming-movement-enabled", new { wasEnabled = state.SmartMovementEnabled, state.BuildMvid });
    }

    public PromeRadarMovementState? ReadState()
    {
        var now = DateTime.UtcNow;
        if (now >= nextReadAtUtc)
        {
            lastState = JsonSerializer.Deserialize<PromeRadarMovementState>(getState.InvokeFunc());
            nextReadAtUtc = now.AddMilliseconds(100);
        }
        return lastState;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        setSession.InvokeAction(false);
    }
}
