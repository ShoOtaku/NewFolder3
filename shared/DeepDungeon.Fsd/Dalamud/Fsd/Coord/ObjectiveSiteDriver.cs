using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// The site-pool decision driver — owns the site ledger, the intent
/// (committed head + advisory tail) and, in knowledge-driven mode, the
/// shared room-route selection. Navigation is deliberately not owned: the
/// caller reads Head, drives movement however the layer requires
/// (NavigationSurface, GoToVerb claims, ops), then reports the typed
/// outcome back through the captured work token.
///
/// Knowledge mode is the normal-floor path: candidates come from the
/// calibrated yield model (legacy fixed selection as the per-room
/// fallback), work completes through probe/chest/intel postconditions in
/// FloorKnowledge, and route order — not reward argmax — decides the head.
/// </summary>
internal sealed class ObjectiveSiteDriver : IDisposable
{
    private readonly ObjectiveIntent _intent = new();
    private readonly List<ObjectiveSite> _sites = new();
    private readonly List<int> _tail = new();
    private string _committedKey = "";
    private SiteWorkToken _committedToken;
    private long _lastRebuildMs;
    private ObjectiveSiteSource.ObjectiveSiteFilter _filter =
        new(OpenBronze: true, OpenSilver: true, OpenGold: true,
            Banded: true, HuntMobs: true, ProbeRooms: true, Exit: true);
    private ObjectiveTunables _tunables = new();

    // ---- knowledge-driven mode ----
    private DetailedMapRunSnapshot? _map;
    private RunKnowledge? _knowledge;
    private Func<FloorWorkPolicy>? _policy;
    private Action<string, object>? _record;
    private FloorWorkPolicy _workPolicy;
    private bool _routeDirty = true;
    private long _seenKnowledgeRevision = -1;
    private long _seenInventoryRevision = -1;
    private long _seenEvidenceVersion = -1;
    private long _nextAttempt;
    private readonly Dictionary<string, long> _attempts = new();
    private readonly List<RoomSitePlanner.RouteWorkItem> _routeWork = new();
    private readonly List<(int Room, Vector3 Pos)> _anchors = new();
    private readonly List<int> _rooms = new();
    private readonly PalantirProvider _palantir = new();
    private NormalFloorGraphSnapshot? _normalGraph;
    private Func<bool>? _passageOpen;
    private FloorActivity _activity;
    // Floor-level geometry: anchors, legal rooms, pairwise room paths and
    // the distance estimator are built ONCE per floor — never per rebuild.
    private long _geometryGeneration = -1;
    private RoomSitePlanner.RoomDistanceEstimator? _dist;
    private readonly Dictionary<(int From, int To), List<int>?> _roomPaths = new();
    private bool _routePassageOpen;
    private bool _routeRebuilt;
    private string _routeState = "";
    private string _routeSource = "";
    private double? _routeRate;
    private string _routeSkipped = "";
    private int _routeProductive;
    private bool _routeResolved;
    private RoomSitePlanner.CandidateModel? _model;
    private FloorKnowledge? _modelFloor;
    private int _modelSightCount = -1;
    private int _modelProbeCount = -1;
    // Shared floor-pooled profile — built once per pinned catalog+floor;
    // the structural rank mapping is cached here, never per frame.
    private RoomSitePlanner.FloorProbeProfile? _floorProfile;
    private DetailedMapCatalog? _profileCatalog;
    private int _profileFloor = -1;
    /// <summary>Pipeline→scheduler channel: true while the bound op has a
    /// meaningful action in progress (dwell/interaction), false during
    /// plain travel.</summary>
    internal bool CommitProtected { get; set; }
    internal bool KnowledgeHoardAcquired => _knowledge?.Floor?.HoardAcquired == true;

    // Side-tables keyed by stable site Key — legacy path only.
    private readonly Dictionary<string, float> _dwellSec = new();
    private readonly Dictionary<string, int> _seen = new();
    private readonly int[] _hops = new int[RoomGraph.MaxRooms];

    private const int EvidenceDebounceRevisions = 2;

    private bool EvidenceConfirmed(ObjectiveSite s) =>
        s.Kind is not (ObjectiveSiteKind.Hoard
            or ObjectiveSiteKind.ConfirmedChest
            or ObjectiveSiteKind.HoardIndicator)
        || _seen.GetValueOrDefault(SiteKey(s)) >= EvidenceDebounceRevisions;
    private int _hopSource = -1;
    private DateTime _lastTickUtc;

    private int _lastFloor = -1;
    private int _spawnRoom = -1;

    private const float RetryableReviveM = 12f;
    private const float DwellRadiusM = 4f;
    private const float DwellTargetSec = 3f;

    /// <summary>The most recently ticking driver — read by the world
    /// overlay. Self-registered in TickDecision, cleared on Dispose.</summary>
    internal static ObjectiveSiteDriver? Active { get; private set; }

    private readonly Dictionary<string, float> _lastScores = new();
    internal IReadOnlyDictionary<string, float> LastScores => _lastScores;
    internal IReadOnlyDictionary<string, float> DwellSeconds => _dwellSec;

    internal ObjectiveSite? Head { get; private set; }
    internal SiteWorkToken HeadToken => _committedToken;

    /// <summary>Chase-position sampler for the quota site — injected by the
    /// host; frozen at each rebuild.</summary>
    internal Func<Vector3?>? ChasePositionProvider;
    internal IReadOnlyList<ObjectiveSite> Sites => _sites;
    internal IReadOnlyList<int> Tail => _tail;
    internal IReadOnlyList<RoomSitePlanner.RouteWorkItem> RouteWork => _routeWork;
    internal string Status { get; private set; } = "idle";
    internal HeadGateResult LastGate { get; private set; } = HeadGateResult.Keep;
    internal bool KnowledgeDriven => _knowledge != null;

    internal int OpenCount
    {
        get
        {
            var n = 0;
            foreach (var s in _sites)
                if (s.Status == ObjectiveSiteStatus.Open)
                    n++;
            return n;
        }
    }

    /// <summary>Drop the committed head without an outcome — the boundary's
    /// context-clear semantic.</summary>
    internal void ClearCommit()
    {
        _committedKey = "";
        _committedToken = default;
        _intent.CommittedSiteId = -1;
        Head = null;
    }

    internal void Configure(
        ObjectiveSiteSource.ObjectiveSiteFilter filter,
        ObjectiveTunables tunables)
    {
        _filter = filter;
        _tunables = tunables;
    }

    /// <summary>Bind the knowledge-driven normal path: the run-pinned map
    /// snapshot (never DetailedMapCatalogManager.Active), the shared run
    /// knowledge, the mode policy, the authoritative passage-open state
    /// (duty flag, not object-range actors), and decision telemetry.</summary>
    internal void ConfigureWork(
        DetailedMapRunSnapshot map,
        RunKnowledge knowledge,
        Func<FloorWorkPolicy> policy,
        Func<bool> passageOpen,
        Action<string, object> record)
    {
        _map = map;
        _knowledge = knowledge;
        _policy = policy;
        _passageOpen = passageOpen;
        _record = record;
        _policySkippedChestKeys.Clear();   // per-floor scope
        _failedWorkKeys.Clear();
        _routeDirty = true;
    }

    /// <summary>Force the next decision tick to rebuild the route —
    /// RequestPlanRefresh's only effect in the normal path.</summary>
    internal void MarkRouteDirty() => _routeDirty = true;

    internal void SetNormalGraph(NormalFloorGraphSnapshot? graph)
    {
        if (ReferenceEquals(_normalGraph, graph))
            return;
        _normalGraph = graph;
        _geometryGeneration = -1;   // legal rooms re-derive on next build
        _modelFloor = null;         // and the model shares that universe
    }

    /// <summary>The floor's live activity pair, sampled once by the
    /// controller after ingest — a passage/combat transition invalidates
    /// the route immediately instead of waiting for the next cadence.</summary>
    internal void SetActivity(in FloorActivity activity)
    {
        if (_activity == activity)
            return;
        _activity = activity;
        _routeDirty = true;
    }

    /// <summary>Floor-level geometry cache: anchors + legal rooms + all
    /// pairwise room paths + the distance estimator, built once per floor
    /// generation. Saved paths live in a dictionary — no dd pointer is
    /// captured by the cached estimator.</summary>
    private unsafe void EnsureGeometry(InstanceContentDeepDungeon* dd, FloorKnowledge fk)
    {
        if (_geometryGeneration == fk.Generation && _dist != null)
            return;
        _geometryGeneration = fk.Generation;
        _anchors.Clear();
        _rooms.Clear();
        // Geometry universe = this floor's actual room graph — the layout
        // centers table spans more rooms than the generated floor has.
        foreach (var r in _normalGraph!.ReachableRooms)
            if (MapPos.TryGetRoomCenter(dd, r, out var c))
            {
                _anchors.Add((r, c));
                _rooms.Add(r);
            }
        _roomPaths.Clear();
        foreach (var a in _rooms)
            foreach (var b in _rooms)
            {
                if (a == b)
                    continue;
                var route = new List<int>();
                _roomPaths[(a, b)] = RoomGraph.TryBuildRoomRoute(dd, a, b, route)
                    ? route : null;
            }
        _dist = new RoomSitePlanner.RoomDistanceEstimator(_anchors,
            (a, b) => _roomPaths.TryGetValue((a, b), out var p) ? p : null);
    }

    internal static string SiteKey(in ObjectiveSite s) =>
        s.Key.Length > 0
            ? s.Key
            : $"{(int)s.Kind}:{(int)(s.Position.X * 2f)}:{(int)(s.Position.Z * 2f)}";

    /// <summary>Per-frame decision tick. Evidence is injected — the driver
    /// never owns an observation scan.</summary>
    internal unsafe void TickDecision(
        InstanceContentDeepDungeon* dd,
        Vector3 playerPos,
        FloorObjectEvidenceSnapshot? evidence)
    {
        Active = this;
        if (dd == null || evidence?.PlayerPosition == null)
        {
            Status = evidence == null ? "no-evidence" : "no-dd";
            Head = null;
            return;
        }
        if (_knowledge != null)
        {
            TickKnowledgeDecision(dd, playerPos, evidence);
            return;
        }
        TickLegacyDecision(dd, playerPos, evidence);
    }

    // ==================== knowledge-driven normal path ====================

    private unsafe void TickKnowledgeDecision(
        InstanceContentDeepDungeon* dd,
        Vector3 playerPos,
        FloorObjectEvidenceSnapshot evidence)
    {
        var fk = _knowledge!.Floor;
        if (fk == null)
        {
            Status = "no-floor-knowledge";
            Head = null;
            return;
        }
        _lastTickUtc = DateTime.UtcNow;

        var nowMs = Environment.TickCount64;
        var playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        var dirty = _routeDirty
            || fk.Revision != _seenKnowledgeRevision
            || _knowledge.InventoryRevision != _seenInventoryRevision
            || evidence.Version != _seenEvidenceVersion
            || nowMs - _lastRebuildMs > 1500;
        if (dirty)
        {
            _lastRebuildMs = nowMs;
            RebuildWorkRoute(dd, evidence, fk, playerPos, playerRoom);
        }
        if (_routeWork.Count == 0)
        {
            // Non-route work may still exist (quota/passage sites are
            // route-independent commitments).
            ObjectiveSite? lone = null;
            foreach (var s in _sites)
                if (s.Status == ObjectiveSiteStatus.Open &&
                    s.Kind is ObjectiveSiteKind.Quota or ObjectiveSiteKind.Passage)
                { lone = s; break; }
            CommitHead(lone);
            Status = lone.HasValue ? $"head={lone.Value.Kind} route=empty" : "work-resolved";
            EmitRouteRecord(fk);
            return;
        }

        // Head = the first item on the selected route. The committed head
        // survives only while the pipeline reports a meaningful action in
        // progress (probe dwell awaiting postcondition, dispatched chest
        // interaction) — plain travel does not protect it, so work
        // discovered on route can become head without thrashing mid-
        // interaction. Work no longer needed is removed regardless.
        // Quota preempts only to finish combat before an open passage —
        // incidental combat alongside useful room work does not force it.
        ObjectiveSite? head = _routeWork[0].Site;
        // No passage-open quota preemption here: quota exists only when no
        // useful work remains, so the route's useful work keeps its head
        // while combat runs as a side channel; quota reaches head only
        // when the route is empty (the lone-head path above).
        if (CommitProtected && _committedKey.Length > 0)
        {
            for (var i = 0; i < _routeWork.Count; i++)
                if (SiteKey(_routeWork[i].Site) == _committedKey &&
                    _routeWork[i].Site.Status == ObjectiveSiteStatus.Open)
                {
                    head = _routeWork[i].Site;
                    break;
                }
        }
        CommitHead(head);
        EmitRouteRecord(fk);

        _tail.Clear();
        for (var i = 0; i < _routeWork.Count && _tail.Count < 8; i++)
        {
            var s = _routeWork[i].Site;
            if (SiteKey(s) != _committedKey)
                _tail.Add(s.Id);
        }
        Status = head.HasValue
            ? $"head={head.Value.Kind}@{head.Value.RoomIndex} route={_routeWork.Count} " +
              $"rate={(fk.ActiveHoardRate?.ToString("0.000") ?? fk.SelectionSource)}"
            : "route-empty";
    }

    /// <summary>site-route telemetry — emitted only after CommitHead so
    /// the record carries the real committed head and its work token,
    /// the actually selected/skipped keys, and the mode inputs.</summary>
    private void EmitRouteRecord(FloorKnowledge fk)
    {
        if (!_routeRebuilt)
            return;
        _routeRebuilt = false;
        var p = _workPolicy;
        _record?.Invoke("site-route", new
        {
            floor = fk.Floor,
            state = _routeState,
            source = _routeSource,
            rate = _routeRate,
            mode = $"hoard={p.SearchHoard} discovery={p.DiscoveryOnly} " +
                   $"harvest={p.HarvestChests}",
            token = !string.IsNullOrEmpty(_committedToken.Key)
                ? $"{_committedToken.Key}#{_committedToken.Attempt}"
                : null,
            head = Head is { } h ? $"{h.Kind}:{SiteKey(h)}" : null,
            route = _routeWork.Count,
            selected = SelectedKeys(),
            skipped = _routeSkipped,
            productive = _routeProductive,
            passageOpen = _routePassageOpen,
            resolved = _routeResolved,
            // pooled-profile telemetry: membership availability, the
            // deterministic unranked fill count and whether point
            // probabilities carry a real floor estimate.
            profileAvailable = _floorProfile?.Available == true,
            unrankedSlots = _model?.UnrankedSlotCount ?? 0,
            probabilityAvailable = _model?.ProbabilityAvailable == true,
        });
    }

    private void CommitHead(ObjectiveSite? head)
    {
        Head = head;
        if (head.HasValue)
        {
            var key = SiteKey(head.Value);
            if (key != _committedKey)
            {
                _committedKey = key;
                // Rebind the already-registered attempt for this floor+key
                // — a suspend/resume or route re-selection is NOT a fresh
                // execution; a new attempt exists only for first commit.
                var generation = _knowledge!.Floor?.Generation ?? 0;
                if (!_attempts.TryGetValue(key, out var attempt))
                    _attempts[key] = attempt = ++_nextAttempt;
                _committedToken = new SiteWorkToken(generation, key, attempt);
                _intent.CommittedSiteId = head.Value.Id;
                _intent.CommittedAtUtc = DateTime.UtcNow;
            }
        }
        else
        {
            _committedKey = "";
            _committedToken = default;
            _intent.CommittedSiteId = -1;
        }
    }

    private unsafe void RebuildWorkRoute(
        InstanceContentDeepDungeon* dd,
        FloorObjectEvidenceSnapshot evidence,
        FloorKnowledge fk,
        Vector3 playerPos,
        int playerRoom)
    {
        _routeDirty = false;
        _seenKnowledgeRevision = fk.Revision;
        _seenInventoryRevision = _knowledge!.InventoryRevision;
        _seenEvidenceVersion = evidence.Version;
        var policy = _policy?.Invoke() ?? default;
        _workPolicy = policy;

        // Floor geometry (anchors, legal rooms, pairwise paths, distance
        // estimator) is built once per floor generation, not per rebuild.
        EnsureGeometry(dd, fk);
        var dist = _dist!;
        List<int>? roomRoute(int a, int b) => dist.RouteBetween(a, b);

        // Exact reveal position through the existing resolver — not
        // GeneratePlan: only the state is needed here.
        var exact = fk.ExactHoardPosition;
        var exactRoom = fk.ExactHoardRoomIndex;
        if (exact.HasValue && exactRoom == null && dist.RoomOf(exact.Value) is var er0 && er0 >= 0)
            exactRoom = er0;

        // Cached calibrated candidate model — rebuilt only when the Sight
        // or completed-probe inputs change (or the floor's knowledge
        // object did), never just because the player/evidence moved.
        var rate = _knowledge!.ObservedHoardRate();
        fk.ActiveHoardRate = rate;
        var observedSight = fk.ObservedSightPositions;
        var spawnRoom = fk.SpawnRoom >= 0 ? fk.SpawnRoom : playerRoom;
        // Shared floor-pooled profile — built ONCE per pinned
        // catalog+floor before completion/Sight filtering, cached on the
        // driver so the structural mapping runs once, not per frame.
        if (_profileFloor != dd->Floor ||
            !ReferenceEquals(_profileCatalog, _map?.Catalog))
        {
            _floorProfile = _map?.Catalog != null
                ? RoomSitePlanner.BuildFloorProfile(_map.Catalog, dd->Floor)
                : null;
            _profileFloor = dd->Floor;
            _profileCatalog = _map?.Catalog;
        }
        if (_modelFloor != fk || _modelSightCount != observedSight.Count ||
            _modelProbeCount != fk.CompletedProbeCount)
        {
            _model = RoomSitePlanner.BuildCandidateModel(
                _map?.Catalog, _map?.HoardYield, dd->ActiveLayoutIndex,
                dd->Floor, spawnRoom, _rooms, observedSight,
                (room, pos) => fk.GetProbeKey(room, pos), fk.IsProbeCompleted,
                _floorProfile);
            _modelFloor = fk;
            _modelSightCount = observedSight.Count;
            _modelProbeCount = fk.CompletedProbeCount;
        }
        var model = _model!;
        // Model availability alone gates model selection — the observed
        // current-run rate is telemetry, never an admission threshold.
        var useModelSelection = model.ModelAvailable;

        DeepDungeonFloorsetTracker.TryGetCurrentFloorsetState(
            dd->Floor, out var floorsetState);
        var opportunity = FloorsetHoardDistributionPolicy.Decide(floorsetState, dd->Floor);
        var state = PlanGenerator.ResolveHoardEvidenceState(new FloorPlanSnapshot
        {
            BandedEnabled = policy.SearchHoard,
            HoardOpenedThisFloor = fk.HoardAcquired,
            ChatSaysNoHoard = fk.ChatSaysNoHoard || fk.InheritedNoHoardInferred,
            InheritedNoHoardInferred = fk.InheritedNoHoardInferred,
            CachedHoardIndicatorRoomIndex = exactRoom,
            ChatSaysHoard = fk.ChatSaysHoard,
            UsedIntuitionThisFloor = fk.HasCurrentFloorIntuitionUse,
            IntuitionActive = fk.NativeIntuitionActive,
            FloorsetHoardOpportunity = opportunity,
        });
        // FloorsetDistributionExcluded is terminal no-search handling —
        // never masqueraded back as BlindSearch for cost selection.
        fk.EvidenceState = state;

        // ---- candidate pool: calibrated model OR the fixed fallback ----
        var candidates = new List<(int Room, string Key, Vector3 Pos, float P)>();
        var optionalProb = new Dictionary<string, double>();
        var candidatesOn = policy.SearchHoard && !fk.HoardAcquired &&
            !exact.HasValue && state == HoardEvidenceState.BlindSearch;
        if (candidatesOn)
        {
            if (useModelSelection)
            {
                // The shared floor-pooled initial set ∪ genuine Sight
                // directs is the admission; every model point still
                // contributes its pooled probability to route scoring.
                foreach (var pt in model.Points)
                {
                    optionalProb[pt.Key] = pt.Probability;
                    if (model.InitialProbeKeys.Contains(pt.Key) ||
                        model.DirectProbeKeys.Contains(pt.Key))
                        candidates.Add((pt.RoomIndex, pt.Key,
                            new Vector3(pt.Position.X, pt.Position.Y, pt.Position.Z),
                            (float)pt.Probability));
                }
                fk.SelectionSource = "floor-coverage-95";
            }
            else
            {
                // Fixed selection = the existing per-room K/Direct set with
                // Palantir fallback — never the full calibrated universe.
                fk.SelectionSource = "fallback";
                foreach (var room in _rooms)
                {
                    if (room == spawnRoom)
                        continue;
                    var added = false;
                    if (_map?.Catalog != null &&
                        _map.Catalog.TryGetRoom(dd->ActiveLayoutIndex, room, out var catRoom))
                    {
                        var plan = DetailedMapRoomCandidatePlanner.BuildPriorityOrder(
                            _map.Catalog, catRoom, dd->Floor, observedSight);
                        if (!plan.UsePalantirFallback)
                        {
                            added = true;
                            foreach (var c in plan.DirectCandidates)
                            {
                                var key = fk.GetProbeKey(room, c.Position);
                                candidates.Add((room, key,
                                    new Vector3(c.Position.X, c.Position.Y, c.Position.Z), 0f));
                            }
                            foreach (var c in plan.OrdinaryCandidates)
                            {
                                var key = fk.GetProbeKey(room, c.Position);
                                candidates.Add((room, key,
                                    new Vector3(c.Position.X, c.Position.Y, c.Position.Z), 0f));
                            }
                        }
                    }
                    if (!added)
                        foreach (var p in _palantir.GetCandidatePositionsForRoom(dd, room))
                            candidates.Add((room,
                                fk.GetProbeKey(room, new RawWorldPosition(p.X, p.Y, p.Z)),
                                p, 0f));
                }
            }
        }

        // ---- room intel work (purpose-scoped, never every room) ----
        // Only HOARD candidates and the Intuition-position search are
        // excluded from the spawn room (spawn-no-hoard rule);
        // harvest intel legitimately includes it.
        var intel = new List<(int Room, Vector3 Pos, string Purpose)>();
        void AddIntel(RoomIntelPurpose purpose, bool excludeSpawn)
        {
            var name = purpose.ToString();
            foreach (var (room, pos) in _anchors)
            {
                if ((excludeSpawn && room == spawnRoom) ||
                    fk.IsIntelCompleted(room, purpose))
                    continue;
                intel.Add((room, pos, name));
            }
        }
        if (!fk.HoardAcquired &&
            state is HoardEvidenceState.IntuitionWaitingForIndicator
                or HoardEvidenceState.IntuitionActiveUnconfirmed)
            AddIntel(RoomIntelPurpose.IntuitionPosition, true);
        if (policy.HarvestChests)
            AddIntel(RoomIntelPurpose.Discovery, false);

        // ---- produce the legal possibility pool ----
        var noHoardTerminal = fk.HoardAcquired ||
            state is HoardEvidenceState.IntuitionNoHoard
                or HoardEvidenceState.Disabled or HoardEvidenceState.AlreadyOpened
                or HoardEvidenceState.FloorsetMaxed;
        var filter = new ObjectiveSiteSource.ObjectiveSiteFilter(
            OpenBronze: policy.OpenBronze,
            OpenSilver: policy.OpenSilver,
            OpenGold: policy.OpenGold,
            // Acquired/NoHoard also filters LIVE lingering indicator sites,
            // not only the physical banded chest work.
            Banded: !policy.DiscoveryOnly &&
                (policy.SearchHoard || policy.HarvestChests) && !noHoardTerminal,
            HuntMobs: true, ProbeRooms: true, Exit: true);
        var produced = ObjectiveSiteSource.Produce(
            evidence, filter, playerRoom, spawnRoom, _anchors, candidates, intel);
        // Raw live indicator sites are a debug/legacy affordance: on the
        // normal path the single cached exact Hoard work from knowledge is
        // the one job at that point — no ind:* + hoard:* duplicates.
        produced.RemoveAll(s => s.Kind == ObjectiveSiteKind.HoardIndicator);

        // Knowledge-observed chests survive object unload: emit work for
        // them even when the entity is not in the live snapshot — the
        // cached position navigates, live evidence confirms the open.
        if (!policy.DiscoveryOnly)
            foreach (var c in fk.ObservedChests)
            {
                if (fk.IsChestCompleted(c.EntityId))
                    continue;
                var kind = (FloorChestKind)c.Kind;
                var allowed = kind switch
                {
                    FloorChestKind.Gold => policy.OpenGold,
                    FloorChestKind.Silver => policy.OpenSilver,
                    FloorChestKind.Banded => filter.Banded,
                    _ => policy.OpenBronze,
                };
                if (!allowed)
                    continue;
                var live = evidence.Available &&
                    evidence.Chests.Any(ch => ch.Object.EntityId == c.EntityId);
                if (live)
                    continue;   // the live evidence already produced it
                produced.Add(new ObjectiveSite(
                    -3, ObjectiveSiteKind.ConfirmedChest, c.RoomIndex, c.Position,
                    ObjectiveSiteSource.RewardOf(ObjectiveSiteKind.ConfirmedChest, kind),
                    3f, ObjectiveSiteStatus.Open,
                    Chest: kind, Key: $"chest:{c.EntityId}"));
            }

        // Explicit policy-skipped chests (overcap) are legal but no longer
        // admitted this floor — filtered AFTER live AND remembered chest
        // generation, BEFORE admission/required counts; never recorded as
        // completed.
        if (_policySkippedChestKeys.Count > 0)
            produced.RemoveAll(s =>
                s.Kind == ObjectiveSiteKind.ConfirmedChest &&
                s.Key is { } ck && _policySkippedChestKeys.Contains(ck));

        // Exact-known hoard: a destination requirement that bypasses
        // optional candidate selection entirely — until its coffer exists,
        // when the physical Banded chest work replaces it.
        bool BandedNear(Vector3 pos) =>
            fk.ObservedChests.Any(c => c.Kind == (int)FloorChestKind.Banded &&
                Vector3.Distance(c.Position, pos) < 4f) ||
            (evidence.Available &&
             Runtime.Search.BandedChestLocator.TryFindNearestAround(
                 evidence, pos, 4f, out var found) && found.HasValue);
        if (exact.HasValue && !fk.HoardAcquired && policy.SearchHoard &&
            !BandedNear(exact.Value))
            produced.Add(new ObjectiveSite(
                -2, ObjectiveSiteKind.Hoard, exactRoom ?? -1, exact.Value,
                1f, 3f, ObjectiveSiteStatus.Open,
                Key: $"hoard:{exact.Value.X:0.###}:{exact.Value.Y:0.###}:{exact.Value.Z:0.###}"));

        // Failed work keys are legal but no longer admitted this floor —
        // excluded AFTER live/remembered/exact generation, BEFORE the
        // completion merge and admission/required counts; never recorded
        // as completed.
        ExcludeFailedWork(produced);

        // Status merge: completion truth lives in knowledge, not in the
        // previous pool, so an omitted-then-reproduced site cannot revive
        // (no permanent Retryable carryover on the normal path).
        for (var i = 0; i < produced.Count; i++)
        {
            var s = produced[i];
            var key = SiteKey(s);
            if (s.Kind == ObjectiveSiteKind.Candidate && fk.IsProbeCompleted(key))
                produced[i] = s with { Status = ObjectiveSiteStatus.Done, Reason = "probed" };
            else if (s.Kind == ObjectiveSiteKind.RoomProbe &&
                     TryIntelPurpose(key, out var pr, out var room) &&
                     fk.IsIntelCompleted(room, pr))
                produced[i] = s with { Status = ObjectiveSiteStatus.Done, Reason = "intel" };
            else if (s.Kind == ObjectiveSiteKind.ConfirmedChest &&
                     TryChestEntity(key, out var entity) && fk.IsChestCompleted(entity))
                produced[i] = s with { Status = ObjectiveSiteStatus.Done, Reason = "opened" };
            else if (s.Kind is ObjectiveSiteKind.Hoard or ObjectiveSiteKind.HoardIndicator &&
                     noHoardTerminal)
                produced[i] = s with { Status = ObjectiveSiteStatus.Done, Reason = "hoard-resolved" };
            else if (s.Kind == ObjectiveSiteKind.HoardIndicator && BandedNear(s.Position))
                // The point's coffer exists — the physical Banded chest
                // work replaces indicator/probe work at the same point.
                produced[i] = s with { Status = ObjectiveSiteStatus.Done, Reason = "revealed" };
        }

        // Authoritative activity — the captured normal-decision sample,
        // not an independent Service.Condition read.
        var passageOpen = _activity.PassageOpen;
        _routePassageOpen = passageOpen;
        var inCombatNow = _activity.InCombat;
        var passageRoom = RoomGraph.GetPassageRoomIndex(dd);
        var passagePos = PassageLocator.TryGetPassageActorPosition(evidence, out var observedPassage)
            ? observedPassage
            : (MapPos.TryGetRoomCenter(dd, passageRoom, out var pc) ? pc : playerPos);

        // ---- required goal work vs optional ordinary chests ----
        // Required: exact reveal work, purpose intel, and the chests the
        // MODE makes required — Banded and harvest-mode chests.
        // Ordinary chests are never a
        // destination — admitted only inside rooms already on the route.
        var requiredSites = new List<ObjectiveSite>();
        var chestSites = new List<ObjectiveSite>();
        var candidateSites = new List<ObjectiveSite>();
        foreach (var s in produced)
        {
            if (s.Status != ObjectiveSiteStatus.Open)
                continue;
            switch (s.Kind)
            {
                case ObjectiveSiteKind.Hoard:
                case ObjectiveSiteKind.HoardIndicator:
                case ObjectiveSiteKind.RoomProbe:
                    requiredSites.Add(s);
                    break;
                case ObjectiveSiteKind.ConfirmedChest:
                    if (s.Chest == FloorChestKind.Banded || policy.HarvestChests)
                        requiredSites.Add(s);
                    else
                        chestSites.Add(s);
                    break;
                case ObjectiveSiteKind.Candidate:
                    candidateSites.Add(s);
                    break;
            }
        }

        Vector3 CenterOf(int room) =>
            MapPos.TryGetRoomCenter(dd, room, out var c) ? c : playerPos;

        // ---- whole-room route ordering ----
        // candidateSites is already the admitted set — the model pool was
        // restricted to the initial-policy + Sight-direct keys at produce
        // time and the fallback produces the fixed per-room set. Required
        // and roomless authoritative work never disappears.
        var selectedCandidates = candidateSites;

        var workByRoom = new Dictionary<int, List<ObjectiveSite>>();
        var roomlessRequired = new List<ObjectiveSite>();
        void AddWork(in ObjectiveSite s)
        {
            if (s.RoomIndex < 0) { roomlessRequired.Add(s); return; }
            if (!workByRoom.TryGetValue(s.RoomIndex, out var list))
                workByRoom[s.RoomIndex] = list = new();
            list.Add(s);
        }
        foreach (var s in requiredSites) AddWork(s);
        foreach (var s in selectedCandidates) AddWork(s);

        var chestByRoom = new Dictionary<int, List<ObjectiveSite>>();
        foreach (var c in chestSites)
            if (c.RoomIndex >= 0)
            {
                if (!chestByRoom.TryGetValue(c.RoomIndex, out var list))
                    chestByRoom[c.RoomIndex] = list = new();
                list.Add(c);
            }

        // Per-room interior preplanned ONCE per rebuild: cheapest
        // insertion evaluated by the expected-search score over the
        // room's work AND ordinary chests; the anchor is the player
        // position in the current room, the room center elsewhere.
        List<ObjectiveSite> OrderRoomItems(int room, List<ObjectiveSite> items)
        {
            var anchor = room == playerRoom ? playerPos : CenterOf(room);
            var order = new List<ObjectiveSite>(items.Count);
            var pool = items.OrderBy(x => x.Key ?? string.Empty,
                StringComparer.Ordinal).ToList();
            foreach (var s in pool)
            {
                var bestPos = order.Count;
                var bestScore = double.MaxValue;
                for (var i = 0; i <= order.Count; i++)
                {
                    var trial = new List<ObjectiveSite>(order.Count + 1);
                    trial.AddRange(order);
                    trial.Insert(i, s);
                    var sc = RoomSitePlanner.ExpectedSearchSeconds(
                        anchor, trial, (a, b) => dist.Meters(a, b), optionalProb);
                    if (sc < bestScore) { bestScore = sc; bestPos = i; }
                }
                order.Insert(bestPos, s);
            }
            return order;
        }
        var interior = new Dictionary<int, List<ObjectiveSite>>();
        List<ObjectiveSite> InteriorFor(int room)
        {
            if (!interior.TryGetValue(room, out var items))
            {
                var pool = new List<ObjectiveSite>();
                if (workByRoom.TryGetValue(room, out var w)) pool.AddRange(w);
                if (chestByRoom.TryGetValue(room, out var ch)) pool.AddRange(ch);
                interior[room] = items = pool.Count > 0
                    ? OrderRoomItems(room, pool) : pool;
            }
            return items;
        }
        var destRooms = new List<int>(workByRoom.Keys);
        destRooms.Sort();

        // Expand a room order along the real RoomDistanceEstimator paths:
        // every visited room is processed at its FIRST traversal —
        // corridor work and chests are served en route, never duplicated.
        List<int> ExpandVisit(IReadOnlyList<int> roomOrder)
        {
            var visit = new List<int>();
            var seen = new HashSet<int>();
            var from = playerRoom;
            foreach (var dest in roomOrder)
            {
                // A destination already served en route is skipped
                // WITHOUT moving the virtual cursor back to it — that
                // would admit chests on a leg the flat route never takes.
                if (seen.Contains(dest))
                    continue;
                var path = roomRoute(from, dest);
                if (path != null)
                    foreach (var r in path)
                        if (seen.Add(r)) visit.Add(r);
                if (seen.Add(dest)) visit.Add(dest);
                from = dest;
            }
            if (playerRoom >= 0 && seen.Add(playerRoom))
                visit.Insert(0, playerRoom);
            return visit;
        }
        List<ObjectiveSite> FlattenVisit(IReadOnlyList<int> visit)
        {
            var flat = new List<ObjectiveSite>();
            foreach (var room in visit)
                flat.AddRange(InteriorFor(room));
            return flat;
        }
        float RouteMeters(IReadOnlyList<ObjectiveSite> flat, Vector3 terminalPos)
        {
            var meters = 0f;
            var prev = playerPos;
            foreach (var s in flat)
            {
                meters += dist.Meters(prev, s.Position);
                prev = s.Position;
            }
            return meters + dist.Meters(prev, terminalPos);
        }
        Vector3 RouteTerminal(List<int> order) =>
            passageOpen ? passagePos
                : (order.Count > 0 ? CenterOf(order[^1]) : playerPos);

        // Destination-room ordering: full permutations up to 6 rooms
        // (720), nearest-room seed + bounded single-room relocations
        // above — computational bounds, not admission limits.
        List<int> bestOrder = new(destRooms);
        List<ObjectiveSite>? bestFlat = null;
        double bestScore = double.MaxValue;
        float bestMeters = float.MaxValue;
        var bestSignature = "";
        void Evaluate(List<int> order, out List<ObjectiveSite> flat,
            out double score, out float meters, out string signature)
        {
            flat = FlattenVisit(ExpandVisit(order));
            score = RoomSitePlanner.ExpectedSearchSeconds(
                playerPos, flat, (a, b) => dist.Meters(a, b), optionalProb);
            meters = RouteMeters(flat, RouteTerminal(order));
            signature = string.Join('>', flat.Select(s => s.Key));
        }
        bool Better(double score, float meters, string signature) =>
            score < bestScore - 1e-9 ||
            (Math.Abs(score - bestScore) <= 1e-9 &&
                (meters < bestMeters - 1e-6f ||
                 (Math.Abs(meters - bestMeters) <= 1e-6f &&
                  string.CompareOrdinal(signature, bestSignature) < 0)));
        void Consider(List<int> order)
        {
            Evaluate(order, out var flat, out var score, out var meters,
                out var signature);
            if (bestFlat == null || Better(score, meters, signature))
            {
                bestScore = score;
                bestMeters = meters;
                bestSignature = signature;
                bestOrder = new List<int>(order);
                bestFlat = flat;
            }
        }
        if (destRooms.Count <= 6)
        {
            void Permute(List<int> items, int k)
            {
                if (k == items.Count) { Consider(items); return; }
                for (var i = k; i < items.Count; i++)
                {
                    (items[k], items[i]) = (items[i], items[k]);
                    Permute(items, k + 1);
                    (items[k], items[i]) = (items[i], items[k]);
                }
            }
            Permute(new List<int>(destRooms), 0);
            if (destRooms.Count == 0)
                Consider(new List<int>());
        }
        else
        {
            var order = new List<int>();
            var rest = new List<int>(destRooms);
            var from = playerRoom;
            while (rest.Count > 0)
            {
                var bi = 0;
                var bd = int.MaxValue;
                for (var i = 0; i < rest.Count; i++)
                {
                    var d = NormalGraphDistance(from, rest[i]);
                    if (d < bd) { bd = d; bi = i; }
                }
                from = rest[bi];
                order.Add(rest[bi]);
                rest.RemoveAt(bi);
            }
            Consider(order);
            var improved = true;
            for (var pass = 0; improved && pass < destRooms.Count; pass++)
            {
                improved = false;
                for (var i = 0; i < order.Count; i++)
                    for (var j = 0; j < order.Count; j++)
                    {
                        if (i == j) continue;
                        var moved = order[i];
                        var trial = new List<int>(order);
                        trial.RemoveAt(i);
                        trial.Insert(j > i ? j - 1 : j, moved);
                        Evaluate(trial, out var flat2, out var sc2,
                            out var m2, out var sig2);
                        if (sc2 < bestScore - 1e-9 ||
                            (Math.Abs(sc2 - bestScore) <= 1e-9 &&
                             m2 < bestMeters - 1e-6f))
                        {
                            order = trial;
                            bestScore = sc2;
                            bestMeters = m2;
                            bestSignature = sig2;
                            bestOrder = new List<int>(trial);
                            bestFlat = flat2;
                            improved = true;
                        }
                    }
            }
        }
        var visit = ExpandVisit(bestOrder);
        var flatWork = bestFlat ?? new List<ObjectiveSite>();

        // Ordinary chests are admitted only on actual routes — they
        // never add a destination. With no work destinations, keep the
        // existing current-room to passage-room admission route; when
        // passage is open, the leg toward it admits chests en route.
        var ordinaryChestKeys = new HashSet<string>();
        foreach (var c in chestSites) ordinaryChestKeys.Add(SiteKey(c));
        var admittedChests = 0;
        foreach (var s in flatWork)
            if (s.Kind == ObjectiveSiteKind.ConfirmedChest &&
                ordinaryChestKeys.Contains(SiteKey(s)))
                admittedChests++;
        if (destRooms.Count == 0 && passageRoom >= 0)
        {
            var seen = new HashSet<int>(visit);
            var leg = new List<int>();
            foreach (var r in roomRoute(playerRoom, passageRoom) ??
                new List<int>())
                if (seen.Add(r)) leg.Add(r);
            if (seen.Add(passageRoom)) leg.Add(passageRoom);
            foreach (var room in leg)
            {
                if (!chestByRoom.ContainsKey(room)) continue;
                foreach (var c in InteriorFor(room))
                    if (c.Kind == ObjectiveSiteKind.ConfirmedChest)
                    { flatWork.Add(c); ++admittedChests; }
            }
        }
        else if (passageOpen && passageRoom >= 0 && visit.Count > 0)
        {
            var seen = new HashSet<int>(visit);
            var leg = new List<int>();
            foreach (var r in roomRoute(visit[^1], passageRoom) ??
                new List<int>())
                if (seen.Add(r)) leg.Add(r);
            if (seen.Add(passageRoom)) leg.Add(passageRoom);
            foreach (var room in leg)
            {
                if (!chestByRoom.ContainsKey(room)) continue;
                foreach (var c in InteriorFor(room))
                    if (c.Kind == ObjectiveSiteKind.ConfirmedChest)
                    { flatWork.Add(c); ++admittedChests; }
            }
        }
        flatWork.AddRange(roomlessRequired);

        // HOARD search obligation — separate from ordinary admitted work.
        // Factual terminal states and a discovered coffer resolve it;
        // pending/known-present/exact states never do; in blind search
        // "no selected hoard-oriented work" is a policy conclusion.
        var hoardWorkRemaining = selectedCandidates.Count;
        foreach (var s in requiredSites)
            if (s.Kind is ObjectiveSiteKind.Hoard or ObjectiveSiteKind.HoardIndicator ||
                (s.Kind == ObjectiveSiteKind.RoomProbe &&
                 s.Key.EndsWith(":IntuitionPosition", StringComparison.Ordinal)))
                hoardWorkRemaining++;
        var hoardResolved =
            fk.HoardAcquired ||
            (policy.DiscoveryOnly && fk.ObservedBandedPositions.Count > 0) ||
            state is HoardEvidenceState.IntuitionNoHoard
                or HoardEvidenceState.Disabled or HoardEvidenceState.AlreadyOpened
                or HoardEvidenceState.FloorsetMaxed
                or HoardEvidenceState.FloorsetDistributionExcluded ||
            (state == HoardEvidenceState.BlindSearch && hoardWorkRemaining == 0);
        fk.SearchObligationsResolved = hoardResolved;

        // Useful work = required work + selected candidates + admitted
        // chests — computed after real path/admission, never a baseline
        // guess. Quota exists only to open the way or finish combat when
        // NO useful work remains.
        var useful = requiredSites.Count + selectedCandidates.Count + admittedChests;
        if (useful == 0 && (!passageOpen || inCombatNow) && _filter.HuntMobs &&
            !_failedWorkKeys.Contains("quota"))
            produced.Add(new ObjectiveSite(
                -1, ObjectiveSiteKind.Quota, -1,
                ChasePositionProvider?.Invoke() ?? playerPos,
                ObjectiveSiteSource.RewardOf(ObjectiveSiteKind.Quota, 0), 10f,
                ObjectiveSiteStatus.Open, Key: "quota"));

        // Passage: open, out of combat, hoard resolved, and no required
        // work left — admitted on-route chests may precede it.
        var passId = -100;
        var passageSites = new List<ObjectiveSite>();
        if (passageOpen && !inCombatNow && hoardResolved &&
            requiredSites.Count == 0 && selectedCandidates.Count == 0)
        {
            if (evidence.PassageActors.Count > 0)
                foreach (var actor in evidence.PassageActors)
                {
                    if (!AdmitPassage(actor)) continue;
                    passageSites.Add(new ObjectiveSite(
                        passId--, ObjectiveSiteKind.Passage, -1, actor.Position,
                        ObjectiveSiteSource.RewardOf(ObjectiveSiteKind.Passage, 0), 4f,
                        ObjectiveSiteStatus.Open, Key: $"pass:{actor.GameObjectId}", ActorId: actor.GameObjectId));
                }
            else if (passageRoom >= 0)
                passageSites.Add(new ObjectiveSite(
                    passId--, ObjectiveSiteKind.Passage, passageRoom, passagePos,
                    ObjectiveSiteSource.RewardOf(ObjectiveSiteKind.Passage, 0), 4f,
                    ObjectiveSiteStatus.Open, Key: $"pass:room:{passageRoom}"));
            ExcludeFailedWork(passageSites);
            produced.AddRange(passageSites);
        }

        _sites.Clear();
        _sites.AddRange(produced);

        // ---- flattened route assembly ----
        _routeWork.Clear();
        _lastScores.Clear();
        foreach (var s in flatWork)
        {
            var key = SiteKey(s);
            _routeWork.Add(new RoomSitePlanner.RouteWorkItem(s, 0f,
                optionalProb.TryGetValue(key, out var wprob) ? wprob : 0d));
        }
        // Admitted on-route chests order before actual passage traversal —
        // the passage site is the terminal item.
        foreach (var s in passageSites)
            _routeWork.Add(new RoomSitePlanner.RouteWorkItem(s, 0f, 0d));

        // Publish effective progress from the SELECTED work — quota and
        // passage are progression-only and never count as pending work.
        fk.PendingWorkCount = useful;
        fk.RequiredWorkRemaining = useful;
        fk.PendingIntelWork = requiredSites.Exists(s => s.Kind == ObjectiveSiteKind.RoomProbe);
        fk.PendingBandedWork = requiredSites.Exists(
            s => s.Kind == ObjectiveSiteKind.ConfirmedChest && s.Chest == FloorChestKind.Banded);
        fk.WorkDecisionInitialized = true;
        _routeRebuilt = true;
        _routeState = state.ToString();
        _routeSource = fk.SelectionSource;
        _routeRate = rate;
        _routeProductive = useful;
        _routePassageOpen = passageOpen;
        _routeResolved = hoardResolved;
        _routeSkipped = "";
        {
            var skipped = new List<string>();
            var routed = new HashSet<string>();
            foreach (var w in _routeWork)
                routed.Add(SiteKey(w.Site));
            foreach (var s in produced)
                if (s.Status == ObjectiveSiteStatus.Open &&
                    s.Kind is not ObjectiveSiteKind.Quota && !routed.Contains(SiteKey(s)))
                    skipped.Add(SiteKey(s));
            _routeSkipped = string.Join(",", skipped);
        }
    }

    private int NormalGraphDistance(int from, int to)
    {
        if (from < 0 || to < 0)
            return 999;
        if (from == to)
            return 0;
        var g = _normalGraph;
        return g != null ? g.RoomDistances[from, to] : 999;
    }

    private string SelectedKeys()
    {
        var keys = new List<string>(_routeWork.Count);
        foreach (var w in _routeWork)
            keys.Add(SiteKey(w.Site));
        return string.Join(",", keys);
    }

    private static void AddLegacyCandidate(
        List<(int Room, string Key, Vector3 Pos, float P)> candidates,
        int room, in RawWorldPosition pos)
    {
        var key = FloorKnowledge.ProbeKey(room, pos);
        foreach (var c in candidates)
            if (c.Key == key)
                return;
        candidates.Add((room, key, new Vector3(pos.X, pos.Y, pos.Z), 0f));
    }

    private static bool TryIntelPurpose(string key, out RoomIntelPurpose purpose, out int room)
    {
        purpose = default;
        room = -1;
        if (!key.StartsWith("probe:", StringComparison.Ordinal))
            return false;
        var parts = key.Split(':');
        return parts.Length == 3 && int.TryParse(parts[1], out room) &&
            Enum.TryParse(parts[2], out purpose);
    }

    private static bool TryChestEntity(string key, out ulong entity)
    {
        entity = 0;
        return key.StartsWith("chest:", StringComparison.Ordinal) &&
            ulong.TryParse(key.Substring(6), out entity);
    }

    /// <summary>Typed outcome for the captured work token — the only write
    /// path from execution back to work truth. A late result updates its
    /// original floor work, never a different head or floor.</summary>
    // Per-floor set of chest keys the EXISTING overcap policy explicitly
    // skipped — distinct from completion truth: the fact stays unopened in
    // knowledge, but the scheduler stops re-admitting the key this floor.
    // Cleared on new floor (ConfigureWork) and Dispose, never on rebuild.
    private readonly HashSet<string> _policySkippedChestKeys = new();
    // Per-floor set of work keys whose latest attempt FAILED — the fact
    // stays incomplete in knowledge, but the scheduler stops re-admitting
    // the key this floor. Same scope/clears as _policySkippedChestKeys.
    private readonly HashSet<string> _failedWorkKeys = new(StringComparer.Ordinal);

    private void ExcludeFailedWork(List<ObjectiveSite> sites)
    {
        if (_failedWorkKeys.Count > 0)
            sites.RemoveAll(s => _failedWorkKeys.Contains(SiteKey(s)));
    }

    private bool AdmitPassage(FloorObjectEvidence actor) =>
        NativePassageAvailability.IsAvailable(actor.BaseId, actor.SharedTimelineState) ||
        CommitProtected && Head is { Kind: ObjectiveSiteKind.Passage } head &&
        head.ActorId == actor.GameObjectId &&
        !NativePassageAvailability.IsRemoved(actor.BaseId, actor.SharedTimelineState);

    private void RejectWorkKey(string key)
    {
        _failedWorkKeys.Add(key);
        _sites.RemoveAll(s => SiteKey(s) == key);
        _routeWork.RemoveAll(w => SiteKey(w.Site) == key);
        _tail.Clear();
    }

    internal void ReportWorkOutcome(SiteWorkToken token, SiteWorkOutcome outcome, string? reason)
    {
        var fk = _knowledge?.Floor;
        if (fk == null || fk.Generation != token.FloorGeneration)
            return;
        if (KnowledgeDriven)
        {
            // Only the LATEST registered attempt for this key may write
            // outcome — a superseded attempt's late result cannot touch
            // newer work of the same key. A late result CAN still update
            // its original valid key while another key is head.
            if (!_attempts.TryGetValue(token.Key, out var latest) ||
                latest != token.Attempt)
                return;
        }
        for (var i = 0; i < _sites.Count; i++)
            if (SiteKey(_sites[i]) == token.Key)
            {
                var s = _sites[i];
                var status = outcome switch
                {
                    SiteWorkOutcome.Completed => ObjectiveSiteStatus.Done,
                    // Non-completion outcomes never establish completion;
                    // Failed keys are removed and excluded below — the next
                    // rebuild must NOT reselect them this floor.
                    _ => ObjectiveSiteStatus.Open,
                };
                _sites[i] = s with { Status = status, Reason = reason };
                break;
            }
        if (outcome == SiteWorkOutcome.Completed)
        {
            if (token.Key.StartsWith("cand:", StringComparison.Ordinal))
                fk.MarkProbeCompleted(token.Key);
            else if (TryIntelPurpose(token.Key, out var purpose, out var room))
                fk.MarkIntelCompleted(room, purpose);
            else if (TryChestEntity(token.Key, out var entity))
                fk.MarkChestCompleted(entity);
            // Hoard acquisition itself is the native count delta —
            // standing on the point is never "acquired".
        }
        // Explicit policy skip (existing overcap decision only): consume
        // the attempt AND stop re-admitting the chest this floor — without
        // recording factual completion. Ordinary cancellations and Failed
        // never add a key.
        if (outcome == SiteWorkOutcome.PolicySkipped &&
            reason == "chest-overcap-policy" &&
            token.Key.StartsWith("chest:", StringComparison.Ordinal))
            _policySkippedChestKeys.Add(token.Key);
        // PolicySkipped consumes the attempt without completion truth;
        // Failed also consumes it — the key is excluded for the rest of
        // this floor so the same work is never re-admitted or re-committed
        // by a route rebuild.
        if (outcome == SiteWorkOutcome.Failed)
            RejectWorkKey(token.Key);
        _routeDirty = true;
        if (Head is { } h && SiteKey(h) == token.Key)
        {
            ClearCommit();
            CommitProtected = false;
        }
    }

    /// <summary>Legacy-path policy skip: the boundary's identity guard
    /// already proved this caller owns the head — consume the head
    /// without completion truth and skip the chest for this floor.</summary>
    internal void ReportHeadPolicySkipped(string reason)
    {
        if (Head is { Kind: ObjectiveSiteKind.ConfirmedChest } h &&
            h.Key is { Length: > 0 } key)
        {
            _policySkippedChestKeys.Add(key);
            // Legacy head selection runs off the CURRENT _sites until the
            // next rebuild — remove the skipped site so it cannot be
            // reselected before then.
            _sites.RemoveAll(s => SiteKey(s) == key);
        }
        ClearCommit();
        _routeDirty = true;
    }

    // ==================== legacy path (controlled survey / debug) ====================

    private unsafe void TickLegacyDecision(
        InstanceContentDeepDungeon* dd,
        Vector3 playerPos,
        FloorObjectEvidenceSnapshot evidence)
    {
        var now = DateTime.UtcNow;
        var dt = (float)Math.Clamp((now - _lastTickUtc).TotalSeconds, 0, 1);
        _lastTickUtc = now;

        var nowMs = Environment.TickCount64;
        if (nowMs - _lastRebuildMs > 1500)
        {
            _lastRebuildMs = nowMs;
            RebuildSitesLegacy(dd, evidence);
        }
        if (_sites.Count == 0)
        {
            Status = "pool-empty";
            Head = null;
            return;
        }

        var playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        EnsureHops(dd, playerRoom);
        ProcessTaskStates(playerPos, dt);

        ObjectiveSite? head = null;
        foreach (var s in _sites)
            if (SiteKey(s) == _committedKey && s.Status == ObjectiveSiteStatus.Open)
            {
                head = s;
                break;
            }

        float Detour(in ObjectiveSite s) => Vector3.Distance(playerPos, s.Position) / 6f;
        var headScore = head.HasValue
            ? ObjectiveScoring.Score(head.Value, Detour(head.Value))
            : 0f;

        ObjectiveSite? best = null;
        var bestScore = 0f;
        _lastScores.Clear();
        foreach (var s in _sites)
        {
            if (s.Status != ObjectiveSiteStatus.Open || !EvidenceConfirmed(s))
                continue;
            var sc = ObjectiveScoring.Score(s, Detour(s));
            _lastScores[SiteKey(s)] = sc;
            if (best == null || sc > bestScore
                || (sc == bestScore && s.Id < best.Value.Id))
            {
                best = s;
                bestScore = sc;
            }
        }
        if (best == null)
        {
            Status = "no-open-sites";
            Head = null;
            return;
        }

        var invested = head.HasValue
            ? DateTime.UtcNow - _intent.CommittedAtUtc
            : TimeSpan.Zero;
        var distToHead = head.HasValue
            ? Vector3.Distance(playerPos, head.Value.Position)
            : float.MaxValue;
        var headOnSite = head.HasValue
            && _dwellSec.TryGetValue(SiteKey(head.Value), out var dw) && dw > 0f;
        var gate = ObjectiveScoring.Gate(
            _intent, best.Value, bestScore, head, headScore,
            invested,
            (DateTime.UtcNow - _intent.CommittedAtUtc).TotalSeconds,
            distToHead, _tunables, headOnSite);
        LastGate = gate;

        if (gate != HeadGateResult.Keep && SiteKey(best.Value) != _committedKey)
        {
            _committedKey = SiteKey(best.Value);
            _intent.CommittedSiteId = best.Value.Id;
            _intent.CommittedAtUtc = DateTime.UtcNow;
            head = best;
        }
        Head = head;
        _committedToken = head.HasValue
            ? new SiteWorkToken(0, _committedKey, 0)
            : default;

        ObjectiveSiteLedger.BuildTail(
            _sites, playerPos, (a, b) => Vector3.Distance(a, b) / 6f, _tail, _tunables);
        var open = 0;
        foreach (var s in _sites)
            if (s.Status == ObjectiveSiteStatus.Open)
                open++;
        Status = head.HasValue
            ? $"head={head.Value.Kind}@{head.Value.RoomIndex} gate={gate} " +
              $"open={open}/{_sites.Count} tail={_tail.Count}"
            : "gate-keep-no-head";
    }

    /// <summary>Legacy head-outcome shim: knowledge mode forwards to the
    /// typed token path.</summary>
    internal void ReportHeadOutcome(bool arrived, string? failureReason)
    {
        if (_knowledge != null)
        {
            if (!string.IsNullOrEmpty(_committedToken.Key))
                ReportWorkOutcome(_committedToken,
                    arrived ? SiteWorkOutcome.Completed : SiteWorkOutcome.Failed,
                    failureReason);
            return;
        }
        if (_committedKey.Length == 0)
            return;
        if (arrived
            && Head is { Kind: ObjectiveSiteKind.Quota or ObjectiveSiteKind.Candidate })
            return;
        MarkSite(_committedKey,
            arrived ? ObjectiveSiteStatus.Done : ObjectiveSiteStatus.Retryable,
            failureReason);
        _committedKey = "";
        _intent.CommittedSiteId = -1;
        Head = null;
    }

    /// <summary>Retire a site by key (e.g. evidence says it is done).</summary>
    internal void MarkSite(string key, ObjectiveSiteStatus status, string? reason = null)
    {
        for (var i = 0; i < _sites.Count; i++)
            if (SiteKey(_sites[i]) == key)
                _sites[i] = _sites[i] with { Status = status, Reason = reason };
    }

    private unsafe void RebuildSitesLegacy(
        InstanceContentDeepDungeon* dd, FloorObjectEvidenceSnapshot evidence)
    {
        var playerRoom = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if (dd->Floor != _lastFloor)
        {
            _lastFloor = dd->Floor;
            _spawnRoom = playerRoom;
            _dwellSec.Clear();
            _seen.Clear();
            _policySkippedChestKeys.Clear();   // skipped chests are per-floor
            _failedWorkKeys.Clear();
        }
        var anchors = new List<(int, Vector3)>();
        for (var r = 0; r < RoomGraph.MaxRooms; r++)
            if (MapPos.TryGetRoomCenter(dd, r, out var c))
                anchors.Add((r, c));
        // Palantir per-room candidates; catalog candidates share the
        // run-pinned snapshot when bound, else the live manager (debug).
        var catalog = _map?.Catalog ?? DetailedMapCatalogManager.Active?.CurrentCatalog;
        var observedTraps = new List<RawWorldPosition>();
        foreach (var ind in evidence.SightTrapIndicators)
            observedTraps.Add(new RawWorldPosition(
                ind.Position.X, ind.Position.Y, ind.Position.Z));
        var candidates = new List<(int Room, string Key, Vector3 Pos, float P)>();
        foreach (var (room, _) in anchors)
        {
            var usedCatalog = false;
            if (catalog != null
                && catalog.TryGetRoom(dd->ActiveLayoutIndex, room, out var catRoom))
            {
                var plan = DetailedMapRoomCandidatePlanner.BuildPriorityOrder(
                    catalog, catRoom, dd->Floor, observedTraps);
                if (!plan.UsePalantirFallback)
                {
                    usedCatalog = true;
                    foreach (var c in plan.DirectCandidates)
                        AddLegacyCandidate(candidates, room, c.Position);
                    foreach (var c in plan.OrdinaryCandidates)
                        AddLegacyCandidate(candidates, room, c.Position);
                }
            }
            if (!usedCatalog)
                foreach (var p in _palantir.GetCandidatePositionsForRoom(dd, room))
                    candidates.Add((room,
                        FloorKnowledge.ProbeKey(room, new RawWorldPosition(p.X, p.Y, p.Z)),
                        p, 0.35f));
        }
        var passageOpen = evidence.PassageActors.Count > 0
            && ScanForTargetablePassage(evidence);
        var produced = ObjectiveSiteSource.Produce(
            evidence, _filter, playerRoom, _spawnRoom, anchors,
            candidates, Array.Empty<(int, Vector3, string)>());
        // Explicit policy-skipped chests never count as productive work —
        // filtered before quota/passage accounting, same as the normal path.
        if (_policySkippedChestKeys.Count > 0)
            produced.RemoveAll(s =>
                s.Kind == ObjectiveSiteKind.ConfirmedChest &&
                s.Key is { } ck && _policySkippedChestKeys.Contains(ck));

        var productive = 0;
        foreach (var s in produced)
            if (s.Kind is not ObjectiveSiteKind.Quota
                and not ObjectiveSiteKind.Passage
                and not ObjectiveSiteKind.Roam)
                productive++;
        var inCombatNow = Service.Condition[ConditionFlag.InCombat];
        var quotaNeeded = (!passageOpen && productive == 0)
            || (passageOpen && inCombatNow);
        if (quotaNeeded && _filter.HuntMobs)
            produced.Add(new ObjectiveSite(
                -1, ObjectiveSiteKind.Quota, -1,
                ChasePositionProvider?.Invoke()
                    ?? (anchors.Count > 0 ? anchors[0].Item2 : Vector3.Zero),
                ObjectiveSiteSource.RewardOf(ObjectiveSiteKind.Quota, 0), 10f,
                ObjectiveSiteStatus.Open, Key: "quota"));

        var passId = -100;
        if (_filter.Exit && passageOpen && !inCombatNow && productive == 0)
            foreach (var actor in evidence.PassageActors)
            {
                if (!AdmitPassage(actor)) continue;
                produced.Add(new ObjectiveSite(
                    passId--, ObjectiveSiteKind.Passage, -1, actor.Position,
                    ObjectiveSiteSource.RewardOf(ObjectiveSiteKind.Passage, 0), 4f,
                    ObjectiveSiteStatus.Open, Key: $"pass:{actor.GameObjectId}", ActorId: actor.GameObjectId));
            }

        // Failed work keys are excluded before the legacy merge — the
        // fact stays incomplete but the key is not re-admitted this floor.
        ExcludeFailedWork(produced);

        var producedKeys = new HashSet<string>();
        foreach (var s in produced) producedKeys.Add(SiteKey(s));
        foreach (var k in new List<string>(_seen.Keys))
            if (!producedKeys.Contains(k)) _seen.Remove(k);
        foreach (var k in producedKeys)
            _seen[k] = _seen.TryGetValue(k, out var n) ? n + 1 : 1;

        var old = new Dictionary<string, ObjectiveSite>();
        foreach (var s in _sites)
            old[SiteKey(s)] = s;
        _sites.Clear();
        foreach (var s in produced)
            _sites.Add(
                old.TryGetValue(SiteKey(s), out var prev)
                    ? s with { Status = prev.Status, Reason = prev.Reason }
                    : s);
        PruneSideTables();
    }

    private static bool ScanForTargetablePassage(FloorObjectEvidenceSnapshot evidence)
    {
        foreach (var a in evidence.PassageActors)
            if (a.IsTargetable && NativePassageAvailability.IsAvailable(a.BaseId, a.SharedTimelineState))
                return true;
        return false;
    }

    /// <summary>Task-state pass (legacy path only): candidate dwell at the
    /// driver's radius, retryable revive on proximity.</summary>
    private void ProcessTaskStates(Vector3 playerPos, float dt)
    {
        for (var i = 0; i < _sites.Count; i++)
        {
            var s = _sites[i];
            if (s.Status != ObjectiveSiteStatus.Open
                && s.Status != ObjectiveSiteStatus.Retryable)
                continue;
            var key = SiteKey(s);
            var dist = Vector3.Distance(playerPos, s.Position);
            switch (s.Kind)
            {
                case ObjectiveSiteKind.Candidate:
                    if (dist <= DwellRadiusM && s.Status == ObjectiveSiteStatus.Open)
                    {
                        var d = _dwellSec.TryGetValue(key, out var acc) ? acc + dt : dt;
                        if (d >= DwellTargetSec)
                            _sites[i] = s with
                            {
                                Status = ObjectiveSiteStatus.Done,
                                Reason = "probed",
                            };
                        else
                            _dwellSec[key] = d;
                    }
                    break;
            }
            if (_sites[i].Status == ObjectiveSiteStatus.Retryable
                && dist <= RetryableReviveM)
                _sites[i] = s with
                {
                    Status = ObjectiveSiteStatus.Open,
                    Reason = null,
                };
        }
    }

    private unsafe void EnsureHops(InstanceContentDeepDungeon* dd, int playerRoom)
    {
        if (playerRoom == _hopSource)
            return;
        _hopSource = playerRoom;
        Array.Fill(_hops, -1);
        if (playerRoom < 0)
            return;
        Span<int> nb = stackalloc int[4];
        var queue = new Queue<int>();
        _hops[playerRoom] = 0;
        queue.Enqueue(playerRoom);
        while (queue.Count > 0)
        {
            var r = queue.Dequeue();
            RoomGraph.EnumerateNeighbors(dd, r, nb, out var n);
            for (var i = 0; i < n; i++)
                if (_hops[nb[i]] < 0)
                {
                    _hops[nb[i]] = _hops[r] + 1;
                    queue.Enqueue(nb[i]);
                }
        }
    }

    private void PruneSideTables()
    {
        var live = new HashSet<string>();
        foreach (var s in _sites)
            live.Add(SiteKey(s));
        foreach (var k in new List<string>(_dwellSec.Keys))
            if (!live.Contains(k)) _dwellSec.Remove(k);
    }

    public void Dispose()
    {
        if (Active == this)
            Active = null;
        _sites.Clear();
        _dwellSec.Clear();
        _routeWork.Clear();
        _policySkippedChestKeys.Clear();
        _failedWorkKeys.Clear();
        Head = null;
        _committedKey = "";
        _committedToken = default;
        _intent.CommittedSiteId = -1;
        Status = "disposed";
    }
}
