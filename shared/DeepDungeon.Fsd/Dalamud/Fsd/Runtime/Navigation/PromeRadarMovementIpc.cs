using global::Dalamud.Plugin;
using global::Dalamud.Plugin.Ipc;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

internal sealed class PromeRadarMovementIpc : IDisposable
{
    private readonly ICallGateProvider<bool, bool> setControl;
    private readonly ICallGateSubscriber<bool> getControl;
    private readonly Action suspendMovement;
    private bool disposed;

    internal PromeRadarMovementIpc(IDalamudPluginInterface pluginInterface, Action suspendMovement)
    {
        this.suspendMovement = suspendMovement;
        getControl = pluginInterface.GetIpcSubscriber<bool>("PromeRadar.IsMechanicMovementActive");
        setControl = pluginInterface.GetIpcProvider<bool, bool>("NewFolder3.Movement.SetPromeRadarControl");
        setControl.RegisterFunc(SetControl);
    }

    private bool SetControl(bool active)
    {
        if (disposed || !Service.Framework.IsInFrameworkUpdateThread) return false;
        PromeRadarMovementGate.SetControl(active, suspendMovement);
        return true;
    }

    internal void Refresh()
    {
        // Reconcile reloads against the live scalar, independently of GetState's JSON cache.
        bool active;
        try { active = getControl.HasFunction && getControl.InvokeFunc(); }
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
