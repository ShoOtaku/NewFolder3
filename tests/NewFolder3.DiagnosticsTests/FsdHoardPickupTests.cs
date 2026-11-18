using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class FsdHoardPickupTests
{
    internal static void Run()
    {
        var counter = new FsdHoardPickupCounter();
        counter.StartSession();
        Check(counter.Observe(4, 23, 2) == 0, "Existing hoards on attachment");
        Check(counter.Observe(4, 23, 3) == 1, "Single pickup");
        Check(counter.Observe(4, 0, 0) == 0, "Loading frame");
        Check(counter.Observe(4, 24, 0) == 0, "Lower transient count");
        Check(counter.Observe(4, 24, 3) == 0, "Repeated count on the next floor");
        Check(counter.Observe(4, 25, 5) == 2, "Multiple pickups between reads");
        Check(counter.Observe(4, 31, 0) == 0, "New floor set baseline");
        Check(counter.Observe(4, 31, 1) == 1, "Pickup in the new floor set");
        counter.BeginAttempt();
        Check(counter.Observe(4, 21, 0) == 0, "Retry baseline");
        Check(counter.Observe(4, 21, 1) == 1 && counter.SessionTotal == 5, "Retry retains session total");
        counter.StartSession();
        Check(counter.SessionTotal == 0 && counter.Observe(4, 21, 1) == 0, "New session clears previous totals");

        using var recording = new StringReader("""
            {"eventType":"floor-changed","data":{"hoardCount":2}}
            {"eventType":"intuition-native-state","data":{"checkpoint":"hoard-completed:coord-ingest"}}
            {"eventType":"intuition-native-state","data":{"checkpoint":"hoard-revealed"}}
            {"eventType":"not-a-pickup","data":{"checkpoint":"hoard-completed:coord-ingest"}}
            {"eventType":"intuition-native-state-unavailable","data":{"checkpoint":"hoard-completed:general-search-tick"}}
            {"eventType":"intuition-native-state","data":{"checkpoint":"hoard-completed:
            """);
        Check(FsdHoardPickupCounter.CountRecordedPickups(recording) == 2, "Import confirmed pickup events only");
        Console.WriteLine("FSD hoard pickup checks passed.");
    }

    private static void Check(bool passed, string description)
    {
        if (!passed)
            throw new InvalidOperationException(description);
    }
}
