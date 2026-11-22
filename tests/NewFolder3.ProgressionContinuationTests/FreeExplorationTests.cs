using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using System.Text.Json;

internal static class FreeExplorationTests
{
    internal static int Run()
    {
        int checks = 0;
        Check((int)FarmingMode.DeepProgression == 3 && (int)FarmingMode.FreeExploration == 4,
            "Adding exploration must preserve saved mode values.");
        foreach (int startFloor in new[] { 1, 21, 31, 51, 71 })
        {
            Check(FarmingPlan.TryCreate(FarmingMode.FreeExploration, SaveUse.Create, startFloor,
                1, false, false, true, true, false, out var plan, out _),
                "Every supported PT checkpoint must produce a normal exploration plan.");
            Check(plan is { ClimbsToSummit: true, UsesSelectedProgressionSave: false,
                    ReusesSave: false, ShowsDetailedMap: false, StopAfterFloor: null,
                    StopsForTerminalReview: false, MaxFloor: 100 },
                "Exploration must reach the normal reward flow without selecting a challenge save or diagnostic hold.");
            var session = new FarmingSession(plan!);
            Check(session.BindCreatedSave(1), "The new empty slot must become this run's source.");
            for (int floor = startFloor; floor <= 91; floor += 10)
            {
                var clear = FarmingSessionPolicy.Decide(plan!, new(floor, true, false, false, false));
                Check(clear.Error.Length == 0 && !clear.RecoverFailure && clear.CountCycle == (floor == 91),
                    "Only the final floorset may count as a full exploration cycle.");
                Check(clear.DeleteOwnedSave == (floor == 91),
                    "Intermediate floorsets must retain the created save for continuation.");
                session.CompleteAttempt(clear, 0);
                if (floor == 91)
                {
                    Check(session.NextFloor == startFloor, "The next cycle must retain the chosen start.");
                    break;
                }
                Check(session.NextFloor == floor + 10, "A clear must advance exactly ten floors.");
                var source = session.PrepareSource();
                var next = new SaveSlotSnapshot(1, false, floor + 10, $"{floor + 10}", "exploration-run");
                Check(source.Observe([new(0, false, floor + 10, $"{floor + 10}", "other-run"), next],
                    out var observed, out _) && observed == next,
                    "Continuation must reuse the created slot even when another save has the same progress.");
                Check(!source.Observe([next with { Identity = "replaced" }], out _, out _),
                    "Replacing the created save must stop continuation.");
            }
        }

        foreach (int invalidFloor in new[] { 0, 11, 41, 61, 70, 81, 91, 100, 101 })
            Check(!FarmingPlan.TryCreate(FarmingMode.FreeExploration, SaveUse.Create, invalidFloor,
                1, false, false, true, true, false, out _, out _),
                "New exploration saves must use a supported entry checkpoint.");
        foreach (uint dungeonId in new uint[] { 1, 2, 3 })
            Check(!FarmingPlan.TryCreate(FarmingMode.FreeExploration, SaveUse.Create, 71,
                1, false, false, true, true, false, out _, out _, dungeonId),
                "The PT entry checkpoints must not be accepted for another dungeon.");
        Check(!FarmingPlan.TryCreate(FarmingMode.FreeExploration, SaveUse.Prepared, 71,
            1, false, false, true, true, false, out _, out _),
            "Starting exploration outside a duty must not silently consume an existing save.");

        var exploration = new FarmingPlan(FarmingMode.FreeExploration, SaveUse.Create, 71,
            1, false, false, true, true, false);
        var failure = FarmingSessionPolicy.Decide(exploration, new(81, false, true, false, false));
        Check(failure is { NextFloor: 71, DeleteOwnedSave: true, RecoverFailure: true, CountCycle: false },
            "A confirmed failure in 81-90 must restart the task-created run at 71.");
        var failedSession = new FarmingSession(exploration, 1);
        failedSession.ConfirmOwnedSaveDeleted();
        failedSession.CompleteAttempt(failure, 0);
        Check(failedSession.OwnedSlot == -1 && failedSession.NextFloor == 71 && failedSession.Failures == 1,
            "Verified failure cleanup must clear source ownership and keep the selected checkpoint.");
        Check(FarmingSessionPolicy.Decide(exploration, new(81, false, false, false, false)) is
            { DeleteOwnedSave: false, RecoverFailure: false, CountCycle: false },
            "An unknown result must retain the save and stop.");
        Check(FarmingSessionPolicy.ReachedStopBoundary(exploration, new(71, true, false, false, false), true),
            "The explicit ten-floor pause must retain its meaning in exploration.");

        var attached = exploration with { AttachCurrentDuty = true, StartFloor = 81 };
        var attachedSession = new FarmingSession(attached);
        Check(attached.ContinuesAttachedProgression && !attached.UsesSelectedProgressionSave,
            "Live exploration must continue without claiming the challenge slot.");
        var continued = FarmingSessionPolicy.Decide(attached, new(81, true, false, false, false));
        attachedSession.CompleteAttempt(continued, 0);
        Check(continued is { NextFloor: 91, DeleteOwnedSave: false, CountCycle: false } &&
            attachedSession.AttachmentCompleted,
            "Taking over 81-90 must continue into 91-100.");
        Check(attachedSession.PrepareSource().Observe(
            [new(0, false, 91, "91", "borrowed-run"), new(1, true, 0, "empty")], out _, out _),
            "An attached run must bind a unique matching next-floor save.");
        foreach (var result in new FarmingAttemptResult[]
                 { new(91, true, false, false, false), new(81, false, true, false, false),
                     new(81, false, false, false, true), new(81, false, false, false, false) })
        {
            var decision = FarmingSessionPolicy.Decide(attached, result);
            Check(!decision.DeleteOwnedSave && !decision.RecoverFailure,
                "Completion, failure, abort and unknown results must preserve borrowed saves.");
        }
        Check(FarmingEntryPolicy.ResolveAttachmentMode(4, 81, FarmingMode.FreeExploration) ==
            FarmingMode.FreeExploration, "Taking over a later floorset must retain exploration mode.");
        Check(FarmingEntryPolicy.ResolveAttachmentMode(1, 71, FarmingMode.FreeExploration) ==
            FarmingMode.DeepProgression, "Exploration selection must not change another dungeon's takeover.");
        Check(!MobPullPolicy.CanInitiate(FarmingMode.FreeExploration, 4, 71, 14194) &&
            MobPullPolicy.CanInitiate(FarmingMode.FreeExploration, 4, 71, 12345),
            "PT71 exploration must retain the existing dangerous-mob pull exclusion.");
        Check(FarmingItemPolicy.For(FarmingMode.FreeExploration).CombatBuffs == CombatBuffUse.ReserveForBoss,
            "Exploration must preserve the progression boss-buff reserve policy.");
        for (int mask = 0; mask < 16; mask++)
            Check(FarmingTargetPolicy.IsValid(FarmingMode.FreeExploration,
                new((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0)),
                "Exploration must allow independent hoard and chest selections.");

        var settings = JsonSerializer.Deserialize<FsdSettings>("""{"Farming":{"Mode":3}}""")!;
        Check(settings.Farming.Mode == FarmingMode.DeepProgression && settings.Farming.ExplorationStartFloor == 71,
            "Existing configurations must keep their mode and gain a default exploration start of 71.");
        var challengePreferences = settings.Farming.GetPreferences(settings);
        settings.Farming.Mode = FarmingMode.FreeExploration;
        var explorationPreferences = settings.Farming.GetPreferences(settings);
        explorationPreferences.BandedEnabled = true;
        explorationPreferences.OpenGold = false;
        explorationPreferences.Cycles = 2;
        Check(!ReferenceEquals(challengePreferences, explorationPreferences) &&
            challengePreferences.BandedEnabled == false && challengePreferences.OpenGold,
            "Exploration preferences must not alter the existing challenge preferences.");
        var restored = JsonSerializer.Deserialize<FsdSettings>(JsonSerializer.Serialize(settings))!;
        FsdSettingsValidator.ValidateOrThrow(restored);
        var restoredPreferences = restored.Farming.GetPreferences(restored);
        Check(restored.Farming.Mode == FarmingMode.FreeExploration &&
            restored.Farming.ExplorationStartFloor == 71 && restoredPreferences.BandedEnabled == true &&
            !restoredPreferences.OpenGold && restoredPreferences.Cycles == 2,
            "The mode, chosen checkpoint and independent targets must survive saving and reloading.");

        return checks;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
    }
}
