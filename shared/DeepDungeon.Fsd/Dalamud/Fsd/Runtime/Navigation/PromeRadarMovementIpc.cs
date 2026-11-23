using global::Dalamud.Plugin;
using global::Dalamud.Plugin.Ipc;
using System.Text.Json;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

internal sealed class PromeRadarMovementIpc : IDisposable
{
    private readonly ICallGateProvider<bool, bool> setControl;
    private readonly ICallGateSubscriber<bool> getControl;
    private readonly ICallGateSubscriber<string> getState;
    private readonly Action suspendMovement;
    private bool disposed;

    internal PromeRadarMovementIpc(IDalamudPluginInterface pluginInterface, Action suspendMovement)
    {
        this.suspendMovement = suspendMovement;
        getControl = pluginInterface.GetIpcSubscriber<bool>("PromeRadar.IsMechanicMovementActive");
        getState = pluginInterface.GetIpcSubscriber<string>("PromeRadar.GetState");
        setControl = pluginInterface.GetIpcProvider<bool, bool>("NewFolder3.Movement.SetPromeRadarControl");
        setControl.RegisterFunc(SetControl);
    }

    private bool SetControl(bool active)
    {
        if (disposed || !Service.Framework.IsInFrameworkUpdateThread) return false;
        if (active && !ReadCurrentState(requireMovementControl: false)) return false;
        PromeRadarMovementGate.SetControl(active, suspendMovement);
        return true;
    }

    private bool ReadCurrentState(bool requireMovementControl)
    {
        if (!getState.HasFunction) return false;
        var state = JsonSerializer.Deserialize<PromeRadarMovementState>(getState.InvokeFunc());
        var now = DateTime.UtcNow;
        return state != null && state.IsCurrent(Service.ClientState.TerritoryType, now) &&
            state.SmartMovementEnabled && (!requireMovementControl ||
                state.OwnsDodgeMovement(Service.ClientState.TerritoryType, now));
    }

    internal void Refresh()
    {
        // Reconcile reloads against the live scalar, independently of GetState's JSON cache.
        bool active;
        try { active = getControl.HasFunction && getControl.InvokeFunc() && ReadCurrentState(requireMovementControl: true); }
        catch
        {
            // An unreadable provider is not evidence that MP released movement.
            PromeRadarMovementGate.SetControl(true, suspendMovement);
            throw;
        }
        if (active != PromeRadarMovementGate.IsBlocked || active && !PromeRadarMovementGate.HandoffComplete)
            SetControl(active);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { setControl.UnregisterFunc(); }
        finally { PromeRadarMovementGate.SetControl(false, suspendMovement); }
    }
}
