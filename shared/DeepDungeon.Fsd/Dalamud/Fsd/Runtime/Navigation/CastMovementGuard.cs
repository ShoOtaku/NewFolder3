using System.Numerics;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

internal static class CastMovementGuard
{
    internal static MasterPlayer.MechanicRuntime.IEncounterSource? Source { get; set; }

    internal static bool ShouldWait()
    {
        var player = Service.LocalPlayer;
        if (player?.IsCasting != true) return false;
        var elapsed = player.CurrentCastTime;
        var remaining = player.TotalCastTime - elapsed;
        var position = new Vector2(player.Position.X, player.Position.Z);
        var territory = (ushort)Service.ClientState.TerritoryType;
        var now = DateTime.UtcNow;
        var frame = Source?.Read();
        return CastMovementPolicy.ShouldWait(true, elapsed, remaining, position, player.GameObjectId,
            territory, now, frame?.Encounter) &&
            CastMovementPolicy.ShouldWait(true, elapsed, remaining, position, player.GameObjectId,
                territory, now, frame?.LocalHazards);
    }
}
