using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.Coord;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using DeepDungeon.Fsd.Dalamud.Utilities;
using MasterPlayer.MechanicRuntime;

internal static class PromeRadarMovementTests
{
    internal static void Run()
    {
        try
        {
            GateAndNavigation();
            foreach (bool precondition in new[] { false, true }) ChestPause(precondition);
            AcceptedDuringPause();
            CachedChestStaysSuspended();
        }
        finally { PromeRadarMovementGate.SetControl(false, () => { }); }
        Console.WriteLine("PASS MP exclusive movement: synchronous revocation, denied cancellation, output/correction gates, long navigation wait, chest identity, frozen timers, reapproach and observed acceptance.");
    }

    private static void GateAndNavigation()
    {
        var now = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
        var navigator = new FakeNavigator();
        var helper = new NavigationHelper(navigator, () => navigator.Running, () => now, _ => { }, _ => { }, () => false, () => false);
        var chest = Vector3.UnitZ * 20;
        helper.Navigate(chest, Vector3.Zero);
        PromeRadarMovementGate.SetControl(true, () =>
        {
            Check(PromeRadarMovementGate.IsBlocked, "Input permission was not revoked before route cancellation.");
            helper.Cancel();
        });
        Check(!navigator.Running, "The old FSD route survived acknowledgement.");
        for (int i = 0; i < 60; ++i)
        {
            now = now.AddSeconds(1);
            Check(helper.Navigate(chest, Vector3.UnitX * 6) == NavigationState.Moving, "MP wait became a path failure.");
        }
        Check(navigator.Starts == 1 && helper.StuckRetryCount == 0 && helper.NavigationIssueCount == 0,
            "FSD redispatched or consumed retries during MP ownership.");
        using (var direct = new DirectMovementController("test"))
        {
            Check(!NativeNavigationOutput.Apply(direct,
                    new(NavigationCommandKind.Move, Vector2.One, Vector2.One), Vector3.Zero),
                "A blocked correction was reported as applied.");
            Check(!direct.Enabled && !DirectMovementController.InputHooksReady,
                "A blocked output reached native initialization/position correction.");
            Check(!direct.TryWriteForcedDirection(0), "A blocked output wrote forced direction.");
        }
        Check(!DeepDungeon.Fsd.Dalamud.moveHelper.VNav.SimpleMove.PathfindAndMoveTo(chest), "VNav bypassed MP's movement gate.");
        PromeRadarMovementGate.SetControl(false, () => throw new InvalidOperationException("Release must not cancel MP"));
        helper.Navigate(chest, Vector3.UnitX * 6);
        Check(navigator.Starts == 2 && navigator.Running && helper.NavigationIssueCount == 0,
            "FSD failed to replan from its displaced position without a retry penalty.");
        try { PromeRadarMovementGate.SetControl(true, () => throw new InvalidOperationException("cancel-failed")); }
        catch (InvalidOperationException ex) when (ex.Message == "cancel-failed") { }
        Check(PromeRadarMovementGate.IsBlocked, "Cancellation failure reopened the input gate.");
        int cancellations = 0;
        int stops = 0;
        bool cancellationFails = true;
        using (var trackedRoute = new NavigatorVNavAdapter((_, _) => true, () => false, () =>
        {
            ++cancellations;
            if (cancellationFails) throw new InvalidOperationException("pending-cancel-failed");
        }, () => ++stops))
        {
            PromeRadarMovementGate.SetControl(false, () => { });
            trackedRoute.PathfindAndMoveTo(chest, false);
            try { PromeRadarMovementGate.SetControl(true, trackedRoute.CancelAll); }
            catch (InvalidOperationException ex) when (ex.Message == "pending-cancel-failed") { }
            Check(PromeRadarMovementGate.IsBlocked && !PromeRadarMovementGate.HandoffComplete && cancellations == 1 && stops == 1,
                "Handoff trusted an idle pathfinding sample or swallowed cancellation failure.");
            cancellationFails = false;
            PromeRadarMovementGate.SetControl(true, trackedRoute.CancelAll);
            Check(cancellations == 2 && stops == 2 && PromeRadarMovementGate.HandoffComplete,
                "Failed cancellation was not retried before acknowledgement.");
        }
        PromeRadarMovementGate.SetControl(false, () => { });
    }

    private static void ChestPause(bool precondition)
    {
        var now = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
        var native = new ChestBoundary { CanStart = !precondition };
        var op = new OpenChestOp(new ChestInteractionTarget(Vector3.UnitZ, FloorChestKind.Gold));
        var facts = Facts(now);
        op.Tick(facts, native, now, OpAuthority.None);
        op.OnNavArrived();
        now = now.AddMilliseconds(100);
        op.Tick(facts, native, now, OpAuthority.None);
        var attempt = op.ActiveChestAttempt;
        Check(precondition ? native.Interactions == 0 : native.Interactions == 1, "The test did not enter chest waiting.");
        op.OnSuspended(now);
        op.OnSuspended(now.AddSeconds(10));
        Check(op.Claim == MoveClaim.None, "A suspended chest kept a navigation claim.");
        now = now.AddMinutes(2);
        Check(op.Tick(facts, native, now, OpAuthority.None) == OpStatus.Running && native.Interactions == (precondition ? 0 : 1),
            "Suspended work interacted or timed out.");
        op.OnResumed(now);
        native.CanStart = true;
        Check(op.Tick(facts, native, now, OpAuthority.None) == OpStatus.Running && op.Failure == null,
            "The MP pause consumed the chest timeout.");
        Check(native.Interactions == (precondition ? 0 : 1), "A stale arrival triggered interaction before reapproach.");
        op.OnNavArrived();
        Check(op.Tick(facts, native, now.AddMilliseconds(100), OpAuthority.None) == OpStatus.Running,
            "Chest could not resume after reapproach.");
        Check(ReferenceEquals(attempt, op.ActiveChestAttempt) && ReferenceEquals(attempt, native.LastAttempt) && op.Attempts == 0,
            "Resumption replaced the chest attempt or spent a retry.");
        Check(op.Tick(facts, native, now.AddSeconds(40), OpAuthority.None) == OpStatus.Failed,
            "Active chest timeout was accidentally disabled rather than paused.");
    }

    private static void AcceptedDuringPause()
    {
        var now = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
        var native = new ChestBoundary();
        var op = new OpenChestOp(new ChestInteractionTarget(Vector3.UnitZ, FloorChestKind.Gold));
        op.Tick(Facts(now), native, now, OpAuthority.None);
        op.OnSuspended(now);
        native.Accepted = true;
        Check(op.Tick(Facts(now.AddSeconds(2)), native, now.AddSeconds(2), OpAuthority.None) == OpStatus.Succeeded && native.Interactions == 0,
            "Server acceptance during the dodge was discarded or opened twice.");
    }

    private static FloorFacts Facts(DateTime now) => new FloorObservation().Assemble(21, 0, now, Vector3.Zero,
        false, false, false, new(true, 1, 1, now, Vector3.Zero, 0, [], [], [], []));
    private static void CachedChestStaysSuspended()
    {
        var now = new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);
        var native = new ChestBoundary();
        var cached = new OpenChestOp(new ChestInteractionTarget(Vector3.UnitZ, FloorChestKind.Gold));
        cached.Tick(Facts(now), native, now, OpAuthority.None);
        cached.OnNavArrived();
        cached.Tick(Facts(now), native, now.AddMilliseconds(100), OpAuthority.None);
        var attempt = cached.ActiveChestAttempt;
        cached.OnSuspended(now.AddMilliseconds(200)); // Its route head was replaced.
        var bound = new OpenChestOp(new ChestInteractionTarget(Vector3.UnitX, FloorChestKind.Gold));
        var pipeline = new CoordPipeline((_, _) => { });
        var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var cache = (Dictionary<SiteWorkToken, FloorOperation>)typeof(CoordPipeline)
            .GetField("_workOperations", fields)!.GetValue(pipeline)!;
        cache[new(1, "cached", 1)] = cached;
        cache[new(1, "bound", 1)] = bound;
        typeof(CoordPipeline).GetField("_objectiveOp", fields)!.SetValue(pipeline, bound);
        pipeline.SetExternalMovement(true, now.AddSeconds(1));
        pipeline.SetExternalMovement(false, now.AddSeconds(60));
        cached.OnResumed(now.AddSeconds(120)); // Rebind A only after B's external pause ended.
        Check(cached.Tick(Facts(now), native, now.AddSeconds(120), OpAuthority.None) == OpStatus.Running &&
              cached.Failure == null && ReferenceEquals(attempt, cached.ActiveChestAttempt),
            "An external pause resumed an unbound cached chest and consumed its deadline.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private sealed class ChestBoundary : INativeBoundary
    {
        internal bool CanStart = true;
        internal bool Accepted;
        internal int Interactions;
        internal ChestInteractionAttempt? LastAttempt;
        public SiteSearchBoundary? Search => null;
        public NavResult Execute(IClaimVerb? verb) => new(NavOutcome.Arrived);
        public NavOutcome Navigate(Vector3 dest, float radius) => NavOutcome.Arrived;
        public void CancelMovement() { }
        public bool IsChestAccepted(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence, out ChestLifecycleSnapshot snapshot)
        { snapshot = default; return Accepted; }
        public bool IsChestTargetMissing(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence) => false;
        public bool TryChestInteract(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot evidence, out ChestLifecycleSnapshot snapshot, out bool retry)
        { LastAttempt = attempt; Interactions++; snapshot = default; retry = false; return true; }
        public float ChestOpenTimeoutSeconds(ChestInteractionTarget waypoint) => 30;
        public bool CanStartChest(ChestInteractionAttempt attempt) => CanStart;
        public ChestPositionObservation ObserveChestPosition(ChestInteractionAttempt attempt, FloorObjectEvidenceSnapshot? evidence) =>
            new(true, Interactions > 0, true, false, 100, Vector3.UnitZ, new(3));
        public EnemyChaseTarget? ChaseTarget(Vector3 playerPos, bool allowNewTargets) => null;
    }
}
