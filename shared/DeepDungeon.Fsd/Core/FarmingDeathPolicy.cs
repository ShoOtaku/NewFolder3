namespace DeepDungeon.Fsd.Core;

public static class FarmingDeathPolicy
{
    // Allow the raising flag and party list to settle before inferring a solo wipe.
    public static bool IsConfirmedSoloFailure(bool dead, bool raisingActive, int partySize, double deadSeconds) =>
        dead && !raisingActive && partySize is >= 0 and <= 1 && deadSeconds >= 3;
}
