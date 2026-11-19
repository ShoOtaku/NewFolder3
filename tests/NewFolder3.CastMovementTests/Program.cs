using System.Numerics;
using BossMod;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using MasterPlayer.MechanicRuntime;

var now = new DateTime(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);
var snapshot = new EncounterPlanningSnapshot(1283, 1, 0x4609, now, default, 14, true, false,
    default, new ArenaBoundsCircle(20), [], [], [], []);
bool Wait(EncounterPlanningSnapshot? state, float remaining = 1.5f, Vector2 position = default, bool casting = true) =>
    CastMovementPolicy.ShouldWait(casting, remaining, position, 100, 1283, now, state);
EncounterPlanningSnapshot Hazard(double seconds, float radius = 4) => snapshot with
{
    Hazards = [new(new SDCircle(default, radius), now.AddSeconds(seconds), EncounterHazardKind.Impact)]
};
Check(Wait(null) && Wait(snapshot), "Safe ordinary travel must wait for the cast");
Check(!Wait(snapshot, casting: false), "Completed/cancelled casts must release travel immediately");
Check(!Wait(Hazard(2)), "Imminent AOE must preempt the cast wait");
Check(Wait(Hazard(10)), "Distant future AOE should allow the current cast to finish");
Check(Wait(Hazard(2), position: new(10, 0)), "An AOE elsewhere must not interrupt this cast");
Check(!Wait(Hazard(4, 12), remaining: 2), "Deep AOE coverage must reserve escape time after the cast");
Check(Wait(Hazard(2) with { Territory = 816 }), "Wrong territory hints must not authorize breaking a cast");
Check(Wait(Hazard(2) with { CapturedAt = now.AddSeconds(-1) }), "Stale hints must not authorize breaking a cast");
var persistent = snapshot with { Hazards = [new(new SDCircle(default, 4), now.AddSeconds(-1), EncounterHazardKind.Persistent)] };
Check(!Wait(persistent), "Active persistent damage must preempt a cast wait");
Check(Wait(persistent with { Hazards = [persistent.Hazards[0] with { ExpiresAt = now.AddMilliseconds(-1) }] }), "Expired danger must allow cast protection");
var chase = new EncounterChaser(100, default, default, 4, 3.5f, 3.5f, 8, now.AddSeconds(3), .6);
Check(!Wait(snapshot with { ChasingHazards = [chase] }), "A live chase targeting this player requires continuous movement");
Check(Wait(snapshot with { ChasingHazards = [chase with { TargetId = 101 }] }), "Another player's chase must not force movement");
var hints = new EncounterHintState([], default, AIHints.SpecialMode.Normal, default, default, false, float.MaxValue, 0, 0, 0, 0, false, default);
Check(!Wait(snapshot with { Hints = hints with { ForceCancelCast = true } }), "Explicit cast cancellation hint must win");
Check(!Wait(snapshot with { Hints = hints with { KeepMoving = true } }), "Forced continuous movement must win");
Check(!Wait(snapshot with { Hints = hints with { MaxCastTime = .5f } }), "Module cast deadline must win");
Console.WriteLine("PASS cast safety: ordinary travel, cast end, imminent/future/remote AOE, escape lead, expired/stale/foreign hints, chase and forced movement.");

var navigator = new FakeNavigator();
bool castingNow = true;
var helper = new NavigationHelper(navigator, () => navigator.Running, () => now, _ => { }, _ => { }, () => false, () => castingNow);
var destination = new Vector3(20, 0, 0);
Check(helper.Navigate(destination, Vector3.Zero) == NavigationState.Moving && navigator.Starts == 0, "A new route must not start during a cast");
for (int i = 0; i < 30; i++)
{
    now = now.AddSeconds(1);
    Check(helper.Navigate(destination, Vector3.Zero) == NavigationState.Moving, "Cast wait must not be reported as path failure");
}
Check(navigator.Cancels == 1 && navigator.Repaths == 0 && helper.StuckRetryCount == 0 && helper.NavigationIssueCount == 0,
    "Cast waiting must not repeatedly cancel or consume stuck/retry budgets");
castingNow = false;
helper.Navigate(destination, Vector3.Zero);
Check(navigator.Starts == 1, "Route must start as soon as the cast ends");
castingNow = true;
helper.Navigate(destination, Vector3.Zero);
Check(!navigator.Running && navigator.Cancels == 2, "A cast starting during travel must cancel the existing route");
now = now.AddSeconds(20);
helper.Navigate(destination, Vector3.Zero);
castingNow = false;
helper.Navigate(destination, Vector3.Zero);
Check(navigator.Starts == 2 && navigator.Repaths == 0 && helper.NavigationIssueCount == 0, "Resuming after a long cast must avoid a false stuck/retry episode");
Console.WriteLine("PASS VNav cast wait: no early dispatch, one cancellation per wait, preserved retry budget, running-path stop and automatic resume.");

void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

foreach (var boundary in new[] { "combat start", "arrival", "pause", "mechanic handoff", "dispose" })
{
    bool pending = false, running = false;
    var events = new List<string>();
    using var adapter = new NavigatorVNavAdapter((_, _) => { pending = true; return true; },
        () => pending, () => { events.Add("cancel"); pending = false; }, () => { events.Add("stop"); running = false; });
    var nav = new NavigationHelper(adapter, () => running, () => now, _ => { }, _ => { }, () => pending, () => false);
    nav.Navigate(new(20, 0, 0), Vector3.Zero);
    if (boundary == "dispose") adapter.Dispose(); else nav.Cancel();
    if (pending) running = true; // Simulate an async result becoming ready after cancellation.
    Check(!running && events.SequenceEqual(new[] { "cancel", "stop" }), $"{boundary} allowed a late route to restart");
    nav.Cancel();
    Check(events.Count == 2, "An idle adapter stopped movement belonging to a later owner");
}
Console.WriteLine("PASS boss navigation cancellation: pending search cancelled before path stop; no late restart or repeated owner stop.");
foreach (var range in new[] { 3f, 25f })
{
    var entry = new Vector3(-300, 0, -290);
    var spawn = new Vector3(-300, 0, -310);
    var point = DeepDungeon.Fsd.Core.BossApproachPosition.Resolve(entry, spawn, range);
    Check(point is { Z: > -308f and < -307f }, "PT70 approach still queues the edge/boss centre");
    Check(Vector3.Distance(point!.Value, spawn) + .1f < range, "Approach point lacks attack/stopping clearance");
}
Check(DeepDungeon.Fsd.Core.BossApproachPosition.Resolve(new(float.NaN,0,0), default, 3) == null, "Invalid boss approach accepted");
Console.WriteLine("PASS boss approach: melee/ranged stop on entry side before PT70 edge, with finite input and tolerance checks.");

int bossFloors = 0;
for (uint dungeon = 1; dungeon <= 4; ++dungeon)
    for (int floor = 1; floor <= (dungeon == 1 ? 200 : 100); ++floor)
        if (DeepDungeon.Fsd.Runtime.DeepDungeonFloorClassifier.Classify(dungeon, floor) == DeepDungeon.Fsd.Runtime.DeepDungeonFloorKind.Boss)
        {
            ++bossFloors;
            foreach (var side in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
            {
                var boss = new Vector3(-300, 0, -300);
                var player = boss + side * 20;
                var point = DeepDungeon.Fsd.Core.BossApproachPosition.Resolve(player, boss, 3)!.Value;
                Check(Vector3.Dot(point - boss, side) > 2 && Vector3.Distance(point, boss) < 3,
                    $"Dungeon {dungeon} floor {floor} endpoint passed through the boss");
            }
        }
Check(bossFloors == 48, "Boss-floor coverage drifted");
Console.WriteLine($"PASS shared approach matrix: {bossFloors} boss floors across all four deep dungeons, four entry directions each.");

OutsideDutyNavigationTests.Run();
