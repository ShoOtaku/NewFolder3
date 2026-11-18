using System.Numerics;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

/// <summary>Protect a cast while standing still is safe; mechanic escape can preempt the wait.</summary>
public static class CastMovementPolicy
{
    public static bool ShouldWait(bool casting, float remaining, Vector2 position, ulong playerId,
        ushort territory, DateTime now, EncounterPlanningSnapshot? snapshot, float speed = 6f)
    {
        if (!casting) return false;
        if (snapshot == null || snapshot.BossDead || snapshot.Territory != territory ||
            now - snapshot.CapturedAt > TimeSpan.FromMilliseconds(500) || snapshot.CapturedAt > now)
            return true;
        remaining = float.IsFinite(remaining) ? Math.Max(0, remaining) : 0;
        if (snapshot.Hints is { } hints &&
            (hints.ForceCancelCast || hints.RequireMovement(now) || remaining > hints.MaxCastTime))
            return false;
        if (snapshot.ChasingHazards.Any(c => c.TargetId == playerId && c.Remaining > 0) ||
            snapshot.Chasers.Any(c => c.TargetId == playerId && c.Remaining > 0))
            return false;

        // Reserve time to escape after finishing the cast, including deeply
        // covered starts. This is a movement preference, not a promise to tank an AOE.
        var escapeSeconds = 1d;
        var point = new WPos(position.X, position.Y);
        speed = float.IsFinite(speed) && speed > 0 ? speed : 6f;
        foreach (var hazard in snapshot.Hazards)
        {
            float depth = hazard.ClearanceMargin - hazard.Shape.Distance(point);
            if (float.IsFinite(depth) && depth > 0)
                escapeSeconds = Math.Max(escapeSeconds, depth / speed + .5d);
        }
        var stationary = EncounterHintEvaluator.EvaluateSegment(snapshot, position, position,
            0, remaining + escapeSeconds, now);
        return stationary.GeometryValid && stationary.Hits == 0 && stationary.Exposure == 0 && !stationary.StartsExposed;
    }
}
