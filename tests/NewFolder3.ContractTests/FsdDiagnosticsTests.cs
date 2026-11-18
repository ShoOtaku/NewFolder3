using System.Text.Json;
using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class FsdDiagnosticsTests
{
    public static void Run()
    {
        TestProgressAndStaleHeartbeat();
        TestCombatAndBoundaries();
        TestRevisitedRoomsAndTransitionChurn();
        TestCursorAndBounds();
        TestFailuresAndConcurrency();
    }

    private static FsdDiagnosticSample Sample(DateTime now) => new()
    {
        CapturedAtUtc = now, Active = true, InDuty = true, PlayerAvailable = true,
        Gate = "floor-update", Floor = 21, FloorGeneration = 1,
        X = 0, Y = 0, Z = 0, NavigationRequested = true
    };

    private static JsonElement State(FsdDiagnosticJournal journal)
    {
        using var json = JsonDocument.Parse(journal.GetSnapshot());
        return json.RootElement.GetProperty("state").Clone();
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }

    private static void TestProgressAndStaleHeartbeat()
    {
        var now = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var journal = new FsdDiagnosticJournal(() => now);
        journal.Publish(Sample(now));
        now = now.AddSeconds(31);
        journal.Record("nav-fail", new { retry = 20 });
        journal.Publish(Sample(now) with { Status = "retry 20", Gate = "repath" });
        var state = State(journal);
        Check(state.GetProperty("assessment").GetString() == "movement-requested-no-displacement", "Retries must not hide a stall");
        Check(state.GetProperty("noObservedProgressSeconds").GetDouble() == 31, "Status churn must not count as progress");
        now = now.AddSeconds(1);
        journal.Publish(Sample(now) with { X = 1 });
        Check(State(journal).GetProperty("noDisplacementSeconds").GetDouble() == 0, "Displacement must be measured independently");
        now = now.AddSeconds(60);
        journal.Publish(Sample(now) with { X = 2 });
        Check(State(journal).GetProperty("assessment").GetString() == "no-observed-progress", "Walking loops must remain visible");
        journal.Record("waypoint-arrived", new { waypoint = 1 });
        Check(State(journal).GetProperty("noObservedProgressSeconds").GetDouble() == 0, "Confirmed waypoint arrival is progress");
        now = now.AddSeconds(6);
        Check(State(journal).GetProperty("updateStale").GetBoolean(), "IPC must identify a stopped framework heartbeat");
    }

    private static void TestCombatAndBoundaries()
    {
        var now = DateTime.UtcNow;
        var journal = new FsdDiagnosticJournal(() => now);
        var sample = Sample(now) with { InCombat = true, TargetId = 5, TargetHp = 100 };
        journal.Publish(sample);
        now = now.AddSeconds(60);
        journal.Publish(sample with { CapturedAtUtc = now, TargetHp = 90 });
        Check(State(journal).GetProperty("noObservedProgressSeconds").GetDouble() == 0, "Damage to the same enemy is progress");
        now = now.AddSeconds(60);
        journal.Publish(sample with { CapturedAtUtc = now, TargetHp = 90 });
        Check(State(journal).GetProperty("assessment").GetString() == "combat", "Normal combat must not use movement stall thresholds");
        now = now.AddSeconds(61);
        journal.Publish(sample with { CapturedAtUtc = now, TargetHp = 90 });
        Check(State(journal).GetProperty("assessment").GetString() == "combat-no-observed-progress", "Frozen combat must eventually be visible");
        journal.Publish(Sample(now) with { FloorGeneration = 2 });
        Check(State(journal).GetProperty("noObservedProgressSeconds").GetDouble() == 0, "New floors reset timing");
        now = now.AddSeconds(31);
        journal.Publish(Sample(now) with { FloorGeneration = 2, NavigationRunning = false, PathfindPending = true });
        Check(State(journal).GetProperty("assessment").GetString() == "pathfind-pending-no-progress", "Orphan pathfinding needs a distinct diagnosis");
    }

    private static void TestRevisitedRoomsAndTransitionChurn()
    {
        var now = DateTime.UtcNow;
        var journal = new FsdDiagnosticJournal(() => now);
        journal.Publish(Sample(now) with { PlayerRoom = 1 });
        now = now.AddSeconds(1);
        journal.Publish(Sample(now) with { PlayerRoom = 2 });
        now = now.AddSeconds(100);
        journal.Publish(Sample(now) with { PlayerRoom = 1, X = 5 });
        Check(State(journal).GetProperty("noObservedProgressSeconds").GetDouble() == 100,
            "Revisiting rooms must not hide a walking loop");
        journal.Publish(Sample(now) with { Transitioning = true, Gate = "wait-map" });
        Check(State(journal).GetProperty("assessment").GetString() == "transition-overdue",
            "Changing a wait gate must not reset transition progress");
        journal.Publish(Sample(now) with { AttemptGeneration = 2 });
        Check(State(journal).GetProperty("noObservedProgressSeconds").GetDouble() == 0,
            "A new duty attempt must reset its evidence timers");
    }

    private static void TestCursorAndBounds()
    {
        var journal = new FsdDiagnosticJournal();
        for (int i = 0; i < FsdDiagnosticJournal.CAPACITY + 5; i++) journal.Record("test", new { i });
        using var first = JsonDocument.Parse(journal.GetEvents("{\"limit\":2}"));
        var page = first.RootElement;
        Check(page.GetProperty("gap").GetBoolean(), "Ring overwrite must be reported");
        Check(page.GetProperty("events").GetArrayLength() == 2 && page.GetProperty("hasMore").GetBoolean(), "Pagination must be bounded");
        long next = page.GetProperty("nextAfterSequence").GetInt64();
        using var second = JsonDocument.Parse(journal.GetEvents(JsonSerializer.Serialize(new { afterSequence = next, limit = 2 })));
        Check(second.RootElement.GetProperty("events")[0].GetProperty("sequence").GetInt64() == next + 1, "Cursor pages must not repeat entries");
        using var filtered = JsonDocument.Parse(journal.GetEvents("{\"eventType\":\"absent\"}"));
        Check(!filtered.RootElement.GetProperty("hasMore").GetBoolean(), "Empty filtered pages must still advance their cursor");
        foreach (string query in new[] { "[]", "{", "{\"limit\":0}", "{\"afterSequence\":-1}", "{\"instanceId\":\"old-instance\"}", new string('x', 4097) })
        {
            using var invalid = JsonDocument.Parse(journal.GetEvents(query));
            Check(!invalid.RootElement.GetProperty("ok").GetBoolean(), "Invalid query must return a JSON error");
        }
        var large = new FsdDiagnosticJournal();
        large.Record("large", new { text = new string('"', 50000) });
        using var bounded = JsonDocument.Parse(large.GetEvents("{}"));
        Check(bounded.RootElement.GetProperty("events").GetArrayLength() == 1, "A large escaped event must not wedge the cursor");
        Check(bounded.RootElement.GetProperty("events")[0].GetProperty("data").GetProperty("truncated").GetBoolean(), "Truncation must be explicit");
    }

    private static void TestFailuresAndConcurrency()
    {
        var journal = new FsdDiagnosticJournal();
        journal.RecordFailure("update", new InvalidOperationException("test failure"));
        journal.Record("runtime-error", new { source = "navigation", error = "failed" });
        Check(State(journal).GetProperty("exceptionCount").GetInt64() == 1, "Exceptions must remain visible");
        Check(State(journal).GetProperty("lastFailure").ValueKind == JsonValueKind.Object, "Swallowed runtime errors must remain visible");
        Parallel.For(0, 100, i =>
        {
            journal.Record("concurrent", new { i });
            using var report = JsonDocument.Parse(journal.GetReport());
            Check(report.RootElement.GetProperty("ok").GetBoolean(), "Concurrent IPC reads must stay valid");
        });
        var now = DateTime.UtcNow;
        var noisy = new FsdDiagnosticJournal(() => now);
        for (int i = 0; i < 100; i++) noisy.Record("coord-tick", new { i });
        Check(State(noisy).GetProperty("sampledOutEvents").GetInt64() == 99, "Per-frame telemetry must be rate limited in IPC history");
        var failures = new FsdDiagnosticJournal(() => now);
        var error = new InvalidOperationException("repeated failure");
        for (int i = 0; i < 100; i++)
        {
            failures.RecordFailure("engine-update", error);
            failures.RecordFailure("application-update", error);
        }
        Check(State(failures).GetProperty("exceptionCount").GetInt64() == 200,
            "Repeated exceptions must retain their total count");
        Check(State(failures).GetProperty("retainedEvents").GetInt32() == 2,
            "Alternating error sources must not flood recent history");
    }
}
