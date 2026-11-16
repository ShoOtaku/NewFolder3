using System;
using System.Collections.Generic;
using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Coord;

/// <summary>
/// A schedulable point of interest — the commitment unit of the site-pool
/// planner. Value lives at point granularity (v = p · r); commitment lives
/// at site granularity (a room's candidate cluster sweeps as one stop,
/// because the second candidate inside a room costs ~0 to check).
/// </summary>
internal enum ObjectiveSiteKind
{
    ConfirmedChest,   // seen chest (gold/silver/bronze) — p = 1
    Candidate,        // catalog/prior spawn point — p = posterior
    HoardIndicator,   // probe whose reward is information
    Hoard,            // confirmed hoard position
    Passage,          // floor exit
    Interactable,     // gated by a predicate (e.g. boss dead)
    Quota,            // synthetic kill-deficit site — position delegated
                      // to the combat chase supply; done = quota cleared
    Roam,             // pseudo-site: patrol stale rooms when the pool is dry
    RoomProbe,        // unexplored-room prior probe
}

internal enum ObjectiveSiteStatus
{
    Open,
    Committed,
    Done,
    Falsified,
    Unreachable,
    Retryable,   // not terminal — may become viable again
}

internal readonly record struct ObjectiveSite(
    int Id,
    ObjectiveSiteKind Kind,
    int RoomIndex,
    Vector3 Position,
    float Value,            // p · r — expected yield
    float TaskCost,         // seconds on-site (kill loot, interact, probe)
    ObjectiveSiteStatus Status,
    string? Reason = null,
    Runtime.Floor.FloorChestKind Chest = Runtime.Floor.FloorChestKind.Bronze,
    string Key = "",        // stable identity across pool rebuilds
    ulong ActorId = 0);     // exact actor owned by entity-bound work

/// <summary>
/// The intent: one committed head plus an advisory tail the evidence
/// stream may freely rewrite. The head never competes in argmax — a
/// challenger must clear the hysteresis gate to take it.
/// </summary>
internal sealed class ObjectiveIntent
{
    internal int CommittedSiteId = -1;
    internal readonly List<int> Tail = new();
    internal DateTime CommittedAtUtc;
}

/// <summary>What the gate reports about a head-swap attempt.</summary>
internal enum HeadGateResult
{
    HeadEmpty,          // no committed head — take the best site
    HeadFinished,       // head done/failed/falsified — must swap
    ChallengerWins,     // beat head × (1+ε) + sunk
    AuthorityOverride,  // authoritative evidence bypasses ε
    Keep,               // challenger did not clear the margin
}

internal static class ObjectiveScoring
{
    /// <summary>Marginal yield per marginal second.</summary>
    internal static float Score(in ObjectiveSite s, float detourSeconds)
    {
        var denom = detourSeconds + s.TaskCost;
        return denom <= 0.001f ? s.Value * 1000f : s.Value / denom;
    }

    /// <summary>
    /// Head-swap gate. ε margin on score, sunk penalty growing with time
    /// already invested in the head, a minimum dwell, an arrival lockout,
    /// and a categorical override for authoritative evidence.
    /// </summary>
    internal static HeadGateResult Gate(
        ObjectiveIntent intent,
        in ObjectiveSite challenger,
        float challengerScore,
        in ObjectiveSite? head,
        float headScore,
        TimeSpan invested,
        double secondsSinceCommit,
        float distToHead,
        ObjectiveTunables t,
        bool headOnSite = false)
    {
        if (head == null || intent.CommittedSiteId < 0)
            return HeadGateResult.HeadEmpty;
        if (head.Value.Status is ObjectiveSiteStatus.Done
            or ObjectiveSiteStatus.Falsified or ObjectiveSiteStatus.Unreachable)
            return HeadGateResult.HeadFinished;
        // On-site protection: a head mid-dwell (candidate evidence being
        // collected, interaction in flight) is not dislodged by ordinary
        // challengers — the work is evidence, abandoning it wastes it.
        if (headOnSite)
            return HeadGateResult.Keep;
        if (secondsSinceCommit < t.MinDwellSeconds)
            return HeadGateResult.Keep;
        if (distToHead < t.ArrivalLockoutMeters)
            return HeadGateResult.Keep;
        // Forward-looking switching cost only: the challenger's yield rate
        // must survive paying MinSwitchSeconds of switch time AND beat the
        // noise-floor margin (Value/second units on both sides — the old
        // score-vs-seconds comparison was dimensionally wrong).
        _ = invested; // retained for the gate signature; no longer priced
        var adjusted = challengerScore > 0f && challenger.Value > 0f
            ? challenger.Value / (challenger.Value / challengerScore + t.MinSwitchSeconds)
            : challengerScore;
        return adjusted > headScore * (1f + t.Epsilon)
            ? HeadGateResult.ChallengerWins
            : HeadGateResult.Keep;
    }
}

internal readonly record struct ObjectiveTunables(
    float Epsilon = 0.4f,
    double MinDwellSeconds = 5.0,
    float ArrivalLockoutMeters = 6f,
    float MinSwitchSeconds = 3f,
    float OnRouteDetourMeters = 8f,
    bool AuthorityOverridesEps = false,
    float Tau = 0.05f)
{
    // Explicit parameterless construction must carry the tuned defaults;
    // the implicit all-zero struct default is not a usable configuration.
    public ObjectiveTunables() : this(0.4f, 5.0, 6f, 3f, 8f, false, 0.05f) { }
}

/// <summary>
/// The site ledger — rebuilt from facts on every evidence version bump
/// (pure function, no incremental bookkeeping). Sites carry just enough
/// for scoring; positions in world space, cost in seconds.
/// </summary>
internal static class ObjectiveSiteLedger
{
    internal static List<ObjectiveSite> Rebuild(
        IReadOnlyList<ObjectiveSite> facts,
        Func<ObjectiveSite, bool> enabled)
    {
        var sites = new List<ObjectiveSite>(facts.Count);
        foreach (var f in facts)
            if (f.Status == ObjectiveSiteStatus.Open && enabled(f))
                sites.Add(f);
        return sites;
    }

    /// <summary>Greedy cheapest-insertion advisory tail, truncated at τ.</summary>
    internal static void BuildTail(
        IReadOnlyList<ObjectiveSite> open,
        Vector3 from,
        Func<Vector3, Vector3, float> detourSeconds,
        List<int> tail,
        ObjectiveTunables t,
        int maxLen = 8)
    {
        tail.Clear();
        var remaining = new List<ObjectiveSite>(open.Count);
        foreach (var s in open)
            if (s.Status == ObjectiveSiteStatus.Open)
                remaining.Add(s);
        var cur = from;
        while (remaining.Count > 0 && tail.Count < maxLen)
        {
            var best = -1; var bestScore = t.Tau; var bestId = int.MaxValue;
            for (var i = 0; i < remaining.Count; i++)
            {
                var s = remaining[i];
                var c = detourSeconds(cur, s.Position);
                var score = ObjectiveScoring.Score(s, c);
                if (score > bestScore
                    || (Math.Abs(score - bestScore) < 1e-6f && s.Id < bestId))
                {
                    best = i; bestScore = score; bestId = s.Id;
                }
            }
            if (best < 0)
                break;
            var pick = remaining[best];
            tail.Add(pick.Id);
            cur = pick.Position;
            remaining.RemoveAt(best);
        }
    }
}
