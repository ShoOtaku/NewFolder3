using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Core;

/// <summary>Bounded, thread-safe diagnostics and progress evidence, independent of game services.</summary>
public sealed class FsdDiagnosticJournal
{
    public const int SCHEMA_VERSION = 1;
    public const int CAPACITY = 512;
    public const int MAX_EVENT_CHARACTERS = 8192;
    private const int MAX_PAGE_CHARACTERS = 48000;
    private readonly object sync = new();
    private readonly Queue<Entry> events = new();
    private readonly Func<DateTime> clock;
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private readonly DateTime startedAtUtc;
    private FsdDiagnosticSample? current;
    private FsdDiagnosticSample? lastActiveSample;
    private long sequence;
    private DateTime stageSince, progressSince, movementSince, lastEmission;
    private float? anchorX, anchorZ;
    private string lastCode = "not-observed";
    private string lastException = "";
    private long exceptionCount;
    private long sampledOutEvents;
    private Entry? lastFailure;
    private readonly Dictionary<string, DateTime> hotEvents = new();
    private readonly Dictionary<string, (string Detail, DateTime At)> failures = new();
    private readonly HashSet<int> visitedRooms = [];
    private string lastRecorderPath = "";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private sealed record Entry(long Sequence, DateTime TimestampUtc, string EventType, JsonElement Data);

    public FsdDiagnosticJournal(Func<DateTime>? clock = null)
    {
        this.clock = clock ?? (() => DateTime.UtcNow);
        startedAtUtc = this.clock();
    }

    /// <summary>Records a bounded copy; retries, status churn and telemetry do not reset progress timers.</summary>
    public void Record(string eventType, object? data)
    {
        lock (sync)
        {
            if (eventType is "coord-tick" or "coord-encounter" or "boss-combat-snapshot")
            {
                var now = clock();
                if (hotEvents.TryGetValue(eventType, out var last) && now - last < TimeSpan.FromSeconds(1))
                {
                    sampledOutEvents++;
                    return;
                }
                hotEvents[eventType] = now;
            }
            Add(eventType, data);
            if (eventType is "runtime-error" or "navigation-plan-fault" or "rotation-control-error"
                or "rotation-state-read-error" or "coord-objective-blocked" or "nav-fail")
                lastFailure = events.Last();
            if (current?.Active == true && eventType is "waypoint-arrived" or "room-search-finished"
                or "result-chest-opened" or "result-altar-completed" or "duty-completed")
                progressSince = clock();
        }
    }

    /// <summary>Retains the last exception and its repetition count without flooding event consumers.</summary>
    public void RecordFailure(string source, Exception error)
    {
        lock (sync)
        {
            exceptionCount++;
            string detail = $"{source}: {error}";
            if (detail.Length > MAX_EVENT_CHARACTERS) detail = detail[..MAX_EVENT_CHARACTERS];
            var now = clock();
            if (!failures.TryGetValue(source, out var previous) || previous.Detail != detail
                || now - previous.At >= TimeSpan.FromSeconds(5))
            {
                Add("diagnostic-exception", new { source, error = detail, exceptionCount });
                failures[source] = (detail, now);
            }
            lastException = detail;
        }
    }

    /// <summary>Publishes one sample and requests a log on state changes, or every ten seconds while active.</summary>
    public bool Publish(FsdDiagnosticSample sample)
    {
        lock (sync)
        {
            var previous = current;
            bool boundary = previous == null || previous.Active != sample.Active || previous.DungeonId != sample.DungeonId
                || previous.Floor != sample.Floor || previous.FloorGeneration != sample.FloorGeneration
                || previous.AttemptGeneration != sample.AttemptGeneration;
            bool stageChanged = boundary || previous!.Gate != sample.Gate || previous.Phase != sample.Phase
                || previous.TaskPhase != sample.TaskPhase || previous.Objective != sample.Objective;
            if (boundary) visitedRooms.Clear();
            bool newRoom = sample.PlayerRoom >= 0 && visitedRooms.Add(sample.PlayerRoom);
            if (stageChanged) stageSince = sample.CapturedAtUtc;
            if (boundary || newRoom || previous!.CompletedRooms < sample.CompletedRooms
                || previous.HoardDiscoveries < sample.HoardDiscoveries
                || (sample.TargetId != 0 && sample.TargetId == previous.TargetId && sample.TargetHp < previous.TargetHp))
                progressSince = sample.CapturedAtUtc;
            if (boundary || Moved(sample))
            {
                anchorX = sample.X;
                anchorZ = sample.Z;
                movementSince = sample.CapturedAtUtc;
            }
            current = sample;
            if (sample.Active) lastActiveSample = sample;
            if (!string.IsNullOrEmpty(sample.RecorderPath)) lastRecorderPath = sample.RecorderPath;
            string code = Diagnose(sample, sample.CapturedAtUtc);
            bool emit = stageChanged || code != lastCode || previous!.StatusIsError != sample.StatusIsError
                || (sample.Active && sample.CapturedAtUtc - lastEmission >= TimeSpan.FromSeconds(10));
            lastCode = code;
            if (emit)
            {
                lastEmission = sample.CapturedAtUtc;
                Add("diagnostic-state", State(sample.CapturedAtUtc));
            }
            return emit;
        }
    }

    private bool Moved(FsdDiagnosticSample sample)
    {
        if (!sample.X.HasValue || !sample.Z.HasValue) return false;
        if (!anchorX.HasValue || !anchorZ.HasValue) return true;
        float dx = sample.X.Value - anchorX.Value, dz = sample.Z.Value - anchorZ.Value;
        return dx * dx + dz * dz >= 0.25f;
    }

    private string Diagnose(FsdDiagnosticSample sample, DateTime now)
    {
        if (!sample.Active) return "inactive";
        if (sample.StatusIsError) return "reported-error";
        if (!sample.PlayerAvailable) return "player-unavailable";
        if (sample.PlayerDead) return "player-dead";
        double stalled = Seconds(now, progressSince);
        if (sample.InCombat) return stalled >= 120 ? "combat-no-observed-progress" : "combat";
        if (sample.Transitioning) return stalled >= 60 ? "transition-overdue" : "transition";
        if (!sample.InDuty) return stalled >= 90 ? "entry-or-exit-overdue" : "outside-duty-flow";
        if (sample.PathfindPending && !sample.NavigationRunning && stalled >= 30) return "pathfind-pending-no-progress";
        if (sample.NavigationRequested && stalled >= 30 && Seconds(now, movementSince) >= 15) return "movement-requested-no-displacement";
        if (stalled >= 90) return "no-observed-progress";
        return sample.Casting ? "casting" : "running";
    }

    private static double Seconds(DateTime now, DateTime since) => Math.Max(0, (now - since).TotalSeconds);

    private object State(DateTime now) => new
    {
        schemaVersion = SCHEMA_VERSION, instanceId, generatedAtUtc = now, startedAtUtc,
        lastSequence = sequence, sample = current, lastActiveSample = current?.Active == true ? null : lastActiveSample,
        sampleAgeSeconds = current == null ? (double?)null : Seconds(now, current.CapturedAtUtc),
        updateStale = current != null && Seconds(now, current.CapturedAtUtc) > 5,
        stageUnchangedSeconds = current == null ? (double?)null : Seconds(now, stageSince),
        noObservedProgressSeconds = current == null ? (double?)null : Seconds(now, progressSince),
        noDisplacementSeconds = current == null ? (double?)null : Seconds(now, movementSince),
        assessment = Assessment(now),
        assessmentIsHypothesis = true,
        exceptionCount, lastException, lastFailure, lastRecorderPath, sampledOutEvents,
        retainedEvents = events.Count, overwrittenEvents = Math.Max(0, sequence - events.Count)
    };

    /// <summary>Returns cached game state with fresh liveness ages, safe from any IPC caller thread.</summary>
    private string Assessment(DateTime now)
    {
        if (current == null) return "not-observed";
        if (current.Active && Seconds(now, current.CapturedAtUtc) > 5) return "update-stale";
        return Diagnose(current, now);
    }

    /// <summary>Returns cached game state with fresh liveness ages, safe from any IPC caller thread.</summary>
    public string GetSnapshot()
    {
        lock (sync) return JsonSerializer.Serialize(new { ok = true, state = State(clock()) }, Json);
    }

    /// <summary>Returns a bounded cursor page; instanceId detects reloads and gap detects lost history.</summary>
    public string GetEvents(string request)
    {
        try
        {
            request ??= "{}";
            if (request.Length > 4096) return Error("invalid-query", "request <= 4096 characters");
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request) ? "{}" : request);
            var root = doc.RootElement;
            long after = root.TryGetProperty("afterSequence", out var a) ? a.GetInt64() : 0;
            int limit = root.TryGetProperty("limit", out var l) ? l.GetInt32() : 50;
            string? filter = root.TryGetProperty("eventType", out var f) ? f.GetString() : null;
            string? expected = root.TryGetProperty("instanceId", out var id) ? id.GetString() : null;
            if (after < 0 || limit is < 1 or > 100 || filter?.Length > 128 || request.Length > 4096)
                return Error("invalid-query", "afterSequence >= 0, limit 1..100, request <= 4096 characters");
            lock (sync)
            {
                if (expected != null && expected != instanceId) return Error("instance-changed", "Restart with afterSequence=0 and the current instanceId");
                if (after > sequence) return Error("cursor-ahead", "Cursor is ahead of this instance");
                var selected = new List<Entry>();
                int size = 0;
                long cursor = after;
                foreach (var entry in events)
                {
                    if (entry.Sequence <= after) continue;
                    if (filter != null && entry.EventType != filter) { cursor = entry.Sequence; continue; }
                    int entrySize = entry.Data.GetRawText().Length + entry.EventType.Length + 150;
                    if (selected.Count >= limit || size + entrySize > MAX_PAGE_CHARACTERS) break;
                    selected.Add(entry);
                    cursor = entry.Sequence;
                    size += entrySize;
                }
                return JsonSerializer.Serialize(new { ok = true, schemaVersion = SCHEMA_VERSION, instanceId,
                    events = selected, nextAfterSequence = cursor, lastSequence = sequence,
                    hasMore = cursor < sequence, gap = events.Count > 0 && after < events.Peek().Sequence - 1 }, Json);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return Error("invalid-query", error.Message);
        }
    }

    /// <summary>Returns the current assessment and a recent bounded event page for support reports.</summary>
    public string GetReport()
    {
        lock (sync)
        {
            using var state = JsonDocument.Parse(GetSnapshot());
            using var recent = JsonDocument.Parse(GetEvents(JsonSerializer.Serialize(new { afterSequence = Math.Max(0, sequence - 30), limit = 30 })));
            return JsonSerializer.Serialize(new { ok = true, schemaVersion = SCHEMA_VERSION,
                snapshot = state.RootElement, recent = recent.RootElement }, Json);
        }
    }

    private void Add(string eventType, object? data)
    {
        string json = JsonSerializer.Serialize(data, Json);
        JsonElement element = json.Length <= MAX_EVENT_CHARACTERS
            ? JsonSerializer.Deserialize<JsonElement>(json)
            : JsonSerializer.SerializeToElement(new { truncated = true, originalCharacters = json.Length, preview = json[..1024] });
        if (events.Count == CAPACITY) events.Dequeue();
        events.Enqueue(new Entry(++sequence, clock(), eventType, element));
    }

    private string Error(string code, string message) => JsonSerializer.Serialize(new { ok = false, schemaVersion = SCHEMA_VERSION, instanceId, error = code, message }, Json);
}
