using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Runtime;

internal static class OutsideDutyNavigationTests
{
    public static void Run()
    {
        foreach (bool leavingDuty in new[] { false, true })
        {
            int starts = 0, stops = 0, cancels = 0;
            bool pending = false, running = false;
            using var navigator = new NavigatorVNavAdapter((_, _) =>
                { starts++; pending = true; return true; }, () => pending,
                () => { cancels++; pending = false; }, () => { stops++; running = false; });
            var now = DateTime.UtcNow;
            var navigation = new NavigationHelper(navigator, () => running, () => now,
                _ => { }, _ => { }, () => pending, () => false);
            // Use the real host tick without constructing game services. Any access
            // to the retired floor controller must fail instead of silently passing.
            var context = (RunContext)RuntimeHelpers.GetUninitializedObject(typeof(RunContext));
            SetField(context, "Duty", new DutyState());
            SetField(context, "Navigator", navigator);
            var host = (RunHost)RuntimeHelpers.GetUninitializedObject(typeof(RunHost));
            SetField(host, "_context", context);
            SetField(host, "_isFsdMode", true);
            SetField(host, "_inDutyControllersActive", leavingDuty);
            SetField(host, "_multiLoopDriver", new MultiLoopDriver(1, false));
            var scenario = new ExitNavigationScenario(navigation);
            SetField(host, "_attachedScenario", scenario);
            if (leavingDuty)
                navigator.PathfindAndMoveTo(new(100, 0, 0), false);

            host.Update(null!);
            int expectedStarts = leavingDuty ? 2 : 1;
            int expectedStops = leavingDuty ? 1 : 0;
            Check(pending && starts == expectedStarts && stops == expectedStops && cancels == expectedStops,
                "Duty-exit cleanup must cancel the old search once, before the scenario starts its rest-area route.");
            pending = false;
            running = true;
            for (int i = 0; i < 20; i++)
            {
                now = now.AddMilliseconds(100);
                host.Update(null!);
            }
            Check(running && starts == expectedStarts && stops == expectedStops && scenario.Updates == 21,
                "Outside-duty host ticks must preserve the scenario route without suspending the retired boss controller.");
        }
        Console.WriteLine("PASS outside-duty navigation: one-time duty cleanup, async exit search survives host tick, active exit route preserved over 20 ticks.");
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ExitNavigationScenario(NavigationHelper navigation) : IScenario
    {
        public string Name => "Rest-area exit regression";
        public bool IsComplete => false;
        public bool ShouldLoop => false;
        public bool RequiresDutyCompletionEvent => false;
        public int Updates { get; private set; }
        public void Initialize(RunContext context) { }
        public void Update(IFramework framework)
        {
            Updates++;
            navigation.Navigate(new Vector3(20, 0, 0), Vector3.Zero);
        }
        public void Dispose() { }
    }
}
