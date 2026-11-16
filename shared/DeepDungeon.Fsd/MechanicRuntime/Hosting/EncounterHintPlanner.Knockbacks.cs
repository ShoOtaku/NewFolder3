using System.Numerics;
using System.Threading;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

public sealed partial class EncounterHintPlanner
{
    // Each launch separates two phases. A walking edge cannot cross the launch
    // time, even if both endpoints remain inside one ordinary safe interval.
    private void AddInterval(EncounterRouteRequest q, int node, double start, double end, int phase)
    {
        var events = q.Snapshot.Knockbacks;
        for (; phase < events.Length; ++phase)
        {
            var at = (events[phase].Source.Activation - q.Now).TotalSeconds;
            if (at >= end) break;
            if (at >= start)
            {
                _states.Add(new(node, new(start, at)) { Knockback = phase });
                start = at;
            }
        }
        _states.Add(new(node, new(start, end)) { Knockback = phase });
    }

    private bool TerminalClear(EncounterRouteRequest q, SafeState state, double arrival)
        => state.Landing is { } landing
            ? TerminalAssessment(q, landing, arrival).Hits == 0
            : TerminalClear(q, state.Node, arrival);

    private void ExpandKnockback(EncounterRouteRequest q, int parent, SafeState source)
    {
        var events = q.Snapshot.Knockbacks;
        var eventIndex = source.Knockback;
        var at = (events[eventIndex].Source.Activation - q.Now).TotalSeconds;
        if (at < source.Arrival || at > source.Window.End) return;
        var from = source.Landing ?? _positions[source.Node];
        var waiting = at > source.Arrival
            ? EncounterHazardRules.Evaluate(q.Snapshot.Hazards, from, from, source.Arrival, at, q.Now, source.Parent < 0)
            : new EncounterTransitionResult(true, 0, 0, false);
        if (waiting.Hits != 0 || EncounterHintEvaluator.Violations(q.Snapshot, from, q.Now, at, out _) != 0) return;
        var landing = EncounterDisplacement.Project(q.Snapshot, events[eventIndex], from, default);
        if (!TerrainLegal(q, from, landing)) return;
        var next = eventIndex + 1;
        var end = next < events.Length ? (events[next].Source.Activation - q.Now).TotalSeconds : _planningHorizon;
        foreach (var hazard in q.Snapshot.Hazards)
        {
            if (hazard.Activation == DateTime.MaxValue || EncounterHazardRules.Clearance(hazard, landing) >= 0) continue;
            var begin = (hazard.Activation - q.Now).TotalSeconds;
            if (hazard.Kind == EncounterHazardKind.Persistent && begin <= at && EncounterHintEvaluator.EndSeconds(hazard, q.Now) > at) return;
            if (begin > at) end = Math.Min(end, begin - TimeEpsilon);
        }
        var state = new SafeState(source.Node, new(at, end))
        {
            Arrival = at, Departure = at, Parent = parent, Knockback = next, Landing = landing,
            Travel = source.Travel, Soft = source.Soft + EncounterScoring.TransitionCost(waiting),
            Margin = Math.Max(source.Margin, waiting.ImpactMarginShortfall),
        };
        _states.Add(state);
        _intervalQueue.Enqueue(_states.Count - 1, (at, at));
    }

    private readonly List<PriorityQueue<int, (float Goal, float Distance)>> _launchSites = [];

    private bool CanHoldLanding(EncounterRouteRequest q, Vector2 landing, double at)
    {
        var point = new WPos(landing.X, landing.Y);
        foreach (var hazard in q.Snapshot.Hazards)
        {
            var begin = (hazard.Activation - q.Now).TotalSeconds;
            if (begin > _planningHorizon || hazard.Activation == DateTime.MaxValue) continue;
            if (hazard.Kind == EncounterHazardKind.Impact ? begin <= at : EncounterHintEvaluator.EndSeconds(hazard, q.Now) <= at) continue;
            if (hazard.Shape.Distance(point) < HintMargin) return false;
        }
        return true;
    }

    private EncounterRoute? DirectKnockback(EncounterRouteRequest q, int first, CancellationToken token)
    {
        _routePoints.Clear();
        _routePoints.Add(new(q.Position, 0));
        if (!ConnectLaunch(q, first, 0, q.Position, 0, EncounterRouteRequest.IsStopped(q.Velocity), token)) return null;
        return Route(q, EncounterRouteKind.Ready, _routePoints.ToArray(), 0, 0, IntervalExpanded, "hint-knockback");
    }

    // Direct SIPP connections through consecutive launches. Keep the exact landing
    // as the next origin. Try standing there before checking other launch cells.
    private bool ConnectLaunch(EncounterRouteRequest q, int phase, int depth, Vector2 from, double start,
        bool stopped, CancellationToken token)
    {
        var events = q.Snapshot.Knockbacks;
        if (phase == events.Length)
        {
            if (TerminalAssessment(q, from, start).Hits != 0
                || q.Region != null && EncounterScoring.GoalDistance(q, from) > 0) return false;
            _routePoints.Add(new(from, _planningHorizon) { CommandVelocity = Vector2.Zero });
            return true;
        }

        var prediction = events[phase];
        var at = (prediction.Source.Activation - q.Now).TotalSeconds;
        var startup = stopped ? EncounterRouteRequest.StartupSeconds : 0;
        var count = _routePoints.Count;
        bool TrySite(Vector2 launch, float distance)
        {
            var delay = distance > 0 ? startup : 0;
            var arrival = start + delay + distance / q.Speed;
            var landing = EncounterDisplacement.Project(q.Snapshot, prediction, launch, default);
            if (arrival > at || EncounterHintEvaluator.Violations(q.Snapshot, launch, q.Now, at, out _) != 0
                || !TerrainLegal(q, launch, landing)) return false;
            var hazards = q.Snapshot.Hazards;
            if (distance > 0)
            {
                if (!TerrainLegal(q, from, launch)
                    || delay > 0 && EncounterHazardRules.Evaluate(hazards, from, from, start, start + delay, q.Now, start == 0).Hits != 0
                    || EncounterHazardRules.Evaluate(hazards, from, launch, start + delay, arrival, q.Now, start == 0 && delay == 0).Hits != 0)
                    return false;
            }
            if (at > arrival && EncounterHazardRules.Evaluate(hazards, launch, launch, arrival, at, q.Now, arrival == 0).Hits != 0)
                return false;
            if (distance > 0) _routePoints.Add(new(launch, arrival) { CommandVelocity = (launch - from) / distance * q.Speed });
            if (at > arrival) _routePoints.Add(new(launch, at) { CommandVelocity = Vector2.Zero });
            if (landing != launch) _routePoints.Add(new(landing, at) { GameDriven = true, CommandVelocity = Vector2.Zero });
            if (ConnectLaunch(q, phase + 1, depth + 1, landing, at, landing != launch || at > arrival || distance == 0 && stopped, token))
                return true;
            _routePoints.RemoveRange(count, _routePoints.Count - count);
            return false;
        }

        if (TrySite(from, 0)) return true;
        while (_launchSites.Count <= depth) _launchSites.Add(new());
        var candidates = _launchSites[depth];
        candidates.Clear();
        var reach = (at - start - startup) * q.Speed;
        if (reach <= 0) return false;
        for (var i = 0; i < _count; ++i)
        {
            if ((i & 255) == 0)
            {
                token.ThrowIfCancellationRequested();
                if (CandidateBudgetExpired()) return false;
            }
            if (!_walk[i]) continue;
            var distance = Vector2.Distance(from, _positions[i]);
            if (distance == 0 || distance > reach) continue;
            var arrival = start + startup + distance / q.Speed;
            var range = _nodeIntervals[i];
            for (var j = range.Start; j < range.Start + range.Count; ++j)
            {
                var state = _states[j];
                if (state.Knockback != phase || state.Window.Start > arrival || state.Window.End < at) continue;
                // A later knockback may have no launch constraint of its own.
                // Reject out-of-bounds landings before limiting the shortlist.
                var landing = EncounterDisplacement.Project(q.Snapshot, prediction, _positions[i], default);
                if (!q.Snapshot.Arena.Contains(new WPos(landing.X, landing.Y) - q.Snapshot.ArenaCenter)) break;
                if (phase + 1 == events.Length && !CanHoldLanding(q, landing, at)) break;
                candidates.Enqueue(i, (_goalCosts[i], distance));
                break;
            }
        }
        for (var attempt = 0; attempt < DirectCandidates && candidates.TryDequeue(out var node, out var priority); ++attempt)
        {
            if (CandidateBudgetExpired()) break;
            if (TrySite(_positions[node], priority.Distance)) return true;
        }
        return false;
    }
}
