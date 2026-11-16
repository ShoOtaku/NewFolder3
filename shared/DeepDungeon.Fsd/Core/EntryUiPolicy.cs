namespace DeepDungeon.Fsd.Core;

public enum EntryUiKind
{
    None, Incomplete, Unknown, EntranceMenu, ReadSaves, PartyChoice,
    FixedPartyConfirmation, SoloExplanation, SoloConfirmation, FloorChoice, DutyConfirmation
}

public enum EntryAction
{
    Wait, InteractNpc, OpenSaves, SelectSave, SelectFixedParty, Confirm,
    AdvanceExplanation, SelectFloor, Commence, Complete, Conflict
}

public readonly record struct EntryDecision(EntryAction Action, string Reason);

/// <summary>Only facts belong here. Dispatch history never substitutes for duty or source facts.</summary>
public readonly record struct EntryFacts(
    bool Transitioning, bool InDuty, uint DungeonId, int Floor,
    EntryUiKind Ui, bool SourceSelected,
    bool QueuePending, bool QueueMatches, bool SoloParty);

public static class EntryUiPolicy
{
    public static EntryDecision Decide(in EntryFacts facts, uint dungeonId, int floor, bool creating)
    {
        if (facts.Transitioning) return Wait("等待過圖或角色載入。");
        if (facts.InDuty)
        {
            if (facts.Floor == 0) return Wait("等待副本樓層資料。");
            if (facts.DungeonId != dungeonId || facts.Floor != floor)
                return Conflict("實際入場地宮／樓層與目標不符。");
            if (!facts.SourceSelected) return Conflict("已入場，但無法確認實際來源存檔。");
            return new(EntryAction.Complete, "已確認副本、樓層與入場來源。");
        }
        if (facts.Ui is EntryUiKind.Unknown or EntryUiKind.Incomplete)
            return Wait("目前視窗內容未知或尚未完整載入；不操作。");
        if (facts.QueuePending && !facts.QueueMatches)
            return Wait("目前任務申請與入場目標不符；不操作。");
        if (facts.Ui == EntryUiKind.DutyConfirmation)
        {
            if (!facts.SourceSelected) return Wait("任務已就緒，但來源存檔尚未確認。");
            if (!facts.QueueMatches) return Wait("任務確認尚未與目標地宮／樓層匹配。");
            return new(EntryAction.Commence, "確認符合目標的任務入場。");
        }
        if (facts.QueuePending) return Wait("等待已提交的入場申請。");
        if (facts.Ui == EntryUiKind.None) return new(EntryAction.InteractNpc, "尋找入口 NPC。");
        if (facts.Ui == EntryUiKind.EntranceMenu) return new(EntryAction.OpenSaves, "開啟讀取存檔列表。");
        if (facts.Ui == EntryUiKind.ReadSaves) return new(EntryAction.SelectSave, "核對來源後選槽。");
        if (!facts.SourceSelected) return Wait("此入場對話缺少可確認的來源；不操作。");
        if (!creating) return Conflict("既有存檔入場出現新建存檔對話。");
        return facts.Ui switch
        {
            EntryUiKind.PartyChoice => new(EntryAction.SelectFixedParty, "選擇固定隊伍。"),
            EntryUiKind.FixedPartyConfirmation => new(EntryAction.Confirm, "確認固定隊伍入場。"),
            EntryUiKind.SoloExplanation when facts.SoloParty => new(EntryAction.AdvanceExplanation, "繼續已知單人入場說明。"),
            EntryUiKind.SoloConfirmation when facts.SoloParty => new(EntryAction.Confirm, "確認單人入場。"),
            EntryUiKind.FloorChoice => new(EntryAction.SelectFloor, "選擇完整匹配的起始層。"),
            _ => Wait("視窗與隊伍／入場目標不符；不操作。")
        };
    }

    private static EntryDecision Wait(string reason) => new(EntryAction.Wait, reason);
    private static EntryDecision Conflict(string reason) => new(EntryAction.Conflict, reason);
}

/// <summary>One pending operation per exact UI revision. A changed screen is new evidence,
/// not a reason to replay the old operation. Uses monotonic elapsed milliseconds.</summary>
public sealed class EntryDispatchGate
{
    private string _revision = string.Empty;
    private long _seenAt;
    private long _lastDispatchAt = long.MinValue;
    private int _attempts;

    public void Observe(string revision, long now)
    {
        if (revision == _revision) return;
        _revision = revision;
        _seenAt = now;
        _lastDispatchAt = long.MinValue;
        _attempts = 0;
    }

    public bool CanDispatch(long now) => now - _seenAt >= 150 && _attempts < 3 &&
        (_lastDispatchAt == long.MinValue || now - _lastDispatchAt >= InteractionTiming.GetRetryMilliseconds());
    public bool Exhausted => _attempts >= 3;
    public void Dispatched(long now) { _lastDispatchAt = now; _attempts++; }
}

public static class EntryMenuChoice
{
    /// <summary>Ordinal comes from current labels, never unlock-dependent menu order.</summary>
    public static int FindUnique(IReadOnlyList<string> labels, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return -1;
        int match = -1;
        for (int i = 0; i < labels.Count; i++)
            if (labels[i] == expected) { if (match >= 0) return -1; match = i; }
        return match;
    }
}
