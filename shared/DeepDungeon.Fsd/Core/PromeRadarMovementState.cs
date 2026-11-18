namespace DeepDungeon.Fsd.Core;

public sealed record PromeRadarMovementState
{
    public int ApiVersion { get; init; }
    public DateTime CapturedAtUtc { get; init; }
    public string? BuildMvid { get; init; }
    public bool Ready { get; init; }
    public bool SmartMovementEnabled { get; init; }
    public bool FsdSessionActive { get; init; }
    public bool Pt30ScriptAvailable { get; init; }
    public bool Pt30ScriptReady { get; init; }
    public string? Pt30ScriptVersion { get; init; }
    public string? Pt30State { get; init; }
    public bool CanMove { get; init; }
    public bool HasMovementControl { get; init; }
    public bool ReplayActive { get; init; }
    public uint TerritoryId { get; init; }
    public string? ActiveModule { get; init; }
    public bool NavigationActive { get; init; }
    public bool CaptureActive { get; init; }
    public string? MechanicError { get; init; }
    public int DangerZoneCount { get; init; }
    public int ObstacleCount { get; init; }
    public int GoalZoneCount { get; init; }
    public string? MovementPhase { get; init; }
    public string? ChaseStatus { get; init; }

    public bool IsCurrent(uint territory, DateTime now) => ApiVersion == 1 && Ready && !ReplayActive &&
        TerritoryId == territory && now - CapturedAtUtc >= TimeSpan.Zero && now - CapturedAtUtc < TimeSpan.FromSeconds(3);

    public bool OwnsPt30(uint territory, DateTime now) => IsCurrent(territory, now) && FsdSessionActive && SmartMovementEnabled && CanMove &&
        NavigationActive && Pt30ScriptReady && Pt30ScriptVersion == "1.0.0";

    public bool OwnsDodgeMovement(uint territory, DateTime now) => IsCurrent(territory, now) && SmartMovementEnabled && HasMovementControl;
}
