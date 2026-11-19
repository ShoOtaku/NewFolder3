using System;
using System.Collections.Generic;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private RoomEscapeRecovery? roomEscape;
    private bool roomEscapeOwnsMovement;

    private unsafe bool TryRoomEscape(InstanceContentDeepDungeon* dd, Vector3 position,
        bool inCombat, bool playerDead)
    {
        if (_navigation == null || _coordPipeline == null)
            return false;
        roomEscape ??= new RoomEscapeRecovery(RecordReplayEvent);
        var now = DateTime.UtcNow;
        var previousTarget = roomEscape.Target;
        var room = RoomGraph.GetLocalPlayerRoomIndex(dd);
        var active = roomEscape.Update(now, position, room,
            _navigation.InputEnabled && _navigation.Status == "moving",
            Kind == FloorRuntimeKind.Normal && dd != null && (uint)room < RoomGraph.MaxRooms &&
                !inCombat && !playerDead && !_navigation.NeedsSafetyPlan && !CastMovementGuard.ShouldWait(),
            () => ConnectedEscapeRooms(dd, room));
        if (previousTarget != roomEscape.Target || active != roomEscapeOwnsMovement)
            _navigation.Cancel();
        if (!active)
        {
            if (roomEscapeOwnsMovement)
                _coordPipeline.SetExternalMovement(false, now);
            roomEscapeOwnsMovement = false;
            return false;
        }
        // Keep the exact objective attempt, including its retries and interaction state.
        // No objective tick runs while the escape owns movement.
        if (!roomEscapeOwnsMovement)
            _coordPipeline.SetExternalMovement(true, now);
        roomEscapeOwnsMovement = true;
        var target = roomEscape.Target!.Value;
        _navigation.Navigate(target.Center, position, RoomEscapeRecovery.ArrivalRadius);
        _status = $"原地移动多次无效，正在前往房间 {target.Room + 1} 脱困";
        return true;
    }

    private static unsafe IReadOnlyList<(int Room, Vector3 Center)> ConnectedEscapeRooms(
        InstanceContentDeepDungeon* dd, int room)
    {
        var result = new List<(int Room, Vector3 Center)>();
        if (dd == null || (uint)room >= RoomGraph.MaxRooms)
            return result;
        // Only reciprocal, open room connections are eligible. The normal navigator
        // still plans the actual route around walls; this never writes direct input.
        Span<int> neighbors = stackalloc int[4];
        RoomGraph.EnumerateNeighbors(dd, room, neighbors, out var count);
        for (var i = 0; i < count; i++)
            if (MapPos.TryGetRoomCenter(dd, neighbors[i], out var center))
                result.Add((neighbors[i], center));
        return result;
    }
}
