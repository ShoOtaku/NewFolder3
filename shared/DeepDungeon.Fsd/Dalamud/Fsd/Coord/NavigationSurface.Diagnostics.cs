using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using BossMod;
using DeepDungeon.Fsd.Dalamud.Utilities;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Coord;

internal sealed partial class NavigationSurface
{
    private sealed record PlanCompletion(EncounterRoute Route, DateTime StartedUtc,
        DateTime CompletedUtc, double ComputeMilliseconds);

    private static readonly Guid PlannerBuild = typeof(NavigationPlanner).Module.ModuleVersionId;
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromMilliseconds(200);
    private long _planSequence, _adoptedPlanId, _issuedPlanId;
    private EncounterPlanningSnapshot? _diagnosticSnapshot;
    private DateTime _diagnosticAtUtc, _positionSampleAtUtc;
    private Vector3 _diagnosticPosition, _diagnosticGoal, _positionSample;
    private float _diagnosticSpeed, _diagnosticRadius;
    private long _nativeWrites, _sampledNativeWrites, _nativeWritePlanId;
    private DateTime _nativeWriteAtUtc;
    private Vector2 _nativeAppliedInput, _nativeUpstreamInput;
    private Vector3 _nativeWriteDestination;

    private void CaptureDiagnosticContext(DateTime now, Vector3 destination, Vector3 position,
        float radius, float speed)
    {
        _diagnosticAtUtc = now;
        _diagnosticGoal = destination;
        _diagnosticPosition = position;
        _diagnosticRadius = radius;
        _diagnosticSpeed = speed;
        _diagnosticSnapshot = null;
    }

    // Called after an actual native ordinary-input write. No serialization,
    // file I/O, world scan, or additional native read occurs in this callback.
    private void OnNativeWrite()
    {
        ++_nativeWrites;
        _nativeWriteAtUtc = DateTime.UtcNow;
        _nativeWritePlanId = _issuedPlanId;
        _nativeAppliedInput = _direct.LastAppliedInput;
        _nativeUpstreamInput = _direct.LastUserInput;
        _nativeWriteDestination = _direct.DesiredPosition;
    }

    private PlanCompletion MeasurePlan(NavigationRequest request, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        var stamp = Stopwatch.GetTimestamp();
        var route = _planner.Plan(in request, token);
        var elapsed = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
        return new(route, started, DateTime.UtcNow, elapsed);
    }

    // Explicit coordinates are intentional: the run recorder does not enable
    // IncludeFields, so serializing Vector2/WPos directly would lose coordinates.
    private static object Position(Vector2 p) => new { x = Number(p.X), z = Number(p.Y) };
    private static object Position(Vector3 p) => new { x = Number(p.X), y = Number(p.Y), z = Number(p.Z) };
    private static object Position(WPos p) => new { x = Number(p.X), z = Number(p.Z) };
    private static object Input(Vector2 p) => new { left = Number(p.X), forward = Number(p.Y) };
    // Null denotes a non-finite diagnostic number, never a substituted zero.
    private static float? Number(float value) => float.IsFinite(value) ? value : null;
    private static double? Number(double value) => double.IsFinite(value) ? value : null;

    private static object? Geometry(EncounterHazardGeometry? geometry) => geometry is { } g ? new
    {
        kind = g.Kind,
        origin = new { x = Number(g.OriginX), z = Number(g.OriginZ) },
        rotationRadians = Number(g.RotationRadians),
        innerRadius = Number(g.InnerRadius),
        outerRadius = Number(g.OuterRadius),
        lengthFront = Number(g.LengthFront),
        lengthBack = Number(g.LengthBack),
        halfWidth = Number(g.HalfWidth),
        halfAngleRadians = Number(g.HalfAngleRadians),
        inverted = g.Inverted,
    } : null;

    private object BuildPlanRecord(long planId, int requestGeneration, int observedGeneration,
        EncounterRouteRequest request, PlanCompletion? completion, string decision,
        DateTime observedAtUtc, string? error)
    {
        var s = request.Snapshot;
        var player = new WPos(request.Position.X, request.Position.Y);
        var route = completion?.Route;
        object? firstMove = null;
        if (route != null)
        {
            for (var i = 1; i < route.Points.Length; ++i)
            {
                var from = route.Points[i - 1];
                var to = route.Points[i];
                if (Vector2.DistanceSquared(from.Position, to.Position) < 1e-6f) continue;
                firstMove = new
                {
                    index = i - 1, from = Position(from.Position), to = Position(to.Position),
                    departureSeconds = Number(from.Seconds),
                    departureUtc = route.OriginUtc.AddSeconds(from.Seconds),
                };
                break;
            }
        }
        return new
        {
            schema = 2, planner = "ai-hints-auto", scoreKind = "hint-constraint-violations",
            solver = _planner.Solver, work = _planner.Work,
            searchEvaluations = _planner.SearchEvaluations, searchExtensions = _planner.SearchExtensions,
            searchExit = _planner.SearchExit, windComparison = route?.WindComparison,
            planId, decision, observedAtUtc, requestGeneration, observedGeneration,
            plannerBuild = PlannerBuild, error,
            firstWaveSeconds = Number(_planner.FirstWaveSeconds),
            secondWaveSeconds = Number(_planner.SecondWaveSeconds),
            intervalExpanded = _planner.IntervalExpanded,
            intervalBudgetReached = _planner.IntervalBudgetReached,
            intervalCandidates = _planner.CandidateCount,
            request = new
            {
                nowUtc = request.Now, playerId = request.PlayerId,
                position = Position(request.Position), goal = Position(request.Goal),
                goalRadius = Number(request.GoalRadius), speed = Number(request.Speed),
                previousDirection = Position(request.PreviousDirection),
                observedVelocity = Position(request.Velocity),
                acceleration = Number(request.Motion.Acceleration),
                snapshot = new
                {
                    territory = s.Territory, encounter = s.EncounterId, bossOid = s.BossOID,
                    capturedAtUtc = s.CapturedAt,
                    ageAtRequestMs = (request.Now - s.CapturedAt).TotalMilliseconds,
                    bossPosition = Position(s.BossPosition), bossRadius = Number(s.BossRadius),
                    bossTargetable = s.BossTargetable, bossDead = s.BossDead,
                    hintModule = s.Hints?.ModuleName,
                    keepMoving = s.Hints?.KeepMoving,
                    pendingKnockback = s.Hints?.PendingKnockback,
                    knockbacks = s.Knockbacks.Select(k => new {
                        activationUtc = k.Source.Activation, origin = Position(k.Source.Origin),
                        distance = Number(k.Source.Distance), kind = k.Source.Kind.ToString(),
                        immunityStatusId = k.PathDependentImmunityStatus,
                    }).ToArray(),
                    specialMode = s.Hints?.Mode.ToString(),
                    arena = new
                    {
                        type = s.Arena.GetType().Name, center = Position(s.ArenaCenter),
                        radius = Number(s.Arena.Radius),
                        geometrySource = s.Hints?.ModuleName,
                    },
                    hazards = s.Hazards.Select((h, index) => new
                    {
                        index, sourceId = h.SourceId, kind = h.Kind.ToString(), predicted = h.Predicted,
                        activationUtc = h.Activation, expiresAtUtc = h.ExpiresAt,
                        secondsFromRequest = (h.Activation - request.Now).TotalSeconds,
                        distanceType = h.Shape.GetType().Name,
                        geometry = Geometry(h.Geometry),
                        signedDistanceAtPlayer = Number(h.Shape.Distance(player)),
                    }).ToArray(),
                    temporaryObstacles = s.TemporaryObstacles.Select(o => new
                    {
                        distanceType = o.GetType().Name,
                        signedDistanceAtPlayer = Number(o.Distance(player)),
                    }).ToArray(),
                    staticObstacles = s.StaticObstacles.Select(o => new
                    {
                        distanceType = o.GetType().Name,
                        signedDistanceAtPlayer = Number(o.Distance(player)),
                    }).ToArray(),
                    movingObstacles = s.MovingObstacles.Select(o => new
                    {
                        sourceId = o.SourceId, position = Position(o.Position),
                        orbitCenter = Position(o.OrbitCenter), radius = Number(o.Radius),
                        angularVelocityRadiansPerSecond = Number(o.AngularVelocity),
                    }).ToArray(),
                    chasers = s.Chasers.Select(c => new
                    {
                        targetId = c.TargetId, targetPosition = Position(c.TargetPosition),
                        previousPosition = Position(c.PreviousPosition), radius = Number(c.Radius),
                        nextStepDistance = Number(c.NextStepDistance), stepDistance = Number(c.StepDistance),
                        remaining = c.Remaining, nextActivationUtc = c.NextActivation,
                        intervalSeconds = Number(c.Interval),
                        adapted = s.ChasingHazards.Contains(c),
                    }).ToArray(),
                    residence = s.Residence is not { } residence ? null : new
                    {
                        statusId = residence.StatusId, initialDeadlineUtc = residence.InitialDeadline,
                        reentrySeconds = residence.ReentrySeconds,
                        statusPresent = residence.StatusPresent, windImmune = residence.WindImmune,
                        requiresImmunityAtUtc = residence.RequiresImmunityAt,
                        safeContactSeconds = Number(residence.InitialSafeContact),
                        immunityContactSeconds = Number(residence.InitialImmunityContact),
                        distanceType = residence.UnsafeRegion.GetType().Name,
                        signedDistanceAtPlayer = Number(residence.UnsafeRegion.Distance(player)),
                    },
                },
            },
            timing = completion == null ? null : new
            {
                startedUtc = completion.StartedUtc, completedUtc = completion.CompletedUtc,
                queueMs = (completion.StartedUtc - request.Now).TotalMilliseconds,
                computeMs = Number(completion.ComputeMilliseconds),
                harvestDelayMs = (observedAtUtc - completion.CompletedUtc).TotalMilliseconds,
                ageAtDecisionMs = (observedAtUtc - request.Now).TotalMilliseconds,
            },
            result = route == null ? null : new
            {
                encounter = route.EncounterId, originUtc = route.OriginUtc,
                kind = route.Kind.ToString(), hits = route.PredictedHits,
                exposure = Number(route.Exposure), expanded = route.Expanded, reason = route.Reason,
                firstMove,
                points = route.Points.Select(p => new
                {
                    seconds = Number(p.Seconds), position = Position(p.Position), gameDriven = p.GameDriven,
                }).ToArray(),
            },
        };
    }

    private void RecordDiagnosticState(DateTime now, string state)
    {
        if (state == _lastDiagState && now - _lastDiag < DiagnosticInterval) return;
        _lastDiagState = state;
        _lastDiag = now;
        var s = _diagnosticSnapshot;
        var r = _route;
        var player = new WPos(_diagnosticPosition.X, _diagnosticPosition.Z);
        var newPositionSample = _diagnosticAtUtc > _positionSampleAtUtc;
        var hasPreviousPosition = _positionSampleAtUtc != default && newPositionSample;
        object? leg = null;
        if (r != null && _routeIndex + 1 < r.Points.Length)
        {
            var a = r.Points[_routeIndex];
            var b = r.Points[_routeIndex + 1];
            leg = new
            {
                from = Position(a.Position), to = Position(b.Position),
                fromSeconds = Number(a.Seconds), toSeconds = Number(b.Seconds),
                waitingSegment = Vector2.DistanceSquared(a.Position, b.Position) < 1e-6f,
            };
        }
        _record("coord-encounter", new
        {
            schema = 2, planner = "ai-hints-auto", scoreKind = "hint-constraint-violations", observedAtUtc = now,
            solver = _planner.Solver, work = _planner.Work,
            encounter = s?.EncounterId ?? _snapshot?.EncounterId ?? 0, status = state,
            planId = _adoptedPlanId, pendingPlanId = (long?)null,
            snapshotPresent = s != null, snapshotCapturedAtUtc = s?.CapturedAt,
            snapshotAgeMs = s == null ? (double?)null : (now - s.CapturedAt).TotalMilliseconds,
            hazards = s?.Hazards.Length ?? 0, moving = s?.MovingObstacles.Length ?? 0,
            chasers = s?.Chasers.Length ?? 0,
            currentHazards = s?.Hazards.Select((h, index) => new
            {
                index, sourceId = h.SourceId, activationUtc = h.Activation, expiresAtUtc = h.ExpiresAt,
                secondsFromNow = (h.Activation - now).TotalSeconds,
                signedDistanceAtPlayer = Number(h.Shape.Distance(player)),
            }).ToArray(),
            kind = r?.Kind.ToString() ?? "none", hits = r?.PredictedHits,
            exposure = r == null ? (float?)null : Number(r.Exposure),
            expanded = r?.Expanded, step = r == null ? (int?)null : _routeIndex,
            routeOriginUtc = r?.OriginUtc,
            routeElapsedSeconds = r == null ? (double?)null : (now - r.OriginUtc).TotalSeconds,
            attack = _attackRegion is { TargetId: not 0 } attack ? new
            {
                targetId = attack.TargetId, target = Position(attack.Target), holding = attack.Holding,
                searching = attack.Searching, stand = attack.Path is { } path ? Position(path.Points[^1]) : null,
            } : null,
            leg, failure = _failure,
            position = Position(_diagnosticPosition), positionObservedAtUtc = _diagnosticAtUtc,
            observedDisplacementXZ = hasPreviousPosition ? Number(Vector2.Distance(
                new(_positionSample.X, _positionSample.Z), new(_diagnosticPosition.X, _diagnosticPosition.Z))) : null,
            observedDisplacementSeconds = hasPreviousPosition
                ? (double?)(_diagnosticAtUtc - _positionSampleAtUtc).TotalSeconds : null,
            goal = Position(_diagnosticGoal), goalRadius = Number(_diagnosticRadius), speed = Number(_diagnosticSpeed),
            observedVelocity = Position(_observed.Velocity),
            movementModel = new { acceleration = Number(_observed.Motion.Acceleration) },
            displacementVelocity = Position(_observed.DisplacementVelocity),
            forcedMovement = _observed.ForcedMovement, movementLocked = _observed.MovementLocked,
            control = new
            {
                directEnabled = _direct.Enabled, activeOwner = DirectMovementController.ActiveOwner,
                inputHooksReady = DirectMovementController.InputHooksReady,
                initializationFailure = DirectMovementController.InitializationFailure,
                ownerControlsAvailable = _direct.ControlsAvailable,
                hookLastSampleAgeMs = _direct.LastInputTimestamp == 0 ? (double?)null
                    : ((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency - _direct.LastInputTimestamp) * 1000,
                hookLastSampleUpstreamInput = _direct.LastInputTimestamp == 0 ? null : Input(_direct.LastUserInput),
                issuedPlanId = _issuedPlanId,
                requestedDestination = _direct.Enabled ? Position(_direct.DesiredPosition) : null,
                nativeWriteCount = _nativeWrites,
                nativeWritesSincePreviousSample = _nativeWrites - _sampledNativeWrites,
                lastNativeWrite = _nativeWrites == 0 ? null : new
                {
                    atUtc = _nativeWriteAtUtc, planId = _nativeWritePlanId,
                    ageMs = (now - _nativeWriteAtUtc).TotalMilliseconds,
                    destination = Position(_nativeWriteDestination),
                    appliedInput = Input(_nativeAppliedInput), upstreamInput = Input(_nativeUpstreamInput),
                },
            },
        });
        _sampledNativeWrites = _nativeWrites;
        if (newPositionSample)
        {
            _positionSampleAtUtc = _diagnosticAtUtc;
            _positionSample = _diagnosticPosition;
        }
    }
}
