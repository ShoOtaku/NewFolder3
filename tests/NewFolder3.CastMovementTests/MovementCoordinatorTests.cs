using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

internal static class MovementCoordinatorTests
{
    internal static void Run()
    {
        OrphanAndStationaryObjective();
        PendingAndFailedHandoff();
        FailedStopRetries();
        AdapterOwnership();
        ManualAndMp();
        Console.WriteLine("PASS movement ownership: orphan/pending VNav cleanup, stop readback, stationary objective, cancellation/read failure, stale adapter, manual rejection, MP acquire/release and fresh replan.");
    }

    private static void OrphanAndStationaryObjective()
    {
        var state = new State { Running = true };
        var coordinator = state.Create();
        var direct = new object();
        Check(coordinator.TryAcquireDirect(direct), "An orphan route prevented a verified takeover.");
        Check(state.Events.SequenceEqual(new[] { "release", "cancel", "stop", "release" }),
            "Takeover did not revoke direct input, cancel pending work and stop following in order.");
        Check(coordinator.CanWriteDirect(direct) && !state.Running, "Direct input preceded VNav stop verification.");
        // A later external route starts while the candidate is standing still.
        state.Pending = true;
        coordinator.SetAutomatic(true, true);
        Check(!state.Pending && !coordinator.CanWriteDirect(direct), "A stationary objective let a late search survive.");
        Check(!coordinator.StartVNav(new object(), false, () => throw new Exception("must not start")),
            "Legacy VNav bypassed normal-floor direct ownership.");
        int calls = state.Events.Count;
        coordinator.SetAutomatic(true, true);
        Check(state.Events.Count == calls, "An already stopped VNav was cancelled every frame.");
        state.Running = true;
        coordinator.SetAutomatic(false, false);
        Check(!state.Running && !coordinator.CanWriteDirect(direct), "Stopping FSD retained a route or direct permission.");
    }

    private static void PendingAndFailedHandoff()
    {
        var state = new State { Pending = true, KeepPending = true };
        var coordinator = state.Create();
        var direct = new object();
        Check(!coordinator.TryAcquireDirect(direct) && !coordinator.CanWriteDirect(direct),
            "A queued cancellation was mistaken for a completed handoff.");
        state.KeepPending = false;
        Check(coordinator.TryAcquireDirect(direct) && !state.Pending, "A completed async cancellation did not unblock takeover.");
        state.Running = true;
        state.FailCancel = true;
        Check(!coordinator.TryAcquireDirect(direct) && !coordinator.CanWriteDirect(direct),
            "Cancellation failure left direct input authorized.");
        Check(!state.Running && state.Events.Last() == "release", "Path.Stop was skipped after pathfinding cancellation threw.");
        state.FailCancel = false;
        state.FailRead = true;
        Check(!coordinator.TryAcquireDirect(direct), "An unreadable VNav provider was treated as idle.");
        state.FailRead = false;
        Check(coordinator.TryAcquireDirect(direct), "Failed handoff could not recover.");
    }

    private static void AdapterOwnership()
    {
        var state = new State();
        var coordinator = state.Create();
        int starts = 0;
        bool Start(Vector3 _, bool __) { starts++; state.Pending = true; return true; }
        using var old = new NavigatorVNavAdapter(Start, coordinator);
        using var next = new NavigatorVNavAdapter(Start, coordinator);
        Check(old.PathfindAndMoveTo(Vector3.UnitX, false), "First adapter failed to acquire VNav.");
        Check(next.PathfindAndMoveTo(Vector3.UnitZ, false) && starts == 2, "Second adapter failed to replace the old route.");
        int calls = state.Events.Count;
        old.CancelAll();
        old.Dispose();
        Check(state.Events.Count == calls && state.Pending, "Retired adapter stopped the new owner's pending route.");
        var direct = new object();
        Check(coordinator.TryAcquireDirect(direct), "Direct navigation did not cancel the pending VNav owner.");
        calls = state.Events.Count;
        next.CancelAll();
        Check(state.Events.Count == calls && coordinator.CanWriteDirect(direct), "Old VNav cleanup revoked the new direct owner.");
        coordinator.ReleaseDirect(direct);
        // False return after enqueue must still be cleaned up.
        using var rejected = new NavigatorVNavAdapter((_, _) => { state.Pending = true; return false; }, coordinator);
        Check(!rejected.PathfindAndMoveTo(Vector3.One, false) && !state.Pending, "A rejected dispatch left async work behind.");
    }

    private static void FailedStopRetries()
    {
        var state = new State { Pending = true, KeepPending = true };
        var coordinator = state.Create();
        coordinator.SetAutomatic(true, false);
        bool waiting = false;
        try { coordinator.SetAutomatic(false, false); }
        catch (InvalidOperationException) { waiting = true; }
        Check(waiting && !coordinator.AutomaticActive, "A failed stop was reported as complete.");
        state.KeepPending = false;
        coordinator.SetAutomatic(false, false);
        Check(!state.Pending, "Inactive FSD stopped retrying its unfinished cancellation.");
        Check(coordinator.StartVNav(new object(), true, () => { state.Running = true; return true; }),
            "Verified stop prevented a later manual route.");
        coordinator.SetAutomatic(false, false);
        Check(state.Running, "A completed stop retry cancelled a new manual route.");
    }

    private static void ManualAndMp()
    {
        var state = new State();
        var coordinator = state.Create();
        using var manual = new NavigatorVNavAdapter((_, _) => { state.Running = true; return true; }, coordinator, true);
        coordinator.SetAutomatic(true, false);
        Check(!coordinator.CanStartManual && !manual.PathfindAndMoveTo(Vector3.One, false),
            "Manual map navigation bypassed an active FSD session.");
        coordinator.SetAutomatic(false, false);
        Check(manual.PathfindAndMoveTo(Vector3.One, false), "Manual navigation remained blocked after FSD stopped.");
        try
        {
            PromeRadarMovementGate.SetControl(true, () =>
            {
                state.Mp = true;
                coordinator.RevokeDirect();
                coordinator.QuiesceVNav();
                Check(!state.Running && !state.Pending, "MP acknowledgement preceded VNav cancellation.");
            });
            Check(!coordinator.TryAcquireDirect(new object()) && !manual.PathfindAndMoveTo(Vector3.Zero, false),
                "FSD or manual movement started during MP ownership.");
            PromeRadarMovementGate.SetControl(false, () => throw new Exception("must not stop MP on release"));
            state.Mp = false;
            var direct = new object();
            Check(coordinator.TryAcquireDirect(direct), "FSD did not reacquire after MP released movement.");
            Check(coordinator.StartVNav(new object(), false, () => true) && !coordinator.CanWriteDirect(direct),
                "Switching to a legitimate VNav flow left direct output enabled.");
        }
        finally { PromeRadarMovementGate.SetControl(false, () => { }); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class State
    {
        internal bool Running, Pending, KeepPending, FailCancel, FailRead, Mp;
        internal readonly List<string> Events = [];
        internal MovementCoordinator Create() => new(
            () => FailRead ? throw new InvalidOperationException("read failed") : (Running, Pending),
            () =>
            {
                Events.Add("cancel");
                if (FailCancel) throw new InvalidOperationException("cancel failed");
                if (!KeepPending) Pending = false;
            },
            () => { Events.Add("stop"); Running = false; },
            () => Events.Add("release"), () => Mp);
    }
}
