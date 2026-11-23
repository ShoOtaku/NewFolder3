using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class FarmingRecoveryTests
{
    public static void Run()
    {
        AttachCurrentDuty();
        Check(FarmingPlan.TryCreate(FarmingMode.Hoard, SaveUse.Create, 21, 2, false, true, true, false, false,
            out var plan, out _), "Hoard plan must be valid");
        var failure = FarmingSessionPolicy.Decide(plan!, new(21, false, true, false, false));
        Check(failure is { CountCycle: false, NextFloor: 21, DeleteOwnedSave: true, RecoverFailure: true, Error: "" },
            "Failed hoard attempts must reset their own save and restart without counting a successful cycle");
        var success = FarmingSessionPolicy.Decide(plan!, new(21, true, false, false, false));
        Check(success is { CountCycle: true, NextFloor: 21, DeleteOwnedSave: true, RecoverFailure: false, Error: "" },
            "Successful attempts must clean up before the next cycle");
        Check(FarmingSessionPolicy.Decide(plan!, new(21, false, false, false, false)).DeleteOwnedSave == false,
            "Unknown outcomes must preserve the save");
        Check(FarmingSessionPolicy.Decide(plan! with { HoldOnFailure = true }, new(21, false, true, false, false)).DeleteOwnedSave == false,
            "Explicit diagnostic hold must preserve the save");
        Check(!FarmingDeathPolicy.IsConfirmedSoloFailure(true, false, 1, 2.99), "Death grace period must be observed");
        Check(!FarmingDeathPolicy.IsConfirmedSoloFailure(true, true, 1, 120), "Raising must retain revival eligibility");
        Check(!FarmingDeathPolicy.IsConfirmedSoloFailure(true, false, 4, 120), "Player death alone is not a party wipe");
        Check(FarmingDeathPolicy.IsConfirmedSoloFailure(true, false, 0, 3), "Solo death without Raising must recover without DutyWiped");
        Check(!FarmingDeathPolicy.IsConfirmedSoloFailure(false, false, 1, 120), "Revival must cancel failure inference");
        var now = DateTime.UtcNow;
        var state = new PromeRadarMovementState { ApiVersion = 1, Ready = true, CapturedAtUtc = now, TerritoryId = 1283,
            SmartMovementEnabled = true, CanMove = true, NavigationActive = true, FsdSessionActive = true, Pt30ScriptReady = true, Pt30ScriptVersion = "1.0.0" };
        Check(state.OwnsPt30(1283, now), "Fresh matching MP script must own PT30 without a BMR module");
        Check(!state.OwnsPt30(1283, now.AddSeconds(4)), "Stale MP state must not claim movement");
        Check(!state.OwnsPt30(816, now), "Wrong territory must not claim movement");
        Check(!(state with { Pt30ScriptReady = false }).OwnsPt30(1283, now), "Missing native script must not claim complete mechanics");
        Check(!(state with { FsdSessionActive = false }).OwnsPt30(1283, now), "MP must acknowledge the FSD session");
        Check(!(state with { Pt30ScriptVersion = "old" }).OwnsPt30(1283, now), "Incompatible script must not claim complete mechanics");
        Check(!(state with { ReplayActive = true }).OwnsPt30(1283, now), "Replay must never claim live movement");
        Check(!(state with { SmartMovementEnabled = false }).OwnsPt30(1283, now), "Disabled MP must not claim movement");
        Check(!state.OwnsDodgeMovement(1283, now), "Enabled but idle MP must not block floor travel");
        Check((state with { HasMovementControl = true }).OwnsDodgeMovement(1283, now), "Active MP dodge must preempt floor travel");
        Check(!(state with { HasMovementControl = true }).OwnsDodgeMovement(1283, now.AddSeconds(4)), "Stale MP control must not preempt travel");
        Console.WriteLine("Farming recovery and PromeRadar handover checks passed.");
    }

    private static void AttachCurrentDuty()
    {
        var pt29 = new FarmingEntryFacts(true, true, 4, 29);
        Check(FarmingEntryPolicy.Decide(pt29, 4, 21, -1, attachCurrentDuty: true).Action == FarmingEntryAction.Attach,
            "Explicit in-duty takeover can use live floor data without claiming a save slot");
        Check(FarmingEntryPolicy.Decide(pt29 with { Floor = 30 }, 4, 21, -1, attachCurrentDuty: true).Action == FarmingEntryAction.Attach,
            "Takeover can begin on a boss floor");
        Check(FarmingEntryPolicy.Decide(pt29 with { Ready = false, Floor = 0 }, 4, 21, -1, attachCurrentDuty: true).Action == FarmingEntryAction.Wait,
            "A transition cannot attach or start entry");
        Check(FarmingEntryPolicy.Decide(pt29 with { DungeonId = 2 }, 4, 21, -1, attachCurrentDuty: true).Action == FarmingEntryAction.Conflict,
            "Takeover must not attach when the dungeon changes");
        Check(FarmingEntryPolicy.Decide(pt29 with { Floor = 31 }, 4, 21, -1, attachCurrentDuty: true).Action == FarmingEntryAction.Conflict,
            "Takeover must stop when the floorset changes");
        Check(FarmingEntryPolicy.Decide(new(true, false, 0, 0), 4, 21, -1, attachCurrentDuty: true).Action == FarmingEntryAction.Conflict,
            "Takeover must not fall back to fresh entry if the player leaves");
        Check(FarmingEntryPolicy.Decide(pt29, 4, 21, -1).Action == FarmingEntryAction.Conflict &&
            FarmingEntryPolicy.Decide(pt29, 4, 21, 0).Action == FarmingEntryAction.Attach,
            "Existing diagnostic attachment still requires a declared slot");
        Check(FarmingEntryPolicy.ResolveAttachmentMode(4, 21, FarmingMode.Hoard) == FarmingMode.Hoard &&
            FarmingEntryPolicy.ResolveAttachmentMode(4, 31, FarmingMode.Aetherpool) == FarmingMode.DeepProgression &&
            FarmingEntryPolicy.ResolveAttachmentMode(1, 191, FarmingMode.Hoard) == FarmingMode.DeepProgression,
            "Takeover mode must be supported on the live dungeon and floorset");
        Console.WriteLine("In-duty farming attachment checks passed.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
