using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    internal bool DiagnosticMovementRequested => _navigation?.InputEnabled == true;
    internal int DiagnosticPlayerRoom => LastObservedRoomIndex;
    internal object CaptureDiagnostics()
    {
        var debug = GetDebugSnapshot();
        return new
        {
            kind = Kind.ToString(), Generation, ReadyAtUtc,
            coordination = _coordPipeline?.CaptureDiagnostics(),
            navigation = _navigation == null ? null : new
            {
                _navigation.Status, _navigation.Failure, _navigation.Solver, _navigation.InputEnabled,
                _navigation.LegIndex, destination = Point(_navigation.FinalDestination),
                commandedTarget = Point(_navigation.CommandedTarget), _navigation.ArrivalRadius,
                pathPoints = _navigation.SmoothedPath.Count
            },
            blockedMovementOperation = _blockedMovementOperation,
            task = debug.TaskPhase,
            search = new { room = LastObservedRoomIndex,
                completedRooms = debug.CompletedRooms.Order().ToArray(),
                roomPath = debug.RoomPath.Take(32).ToArray(), debug.HoardEvidenceState,
                blockedWork = _siteDriver?.BlockedWorkReason },
            activeWork = debug.Work.Take(32).ToArray()
        };
    }

    private static object Point(Vector3 value) => new { x = value.X, y = value.Y, z = value.Z };
}
