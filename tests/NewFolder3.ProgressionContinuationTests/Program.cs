using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

int checks = 0;
var progression = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 11, 1, false,
    false, true, true, false) { AttachCurrentDuty = true };

foreach (uint dungeonId in new uint[] { 1, 2, 4 })
{
    var plan = progression with { DungeonId = dungeonId };
    var session = new FarmingSession(plan);
    Check(plan.ContinuesAttachedProgression && !session.AttachmentCompleted,
        "Deep progression must initially attach to the live duty without claiming its save.");
    for (int entry = 11; entry <= plan.TerminalEntryFloor; entry += 10)
    {
        var result = new FarmingAttemptResult(entry, true, false, false, false);
        var decision = FarmingSessionPolicy.Decide(plan, result);
        Check(!decision.DeleteOwnedSave && !decision.RecoverFailure && decision.Error.Length == 0,
            "A borrowed save must survive every successful floorset, including the summit.");
        Check(decision.CountCycle == (entry == plan.TerminalEntryFloor),
            "Only the summit completes a progression cycle.");
        if (entry < plan.TerminalEntryFloor)
            Check(decision.NextFloor == entry + 10, "A clear must advance exactly one floorset.");
        session.CompleteAttempt(decision, 0);
        Check(session.AttachmentCompleted && session.OwnedSlot == -1,
            "Continuation must finish live attachment without taking deletion ownership.");
        if (entry == plan.TerminalEntryFloor) break;
        var source = session.PrepareSource();
        var expected = new SaveSlotSnapshot(1, false, entry + 10, $"{entry + 10}", "original-run");
        Check(source.Observe([new(0, true, 0, "empty"), expected], out var observed, out _) && observed == expected,
            "Continuation must bind the existing save at the next floorset rather than create a new save.");
        Check(!source.Observe([new(0, false, entry + 10, $"{entry + 10}", "different-run")], out _, out _),
            "A bound continuation must never switch to another slot.");
        Check(!source.Observe([expected with { Identity = "replacement" }], out _, out _),
            "Replacing a save in the same slot must be rejected.");
    }
    foreach (var result in new FarmingAttemptResult[]
             { new(21, false, true, false, false), new(21, false, false, false, true), new(21, false, false, false, false) })
    {
        var decision = FarmingSessionPolicy.Decide(plan, result);
        Check(!decision.DeleteOwnedSave && !decision.RecoverFailure && !decision.CountCycle && decision.Error.Length > 0,
            "Failure, abort and unknown outcomes must stop without deleting or restarting a borrowed save.");
    }
    Check(FarmingSessionPolicy.ReachedStopBoundary(plan, new(21, true, false, false, false), true),
        "The explicit floorset pause must still apply to attached progression.");
}

var unbound = new FarmingSession(progression);
unbound.CompleteAttempt(FarmingSessionPolicy.Decide(progression, new(11, true, false, false, false)), 0);
var binding = unbound.PrepareSource();
Check(!binding.Observe([new(0, false, 21, "21", "a"), new(1, false, 21, "21", "b")], out _, out _),
    "Ambiguous initial sources must never be selected automatically.");
Check(!binding.Observe([new(0, false, 31, "31", "a"), new(1, true, 0, "empty")], out _, out _),
    "Unexpected progression must not be treated as the next floorset.");
Check(!binding.Observe([new(0, false, 21, "21", "a", false), new(1, true, 0, "empty")], out _, out _),
    "An unavailable saved run cannot be entered.");
Check(binding.Observe([new(0, false, 51, "51", "unrelated"), new(1, false, 21, "21", "current")], out var resumed, out _) &&
    resumed.Index == 1, "An unrelated occupied save must not block a uniquely matching next floorset.");
Check(!binding.Observe([new(0, false, 21, "21", "unrelated"), new(1, false, 31, "31", "current")], out _, out _),
    "A previously pinned source must not be replaced by another save with matching progress.");
Check(!PreparedSavePolicy.TryResolve([new(0, false, 11, "11", "a"), new(1, false, 21, "21", "b")],
    null, true, null, out _, out _), "Reusable prepared-save modes must still require a unique occupied source.");
Check(FarmingEntryPolicy.Decide(new(true, false, 0, 0), 4, 21, -1).Action == FarmingEntryAction.Enter,
    "After a confirmed clear, continuation must be able to start entry from outside the duty.");
Check(!(progression with { Mode = FarmingMode.Hoard }).ContinuesAttachedProgression,
    "Hoard attachment must remain limited to the current floorset.");
var owned = progression with { AttachCurrentDuty = false, StartFloor = 1 };
Check(FarmingSessionPolicy.Decide(owned, new(21, false, true, false, false)) is
    { DeleteOwnedSave: true, RecoverFailure: true, CountCycle: false },
    "Failures on task-created saves must retain their reset-and-restart behavior.");
Check(FarmingSessionPolicy.Decide(owned, new(91, true, false, false, false)) is
    { DeleteOwnedSave: true, CountCycle: true }, "Task-created saves must retain summit cleanup.");
foreach (int selectedSlot in new[] { 0, 1 })
{
    var selectedPlan = progression with { AttachCurrentDuty = false, StartFloor = 1, ProgressionSaveSlot = selectedSlot };
    var saved = new SaveSlotSnapshot(selectedSlot, false, 31, "31", "selected-run");
    var other = new SaveSlotSnapshot(1 - selectedSlot, false, 31, "31", "other-run");
    Check(SelectedProgressionSavePolicy.TrySelect([other, saved], selectedSlot, 100, out var selected, out _) && selected == saved,
        "An explicit slot must resolve even when both saves have the same progress.");
    Check(!SelectedProgressionSavePolicy.TrySelect([other], selectedSlot, 100, out _, out _),
        "A missing selected slot must never fall back to the other save.");
    Check(!SelectedProgressionSavePolicy.TrySelect([saved, saved], selectedSlot, 100, out _, out _),
        "Duplicate slot observations cannot establish a source.");
    Check(!SelectedProgressionSavePolicy.TrySelect([other, saved with { Enterable = false }], selectedSlot, 100, out _, out _),
        "An unavailable chosen save must be preserved, not replaced or automatically deleted.");
    Check(!SelectedProgressionSavePolicy.TrySelect([saved with { StartFloor = 32 }], selectedSlot, 100, out _, out _),
        "A selected save still requires a valid floorset start.");
    Check(!SelectedProgressionSavePolicy.TrySelect([saved with { StartFloor = 101 }], selectedSlot, 100, out _, out _),
        "A completed or out-of-range save must not be treated as a playable source.");

    var selectedSession = new FarmingSession(selectedPlan);
    Check(!selectedSession.BindSelectedSave(other), "Session binding must refuse a different slot.");
    Check(selectedSession.BindSelectedSave(saved) && selectedSession.NextFloor == 31 && selectedSession.OwnedSlot == selectedSlot,
        "Starting outside the dungeon must resume the selected save's actual progress instead of requiring an empty slot.");
    var next = FarmingSessionPolicy.Decide(selectedPlan, new(31, true, false, false, false));
    Check(next is { NextFloor: 41, DeleteOwnedSave: false, CountCycle: false, RecoverFailure: false },
        "A successful selected-save floorset must retain the save and advance.");
    selectedSession.CompleteAttempt(next, 0);
    var nextSource = selectedSession.PrepareSource();
    var advanced = saved with { StartFloor = 41, Progress = "41" };
    Check(nextSource.RequiredSlot == selectedSlot && nextSource.Observe([other, advanced], out selected, out _) && selected == advanced,
        "After a clear, only the same chosen slot may advance to the expected floor.");
    Check(selectedSession.BindSelectedSave(advanced), "A verified next floorset must retain the bound identity.");
    Check(!selectedSession.BindSelectedSave(advanced with { Identity = "replaced" }),
        "A replaced save in the chosen slot must stop continuation.");

    var failure = FarmingSessionPolicy.Decide(selectedPlan, new(41, false, true, false, false));
    Check(failure is { NextFloor: 1, DeleteOwnedSave: true, RecoverFailure: true, CountCycle: false },
        "Only a confirmed failure may reset the chosen save and restart from floor one.");
    selectedSession.ConfirmOwnedSaveDeleted();
    selectedSession.CompleteAttempt(failure, 0);
    var empty = saved with { Empty = true, StartFloor = 0, Progress = "empty", Identity = "" };
    Check(SelectedProgressionSavePolicy.TrySelect([other, empty], selectedSlot, 100, out selected, out _) && selected == empty,
        "A cleared selected slot must be reused even while another slot is occupied.");
    Check(selectedSession.BindSelectedSave(empty) && selectedSession.NextFloor == 1 && selectedSession.OwnedSlot == selectedSlot,
        "Failure recovery must create the new run in the same selected slot.");
    foreach (int floor in new[] { 1, 11, 21, 31, 41, 51, 61, 71, 81, 91 })
    {
        var clearDecision = FarmingSessionPolicy.Decide(selectedPlan, new(floor, true, false, false, false));
        Check(!clearDecision.DeleteOwnedSave && !clearDecision.RecoverFailure && clearDecision.Error.Length == 0,
            "No successful floorset, including the summit, may reset a selected save.");
        Check(clearDecision.CountCycle == (floor == 91), "A selected-save clear must count only at the summit.");
    }
    foreach (var outcome in new FarmingAttemptResult[]
             { new(31, false, false, false, true), new(31, false, false, false, false) })
        Check(FarmingSessionPolicy.Decide(selectedPlan, outcome) is { DeleteOwnedSave: false, RecoverFailure: false, CountCycle: false },
            "Aborting or losing outcome evidence cannot authorize resetting the selected save.");

    var attachedPlan = selectedPlan with { StartFloor = 21, AttachCurrentDuty = true };
    var attachedSession = new FarmingSession(attachedPlan);
    Check(!attachedPlan.UsesSelectedProgressionSave,
        "A selected-slot preference must not override the verified source of a borrowed attachment.");
    Check(attachedSession.OwnedSlot == -1 && !attachedSession.AttachmentCompleted,
        "A configured selected slot must not confer ownership during live takeover.");
    var attachedFailure = FarmingSessionPolicy.Decide(attachedPlan, new(21, false, true, false, false));
    Check(attachedFailure is { DeleteOwnedSave: false, RecoverFailure: false, CountCycle: false } &&
        attachedFailure.Error.Length > 0, "Live takeover with no verified source must retain its save after failure.");
    Check(FarmingSessionPolicy.Decide(attachedPlan with { HoldOnFailure = true }, new(21, false, true, false, false)) is
        { DeleteOwnedSave: false, RecoverFailure: false }, "Explicit diagnostic holds must override failure recovery.");
}
Check(!SelectedProgressionSavePolicy.TrySelect([], -1, 100, out _, out _) &&
    !SelectedProgressionSavePolicy.TrySelect([], 2, 100, out _, out _), "Only the two real save slots can be selected.");
checks += FreeExplorationTests.Run();
var candidate = new SaveSlotSnapshot(0, false, 21, "21", "original");
Check(OwnedSavePolicy.ValidateForDeletion([candidate], 0, 21, null) != null,
    "Matching progress and a configured slot cannot prove ownership.");
Check(OwnedSavePolicy.ValidateForDeletion([candidate with { Identity = "replacement" }], 0, 21, candidate) != null,
    "A replaced identity in the same slot and floorset must be rejected.");
Check(OwnedSavePolicy.ValidateForDeletion([candidate], 0, 21, candidate) == null,
    "A verified matching source may be cleaned.");
var createdSession = new FarmingSession(owned with { StartFloor = 21 });
Check(createdSession.BindCreatedSave(0) && createdSession.CreatedBySession &&
    OwnedSavePolicy.ValidateForDeletion([candidate], createdSession.OwnedSlot, 21, null) != null,
    "Creation provenance alone must not authorize deletion before an identity was pinned.");
Check(OwnedSavePolicy.ValidateForDeletion([candidate with { Identity = "replacement" }],
    createdSession.OwnedSlot, 21, null) != null,
    "A replacement in the created slot must remain protected even at matching progress.");
Check(!unbound.CreatedBySession && unbound.OwnedSlot == -1 && unbound.CanPersistPause &&
      unbound.ContinuationSlot == resumed.Index,
    "A verified borrowed continuation may persist a pause without acquiring deletion ownership.");
var unknownAttachment = new FarmingSession(progression);
Check(!unknownAttachment.CanPersistPause && unknownAttachment.ContinuationSlot == null,
    "Unidentified live attachments must reject a persistent pause before promising a checkpoint.");
var borrowedCheckpoint = new FarmingSession(progression);
var pausedBorrowed = candidate with { Index = 1, StartFloor = 31, Progress = "31" };
borrowedCheckpoint.RestoreCheckpoint(pausedBorrowed, 2, 123, () => null, _ => { });
Check(borrowedCheckpoint.AttachmentCompleted && borrowedCheckpoint.OwnedSlot == -1 &&
      borrowedCheckpoint.ContinuationSlot == 1 && !borrowedCheckpoint.CreatedBySession,
    "Restoring a borrowed checkpoint must skip live takeover without claiming deletion ownership.");
Check(FarmingEntryPolicy.Decide(new(true, false, 0, 0), 4, borrowedCheckpoint.NextFloor,
    borrowedCheckpoint.OwnedSlot, progression.AttachCurrentDuty && !borrowedCheckpoint.AttachmentCompleted).Action
    == FarmingEntryAction.Enter,
    "A restored borrowed checkpoint must enter from outside instead of demanding the old live duty.");
Check(borrowedCheckpoint.PrepareSource().Observe([pausedBorrowed], out _, out _),
    "Restored borrowed continuation must verify the exact checkpoint before entry.");
Check(!borrowedCheckpoint.PrepareSource().Observe([pausedBorrowed with { Identity = "replacement" }], out _, out _),
    "Borrowed checkpoint recovery must reject a replacement source.");
createdSession.ConfirmOwnedSaveDeleted();
Check(!createdSession.CreatedBySession && createdSession.OwnedSlot == -1,
    "Deletion completion must revoke the old creation provenance.");
var fingerprintSlot = candidate with { RowFingerprint = new string('A', 64) };
var checkpointSession = new FarmingSession(owned);
checkpointSession.RestoreCheckpoint(fingerprintSlot, 2, 123, () => null, _ => { });
Check(!checkpointSession.PrepareSource().Observe([fingerprintSlot with { RowFingerprint = new string('B', 64) }],
    out _, out _), "Checkpoint recovery must reject a changed row fingerprint even when slot, floor and identity match.");
Check(checkpointSession.Source.Observe([fingerprintSlot], out _, out _), "The original checkpoint must still validate.");
Console.WriteLine($"Progression continuation and selected-slot checks passed: {checks}.");

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
