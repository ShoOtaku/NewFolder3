using System.Numerics;
using System.Threading;
using DeepDungeon.Fsd.Core;

namespace MasterPlayer.MechanicRuntime;

/// <summary>A persistent arrival region for one observed target. The host supplies
/// ground and sight observations; search, arrival and route continuity use the same goal.</summary>
public sealed class NavigationRegionGoal(
    Func<INavigationGrid, Vector3, Vector3?> ground,
    Func<Vector3, Vector3, float, float, bool?> canStand,
    Func<Vector3, Vector3, float, float>? horizontalRadiusSquared = null)
{
    private const float StandClearance = NavigationFollower.Arrival + .01f;
    private readonly GridAStar.Search _search = new();
    private readonly List<Vector3> _points = new(), _rejected = new();
    private INavigationGrid? _terrain;
    private Vector3 _player;
    private readonly Dictionary<Vector2, bool> _accepted = new();

    private Vector3 _searchTarget, _pathTarget;
    private bool _fallback;
    private DateTime _nextCheck;
    private float _approach, _hold;
    public ulong TargetId { get; private set; }
    public Vector3 Target { get; private set; }
    public bool Holding { get; private set; }
    public bool Searching { get; private set; }
    public bool Unreachable { get; private set; }
    public NavigationPath? Path { get; private set; }

    // The interval lattice ranks cells by a cheap lower bound. Only reachable
    // finalists and simulated endpoints request ground and sight observations.
    internal float Distance(Vector2 point, float distanceToApproach, bool lowerBound)
    {
        var range = Holding || (_fallback && Path != null) ? _hold : _approach;
        float outside = Math.Max(0, Vector2.Distance(point, new(Target.X, Target.Z)) - range);
        if (lowerBound) return outside;
        if (outside > 0) return Math.Max(outside, distanceToApproach);
        if (!_accepted.TryGetValue(point, out var accepted))
        {
            var current = point == new Vector2(_player.X, _player.Z);
            var stand = current ? _player : ground(_terrain!, new(point.X, _player.Y, point.Y));
            accepted = stand is { } p && Accept(p, Target, range, current ? 0 : StandClearance) == true;
            _accepted.Add(point, accepted);
        }
        return accepted ? 0 : Math.Max(distanceToApproach, NavigationFollower.Arrival);
    }

    public void Observe(ulong id, Vector3 target, float approach, float hold)
    {
        if (TargetId != id || approach != _approach || hold != _hold) Reset();
        TargetId = id;
        Target = target;
        _approach = approach;
        _hold = hold;
    }

    public void Reset()
    {
        TargetId = 0;
        _terrain = null;
        _rejected.Clear();
        Invalidate();
    }

    internal void Invalidate()
    {
        Holding = Searching = Unreachable = _fallback = false;
        Path = null;
        _nextCheck = default;
    }

    public void Reject(Vector3 stand)
    {
        _rejected.Add(stand);
        Invalidate();
    }

    public void ClearRejected()
    {
        _rejected.Clear();
        Invalidate();
    }

    private bool? Accept(Vector3 point, Vector3 target, float range, float clearance)
    {
        foreach (var rejected in _rejected)
            if (NavigationRange.Point.Contains(rejected, point)) return false;
        return canStand(point, target, range, clearance);
    }

    // Check the actual position first: an already satisfied goal needs no terrain search.
    internal bool ObservePlayer(INavigationGrid? map, Vector3 player, DateTime now)
    {
        _accepted.Clear();
        _player = player;
        if (!ReferenceEquals(map?.Topology, _terrain?.Topology))
        {
            Invalidate();
            _terrain = map;
        }
        bool atEnd = Path is { } pending && NavigationRange.Point.HasArrived(player, pending.Points[^1]);
        if (now < _nextCheck && !atEnd) return false;
        _nextCheck = now.AddMilliseconds(100);
        float range = Holding || (_fallback && Path != null) ? _hold : _approach;
        Holding = Accept(player, Target, range, 0) ?? Holding;
        if (Holding)
        {
            Path = null;
            Searching = Unreachable = _fallback = false;
        }
        return true;
    }

    // Resolve against the full physical map even when the timed solver uses a local window.
    internal void Resolve(INavigationGrid map, Vector3 player, bool refresh, CancellationToken token)
    {
        if (Holding) return;
        if (refresh)
        {
            if (Path is { } path)
            {
                var end = path.Points[^1];
                if (!NavigationRange.Point.HasArrived(player, end))
                {
                    if (Accept(end, Target, _fallback ? _hold : _approach, StandClearance) == true)
                    {
                        Searching = false;
                        return;
                    }
                    // A still-usable fallback stand buys time for the incremental
                    // search. If both ranges fail, connect the remaining corridor to
                    // a verified live stand before discarding its traversable prefix.
                    if (_hold <= _approach || Accept(end, Target, _hold, StandClearance) != true)
                    {
                        if (Target != _pathTarget && TryReconnect(map, player, path, _pathTarget))
                        {
                            Searching = Unreachable = false;
                            return;
                        }
                        Path = null;
                    }
                }
                else Path = null;
            }
            if (TryDirect(map, player))
            {
                Searching = Unreachable = false;
                return;
            }
            if (!Searching) Begin(map, player);
        }
        if (!Searching) return;
        var status = _search.Step(int.MaxValue, 1, token);
        if (status == GridSearchStatus.Running) return;
        Searching = false;
        if (status == GridSearchStatus.Found)
        {
            _search.CopyPath(_points);
            var end = _points[^1];
            var range = _search.UsedFallback ? _hold : _approach;
            if (Accept(player, Target, range, 0) == true)
            {
                Holding = true;
                Path = null;
            }
            else if (!NavigationRange.Point.HasArrived(player, end)
                && Accept(end, Target, range, StandClearance) == true)
            {
                _pathTarget = Target;
                _fallback = _search.UsedFallback;
                Path = new(map, _points.ToArray());
            }
            else if (Target != _searchTarget)
            {
                // A time-sliced search can finish after its target has moved.
                TryReconnect(map, player, new(map, _points.ToArray()), _searchTarget);
            }
        }
        Unreachable = status == GridSearchStatus.Exhausted && Target == _searchTarget && Path == null;
    }

    private bool TryReconnect(INavigationGrid map, Vector3 player, NavigationPath path, Vector3 previousTarget)
    {
        var points = path.Points;
        var seed = points[^1] + (Target - previousTarget);
        if (ground(map, seed) is not { } stand || Accept(stand, Target, _approach, StandClearance) != true)
            return false;

        // Only reuse the part ahead of the player. Prefer the shortest retained
        // prefix, so a patrol never leaves a growing trail of obsolete endpoints.
        int join = points.Length - 1;
        while (join >= 0 && !map.CanTraverse(player, points[join])) --join;
        if (map.CanTraverse(player, stand))
        {
            Path = new(map, [player, stand]);
        }
        else
        {
            if (join < 0) return false;
            int exit = join;
            while (!map.CanTraverse(points[exit], stand))
            {
                if (exit + 1 == points.Length || !map.CanTraverse(points[exit], points[exit + 1]))
                    return false;
                ++exit;
            }
            var route = new Vector3[exit - join + 3];
            route[0] = player;
            Array.Copy(points, join, route, 1, exit - join + 1);
            route[^1] = stand;
            Path = new(map, route);
        }
        _pathTarget = Target;
        _fallback = false;
        return true;
    }

    private bool TryDirect(INavigationGrid map, Vector3 player)
    {
        var delta = new Vector2(player.X - Target.X, player.Z - Target.Z);
        var range = _approach - StandClearance;
        var radiusSquared = horizontalRadiusSquared?.Invoke(player, Target, range) ?? range * range;
        if (radiusSquared <= 0 || delta.LengthSquared() <= radiusSquared) return false;
        // Follow a clear lane into the region. The player's own range check
        // stops the follower at entry, even when this interior waypoint is farther away.
        var offset = Vector2.Normalize(delta) * (MathF.Sqrt(radiusSquared) * .5f);
        var seed = new Vector3(Target.X + offset.X, player.Y, Target.Z + offset.Y);
        if (ground(map, seed) is not { } end || !map.CanTraverse(player, end)
            || Accept(end, Target, _approach, StandClearance) != true) return false;
        _pathTarget = Target;
        _fallback = false;
        Path = new(map, [player, end]);
        return true;
    }

    private void Begin(INavigationGrid map, Vector3 player)
    {
        _searchTarget = Target;
        Searching = true;
        Unreachable = false;
        float Distance(Vector3 p) => Vector2.Distance(new(p.X, p.Z), new(_searchTarget.X, _searchTarget.Z));
        bool? Candidate(Vector3 point, float range) => NavigationRange.Point.HasArrived(player, point)
            ? Accept(player, _searchTarget, range, 0) : Accept(point, _searchTarget, range, StandClearance);
        _search.Begin(map, player, p => Candidate(p, _approach),
            p => Math.Max(0, Distance(p) - _approach), p => ground(map, p),
            p => Distance(p) <= _hold, _hold > _approach ? p => Candidate(p, _hold) : null);
    }
}
