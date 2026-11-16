using System;
using System.Numerics;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Native-free planning contracts: immutable copies of world
/// state for the encounter planner. No Actor/native pointers in records.</summary>
// Impact is one check at Activation; repeated damage requires repeated entries.
// Persistent remains forbidden until ExpiresAt, or while present if the end is unknown.
public enum EncounterHazardKind { Impact, Persistent }

/// <summary>Detached geometry provenance for diagnostics/reconstruction.
/// The planner continues to evaluate Shape; these values do not affect scoring.</summary>
public sealed record EncounterHazardGeometry(string Kind, float OriginX, float OriginZ,
    float RotationRadians = 0, float InnerRadius = 0, float OuterRadius = 0,
    float LengthFront = 0, float LengthBack = 0, float HalfWidth = 0,
    float HalfAngleRadians = 0, bool Inverted = false);

public readonly record struct EncounterHazard(ShapeDistance Shape, DateTime Activation,
    EncounterHazardKind Kind, ulong SourceId = 0, bool Predicted = false)
{
    internal static EncounterHazard FromHint(in AIHints.ForbiddenZone zone)
    {
        var timedImpact = zone.SingleImpact && zone.activation != default && zone.activation != DateTime.MaxValue;
        return new(zone.shapeDistance, zone.activation, timedImpact ? EncounterHazardKind.Impact : EncounterHazardKind.Persistent, zone.Source);
    }

    public EncounterHazardGeometry? Geometry { get; init; }
    internal float ClearanceMargin { get; init; } = EncounterHintPlanner.HintMargin;
    public DateTime? ExpiresAt { get; init; }
}

public readonly record struct EncounterMovingCircle(ulong SourceId, WPos Position,
    WPos OrbitCenter, float Radius, float AngularVelocity);

public readonly record struct EncounterChaser(ulong TargetId, WPos TargetPosition,
    WPos PreviousPosition, float Radius, float NextStepDistance, float StepDistance,
    int Remaining, DateTime NextActivation, double Interval);

public sealed record EncounterPlanningSnapshot(ushort Territory, ulong EncounterId,
    uint BossOID, DateTime CapturedAt, Vector3 BossPosition, float BossRadius,
    bool BossTargetable, bool BossDead, WPos ArenaCenter, ArenaBounds Arena,
    EncounterHazard[] Hazards, ShapeDistance[] StaticObstacles,
    EncounterMovingCircle[] MovingObstacles, EncounterChaser[] Chasers)
{
    // A local hazard frame has no encounter actor. Its bounds limit the search
    // window; the duty's navigation map supplies the physical terrain.
    public bool UsesDutyTerrain { get; private init; }

    public static EncounterPlanningSnapshot? CaptureLocalHazards(ushort territory, DateTime now,
        WPos player, AIHints hints)
    {
        if (hints.ForbiddenZones.Count == 0) return null;
        var hazards = new EncounterHazard[hints.ForbiddenZones.Count];
        for (var i = 0; i < hazards.Length; ++i)
        {
            hazards[i] = EncounterHazard.FromHint(hints.ForbiddenZones[i]);
        }
        var bounds = AIHints.DefaultBounds;
        return new(territory, 0, 0, now, default, 0, false, false,
            player.Rounded(bounds.Radius / 2), bounds, hazards, [], [], [])
        { UsesDutyTerrain = true };
    }

    /// <summary>When present, this is a synchronous AIHints frame view.
    /// Chasers retain diagnostic provenance; ChasingHazards are the adapted rules.</summary>
    public EncounterHintState? Hints { get; init; }
    // BMR provides activation and displacement geometry, not travel duration.
    public KnockbackPrediction[] Knockbacks { get; init; } = [];
    public EncounterChaser[] ChasingHazards { get; init; } = [];
    internal EncounterPathMechanic? PathMechanic { get; init; }
    // Keep the recorded PT50 shape and the public replay API readable by existing callers.
    public EncounterResidenceHazard? Residence
    {
        get => PathMechanic as EncounterResidenceHazard;
        init => PathMechanic = value;
    }
    // Directed barriers: entry is blocked, an already covered start may escape.
    public ShapeDistance[] TemporaryObstacles { get; init; } = [];
}

/// <summary>Host-registered snapshot provider — the planner reads the
/// latest immutable copy; the host owns construction cadence.</summary>
public static class EncounterPlanningSnapshotRegistry
{
    private static Func<EncounterPlanningSnapshot?>? _provider;
    private static Func<EncounterPlanningSnapshot?>? _localHazards;
    public static void SetProvider(Func<EncounterPlanningSnapshot?>? provider,
        Func<EncounterPlanningSnapshot?>? localHazards = null)
    {
        _provider = provider;
        _localHazards = localHazards;
    }

    public static EncounterPlanningSnapshot? ReadLocalHazards() => _localHazards?.Invoke();
    public static EncounterPlanningSnapshot? Read() => _provider?.Invoke();
}
