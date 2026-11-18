using System.Text.Json;

namespace DeepDungeon.Fsd.Runtime;

public sealed class FsdHoardPickupCounter
{
    private (uint Dungeon, int Floorset) sampleKey;
    private int highestCount;

    public long SessionTotal { get; private set; }

    public void StartSession()
    {
        SessionTotal = 0;
        BeginAttempt();
    }

    public void BeginAttempt() => sampleKey = default;

    public int Observe(uint dungeon, byte floor, int hoardCount)
    {
        if (dungeon == 0 || floor == 0 || hoardCount < 0)
            return 0;

        var key = (dungeon, (floor - 1) / 10);
        if (key != sampleKey)
        {
            // Attaching to a duty can start with hoards already collected.
            sampleKey = key;
            highestCount = hoardCount;
            return 0;
        }

        if (hoardCount <= highestCount)
            return 0;

        int pickedUp = hoardCount - highestCount;
        highestCount = hoardCount;
        SessionTotal += pickedUp;
        return pickedUp;
    }

    public static long CountRecordedPickups(TextReader reader)
    {
        long total = 0;
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"checkpoint\":\"hoard-completed:", StringComparison.Ordinal))
                continue;

            try
            {
                using var record = JsonDocument.Parse(line);
                var entry = record.RootElement;
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("eventType", out var eventType) &&
                    eventType.ValueKind == JsonValueKind.String &&
                    eventType.GetString() is "intuition-native-state" or "intuition-native-state-unavailable" &&
                    entry.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("checkpoint", out var checkpoint) && checkpoint.ValueKind == JsonValueKind.String &&
                    checkpoint.GetString()?.StartsWith("hoard-completed:", StringComparison.Ordinal) == true)
                    total++;
            }
            catch (JsonException)
            {
                // A recording interrupted during a write may end with an incomplete entry.
            }
        }
        return total;
    }
}
