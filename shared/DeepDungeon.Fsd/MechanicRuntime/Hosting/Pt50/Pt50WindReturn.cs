using DeepDungeon.MechanicPlanning;

namespace MasterPlayer.MechanicRuntime;

// PT50 proposes an excursion from observed safe ground. The ordinary forecast
// evaluates every segment, the knockback event, and the reset after the return.
public sealed partial record EncounterResidenceHazard
{
    private const int Directions = 8;
    private const float SampleSpacing = .25f;

    internal override void ProposeRoutes(EncounterForwardProblem problem, ref MechanicRouteSelection selection)
    {
        var q = problem.Request;
        if (RequiresImmunityAt is not { } wind || wind <= q.Now || wind == DateTime.MaxValue) return;
        if (UnsafeRegion.Distance(new(q.Position.X, q.Position.Y)) < EncounterHintPlanner.HintMargin) return;
        selection.ProposalKind = "wind-round-trip";
        var windTime = problem.StartTime + (wind - q.Now).TotalSeconds;
        for (var spoke = 0; spoke < Directions; ++spoke)
        {
            var angle = spoke * MathF.Tau / Directions;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            float firstUnsafe = 0;
            var maxDistance = problem.Speed * Math.Min(ReentrySeconds, windTime - problem.StartTime);
            for (float distance = SampleSpacing; distance <= maxDistance; distance += SampleSpacing)
            {
                var p = q.Position + direction * distance;
                var clearance = UnsafeRegion.Distance(new(p.X, p.Y));
                if (firstUnsafe == 0 && clearance < EncounterHintPlanner.HintMargin) firstUnsafe = distance;
                if (clearance > -EncounterHintPlanner.HintMargin - NavigationFollower.Arrival) continue;

                var travel = q.Motion.TravelSeconds(distance, 0, q.Speed);
                // Split the remaining time between gaining the buff early, keeping
                // it past impact, and reaching the reset before the sand expires.
                var transition = (distance - firstUnsafe) / q.Speed;
                var spare = (ReentrySeconds - ImmunityGrantSeconds
                    - ResetDwellSeconds - 2 * transition) / 3;
                if (spare <= 0) break;
                var depart = windTime - travel - ImmunityGrantSeconds - spare;
                if (depart <= problem.StartTime) break;
                var back = windTime + spare;
                if (back + travel + ResetDwellSeconds >= problem.End) break;
                var velocity = q.Speed * direction;
                var candidate = new ControlPlan([Vector2.Zero, velocity, Vector2.Zero, -velocity, Vector2.Zero],
                    [depart, depart + travel, back, back + travel, problem.End]);
                selection.Evaluate(problem, candidate);
                break;
            }
        }
    }
}
