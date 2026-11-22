using System.Text.Json;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Plugin.Ipc;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

internal sealed class PromeRadarTargetProvider : ICombatTargetProvider
{
    private readonly ICallGateSubscriber<string> getState = Service.PluginInterface.GetIpcSubscriber<string>("PromeRadar.GetState");
    private PromeRadarMovementState? state;
    private DateTime nextReadAt;
    private DateTime nextWarningAt;

    public bool TrySelect(in CombatTargetRequest request, out CombatTargetChoice choice)
    {
        choice = default;
        if (!request.BossFloor || !request.InCombat || Service.ClientState.TerritoryType != 1290) return false;
        var now = DateTime.UtcNow;
        if (now >= nextReadAt)
        {
            nextReadAt = now.AddMilliseconds(100);
            state = null;
            if (!getState.HasFunction) return false;
            try { state = JsonSerializer.Deserialize<PromeRadarMovementState>(getState.InvokeFunc()); }
            catch (Exception ex)
            {
                if (now >= nextWarningAt)
                {
                    nextWarningAt = now.AddSeconds(5);
                    Service.Log.Warning($"[CombatAssist] Cannot read PromeRadar PT99 target: {ex}");
                }
            }
        }
        if (state is not { Pt99TargetControlActive: true } current || !current.IsCurrent(1290, now)
            || now - current.CapturedAtUtc > TimeSpan.FromMilliseconds(500)) return false;

        IBattleChara? target = null;
        if (current.Pt99TargetId != 0)
        {
            var actor = current.Pt99TargetId <= uint.MaxValue ? Service.GameObjects.SearchByEntityId((uint)current.Pt99TargetId) : null;
            target = (actor ?? Service.GameObjects.SearchById(current.Pt99TargetId)) as IBattleNpc;
            if (target is not { IsDead: false, IsTargetable: true, CurrentHp: > 1 }
                || target.BaseId is not (0x48EA or 0x48EB or 0x48EC) || !request.Allowed(target)) target = null;
        }
        // A zero or vanished target is an explicit hold, never a generic boss fallback.
        choice = new(target, "promeradar-pt99", target == null);
        return true;
    }
}
