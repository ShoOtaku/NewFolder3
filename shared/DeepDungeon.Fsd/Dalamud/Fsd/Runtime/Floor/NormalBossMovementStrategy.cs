using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Adapts the existing encounter planner and approach fallback.</summary>
internal sealed unsafe class NormalBossMovementStrategy(FloorExplorationController floor) : IBossMovementStrategy
{
    public string Name => "mechanics";
    public bool SuppressCombatItems => false;
    public bool RequestsNorthCamera => false;
    public void Start() { }

    public string? Update(InstanceContentDeepDungeon* dungeon)
    {
        if (!floor.UpdateBossEncounter(dungeon))
        {
            floor.UpdateBossEscape(dungeon);
            floor.Boss.UpdateApproach(dungeon);
        }
        return null; // Existing controllers publish the normal-strategy status.
    }

    public void Stop() => floor.CancelCoordMovement();
}
