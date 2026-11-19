using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

internal sealed class FarmingSession(FarmingPlan plan, int ownedSlot = -1)
{
    public FarmingPlan Plan { get; } = plan;
    public PreparedSaveBinding Source { get; private set; } = new(maxFloor: plan.MaxFloor);
    public bool BandedEnabled { get; private set; } = plan.BandedEnabled;
    public bool OpenGold { get; private set; } = plan.OpenGold;
    public bool OpenSilver { get; private set; } = plan.OpenSilver;
    public bool OpenBronze { get; private set; } = plan.OpenBronze;
    public int NextFloor { get; private set; } = plan.StartFloor;
    public int OwnedSlot { get; private set; } = ownedSlot switch
    {
        >= 0 => ownedSlot,
        _ when plan.AttachCurrentDuty && plan.UsesSelectedProgressionSave => plan.ProgressionSaveSlot.GetValueOrDefault(),
        _ => -1
    };
    public bool AttachmentCompleted { get; private set; }
    public int Failures { get; private set; }
    public int Discoveries { get; private set; }

    public bool TrySetTargets(in FarmingTargets targets)
    {
        if (!FarmingTargetPolicy.IsValid(Plan.Mode, targets)) return false;
        BandedEnabled = targets.Hoard;
        OpenGold = targets.Gold;
        OpenSilver = targets.Silver;
        OpenBronze = targets.Bronze;
        return true;
    }

    public PreparedSaveBinding PrepareSource()
    {
        Source.ConfigureForEntry(Plan.ReusesSave, Plan.SaveUse == SaveUse.Prepared ? null : NextFloor,
            OwnedSlot >= 0 ? OwnedSlot : Source.Pinned?.Index, Plan.MaxFloor);
        return Source;
    }

    public bool BindCreatedSave(int slot)
    {
        OwnedSlot = slot;
        return slot >= 0;
    }

    public bool BindSelectedSave(in SaveSlotSnapshot source)
    {
        if (!Plan.UsesSelectedProgressionSave || source.Index != Plan.ProgressionSaveSlot ||
            OwnedSlot >= 0 && (source.Empty || source.StartFloor != NextFloor)) return false;
        if (!source.Empty)
        {
            Source.ConfigureForEntry(false, source.StartFloor, source.Index, Plan.MaxFloor);
            if (!Source.Observe([source], out _, out _)) return false;
        }
        OwnedSlot = source.Index;
        NextFloor = source.Empty ? 1 : source.StartFloor;
        return true;
    }

    public void ConfirmOwnedSaveDeleted()
    {
        OwnedSlot = -1;
        Source = new PreparedSaveBinding(maxFloor: Plan.MaxFloor);
    }

    public void CompleteAttempt(in FarmingSessionDecision decision, int discoveries)
    {
        NextFloor = decision.NextFloor;
        if (Plan.ContinuesAttachedProgression && decision.Error.Length == 0)
            AttachmentCompleted = true;
        Discoveries += discoveries;
        if (decision.RecoverFailure) Failures++;
    }
}

internal sealed class PreparedSaveBinding
{
    public SaveSlotSnapshot? Pinned { get; private set; }
    public bool Reusable { get; private set; }
    public int? ExpectedFloor { get; private set; }
    public int? RequiredSlot { get; private set; }
    public int MaxFloor { get; private set; }
    public string Description => Pinned is { } value ? $"存檔 {value.Index + 1}，起點 {value.StartFloor} 層" : "唯一存檔待讀取";

    public PreparedSaveBinding(bool reusable = false, int? expectedFloor = null, int? requiredSlot = null, int maxFloor = 100) =>
        ConfigureForEntry(reusable, expectedFloor, requiredSlot, maxFloor);

    public void ConfigureForEntry(bool reusable, int? expectedFloor, int? requiredSlot, int maxFloor = 100)
    {
        Reusable = reusable;
        ExpectedFloor = expectedFloor;
        RequiredSlot = requiredSlot;
        MaxFloor = maxFloor;
    }

    public bool Observe(IReadOnlyList<SaveSlotSnapshot> slots, out SaveSlotSnapshot source, out string error)
    {
        if (!PreparedSavePolicy.TryResolve(slots, Reusable ? Pinned : null, Reusable,
                ExpectedFloor, out source, out error, RequiredSlot, MaxFloor)) return false;
        // Non-reusable progress is permitted to advance only to the explicitly expected floorset.
        if (Pinned.HasValue && source.Index != Pinned.Value.Index)
        {
            error = "來源存檔槽位已變更；已停止。";
            return false;
        }
        if (Pinned is { } pinned && (source.Identity != pinned.Identity ||
            ((!ExpectedFloor.HasValue || pinned.StartFloor == ExpectedFloor.Value) && source.Progress != pinned.Progress)))
        {
            error = "來源存檔內容已變更；已停止。";
            return false;
        }
        Pinned = source;
        return true;
    }
}
