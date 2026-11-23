using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Runtime;

internal static class PromeRadarFloorMovementTests
{
    internal static void Run()
    {
        foreach (bool original in new[] { false, true })
        {
            SwitchAndRestore(original);
            StopWhileQueued(original);
        }
        foreach (uint dungeon in new[] { 1u, 2u })
            for (int floor = 1; floor <= (dungeon == 1 ? 200 : 100); floor++)
                FloorCoverage(dungeon, floor);
        LoadingAndFloorset();
        MissingReloadAndUserChange();
        FailureAndTimeout();
        Console.WriteLine("PASS MP floor policy: all Palace/HoH floors, queued switch acknowledgement, live ownership release, reverse/stop compensation, original-state restoration, transitions, provider reload, manual override and IPC timeout.");
    }

    private static DeepDungeonStateSnapshot Floor(uint dungeon, int floor) =>
        new(true, true, true, dungeon, (byte)floor,
            DeepDungeonFloorClassifier.Classify(dungeon, floor), false, 1);

    private static void SwitchAndRestore(bool original)
    {
        var h = new Harness(original);
        using var policy = h.Create();
        bool ready = policy.Update(true, Floor(1, 9));
        Check(ready == original, "Normal floor trusted an unapplied enable request.");
        h.Apply();
        Check(policy.Update(true, Floor(1, 9)) && h.Enabled, "Normal floor did not enable MP.");
        Check(!policy.Update(true, Floor(1, 10)) && policy.SuppressControl && h.Enabled,
            "Spin started before the queued disable was applied.");
        h.Active = true;
        h.Apply();
        Check(!policy.Update(true, Floor(1, 10)), "Disabled switch was treated as released movement.");
        h.Active = false;
        Check(policy.Update(true, Floor(1, 10)), "Spin did not become ready after MP released movement.");
        Check(!policy.Update(true, Floor(1, 11)), "Return to normal floor skipped enable acknowledgement.");
        h.Apply();
        Check(policy.Update(true, Floor(1, 11)) && h.Enabled, "MP did not resume on the next normal floor.");
        policy.Update(false, Floor(1, 11));
        h.Apply();
        Check(policy.Update(false, Floor(1, 11)) && h.Enabled == original, "Stop lost the original switch state.");
    }

    private static void StopWhileQueued(bool original)
    {
        var h = new Harness(original);
        using (var policy = h.Create())
        {
            // Request the opposite of the current state, then stop before MP drains its queue.
            policy.Update(true, Floor(1, original ? 10 : 9));
            policy.Update(false, Floor(1, 10));
            h.Apply();
            Check(h.Enabled == original, "Stop did not compensate an unapplied switch request.");
        }
        h = new Harness(original);
        using (var policy = h.Create())
            policy.Update(true, Floor(1, original ? 10 : 9));
        h.Apply();
        Check(h.Enabled == original, "Dispose did not compensate a queued switch request.");

        h = new Harness(original);
        using var reversed = h.Create();
        reversed.Update(true, Floor(1, original ? 10 : 9));
        reversed.Update(true, Floor(1, original ? 11 : 10));
        h.Apply();
        Check(reversed.Update(true, Floor(1, original ? 11 : 10)) && h.Enabled == original,
            "A rapid floor reversal left an opposite request queued.");
    }

    private static void FloorCoverage(uint dungeon, int floor)
    {
        var h = new Harness(true);
        using var policy = h.Create();
        var state = Floor(dungeon, floor);
        policy.Update(true, state);
        h.Apply();
        Check(policy.Update(true, state), "A supported floor did not settle.");
        bool spin = DeepDungeonBossPolicy.UsesSpin(dungeon, floor);
        Check(policy.SuppressControl == spin && h.Enabled != spin, "MP/spin selection disagreed with the shared boss policy.");
    }

    private static void LoadingAndFloorset()
    {
        var h = new Harness(true);
        using var policy = h.Create();
        var boss = Floor(1, 10);
        policy.Update(true, boss);
        h.Apply();
        policy.Update(true, boss);
        int writes = h.Writes.Count;
        Check(!policy.Update(true, boss with { IsTransitioning = true }) && policy.SuppressControl,
            "Loading re-enabled MP on a spin floor.");
        Check(!policy.Update(true, boss with { IsValid = false }) && policy.SuppressControl,
            "Missing duty observations released the spin exclusion.");
        Check(policy.Update(true, new(true, false, false, 0, 0, DeepDungeonFloorKind.Unknown, false, 2)) &&
            policy.SuppressControl && h.Writes.Count == writes, "Floorset exit discarded the run's switch lease.");
        Check(!policy.Update(true, Floor(1, 11)), "Next floorset failed to request MP re-enable.");
        h.Apply();
        Check(policy.Update(true, Floor(1, 11)), "Next floorset failed to settle.");
        writes = h.Writes.Count;
        Check(policy.Update(true, Floor(4, 30)) && h.Writes.Count == writes,
            "The Palace/HoH switch policy changed PT30's existing MP ownership.");
    }

    private static void MissingReloadAndUserChange()
    {
        var h = new Harness(true) { Available = false };
        using var policy = h.Create();
        Check(policy.Update(true, Floor(1, 10)) && policy.SuppressControl, "An absent MP unnecessarily blocked spin.");
        Check(!policy.Update(true, Floor(1, 11)), "Normal travel silently proceeded without MP.");
        h.Available = true;
        Check(policy.Update(true, Floor(1, 11)), "MP appearance was not detected.");
        policy.Update(true, Floor(1, 20));
        h.Apply();
        policy.Update(true, Floor(1, 20));
        h.Build = "reloaded";
        h.Enabled = false;
        policy.Update(false, Floor(1, 20));
        Check(!h.Writes.Any() || h.Writes.Last() == false, "Old provider restoration leaked into the new MP instance.");
        Check(!h.Enabled, "Provider reload lost its own switch choice.");

        h = new Harness(true);
        using var manual = h.Create();
        manual.Update(true, Floor(1, 10));
        h.Apply();
        manual.Update(true, Floor(1, 10));
        h.Enabled = true; // The user changed the confirmed switch.
        int writes = h.Writes.Count;
        Check(manual.Update(false, Floor(1, 10)) && h.Writes.Count == writes,
            "Stop overwrote a user change after the last acknowledged write.");
    }

    private static void FailureAndTimeout()
    {
        var h = new Harness(true);
        using var policy = h.Create();
        h.FailRead = true;
        Check(!policy.Update(true, Floor(1, 10)) && policy.SuppressControl, "An unreadable provider authorized spin.");
        h.Now = h.Now.AddSeconds(10);
        CheckThrows(() => policy.Update(true, Floor(1, 10)), "Unacknowledged handoff did not time out.");
        h.FailRead = false;
        policy.Update(false, Floor(1, 10));

        h = new Harness(true) { FailWrite = true };
        var failed = h.Create();
        Check(!failed.Update(true, Floor(1, 10)), "A failed disable request authorized spin.");
        h.FailWrite = false;
        failed.Dispose();
        h.Apply();
        Check(h.Enabled, "Ambiguous write failure was not compensated during cleanup.");
    }

    private static void CheckThrows(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Harness(bool enabled)
    {
        internal bool Enabled = enabled;
        internal bool Active;
        internal bool Available = true;
        internal string Build = "original";
        internal bool FailRead;
        internal bool FailWrite;
        internal DateTime Now = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);
        internal readonly List<bool> Writes = [];
        private readonly Queue<bool> queue = new();

        internal PromeRadarFloorMovement Create() => new(
            () => FailRead ? throw new InvalidOperationException("read failed") : new(Available, Enabled, Active, Build),
            value =>
            {
                Writes.Add(value);
                queue.Enqueue(value);
                if (FailWrite) throw new InvalidOperationException("write failed after queue");
            },
            (_, _) => { }, () => Now);

        internal void Apply()
        {
            while (queue.TryDequeue(out bool next)) Enabled = next;
        }
    }
}
