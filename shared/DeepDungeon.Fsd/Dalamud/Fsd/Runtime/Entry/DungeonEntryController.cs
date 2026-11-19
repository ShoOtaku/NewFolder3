using System.Diagnostics;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

internal enum EntryOutcome { Pending, Entered, SourceVerified, ResetRequired, Conflict }
internal readonly record struct EntryResult(EntryOutcome Outcome, int Slot, int Floor, string Reason);

/// <summary>One goal, observed UI content, one guarded command. No remembered UI steps.</summary>
internal sealed class DungeonEntryController : IDisposable
{
    private readonly DungeonData _dungeon;
    private readonly int _requestedFloor, _preferredSlot;
    private readonly PreparedSaveBinding? _prepared;
    private readonly bool _verifyOnly;
    private readonly bool _resetWhenFull;
    private readonly int? _selectedProgressionSlot;
    private EntranceWindowCleanup _cleanup = new();
    private bool _commenced;
    private readonly EntryDispatchGate _dispatch = new();
    private RunContext? _context;
    private PtEntryText? _text;
    private NavigationHelper? _navigation;
    private SaveSlotSnapshot? _bound;
    private bool _verifiedSource;
    private long _nextReadAt;
    private long _lastNativeRequestAt = long.MinValue;
    private long _panelsAbsentSince;
    private string _lastStatus = "";
    public bool IsPrepared => _context != null;
    public EntryResult Result { get; private set; } = new(EntryOutcome.Pending, -1, 0, "");
    public int SourceFloor => _bound is { Empty: false } source ? source.StartFloor : _requestedFloor;
    public int SourceSlot => _bound?.Index ?? -1;
    public SaveSlotSnapshot? SelectedSource => _bound;
    internal SaveSlotSnapshot? ResetSource { get; private set; }

    public DungeonEntryController(DungeonData dungeon, int floor, int preferredSlot = 0,
        PreparedSaveBinding? prepared = null, bool verifyOnly = false, bool resetWhenFull = false,
        int? selectedProgressionSlot = null)
    {
        _dungeon = dungeon;
        _requestedFloor = floor;
        _preferredSlot = preferredSlot;
        _prepared = prepared;
        _verifyOnly = verifyOnly;
        _resetWhenFull = resetWhenFull && prepared == null && !verifyOnly;
        _selectedProgressionSlot = selectedProgressionSlot;
    }

    public void Prepare(RunContext context)
    {
        _context = context;
        if (!_verifyOnly) context.EntryReadyForDuty = false;
        _navigation = new(context.Navigator);
        try
        {
            _text = PtEntryText.ForDungeon(_dungeon.DutyDungeonId);
            if (_prepared == null && !_selectedProgressionSlot.HasValue) _ = _text.FloorLabel(_requestedFloor);
            _cleanup = new();
            _commenced = false;
            _panelsAbsentSince = 0;
            SetStatus("等待可識別的入口 UI。");
        }
        catch (Exception ex) { Fail($"無法初始化入場控制器：{ex.Message}"); }
    }

    public unsafe EntryResult Update()
    {
        var ctx = _context;
        if (ctx == null || _text == null || Result.Outcome != EntryOutcome.Pending) return Result;
        if (DutyState.IsClientTransitioning || ctx.Duty.IsTransitioning)
        { _navigation?.Cancel(); SetStatus("等待過圖與角色載入。"); return Result; }
        long now = Environment.TickCount64;
        if (now < _nextReadAt) return Result;
        _nextReadAt = now + 100;

        if (!ctx.Duty.IsInDuty)
        {
            if (_commenced) { SetStatus("已確認入場，等待進本。"); return Result; }
            if (!_cleanup.Ready(ctx)) return Result;
        }

        var ui = EntryUiReader.Read(_text, SourceFloor);
        _dispatch.Observe(ui.Revision, now);
        if (ui.Kind != EntryUiKind.None) _navigation?.Cancel();
        if (!ctx.Duty.IsInDuty && ui.QueuePending && ui.QueueMatches)
            DeepDungeonUi.CloseSaveAndMenuIfNoModal();
        if (_verifyOnly)
        {
            if (ctx.Duty.IsInDuty) return Fail("存檔驗證期間不應入場。");
            if (_verifiedSource)
            {
                if (ui.Kind is EntryUiKind.ReadSaves or EntryUiKind.EntranceMenu)
                { DeepDungeonUi.TryCloseAddon(ui.Addon); return Result; }
                if (ui.Kind == EntryUiKind.None)
                { Result = new(EntryOutcome.SourceVerified, SourceSlot, SourceFloor, "已驗證來源存檔。"); return Result; }
            }
        }

        var facts = new EntryFacts(false, ctx.Duty.IsInDuty, ctx.Duty.DungeonId, ctx.Duty.Floor,
            ui.Kind, _bound.HasValue, ui.QueuePending, ui.QueueMatches, Service.PartyList.Length <= 1);
        bool creating = _bound?.Empty ?? (_prepared == null && !_selectedProgressionSlot.HasValue);
        var decision = EntryUiPolicy.Decide(facts, _dungeon.DutyDungeonId, SourceFloor, creating);
        if (decision.Action == EntryAction.Conflict) return Fail(decision.Reason);
        if (decision.Action == EntryAction.Complete)
        {
            if (!DeepDungeonUi.WaitAndCloseSaveAndMenuAfterEntry(ref _panelsAbsentSince))
            {
                SetStatus("已入場，正在關閉殘留入口視窗。");
                return Result;
            }
            Result = new(EntryOutcome.Entered, SourceSlot, SourceFloor, decision.Reason);
            ctx.EntryReadyForDuty = true;
            SetStatus(decision.Reason);
            return Result;
        }
        if (decision.Action == EntryAction.Wait)
        { SetStatus(decision.Reason + (ui.Detail.Length > 0 ? $" [{ui.Addon}: {ui.Detail}]" : "")); return Result; }

        // Between server-driven dialogs the parent list can briefly be exposed.
        // Do not submit a second slot request before the next modal arrives.
        if (decision.Action is EntryAction.OpenSaves or EntryAction.SelectSave &&
            _lastNativeRequestAt != long.MinValue && now - _lastNativeRequestAt < 1200)
            return Result;

        if (decision.Action == EntryAction.SelectSave)
        {
            if (ui.Slots == null) return Result;
            if (_prepared != null)
            {
                if (!BindPrepared(ui.Slots, out string error)) { SetStatus(error); return Result; }
                if (_verifyOnly) { _verifiedSource = true; return Result; }
            }
            else if (_selectedProgressionSlot is { } selectedSlot && _bound == null)
            {
                int maxFloor = _dungeon.DungeonId == 1 ? 200 : 100;
                if (!SelectedProgressionSavePolicy.TrySelect(ui.Slots, selectedSlot, maxFloor, out var source, out var error))
                {
                    SetStatus(error);
                    return Result;
                }
                _bound = source;
            }
            else if (_bound == null)
            {
                var candidates = ui.Slots.Where(slot => slot.Empty && slot.Enterable).ToArray();
                if (candidates.Length == 0)
                {
                    if (!_resetWhenFull)
                    {
                        SetStatus("目前沒有可用空槽；等待存檔列表變更。");
                        return Result;
                    }
                    if (!SaveSlotResetPolicy.TrySelect(ui.Slots, _preferredSlot, out var source, out var error))
                    {
                        SetStatus(error);
                        return Result;
                    }
                    ResetSource = source;
                    string reason = $"沒有空槽；先重置存檔 {source.Index + 1}，再建立 {SourceFloor} 層存檔。";
                    Result = new(EntryOutcome.ResetRequired, source.Index, SourceFloor, reason);
                    SetStatus(reason);
                    return Result;
                }
                _bound = candidates.FirstOrDefault(slot => slot.Index == _preferredSlot, candidates[0]);
            }
            if (!ui.Slots.Any(slot => SameSource(_bound!.Value, slot) && slot.Enterable))
                return Fail("綁定的來源存檔已變更；不改選其他槽位。");
        }
        if (decision.Action == EntryAction.InteractNpc)
        {
            if (_lastNativeRequestAt != long.MinValue && now - _lastNativeRequestAt < InteractionTiming.GetRetryMilliseconds()) return Result;
            ApproachNpc(now);
            return Result;
        }
        if (!_dispatch.CanDispatch(now))
        {
            if (_dispatch.Exhausted) SetStatus("同一視窗操作尚未生效；停止重送，等待內容變更。");
            return Result;
        }

        // Re-resolve the live addon and exact content in the same framework callback.
        var current = EntryUiReader.Read(_text, SourceFloor);
        if (current.Revision != ui.Revision || current.QueuePending != ui.QueuePending || current.QueueMatches != ui.QueueMatches)
            return Result;
        bool sent = decision.Action switch
        {
            EntryAction.OpenSaves => EntryUiReader.ActivateChoice((AtkUnitBase*)current.Address, _text["START_MENU_ENTER"]),
            EntryAction.SelectSave => _bound is { } slot && DeepDungeonUi.ClickSaveSlotForEntry(slot.Index),
            EntryAction.SelectFixedParty => EntryUiReader.ActivateSelectStringChoice((AtkUnitBase*)current.Address, _text["ENTER_MENU_FIXED"]),
            EntryAction.SelectFloor => EntryUiReader.ActivateSelectStringChoice((AtkUnitBase*)current.Address, _text.FloorLabel(SourceFloor)),
            EntryAction.Confirm => DeepDungeonUi.Fire((AtkUnitBase*)current.Address, 0),
            EntryAction.AdvanceExplanation => DeepDungeonUi.Fire((AtkUnitBase*)current.Address),
            EntryAction.Commence => DeepDungeonUi.ClickCommenceButton(),
            _ => false
        };
        _dispatch.Dispatched(now);
        if (sent) _lastNativeRequestAt = now;
        if (sent && decision.Action == EntryAction.Commence) _commenced = true;
        SetStatus(sent ? decision.Reason : "原生操作未送出；等待重試。");
        ctx.RecordDiagnostic?.Invoke("entry-action", new { action = decision.Action.ToString(), ui.Kind,
            ui.Addon, ui.AddonId, sourceSlot = SourceSlot, floor = SourceFloor, sent });
        Service.Log.Info($"[DungeonEntry] action={decision.Action}, ui={ui.Kind}, slot={SourceSlot}, floor={SourceFloor}, sent={sent}");
        return Result;
    }

    private bool BindPrepared(IReadOnlyList<SaveSlotSnapshot> slots, out string error)
    {
        if (!_prepared!.Observe(slots, out var source, out error)) return false;
        _bound = source;
        if (_context != null) _context.PreparedSaveDescription = _prepared.Description;
        return true;
    }

    private static bool SameSource(in SaveSlotSnapshot expected, in SaveSlotSnapshot actual) =>
        expected.Index == actual.Index && expected.Empty == actual.Empty && expected.StartFloor == actual.StartFloor &&
        expected.Progress == actual.Progress && expected.Identity == actual.Identity;

    private void ApproachNpc(long now)
    {
        var npc = NpcInteractionGuard.FindByBaseId(_dungeon.NpcDataId);
        var player = Service.LocalPlayer;
        if (npc == null || player == null) { SetStatus("等待入口 NPC 與角色。"); return; }
        if (!NpcInteractionGuard.InteractionRange.HasArrived(player.Position, npc.Position))
        {
            if (!moveHelper.VNav.NavmeshReady()) { SetStatus("等待入口導航網格。"); return; }
            var state = _navigation!.Navigate(npc.Position, player.Position, NpcInteractionGuard.InteractionRange);
            SetStatus($"前往入口 NPC：{state}");
            return;
        }
        _navigation?.Cancel();
        if (!_dispatch.CanDispatch(now)) return;
        bool sent = NpcInteractionGuard.TryInteract(_dungeon.NpcDataId, "PT entry", out var status);
        _dispatch.Dispatched(now);
        if (sent) _lastNativeRequestAt = now;
        SetStatus(status);
    }

    private EntryResult Fail(string reason)
    {
        Result = new(EntryOutcome.Conflict, SourceSlot, SourceFloor, reason);
        if (_context != null) _context.StatusIsError = true;
        SetStatus(reason);
        _navigation?.Cancel();
        return Result;
    }
    private void SetStatus(string status)
    {
        if (_context != null) _context.StatusLine = status;
        if (_lastStatus == status) return;
        _lastStatus = status;
        Service.Log.Info($"[DungeonEntry] {status}");
    }
    public void Dispose() { _navigation?.Cancel(); _context = null; }
}
