namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

internal static class PromeRadarMovementGate
{
    internal static bool IsBlocked { get; private set; }
    internal static bool HandoffComplete { get; private set; }

    internal static void SetControl(bool active, Action suspendMovement)
    {
        // Revoke permission before cancellation, including when cancellation fails.
        IsBlocked = active;
        HandoffComplete = false;
        if (active) suspendMovement();
        HandoffComplete = active;
    }
}
