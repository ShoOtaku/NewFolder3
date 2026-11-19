using DeepDungeon.Fsd.Core;

var progression = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 2, true,
    false, true, true, false);
var hoard = new FarmingPlan(FarmingMode.Hoard, SaveUse.Create, 21, 2, true,
    true, true, false, false);
int checks = 0;

foreach (var plan in new[] { progression, progression with { DungeonId = 1 },
             progression with { DungeonId = 2 }, hoard })
{
    foreach (int entryFloor in new[] { plan.StartFloor, plan.TerminalEntryFloor })
    {
        var result = new FarmingAttemptResult(entryFloor, true, false, false, false);
        Check(!FarmingSessionPolicy.ReachedStopBoundary(plan, result), "Default settings must continue normal farming.");
        Check(FarmingSessionPolicy.ReachedStopBoundary(plan, result, true), "A cleared floorset must pause even with infinite cycles.");
        Check(!FarmingSessionPolicy.ReachedStopBoundary(plan, result with { DutyCompleted = false }, true), "An unknown outcome must not trigger a pause.");
        Check(!FarmingSessionPolicy.ReachedStopBoundary(plan, result with { DutyFailed = true }, true), "A failed attempt must retain existing recovery behavior.");
        Check(!FarmingSessionPolicy.ReachedStopBoundary(plan, result with { Aborted = true }, true), "An aborted attempt must not be treated as a cleared floorset.");
        Check(!FarmingSessionPolicy.ReachedStopBoundary(plan, result with { EntryFloor = 0 }, true), "An unread entry floor must not pause.");
        Check(!FarmingSessionPolicy.ReachedStopBoundary(plan, result with { EntryFloor = 22 }, true), "A non-boundary entry floor must not pause.");
        Check(!FarmingSessionPolicy.ReachedStopBoundary(plan, result with { EntryFloor = plan.MaxFloor + 1 }, true), "An out-of-range entry floor must not pause.");
    }
}

var clear = new FarmingAttemptResult(21, true, false, true, false);
foreach (var mode in new[] { FarmingMode.Aetherpool, FarmingMode.HoardDiscovery })
    Check(!FarmingSessionPolicy.ReachedStopBoundary(hoard with { Mode = mode, SaveUse = SaveUse.Prepared }, clear, true),
        "Prepared-save harvesting must retain its existing safety boundary.");

var diagnostic = progression with { StopAfterFloor = 30 };
Check(!FarmingSessionPolicy.ReachedStopBoundary(diagnostic, clear with { EntryFloor = 11 }), "Diagnostic checkpoints must not stop early.");
Check(FarmingSessionPolicy.ReachedStopBoundary(diagnostic, clear), "Existing diagnostic stop boundaries must still work.");
Check(FarmingSessionPolicy.ReachedStopBoundary(diagnostic, clear with { EntryFloor = 31 }), "Existing checkpoints must still stop after their boundary.");
Check(FarmingSessionPolicy.ReachedStopBoundary(diagnostic, clear with { EntryFloor = 11 }, true), "The live pause request must take effect before a later diagnostic checkpoint.");
Check(!FarmingSessionPolicy.ReachedStopBoundary(progression, clear, false), "Unchecking the live request must allow continuation.");

Console.WriteLine($"Floorset pause checks passed: {checks}.");

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
