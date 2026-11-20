using System.Numerics;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

internal static class CastMovementGuard
{
    internal static bool ShouldWait()
    {
        var player = Service.LocalPlayer;
        if (player?.IsCasting != true) return false;
        var elapsed = player.CurrentCastTime;
        var remaining = player.TotalCastTime - elapsed;
        var position = new Vector2(player.Position.X, player.Position.Z);
        var territory = (ushort)Service.ClientState.TerritoryType;
        var now = DateTime.UtcNow;
        return CastMovementPolicy.ShouldWait(true, elapsed, remaining, position, player.GameObjectId,
            territory, now, EncounterPlanningSnapshotRegistry.Read()) &&
            CastMovementPolicy.ShouldWait(true, elapsed, remaining, position, player.GameObjectId,
                territory, now, EncounterPlanningSnapshotRegistry.ReadLocalHazards());
    }
}
