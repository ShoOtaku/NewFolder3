using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using BossMod;

namespace MasterPlayer.MechanicRuntime;

public sealed partial class EncounterHintPlanner
{
    // Numerical separation only, not an assumed duration of a damage effect.
    private const double TimeEpsilon = 0.000001;
    private const int DirectCandidates = 32;
    private const int MaxIntervalExpansions = 30000;
    private const int MaxRecoveryExpansions = 6000;
    private double _planningHorizon;
    private Vector2 _intervalTarget;
    private bool _intervalUseHeuristic;
    private readonly record struct Window(double Start, double End);
    private readonly record struct WindowRange(int Start, int Count);
    private struct SafeState(int node, Window window)
    {
        public int Node = node;
        public Window Window = window;
        public double Arrival = double.PositiveInfinity;
        public double Departure;
        public int Parent = -1;
        public int Knockback;
        public Vector2? Landing;
        public double Travel, Soft, Margin;
    }

    // Storage survives replans; contacts must not survive a new hint frame.
    private WindowRange[] _nodeIntervals = [];
    private Window?[] _windows = [];
    private float[] _goalCosts = [];
    private readonly List<SafeState> _states = new();
    private readonly List<Window> _blocks = new();
    private readonly List<Window> _edgeWindows = new();
    private readonly Dictionary<(int From, int To), WindowRange> _edgeRanges = new();
    private readonly Dictionary<(int From, int To), bool> _edgeGeometry = new();
    private readonly PriorityQueue<int, (double Score, double Arrival)> _intervalQueue = new();
    private readonly PriorityQueue<int, (float Goal, float Distance)> _directCandidates = new();
    private readonly List<int> _chain = new();
    private readonly List<EncounterRoutePoint> _routePoints = new();
    public int IntervalExpanded { get; private set; }
    public bool IntervalBudgetReached { get; private set; }

    private void ResetIntervals()
    {
        _states.Clear(); _blocks.Clear(); _edgeWindows.Clear(); _edgeRanges.Clear();
        _edgeGeometry.Clear();
        _intervalQueue.Clear(); _directCandidates.Clear(); _chain.Clear(); _routePoints.Clear();
        IntervalExpanded = 0; IntervalBudgetReached = false;
    }

    private EncounterRoute? PlanIntervals(EncounterRouteRequest q, CancellationToken token, bool recovery = false)
    {
        ResetIntervals();
        if (CandidateBudgetExpired()) return null;
        var knockbacks = q.Snapshot.Knockbacks;
        int firstKnockback = 0;
        while (firstKnockback < knockbacks.Length && knockbacks[firstKnockback].Source.Activation < q.Now) ++firstKnockback;
        bool displaced = firstKnockback < knockbacks.Length;
        // A currently covered start has no initial safe interval. Recovery
        // supplies a relaxed request and scores its result against the original.
        for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
            if (_distances[h][_count] < 0 && EncounterHintEvaluator.Active(q.Snapshot.Hazards[h], q.Now, 0))
                return null;
        if (_nodeIntervals.Length != _count + 1) _nodeIntervals = new WindowRange[_count + 1];
        if (_goalCosts.Length != _count + 1) _goalCosts = new float[_count + 1];
        if (_windows.Length < q.Snapshot.Hazards.Length) Array.Resize(ref _windows, q.Snapshot.Hazards.Length);
        for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
        {
            var zone = q.Snapshot.Hazards[h];
            var t = (zone.Activation - q.Now).TotalSeconds;
            _windows[h] = zone.Activation == DateTime.MaxValue ? null
                : zone.Kind == EncounterHazardKind.Persistent
                    ? new Window(Math.Max(0, t - TimeEpsilon), Math.Min(_planningHorizon, EncounterHintEvaluator.EndSeconds(zone, q.Now) + TimeEpsilon))
                : t < -TimeEpsilon || t > _planningHorizon + TimeEpsilon ? null
                : new Window(Math.Max(0, t - TimeEpsilon), t + TimeEpsilon);
            if (_windows[h] is { } activeWindow && (activeWindow.Start > _planningHorizon || activeWindow.End <= 0 || activeWindow.End < activeWindow.Start)) _windows[h] = null;
        }
        var minimumGoal = float.PositiveInfinity;
        var targetDistance = float.PositiveInfinity;
        _intervalUseHeuristic = !recovery || _allowUnsafeRecovery;
        _intervalTarget = q.Position;
        for (var i = 0; i <= _count; ++i)
        {
            if ((i & 255) == 0)
            {
                token.ThrowIfCancellationRequested();
                if (CandidateBudgetExpired()) return null;
            }
            var start = _states.Count;
            if (_walk[i])
            {
                _blocks.Clear();
                for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
                    if (_distances[h][i] < 0 && _windows[h] is { } window) _blocks.Add(window);
                MergeBlocks();
                double t = 0;
                foreach (var block in _blocks)
                {
                    if (block.Start > t) AddInterval(q, i, t, block.Start, firstKnockback);
                    t = Math.Max(t, block.End);
                }
                if (t < _planningHorizon) AddInterval(q, i, t, _planningHorizon, firstKnockback);
            }
            _nodeIntervals[i] = new(start, _states.Count - start);
            _goalCosts[i] = !_walk[i] ? float.PositiveInfinity : recovery
                ? Vector2.Distance(q.Position, _positions[i]) / q.Speed + (_allowUnsafeRecovery ? 1000 * _recoveryPenalty[i] : 0)
                : q.Snapshot.Hints is { StackGoals.Length: > 0 }
                    ? EncounterScoring.StackDistance(q, _positions[i])
                    : EncounterScoring.GoalCost(q, _positions[i], lowerBound: true);
            if (_states.Count > start && _states[^1].Window.End == _planningHorizon && (displaced || TerminalClear(q, i, 0)))
            {
                var distance = Vector2.DistanceSquared(q.Position, _positions[i]);
                if (_goalCosts[i] < minimumGoal || _goalCosts[i] == minimumGoal && distance < targetDistance)
                {
                    minimumGoal = _goalCosts[i];
                    targetDistance = distance;
                    _intervalTarget = _positions[i];
                }
            }
        }

        if (displaced)
        {
            // Aim for a reachable launch site. A direct walk and a projected hold
            // is the common case; the interval graph handles later movement.
            if (DirectKnockback(q, firstKnockback, token) is { } direct) return direct;
            _intervalUseHeuristic = false;
        }
        // Rank cheap endpoint checks first. Bound expensive whole-segment
        // attempts; failure here falls through to the connected-grid search.
        for (var i = 0; !displaced && i <= _count; ++i)
        {
            if ((i & 255) == 0 && CandidateBudgetExpired()) return null;
            var range = _nodeIntervals[i];
            if (range.Count != 1 || _states[range.Start].Window != new Window(0, _planningHorizon) || !TerminalClear(q, i, 0)) continue;
            var distance = Vector2.Distance(q.Position, _positions[i]);
            var arrival = distance / q.Speed + (distance > 0 && EncounterRouteRequest.IsStopped(q.Velocity) ? EncounterRouteRequest.StartupSeconds : 0);
            if (arrival > _planningHorizon) continue;
            if (recovery && !RecoveryTerminal(i, arrival, q.Position)) continue;
            _directCandidates.Enqueue(i, (_goalCosts[i], distance));
        }
        EncounterRoute? safeApproach = null;
        for (var attempt = 0; attempt < DirectCandidates && _directCandidates.TryDequeue(out var node, out _); ++attempt)
        {
            token.ThrowIfCancellationRequested();
            if (CandidateBudgetExpired()) return safeApproach;
            var distance = Vector2.Distance(q.Position, _positions[node]);
            var startup = distance > 0 && EncounterRouteRequest.IsStopped(q.Velocity) ? EncounterRouteRequest.StartupSeconds : 0;
            var duration = startup + distance / q.Speed;
            if (duration > _planningHorizon) continue;
            EncounterTransitionResult check = new(true, 0, 0, false), starting = new(true, 0, 0, false);
            if (duration > 0)
            {
                if (!TerrainLegal(q, q.Position, _positions[node])) continue;
                if (startup > 0)
                {
                    starting = EncounterHazardRules.Evaluate(q.Snapshot.Hazards, q.Position, q.Position, 0, startup, q.Now);
                    if (starting.Hits != 0) continue;
                }
                check = EncounterHintEvaluator.EvaluateSegment(q.Snapshot, q.Position, _positions[node], startup, duration, q.Now, geometryKnownLegal: true, includeStartImpacts: startup == 0);
                if (!check.GeometryValid || check.Hits != 0) continue;
            }
            var tail = TerminalAssessment(q, _positions[node], duration);
            var assessment = EncounterScoring.StaticScore(q, _positions[node], Vector2.Distance(q.Position, _positions[node]),
                EncounterScoring.TransitionCost(starting) + EncounterScoring.TransitionCost(check) + EncounterScoring.TransitionCost(tail),
                Math.Max(starting.ImpactMarginShortfall, Math.Max(check.ImpactMarginShortfall, tail.ImpactMarginShortfall)),
                check.Hits + tail.Hits, check.Exposure + tail.Exposure);
            var target = _positions[node];
            var command = distance > 0 ? (target - q.Position) / distance * q.Speed : Vector2.Zero;
            var arrivalPoint = new EncounterRoutePoint(target, duration) { CommandVelocity = command };
            EncounterRoutePoint[] points = duration >= _planningHorizon ? [new(q.Position, 0), arrivalPoint]
                : duration > 0 ? [new(q.Position, 0), arrivalPoint, new(target, _planningHorizon)]
                : [new(q.Position, 0), new(q.Position, _planningHorizon)];
            var route = Route(q, EncounterRouteKind.Ready, points, 0, 0, 0, "hint-common-safe") with { Assessment = assessment };
            if (q.Region == null || EncounterScoring.GoalDistance(q, _positions[node]) == 0) return route;
            safeApproach ??= route;
        }

        var initial = _nodeIntervals[_count];
        if (initial.Count == 0 || _states[initial.Start].Window.Start != 0) return safeApproach;
        var root = _states[initial.Start];
        root.Arrival = 0;
        _states[initial.Start] = root;
        _intervalQueue.Enqueue(initial.Start, (IntervalHeuristic(q.Position, q.Speed), 0));
        var best = -1;
        var partial = -1;
        var bestGoal = float.PositiveInfinity;
        while (_intervalQueue.TryDequeue(out var id, out var priority))
        {
            var state = _states[id];
            if (priority.Arrival != state.Arrival) continue;
            if (state.Parent >= 0 && !state.Landing.HasValue && (partial < 0 || _goalCosts[state.Node] < _goalCosts[_states[partial].Node]
                || _goalCosts[state.Node] == _goalCosts[_states[partial].Node] && state.Arrival > _states[partial].Arrival))
                partial = id;
            if ((++IntervalExpanded & 127) == 0) token.ThrowIfCancellationRequested();
            // All future constraints at this point are over; remaining here
            // is safe through the horizon, including waits after arrival.
            if (state.Knockback == knockbacks.Length && state.Window.End == _planningHorizon
                && TerminalClear(q, state, state.Arrival)
                && (!recovery || RecoveryTerminal(state.Node, state.Arrival, q.Position)))
            {
                var position = state.Landing ?? _positions[state.Node];
                var cost = recovery ? _goalCosts[state.Node] : q.Snapshot.Hints is { StackGoals.Length: > 0 }
                    ? EncounterScoring.StackDistance(q, position) : EncounterScoring.GoalCost(q, position);
                if (best < 0 || cost < bestGoal)
                {
                    best = id;
                    bestGoal = cost;
                }
                // Continue through safe cells when a wall blocks the direct
                // attack approach. Stopping at the first safe node would hold
                // forever even though a route around that wall exists.
                if (recovery && !_allowUnsafeRecovery || bestGoal <= minimumGoal) return BuildIntervalRoute(q, best);
            }
            if (!ConsumeExpansion() || IntervalExpanded >= (recovery ? MaxRecoveryExpansions : MaxIntervalExpansions))
            {
                IntervalBudgetReached = true;
                return best >= 0 ? BuildIntervalRoute(q, best) : safeApproach ?? (partial >= 0 ? BuildIntervalRoute(q, partial, complete: false) : null);
            }
            if (state.Knockback < knockbacks.Length) ExpandKnockback(q, id, state);
            if (state.Node == _count || state.Landing.HasValue)
            {
                var position = state.Landing ?? _positions[state.Node];
                var (x, z) = q.Grid!.WorldToGrid(new(position.X, 0, position.Y));
                if (!q.Grid.IsFree(x, z)) (x, z) = GridAStar.NearestFree(q.Grid, x, z);
                for (var dz = -2; dz <= 2; ++dz)
                    for (var dx = -2; dx <= 2; ++dx)
                        if ((uint)(x + dx) < _width && (uint)(z + dz) < _height)
                            RelaxInterval(q, id, (z + dz) * _width + x + dx);
            }
            else
                for (var d = 0; d < 8; ++d)
                    if ((_edges[state.Node] & (1 << d)) != 0)
                        RelaxInterval(q, id, state.Node + Directions[d].X + Directions[d].Z * _width);
        }
        return best >= 0 ? BuildIntervalRoute(q, best) : safeApproach ?? (partial >= 0 ? BuildIntervalRoute(q, partial, complete: false) : null);
    }

    private bool TerminalClear(EncounterRouteRequest q, int node, double arrival)
    {
        for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
            if (_windows[h] is { } window && window.End >= arrival && _distances[h][node] < 0) return false;
        return true;
    }

    private EncounterTransitionResult TerminalAssessment(EncounterRouteRequest q, Vector2 position, double arrival) =>
        arrival >= _planningHorizon ? new(true, 0, 0, false)
            : EncounterHazardRules.Evaluate(q.Snapshot.Hazards, position, position, arrival, _planningHorizon, q.Now,
                includeStartImpacts: arrival == 0);

    private void RelaxInterval(EncounterRouteRequest q, int parent, int node)
    {
        var range = _nodeIntervals[node];
        if (range.Count == 0) return;
        var source = _states[parent];
        var from = source.Landing ?? _positions[source.Node];
        var to = _positions[node];
        var duration = Vector2.Distance(from, to) / q.Speed;
        if (duration < TimeEpsilon) return;
        bool geometryLegal;
        if (source.Landing.HasValue) geometryLegal = TerrainLegal(q, from, to);
        else if (!_edgeGeometry.TryGetValue((source.Node, node), out geometryLegal))
        {
            geometryLegal = TerrainLegal(q, from, to);
            // Temporary barriers permit escape but forbid re-entry, so this
            // cache is directed and is cleared for every planning request.
            _edgeGeometry.Add((source.Node, node), geometryLegal);
        }
        if (!geometryLegal) return;
        var edges = EdgeWindows(q, source.Node, node, duration, source.Landing);
        var stopped = source.Landing.HasValue || source.Parent < 0 && EncounterRouteRequest.IsStopped(q.Velocity);
        var restart = EncounterRouteRequest.StartupSeconds;
        for (var i = range.Start; i < range.Start + range.Count; ++i)
        {
            var dest = _states[i];
            if (dest.Knockback != source.Knockback) continue;
            var earliest = Math.Max(source.Arrival, dest.Window.Start + TimeEpsilon - duration);
            var depart = NextOpenDeparture(edges, earliest);
            // depart is the time displacement starts. Issue walking input one startup earlier.
            var startup = stopped || depart > source.Arrival + TimeEpsilon ? restart : 0;
            // A moving arrival that must stop needs a real wait command before restarting.
            var minimumWait = !stopped && startup > 0 ? 2 * TimeEpsilon : 0;
            if (startup > 0) depart = NextOpenDeparture(edges, Math.Max(depart, source.Arrival + minimumWait + startup));
            var arrive = depart + duration;
            if (depart >= source.Window.End || arrive >= dest.Window.End || arrive >= dest.Arrival) continue;
            // Contact sampling is only used to propose departures. Check
            // the actual impact position exactly before accepting the edge.
            var check = EncounterHintEvaluator.EvaluateSegment(q.Snapshot, from, to, depart, arrive, q.Now, geometryKnownLegal: true,
                includeStartImpacts: source.Parent < 0 && depart == 0);
            if (!check.GeometryValid || check.Hits != 0) continue;
            var waiting = depart > source.Arrival
                ? EncounterHintEvaluator.EvaluateSegment(q.Snapshot, from, from, source.Arrival, depart, q.Now, geometryKnownLegal: true, includeStartImpacts: source.Parent < 0)
                : new EncounterTransitionResult(true, 0, 0, false);
            if (waiting.Hits != 0) continue;
            dest.Travel = source.Travel + Vector2.Distance(from, to);
            dest.Soft = source.Soft + EncounterScoring.TransitionCost(waiting) + EncounterScoring.TransitionCost(check);
            dest.Margin = Math.Max(source.Margin, Math.Max(waiting.ImpactMarginShortfall, check.ImpactMarginShortfall));
            dest.Arrival = arrive; dest.Departure = Math.Max(source.Arrival, depart - startup); dest.Parent = parent;
            _states[i] = dest;
            _intervalQueue.Enqueue(i, (arrive + IntervalHeuristic(to, q.Speed), arrive));
        }
    }

    private double NextOpenDeparture(WindowRange edges, double earliest)
    {
        for (var i = edges.Start; i < edges.Start + edges.Count; ++i)
        {
            var block = _edgeWindows[i];
            if (earliest < block.Start - TimeEpsilon) break;
            if (earliest <= block.End) earliest = block.End + TimeEpsilon;
        }
        return earliest;
    }

    // Goal delegates still select the endpoint. The queue aims at the nearest
    // endpoint with the minimum goal cost, instead of expanding a time circle.
    private double IntervalHeuristic(Vector2 point, float speed) =>
        _intervalUseHeuristic ? Vector2.Distance(point, _intervalTarget) / speed : 0;

    private WindowRange EdgeWindows(EncounterRouteRequest q, int from, int to, double duration, Vector2? landing)
    {
        if (!landing.HasValue && _edgeRanges.TryGetValue((from, to), out var cached)) return cached;
        _blocks.Clear();
        var a = landing ?? _positions[from]; var b = _positions[to];
        var length = Vector2.Distance(a, b);
        var steps = Math.Max(2, (int)MathF.Ceiling(length / .0625f));
        for (var h = 0; h < q.Snapshot.Hazards.Length; ++h)
        {
            if (_windows[h] is not { } window) continue;
            var shape = q.Snapshot.Hazards[h].Shape;
            var margin = q.Snapshot.Hazards[h].ClearanceMargin;
            var da = landing.HasValue ? shape.Distance(new(a.X, a.Y)) - margin : _distances[h][from]; var db = _distances[h][to];
            if (shape is SDHalfPlane)
            {
                if (da >= 0 && db >= 0) continue;
                double enter = 0, leave = 1;
                if (da >= 0) enter = da / (da - db);
                else if (db >= 0) leave = da / (da - db);
                _blocks.Add(new(window.Start - leave * duration, window.End - enter * duration));
                continue;
            }
            if (shape is SDCircle or SDDonut)
            {
                // Only these verified distance fields use a Lipschitz bound.
                if (da > length + .0001f || db > length + .0001f) continue;
                if (da < -length - .0001f || db < -length - .0001f)
                {
                    _blocks.Add(new(window.Start - duration, window.End));
                    continue;
                }
            }
            var inside = da < 0;
            double enterAt = 0;
            for (var step = 1; step <= steps; ++step)
            {
                var f = (double)step / steps;
                var next = Distance(shape, a, b, f, margin) < 0;
                if (next != inside)
                {
                    var low = (double)(step - 1) / steps; var high = f;
                    for (var iteration = 0; iteration < 15; ++iteration)
                    {
                        var mid = (low + high) * .5;
                        if ((Distance(shape, a, b, mid, margin) < 0) == inside) low = mid;
                        else high = mid;
                    }
                    if (inside) _blocks.Add(new(window.Start - high * duration, window.End - enterAt * duration));
                    else enterAt = low;
                }
                inside = next;
            }
            if (inside) _blocks.Add(new(window.Start - duration, window.End - enterAt * duration));
        }
        MergeBlocks();
        var result = new WindowRange(_edgeWindows.Count, _blocks.Count);
        _edgeWindows.AddRange(_blocks);
        if (!landing.HasValue) _edgeRanges.Add((from, to), result);
        return result;
    }

    private static float Distance(ShapeDistance shape, Vector2 a, Vector2 b, double fraction, float margin)
    {
        var p = Vector2.Lerp(a, b, (float)fraction);
        var d = shape.Distance(new(p.X, p.Y)) - margin;
        return float.IsFinite(d) ? d : float.NegativeInfinity;
    }

    private void MergeBlocks()
    {
        _blocks.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var used = 0;
        for (var i = 0; i < _blocks.Count; ++i)
        {
            var item = _blocks[i];
            if (used > 0 && item.Start <= _blocks[used - 1].End + TimeEpsilon)
                _blocks[used - 1] = new(_blocks[used - 1].Start, Math.Max(_blocks[used - 1].End, item.End));
            else _blocks[used++] = item;
        }
        if (used < _blocks.Count) _blocks.RemoveRange(used, _blocks.Count - used);
    }

    private EncounterRoute BuildIntervalRoute(EncounterRouteRequest q, int goal, bool complete = true)
    {
        _chain.Clear(); _routePoints.Clear();
        for (var id = goal; id >= 0; id = _states[id].Parent) _chain.Add(id);
        _routePoints.Add(new(q.Position, 0));
        for (var index = _chain.Count - 2; index >= 0; --index)
        {
            var state = _states[_chain[index]];
            if (state.Departure > _routePoints[^1].Seconds)
                _routePoints.Add(new(_routePoints[^1].Position, state.Departure));
            if (state.Landing is { } landing)
            {
                _routePoints.Add(new(landing, state.Arrival) { GameDriven = true, CommandVelocity = Vector2.Zero });
                continue;
            }
            var target = _positions[state.Node];
            var command = Vector2.Normalize(target - _routePoints[^1].Position) * q.Speed;
            _routePoints.Add(new(target, state.Arrival) { CommandVelocity = command });
        }
        if (complete && _routePoints[^1].Seconds < _planningHorizon)
            _routePoints.Add(new(_routePoints[^1].Position, _planningHorizon));
        var final = _states[goal];
        var endpoint = final.Landing ?? _positions[final.Node];
        var tail = TerminalAssessment(q, endpoint, final.Arrival);
        var assessment = EncounterScoring.StaticScore(q, endpoint, final.Travel,
            final.Soft + EncounterScoring.TransitionCost(tail), Math.Max(final.Margin, tail.ImpactMarginShortfall), tail.Hits, tail.Exposure);
        return Route(q, EncounterRouteKind.Ready, _routePoints.ToArray(), tail.Hits, tail.Exposure, IntervalExpanded,
            complete ? "hint-safe-interval" : "hint-safe-prefix") with { Complete = complete, Assessment = assessment };
    }
}
