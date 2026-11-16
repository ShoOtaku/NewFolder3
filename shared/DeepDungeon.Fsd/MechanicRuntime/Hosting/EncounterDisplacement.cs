using BossMod;
using static BossMod.Components.GenericKnockback;

namespace MasterPlayer.MechanicRuntime;

public readonly record struct KnockbackPrediction(Knockback Source, bool StopAtWall, bool StopAfterWall,
    float HitboxRadius, uint PathDependentImmunityStatus);

internal static class EncounterDisplacement
{
    // An upstream activation-time projection. This does not invent an animation
    // duration, and must never become walking input or a position correction.
    internal static Vector2 Project(EncounterPlanningSnapshot snapshot, KnockbackPrediction prediction,
        Vector2 from, in MechanicBranchState branch)
    {
        if (!prediction.Source.IgnoreImmunes && prediction.PathDependentImmunityStatus != 0
            && snapshot.PathMechanic?.GrantsImmunity(branch, prediction.PathDependentImmunityStatus) == true)
            return from;
        var p = ProjectLanding(prediction.Source, new(from.X, from.Y), prediction.HitboxRadius,
            snapshot.Arena, snapshot.ArenaCenter, prediction.StopAtWall, prediction.StopAfterWall);
        return new(p.X, p.Z);
    }

    private const float approxHitBoxRadius = 0.499f;
    private const float maxIntersectionError = 0.5f - approxHitBoxRadius;

    private static WPos ProjectLanding(in Knockback s, WPos from, float hitboxRadius,
        ArenaBounds arena, WPos center, bool stopAtWall, bool stopAfterWall)
    {
        if (s.Shape != null && !s.Shape.Check(from, s.Origin, s.Direction))
        {
            return from; // not affected at this candidate position
        }

        var dir = s.Kind switch
        {
            Kind.AwayFromOrigin => from != s.Origin ? (from - s.Origin).Normalized() : default,
            Kind.TowardsOrigin => from != s.Origin ? (s.Origin - from).Normalized() : default,
            Kind.DirBackward => (s.Direction + 180f.Degrees()).ToDirection(),
            Kind.DirForward => s.Direction.ToDirection(),
            Kind.DirLeft => s.Direction.ToDirection().OrthoL(),
            Kind.DirRight => s.Direction.ToDirection().OrthoR(),
            _ => default
        };
        if (dir == default)
        {
            return from;
        }

        var distance = s.Distance;
        if (s.Kind == Kind.TowardsOrigin)
        {
            distance = Math.Min(distance, (s.Origin - from).Length() - s.MinDistance);
        }

        if (s.Kind == Kind.DirBackward)
        {
            var perpendicularDir = s.Direction.ToDirection().OrthoL();
            var perpendicularDistance = Math.Abs((from - s.Origin).Cross(perpendicularDir) / perpendicularDir.Length());
            distance = Math.Min(distance, perpendicularDistance - s.MinDistance);
        }

        if (distance <= 0f)
        {
            return from;
        }

        if (stopAtWall)
        {
            distance = Math.Min(distance, arena.IntersectRay(from - center, dir) - Math.Max(maxIntersectionError, hitboxRadius - approxHitBoxRadius)); // hitbox radius can be != 0.5 if player is transformed/mounted, but normal arenas with walls should account for walkable arena in their shape already
        }

        if (stopAfterWall)
        {
            distance = Math.Min(distance, arena.IntersectRay(from - center, dir) + maxIntersectionError);
        }

        var walls = s.SafeWalls;
        var lenW = walls.Length;
        if (lenW != 0)
        {
            var distanceToWall = float.MaxValue;
            for (var j = 0; j < lenW; ++j)
            {
                var wall = walls[j];
                var t = Intersect.RaySegment(from, dir, wall.Vertex1, wall.Vertex2);
                if (t < distanceToWall && t <= s.Distance)
                {
                    distanceToWall = t;
                }
            }
            var hitboxradius = hitboxRadius < approxHitBoxRadius ? 0.5f : hitboxRadius; // some NPCs have less than 0.5 radius and cause error while clamping
            distance = distanceToWall < float.MaxValue
                ? Math.Min(distance, distanceToWall - Math.Clamp(hitboxradius - approxHitBoxRadius, maxIntersectionError, hitboxradius - approxHitBoxRadius))
                : Math.Min(distance, arena.IntersectRay(from - center, dir) + maxIntersectionError);
        }

        return from + distance * dir;
    }

    internal static MechanicBranchState CreateBranch(EncounterRouteRequest request, double start)
    {
        MechanicBranchState branch = default;
        request.Snapshot.PathMechanic?.Initialize(ref branch, request.Position, request.Now, start);
        return branch;
    }

    // Route conversion and reuse need the same contact history as the scoring
    // simulation. Advance only walking and residence here, not hazards or score.
    internal static Vector2 AdvanceWalking(EncounterRouteRequest request, Vector2 position,
        ref Vector2 velocity, ref double startupRemaining, ref MechanicBranchState branch, Vector2 command, double start, double end)
    {
        if (request.Snapshot.Knockbacks.Length == 0
            || request.Snapshot.PathMechanic is not { } rule)
            return position + request.AdvanceWalking(ref velocity, ref startupRemaining, command, Math.Max(0, end - start));
        var origin = position; var initialVelocity = velocity; var at = start;
        var initialStartup = startupRemaining;
        while (at < end - 1e-9)
        {
            var next = Math.Min(end, at + .05);
            var atVelocity = initialVelocity;
            startupRemaining = initialStartup;
            var p = origin + request.AdvanceWalking(ref atVelocity, ref startupRemaining, command, next - start);
            rule.Advance(ref branch, position, p, at, next);
            position = p; velocity = atVelocity; at = next;
        }
        return position;
    }
}
