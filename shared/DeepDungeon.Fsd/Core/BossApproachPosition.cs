using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public static class BossApproachPosition
{
    public static Vector3? Resolve(Vector3 player, Vector3 boss, float actionRange)
    {
        if (!CombatPositionPolicy.IsFinite(player) || !CombatPositionPolicy.IsFinite(boss)
            || !float.IsFinite(actionRange) || actionRange <= .5f) return null;
        var direction = player - boss;
        direction.Y = 0;
        if (direction.LengthSquared() < .0001f) return null;
        // Finish on the entry side, with room for movement tolerance. Never
        // queue a route through the boss's centre at the far arena edge.
        float distance = MathF.Min(3, actionRange) - .5f;
        var point = boss + Vector3.Normalize(direction) * distance;
        point.Y = player.Y;
        return point;
    }
}
