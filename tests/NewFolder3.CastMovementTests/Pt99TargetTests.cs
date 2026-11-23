using System.Reflection;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.Runtime.Combat;

internal static class Pt99TargetTests
{
    internal static void Run()
    {
        var names = new[] { "PluginInterface", "ClientState", "GameObjects", "TargetManager", "Log" };
        var properties = names.Select(n => typeof(Service).GetProperty(n, BindingFlags.Static | BindingFlags.NonPublic)!).ToArray();
        var originals = properties.Select(p => p.GetValue(null)).ToArray();
        var now = DateTime.UtcNow;
        var fresh = new PromeRadarMovementState { ApiVersion = 1, Ready = true, TerritoryId = 1290,
            CapturedAtUtc = now, Pt99TargetControlActive = true, Pt99TargetId = 1 };
        string json = JsonSerializer.Serialize(fresh);
        uint territory = 1290;
        bool available = true, dead = false, targetable = true;
        uint hp = 1000, oid = 0x48EA;
        var reads = 0;
        var warnings = 0;
        IBattleNpc target = TargetServiceProxy.Create<IBattleNpc>((name, _) => name switch
        {
            "get_BaseId" => oid, "get_IsDead" => dead, "get_IsTargetable" => targetable,
            "get_CurrentHp" => hp, "get_GameObjectId" => 1ul,
            _ => throw new NotSupportedException(name)
        });
        IGameObject? applied = null;
        var subscriber = TargetServiceProxy.Create<ICallGateSubscriber<string>>((name, _) => name switch
        {
            "get_HasFunction" => available,
            "InvokeFunc" => Read(),
            _ => throw new NotSupportedException(name)
        });
        string Read() { ++reads; return json; }
        void Set(string name, object value) => properties.Single(p => p.Name == name).SetValue(null, value);
        try
        {
            Set("PluginInterface", TargetServiceProxy.Create<IDalamudPluginInterface>((name, _) =>
                name == "GetIpcSubscriber" ? subscriber : throw new NotSupportedException(name)));
            Set("ClientState", TargetServiceProxy.Create<IClientState>((name, _) =>
                name == "get_TerritoryType" ? territory : throw new NotSupportedException(name)));
            Set("GameObjects", TargetServiceProxy.Create<IObjectTable>((name, _) =>
                name is "SearchByEntityId" or "SearchById" ? target : throw new NotSupportedException(name)));
            Set("TargetManager", TargetServiceProxy.Create<ITargetManager>((name, values) =>
            {
                if (name == "get_Target") return applied;
                if (name == "set_Target") { applied = (IGameObject?)values[0]; return null; }
                throw new NotSupportedException(name);
            }));
            Set("Log", TargetServiceProxy.Create<IPluginLog>((_, _) => { ++warnings; return null; }));
            var request = new CombatTargetRequest(null, true, true, false, 25, null);
            var provider = new PromeRadarTargetProvider();
            void State(PromeRadarMovementState state)
            {
                json = JsonSerializer.Serialize(state);
                typeof(PromeRadarTargetProvider).GetField("nextReadAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, DateTime.MinValue);
            }
            Check(provider.TrySelect(request, out var choice) && choice.Target == target && choice.Source == "promeradar-pt99", "MP target directive must be handled");
            provider.TrySelect(request, out _);
            Check(reads == 1, "IPC reads must be cached within 100ms");
            var policy = new CombatAssistPolicy();
            var selected = (CombatTargetChoice)typeof(CombatAssistPolicy).GetMethod("Choose", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(policy, [request])!;
            Check(selected.Source == "promeradar-pt99" && selected.Target == target, "MP must precede both encounter and generic selection");
            var apply = typeof(CombatAssistPolicy).GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
            apply.Invoke(null, [selected]);
            Check(applied == target, "Shared attack/navigation apply must use the MP target");
            State(fresh with { Pt99TargetId = 0 });
            Check(provider.TrySelect(request, out choice) && choice.HoldFire && choice.Target == null, "Zero is an explicit hold, never generic fallback");
            apply.Invoke(null, [choice]);
            Check(applied == null, "MP hold must clear an existing NPC target");
            foreach (var invalid in new Action[] { () => dead = true, () => { dead = false; targetable = false; },
                () => { targetable = true; hp = 1; }, () => { hp = 1000; oid = 0x1234; } })
            {
                invalid(); State(fresh);
                Check(provider.TrySelect(request, out choice) && choice.HoldFire, "An invalid live MP target must hold instead of falling back");
            }
            oid = 0x48EA;
            foreach (var state in new[] { fresh with { Pt99TargetControlActive = false }, fresh with { ReplayActive = true },
                fresh with { Ready = false }, fresh with { TerritoryId = 1283 }, fresh with { CapturedAtUtc = now.AddSeconds(-1) },
                fresh with { CapturedAtUtc = now.AddSeconds(10) } })
            {
                State(state);
                Check(!provider.TrySelect(request, out _), "Foreign, stale, future, replay and inactive directives cannot claim target ownership");
            }
            State(fresh);
            territory = 1289;
            var before = reads;
            Check(!provider.TrySelect(request, out _) && reads == before, "Other territories must not read the PT99 IPC");
            territory = 1290;
            Check(!provider.TrySelect(request with { InCombat = false }, out _), "Preparation must not claim target ownership");
            available = false;
            Check(!provider.TrySelect(request, out _), "Missing MP endpoint preserves the original selection chain");
            available = true;
            typeof(PromeRadarTargetProvider).GetField("nextReadAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, DateTime.MinValue);
            json = "invalid-json";
            Check(!provider.TrySelect(request, out _) && warnings == 1, "Malformed IPC data releases ownership and reports the read failure");
            Console.WriteLine("PASS PT99 MP/FSD targeting: provider precedence, shared apply, stable cache, explicit hold, invalid/dead targets, freshness, scope, replay, missing endpoint and malformed JSON");
        }
        finally
        {
            for (var i = 0; i < properties.Length; ++i) properties[i].SetValue(null, originals[i]);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
