using System.IO;
using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

internal sealed class FsdHoardStatistics
{
    private readonly FsdSettings settings;
    private readonly FsdHoardPickupCounter counter = new();
    private Task<long>? historyImport;
    private long pendingPickups;
    private bool savePending;
    private DateTime nextSaveAtUtc;

    internal FsdHoardStatistics(FsdSettings settings, string runLogDirectory)
    {
        this.settings = settings;
        if (settings.LifetimeHoardPickups == null)
            historyImport = BeginHistoryImport(runLogDirectory);
    }

    internal long SessionPickups => counter.SessionTotal;
    internal long? LifetimePickups => settings.LifetimeHoardPickups;

    internal void StartSession() => counter.StartSession();
    internal void BeginAttempt() => counter.BeginAttempt();

    internal void Observe(uint dungeon, byte floor, int count) =>
        pendingPickups += counter.Observe(dungeon, floor, count);

    internal void Update(DateTime now, bool flush = false)
    {
        if (historyImport is { } import)
        {
            if (!import.IsCompleted)
                return;

            historyImport = null;
            if (import.IsCompletedSuccessfully)
            {
                settings.LifetimeHoardPickups = import.Result;
                savePending = true;
            }
            else
            {
                Service.Log.Error($"[FSD] Failed to import hoard pickup history: {import.Exception}");
                settings.LifetimeHoardPickups = 0;
            }
        }

        if (pendingPickups > 0)
        {
            settings.LifetimeHoardPickups = (settings.LifetimeHoardPickups ?? 0) + pendingPickups;
            pendingPickups = 0;
            savePending = true;
        }
        if (!savePending || (!flush && now < nextSaveAtUtc))
            return;

        try
        {
            settings.Save();
            savePending = false;
        }
        catch (Exception ex)
        {
            nextSaveAtUtc = now.AddSeconds(5);
            Service.Log.Error($"[FSD] Failed to save hoard pickup totals: {ex}");
        }
    }

    private static Task<long> BeginHistoryImport(string directory)
    {
        // Open the old files before a new recorder can prune them.
        var readers = new List<StreamReader>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*-ddrun.jsonl"))
            {
                try
                {
                    readers.Add(new StreamReader(File.Open(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Service.Log.Warning($"[FSD] Could not open a hoard history recording: {ex.Message}");
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // No FSD recordings have been created yet.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Service.Log.Warning($"[FSD] Could not enumerate hoard history recordings: {ex.Message}");
        }

        if (readers.Count == 0)
            return Task.FromResult(0L);

        return Task.Run(() =>
        {
            long total = 0;
            foreach (var reader in readers)
            {
                using (reader)
                {
                    try
                    {
                        total += FsdHoardPickupCounter.CountRecordedPickups(reader);
                    }
                    catch (IOException ex)
                    {
                        Service.Log.Warning($"[FSD] Could not read a hoard history recording: {ex.Message}");
                    }
                }
            }
            return total;
        });
    }
}
