using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

var now = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
var events = new List<string>();
var recovery = new RoomEscapeRecovery((name, _) => events.Add(name));
IReadOnlyList<(int Room, Vector3 Center)> Rooms() => [(0, default), (1, new(15, 0, 0)), (5, new(0, 0, 20))];
bool Tick(Vector3 position = default, int room = 0, bool moving = true, bool allowed = true)
{
    now = now.AddSeconds(.5);
    return recovery.Update(now, position, room, moving, allowed, Rooms);
}
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
void Trigger()
{
    recovery.Reset();
    Tick();
    for (var i = 0; i < 30; i++) Tick(new(i % 2, 0, 0));
    Check(recovery.Target?.Room == 1, "Repeated small movements must select the nearest other connected room");
}

Tick();
for (var i = 0; i < 29; i++)
    Check(!Tick(new(i % 2, 0, 0)), "A single short local retry must not trigger a room escape");
Check(Tick() && recovery.Target?.Room == 1, "Three ineffective five-second windows must trigger a room escape");
for (var i = 0; i < 10; i++) Tick();
Check(recovery.Target?.Room == 1, "The escape destination must stay fixed while travelling");
Check(Tick(new(15, 0, 0)) && recovery.Target != null, "Position alone must not claim entry into a different room");
Check(!Tick(new(15, 0, 0), room: 1) && recovery.Target == null, "Observed arrival in the selected room must resume the original task");
Check(events.Contains("room-escape-ended"), "Escape arrival must be recorded");

recovery.Reset();
for (var i = 0; i < 180; i++)
    Check(!Tick(new(i * .5f, 0, 0)), "Genuine travel must reset the local stall window");
recovery.Reset();
for (var i = 0; i < 180; i++)
    Check(!Tick(moving: false), "Idle, interaction and arrival must not count as ineffective movement");
recovery.Reset();
for (var i = 0; i < 180; i++)
    Check(!Tick(allowed: false), "Combat, death and safety holds must not trigger a room escape");

Trigger();
recovery.Pause();
now = now.AddMinutes(3);
Tick();
Check(recovery.Target?.Room == 1, "Cast or external ownership pauses must not time out the escape");
for (var i = 0; i < 59; i++) Tick();
Check(recovery.Target?.Room == 1, "Room escape must allow its full held effort window");
Tick();
Check(recovery.Target?.Room == 5, "A timed-out room must give another connected room a chance");
for (var i = 0; i < 60; i++) Tick();
Check(recovery.Target == null, "Exhausted room choices must end the bounded escape");
for (var i = 0; i < 59; i++)
    Check(!Tick(), "A failed escape must cool down instead of repeatedly selecting the same room");

Trigger();
Check(!Tick(allowed: false) && recovery.Target == null, "Combat must immediately release room escape ownership");
recovery.Reset();
for (var i = 0; i < 32; i++)
{
    now = now.AddSeconds(.5);
    recovery.Update(now, default, 0, true, true, () => []);
}
Check(recovery.Target == null && events.Contains("room-escape-unavailable"), "Missing room connections must be handled without fabricated destinations");
Console.WriteLine("PASS room escape: local retry threshold, real progress, fixed destination, observed entry, safety/idle exclusions, pause, timeout alternatives, cooldown and no connected room.");
