using System.Numerics;
using System.Diagnostics;

namespace DeepDungeon.MechanicPlanning;

// Time-directed SST-inspired search in the full hybrid state. Event jumps are
// forced by the simulator, not selected as controls. Best-near parent selection
// and witness sparsification follow SST ideas; fixed time layers, bounded
// controls and lack of physical inactive-leaf deletion are explicit adaptations.
// No HySST completeness/optimality theorem is claimed for this implementation.
sealed class HybridTreePlanner<TState>(int extensions, int rounds, int seed, double integrationStep = .1,
    bool sparsify = true, bool positionOnly = false, bool poolStates = true,
    bool stopOnFeasible = false, bool reusePrefix = false, bool requireImpactMargin = false) : IResearchPlanner<TState>, ITreeDiagnostics
    where TState : class
{
    readonly Random _rng = new(seed);
    Vector2[]? _previous;
    double _at;
    readonly List<TState> _states = [];
    int _usedStates;
    TState Rent(PlanningProblem<TState> q, TState source)
    {
        if (!poolStates) return q.Copy(source);
        if (_usedStates == _states.Count) _states.Add(q.Copy(source));
        else q.CopyFrom(_states[_usedStates], source);
        return _states[_usedStates++];
    }
    public int Evaluations { get; private set; }
    public double LastCost { get; private set; }
    public int Accepted { get; private set; }
    public int Complete { get; private set; }
    public TreeDiagnostics? SearchDiagnostics { get; private set; }
    sealed class Vertex(TState state, PlanScore key, int parent, Vector2 u, int depth)
    {
        public readonly TState S = state;
        public readonly PlanScore Key = key;
        public readonly int Parent = parent, Depth = depth;
        public readonly Vector2 U = u;
        public bool Active = true;
    }
    sealed class Witness(TState state, int representative)
    {
        public readonly TState S = state;
        public int Representative = representative;
    }

    public ControlPlan Plan(PlanningProblem<TState> problem)
    {
        var diagnostics = SearchDiagnostics = new TreeDiagnostics
        { Acceptance = stopOnFeasible ? requireImpactMargin ? "impact-margin" : "first-feasible" : "refine", ReusePrefix = reusePrefix };
        var phaseAt = Stopwatch.GetTimestamp(); var phase = "setup";
        void Phase(string next)
        {
            var now = Stopwatch.GetTimestamp(); var elapsed = Stopwatch.GetElapsedTime(phaseAt, now).TotalMilliseconds;
            switch (phase)
            {
                case "setup": diagnostics.SetupMs += elapsed; break;
                case "warm": diagnostics.WarmMs += elapsed; break;
                case "expand": diagnostics.ExpandMs += elapsed; break;
                case "tail": diagnostics.TailMs += elapsed; break;
            }
            phaseAt = now; phase = next;
        }
        var q = problem.WithResolution(integrationStep); var start = q.CreateState();
        var guidance = (IStateSearchGuidance<TState>)q; Evaluations = 0; Accepted = 0; Complete = 0;
        _usedStates = 0;
        var nodes = new List<Vertex> { new(Rent(q, start), q.Score(start, false), -1, default, 0) };
        var layers = Enumerable.Range(0, q.Knots + 1).Select(_ => new List<int>()).ToArray(); layers[0].Add(0);
        var witnesses = Enumerable.Range(0, q.Knots + 1).Select(_ => new List<Witness>()).ToArray();
        witnesses[0].Add(new(nodes[0].S, 0));
        double Distance(TState a, TState b) => positionOnly ? Vector2.DistanceSquared(q.Position(a), q.Position(b)) : guidance.Distance(a, b);
        var best = new Vector2[q.Knots]; var bestScore = q.Evaluate(best); Evaluations++;
        ControlPlan Finish()
        {
            Phase("done");
            _previous = best; _at = q.StartTime; LastCost = bestScore.Display; return new(best, q.Ends, bestScore);
        }
        bool Acceptable(PlanScore key) => key.StackDistance == 0 && key.GeometryFailures == 0 && key.Failures == 0 && (!requireImpactMargin || key.ImpactMarginShortfall <= 1e-7);
        bool Ready() => stopOnFeasible && Acceptable(bestScore);
        bool Better(PlanScore key) => key.CompareTo(bestScore) < 0 || stopOnFeasible && Acceptable(key);
        if (q.CanHold(bestScore) || Ready()) { diagnostics.Exit = "hold"; return Finish(); }
        Vector2[] Path(int n)
        {
            var u = new Vector2[q.Knots];
            for (var i = n; nodes[i].Parent >= 0; i = nodes[i].Parent) u[nodes[i].Depth - 1] = nodes[i].U;
            return u;
        }
        void Consider(Vector2[] u, int prefix = -1)
        {
            var key = reusePrefix && prefix >= 0
                ? q.EvaluateTail(u, nodes[prefix].Depth, nodes[prefix].S)
                : q.Evaluate(u);
            if (reusePrefix && prefix >= 0) diagnostics.TailPrefixStepsSaved += nodes[prefix].Depth;
            Evaluations++;
            // The caller may mutate a completion to try another tail. Keep the
            // accepted trajectory immutable so its stored score remains valid.
            if (Better(key)) { bestScore = key; best = (Vector2[])u.Clone(); }
        }
        int Add(int parent, Vector2 u)
        {
            var from = nodes[parent]; var s = Rent(q, from.S);
            var depth = from.Depth + 1;
            q.Advance(s, u, q.Ends[depth - 1]); Evaluations++;
            diagnostics.Extensions++;
            var key = q.Score(s, false);
            if (!q.CanExpand(s)) { if (poolStates) _usedStates--; return -1; }
            var node = new Vertex(s, key, parent, u, depth);
            var local = witnesses[depth]; Witness? nearest = null; var distance = double.PositiveInfinity;
            foreach (var w in sparsify ? local : [])
            {
                var d = Distance(s, w.S);
                if (d < distance) { distance = d; nearest = w; }
            }
            if (nearest != null && distance <= .6 * .6)
            {
                if (node.Key.CompareTo(nodes[nearest.Representative].Key) >= 0) { if (poolStates) _usedStates--; return -1; }
                nodes[nearest.Representative].Active = false;
            }
            else { nearest = new(s, -1); local.Add(nearest); }
            var id = nodes.Count; nodes.Add(node); nearest.Representative = id; Accepted++;
            // Keep damaging branches as complete/fallback candidates, but spend
            // further extensions on the least-violating prefixes at this time.
            // An exposed start still expands: all of its first prefixes carry
            // the initial violation. Safe chase branches retain search priority.
            var layer = layers[depth];
            if (layer.Count == 0 || key.Failures < nodes[layer[0]].Key.Failures) layer.Clear();
            if (layer.Count == 0 || key.Failures == nodes[layer[0]].Key.Failures) layer.Add(id);
            if (depth == q.Knots)
            {
                Complete++;
                var score = q.Score(s, true);
                if (Better(score)) { bestScore = score; best = Path(id); }
            }
            return id;
        }
        // Repropagate previous controls through the new observed state. Do not
        // reuse stale search states or frozen pursuit landings from older plans.
        Phase("warm");
        if (_previous != null)
        {
            var warm = ControlMath.Shift(_previous, q.Knots, (q.StartTime - _at) / ControlMath.Knot);
            Consider(warm);
            if (Ready()) { diagnostics.Exit = "warm"; return Finish(); }
            var p = 0;
            foreach (var u in warm) { p = Add(p, u); if (p < 0) break; }
        }
        Phase("expand");
        var deepest = layers.Select((l, k) => (l.Count, k)).Where(v => v.Count > 0).Max(v => v.k);
        var sample = q.Copy(start);
        for (var iteration = 0; iteration < Math.Max(1, extensions) * Math.Max(1, rounds); ++iteration)
        {
            diagnostics.Iterations++;
            var depth = _rng.NextDouble() < .25 ? Math.Min(deepest, q.Knots - 1) : _rng.Next(Math.Min(deepest + 1, q.Knots));
            var available = layers[depth]; if (available.Count == 0) continue;
            q.CopyFrom(sample, nodes[available[_rng.Next(available.Count)]].S);
            guidance.SampleTarget(sample, _rng, perturbHistory: true);
            var nearestId = -1; var nearestDistance = double.PositiveInfinity; var nearId = -1;
            foreach (var n in available)
            {
                if (!nodes[n].Active) continue;
                var d = Distance(nodes[n].S, sample);
                diagnostics.DistanceTests++;
                if (d < nearestDistance) { nearestDistance = d; nearestId = n; }
                if (d <= 3.5 * 3.5 && (nearId < 0 || nodes[n].Key.CompareTo(nodes[nearId].Key) < 0)) nearId = n;
            }
            var parent = nearId >= 0 ? nearId : nearestId; if (parent < 0) continue;
            Vector2 direction;
            if (_rng.NextDouble() < .5)
            {
                var angle = (float)(_rng.NextDouble() * 2 * Math.PI);
                direction = new(MathF.Cos(angle), MathF.Sin(angle));
            }
            else
            {
                direction = q.Position(sample) - q.Position(nodes[parent].S);
                direction = direction.LengthSquared() > .0001f ? Vector2.Normalize(direction) : Vector2.UnitX;
            }
            var speed = _rng.NextDouble() < .05 ? 0 : q.Speed;
            var control = direction * speed;
            var duration = _rng.Next(1, 5);
            for (var leg = 0; leg < duration && nodes[parent].Depth < q.Knots; ++leg)
            {
                parent = Add(parent, control); if (parent < 0) break;
                deepest = Math.Max(deepest, nodes[parent].Depth);
                if (Ready()) { diagnostics.Exit = "complete"; return Finish(); }
            }
        }
        // Incomplete prefixes are only candidates: evaluate their entire hold /
        // constant-control completion, never report a short safe prefix as safe.
        Phase("tail");
        foreach (var n in Enumerable.Range(0, nodes.Count).Where(n => nodes[n].Active && nodes[n].Depth < q.Knots)
                     .OrderByDescending(n => nodes[n].Depth).ThenBy(n => nodes[n].Key).Take(12))
        {
            var u = Path(n); Consider(u, n);
            if (Ready()) { diagnostics.Exit = "tail-hold"; return Finish(); }
            for (var k = nodes[n].Depth; k < u.Length; ++k) u[k] = nodes[n].U;
            Consider(u, n);
            if (Ready()) { diagnostics.Exit = "tail-control"; return Finish(); }
        }
        return Finish();
    }

}
