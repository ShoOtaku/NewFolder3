using System.Text.Json;

namespace DeepDungeon.Fsd.Core;

/// <summary>An immutable game-thread observation; IPC readers never access live game objects.</summary>
public sealed record FsdDiagnosticSample
{
    public DateTime CapturedAtUtc { get; init; }
    public bool Active { get; init; }
    public string Scenario { get; init; } = "";
    public string Gate { get; init; } = "inactive";
    public string Phase { get; init; } = "";
    public string TaskPhase { get; init; } = "";
    public string Objective { get; init; } = "";
    public string Status { get; init; } = "";
    public bool StatusIsError { get; init; }
    public uint DungeonId { get; init; }
    public int Floor { get; init; }
    public long FloorGeneration { get; init; }
    public long AttemptGeneration { get; init; }
    public bool InDuty { get; init; }
    public bool Transitioning { get; init; }
    public bool InCombat { get; init; }
    public bool PlayerAvailable { get; init; }
    public bool PlayerDead { get; init; }
    public bool Casting { get; init; }
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? Z { get; init; }
    public ulong TargetId { get; init; }
    public uint? TargetHp { get; init; }
    public int PlayerRoom { get; init; } = -1;
    public int CompletedRooms { get; init; }
    public int HoardDiscoveries { get; init; }
    public bool NavigationRequested { get; init; }
    public bool NavigationRunning { get; init; }
    public bool PathfindPending { get; init; }
    public string RecorderPath { get; init; } = "";
    public JsonElement? Details { get; init; }
}
