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
