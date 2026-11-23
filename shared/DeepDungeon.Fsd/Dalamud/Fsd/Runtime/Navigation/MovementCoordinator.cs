using DeepDungeon.Fsd.Dalamud.Utilities;
using VNav = DeepDungeon.Fsd.Dalamud.moveHelper.VNav;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

/// <summary>Framework-thread ownership; native callbacks only read the granted permission.</summary>
internal sealed class MovementCoordinator
{
    internal static MovementCoordinator Shared { get; } = new(
        VNav.ReadMovementState,
        () => VNav.Nav.PathfindCancelAll(throwOnFailure: true),
        () => VNav.Path.Stop(throwOnFailure: true),
        DirectMovementController.ReleaseActive,
        () => PromeRadarMovementGate.IsBlocked);

    private readonly Func<(bool Available, bool Running, bool Pending)> readVNav;
    private readonly Action cancelPathfinding;
    private readonly Action stopPath;
    private readonly Action releaseDirect;
    private readonly Func<bool> mpActive;
    private object? vnavOwner;
    private object? directOwner;
    private bool vnavDirty;
    private bool automaticStopPending;

    internal MovementCoordinator(Func<(bool Available, bool Running, bool Pending)> readVNav,
        Action cancelPathfinding, Action stopPath, Action releaseDirect, Func<bool> mpActive)
    {
        this.readVNav = readVNav;
        this.cancelPathfinding = cancelPathfinding;
        this.stopPath = stopPath;
        this.releaseDirect = releaseDirect;
        this.mpActive = mpActive;
    }

    internal bool AutomaticActive { get; private set; }
    internal bool DirectRequired { get; private set; }
    internal string Status { get; private set; } = "idle";
    internal string? LastError { get; private set; }
    internal bool CanStartManual => !AutomaticActive && !mpActive();
    internal bool CanWriteDirect(object owner) => !mpActive() && !vnavDirty &&
        vnavOwner == null && ReferenceEquals(directOwner, owner);

    internal void SetAutomatic(bool active, bool directRequired)
    {
        bool stopping = AutomaticActive && !active;
        AutomaticActive = active;
        DirectRequired = active && directRequired;
        if (stopping)
        {
            automaticStopPending = true;
            RevokeDirect();
        }
        if (DirectRequired || automaticStopPending) QuiesceVNav();
    }

    internal bool TryAcquireDirect(object owner)
    {
        if (mpActive()) return false;
        try
        {
            QuiesceVNav();
            if (!ReferenceEquals(directOwner, owner))
            {
                RevokeDirect();
                directOwner = owner;
            }
            Status = "fsd-direct";
            return true;
        }
        catch (Exception error)
        {
            RevokeDirect();
            LastError = error.ToString();
            Status = "waiting-vnav-stop";
            return false;
        }
    }

    internal bool StartVNav(object owner, bool manual, Func<bool> start)
    {
        if (mpActive() || DirectRequired || manual && !CanStartManual) return false;
        RevokeDirect();
        QuiesceVNav();
        // An invocation can enqueue work before returning false or throwing.
        vnavOwner = owner;
        vnavDirty = true;
        Status = manual ? "manual-vnav" : "fsd-vnav";
        try
        {
            if (start()) return true;
            QuiesceVNav();
            return false;
        }
        catch
        {
            QuiesceVNav();
            throw;
        }
    }

    internal void CancelVNav(object owner)
    {
        // A retired adapter must never stop the next owner's route.
        if (ReferenceEquals(vnavOwner, owner)) QuiesceVNav();
    }

    internal void QuiesceVNav()
    {
        try
        {
            var state = readVNav();
            if (!state.Available)
            {
                // An entirely unregistered provider cannot retain queued work.
                if (vnavDirty) RevokeDirect();
                vnavOwner = null;
                vnavDirty = false;
                automaticStopPending = false;
                LastError = null;
                Status = mpActive() ? "prome-radar" : "idle";
                return;
            }
            if (!vnavDirty && !state.Running && !state.Pending)
            {
                automaticStopPending = false;
                LastError = null;
                return;
            }
            // Revoke direct output before calling either IPC, even on failure.
            RevokeDirect();
            vnavDirty = true;
            try { cancelPathfinding(); }
            finally { stopPath(); }
            state = readVNav();
            if (state.Running || state.Pending)
                throw new InvalidOperationException("VNav cancellation is pending; movement ownership has not transferred.");
            vnavOwner = null;
            vnavDirty = false;
            automaticStopPending = false;
            LastError = null;
            Status = mpActive() ? "prome-radar" : "idle";
        }
        catch (Exception error)
        {
            RevokeDirect();
            vnavDirty = true;
            LastError = error.ToString();
            Status = "waiting-vnav-stop";
            throw;
        }
    }

    internal void ReleaseDirect(object owner)
    {
        if (ReferenceEquals(directOwner, owner)) directOwner = null;
    }

    internal void RevokeDirect()
    {
        directOwner = null;
        releaseDirect();
    }
}
