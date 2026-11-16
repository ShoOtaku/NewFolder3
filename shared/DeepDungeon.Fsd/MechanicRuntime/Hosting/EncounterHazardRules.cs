using BossMod;
namespace MasterPlayer.MechanicRuntime;

internal static class EncounterHazardRules
{
    public static float Clearance(in EncounterHazard hazard, Vector2 position) =>
        hazard.Shape.Distance(new(position.X, position.Y)) - hazard.ClearanceMargin;
    public static EncounterTransitionResult Evaluate(ReadOnlySpan<EncounterHazard> zones,
        Vector2 from, Vector2 to, double t0, double t1, DateTime now, bool includeStartImpacts = true)
    {
        var duration = t1 - t0;
        var samples = Math.Max(1, Math.Max((int)Math.Ceiling(duration / .05),
            (int)MathF.Ceiling(Vector2.Distance(from, to) / .0625f)));
        var hits = 0;
        var exposure = 0f;
        var marginSquared = 0d; var marginShortfall = 0d;
        var startsExposed = false;
        var start = new WPos(from.X, from.Y);
        var length = Vector2.Distance(from, to);
        foreach (var zone in zones)
        {
            if (zone.Activation == DateTime.MaxValue) continue;
            var activation = (zone.Activation - now).TotalSeconds;
            if (zone.Kind == EncounterHazardKind.Impact)
            {
                // One damage check at its supplied timestamp, including an
                // impact between sample ticks. A completed impact is not a wall.
                if (activation < t0 - .000001 || activation > t1 + .000001) continue;
                if (!includeStartImpacts && activation <= t0 + .000001) continue;
                var v = Vector2.Lerp(from, to, (float)Math.Clamp((activation - t0) / duration, 0, 1));
                var d = zone.Shape.Distance(new(v.X, v.Y)) - zone.ClearanceMargin;
                if (!float.IsFinite(d)) return new(false, 0, 0, startsExposed);
                var shortfall = Math.Max(0, .65f - zone.ClearanceMargin - d);
                marginShortfall = Math.Max(marginShortfall, shortfall);
                marginSquared += shortfall * shortfall;
                if (d < 0)
                {
                    ++hits;
                    startsExposed |= activation <= t0 + .000001;
                }
                continue;
            }
            var finish = Math.Min(t1, EncounterHintEvaluator.EndSeconds(zone, now));
            if (activation > finish || finish <= t0) continue;
            var startDistance = zone.Shape.Distance(start) - zone.ClearanceMargin;
            if (!float.IsFinite(startDistance)) return new(false, 0, 0, startsExposed);
            // These two distance fields are 1-Lipschitz. If either endpoint
            // has more clearance than the entire edge length, no interior
            // sample can be inside. Complex hint shapes still use full sampling.
            if (zone.Shape is SDCircle or SDDonut)
            {
                if (startDistance > length + .0001f) continue;
                var endDistance = zone.Shape.Distance(new(to.X, to.Y)) - zone.ClearanceMargin;
                if (!float.IsFinite(endDistance)) return new(false, 0, 0, startsExposed);
                if (endDistance > length + .0001f) continue;
            }
            if (activation <= t0 && startDistance < 0) startsExposed = true;
            var violated = false;
            var begin = Math.Max(t0, activation);
            for (var i = 0; i <= samples; ++i)
            {
                var seconds = begin + (finish - begin) * i / samples;
                var v = Vector2.Lerp(from, to, (float)((seconds - t0) / duration));
                var distance = zone.Shape.Distance(new(v.X, v.Y)) - zone.ClearanceMargin;
                if (!float.IsFinite(distance)) return new(false, 0, 0, startsExposed);
                if (distance >= 0) continue;
                violated = true;
                exposure += -distance * (float)((finish - begin) / samples) * (i == 0 || i == samples ? .5f : 1f);
            }
            if (violated) ++hits;
        }
        return new(true, hits, exposure, startsExposed, marginSquared, marginShortfall);
    }
}
