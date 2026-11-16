using System.Numerics;
using global::Dalamud.Utility;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Extensions;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

internal enum ExitOutcome { Pending, Completed }

/// <summary>Reconciles a requested exit with current duty, actor and UI facts.</summary>
internal sealed class DungeonExitController(bool returnToEntrance, uint entranceNpcBaseId = 0) : IDisposable
{
    private const uint PtRestExitDataId = 2014757;
    private const uint PotdRestExitDataId = 2006016;
    private const uint HohRestExitDataId = 2009523;
    private const uint EoRestExitDataId = 2013290;
    private readonly uint _entranceNpcBaseId = entranceNpcBaseId;
    private static readonly NavigationRange RestExitRange = new(2f, NavigationDistanceMetric.Spatial);
    private static readonly string[] BlockingWindows = ["SelectString", "Talk", "EventTalk", "ContentsFinderConfirm"];
    private static readonly uint[] RestWarpRowIds = [131612, 131236, 131298, 131517];
    private RunContext? _context;
    private NavigationHelper? _navigation;
    private EntryDispatchGate _dispatch = new();
    private string _dutyPrompt = "", _resultTitle = "", _resultExit = "", _status = "";
    private readonly HashSet<string> _restPrompts = new(StringComparer.Ordinal);
    private long _nextRead, _lastRequest = long.MinValue;
    public bool IsPrepared => _context != null;
    public ExitOutcome Outcome { get; private set; }

    public void Prepare(RunContext context)
    {
        _context = context;
        _navigation = new(context.Navigator);
        _dispatch = new();
        _nextRead = 0;
        _lastRequest = long.MinValue;
        Outcome = ExitOutcome.Pending;
        _dutyPrompt = PtEntryText.Normalize(Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>()
            .GetRow(2545).Text.ExtractText());
        _restPrompts.Clear();
        var warpSheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Warp>();
        foreach (uint warpId in RestWarpRowIds)
        {
            string q = PtEntryText.Normalize(warpSheet.GetRow(warpId).Question.ExtractText());
            if (q.Length > 0) _restPrompts.Add(q);
        }
        _resultTitle = PtEntryText.Normalize(Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(10438).Text.ExtractText());
        _resultExit = PtEntryText.Normalize(Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(10444).Text.ExtractText());
        SetStatus("等待目前副本與出口 UI。");
    }

    public unsafe ExitOutcome Update()
    {
        var ctx = _context;
        if (ctx == null || Outcome == ExitOutcome.Completed) return Outcome;
        if (DutyState.IsClientTransitioning || ctx.Duty.IsTransitioning)
        { _navigation?.Cancel(); SetStatus("等待退本過圖完成。"); return Outcome; }
        long now = Environment.TickCount64;
        if (now < _nextRead) return Outcome;
        _nextRead = now + 100;
        var exit = ctx.Duty.IsInDuty ? FindResultExit(ctx) : FindNearestRestExit();
        bool atEntrance = IsAtEntrance();
        var ui = ReadUi(ctx.Duty.IsInDuty);
        var facts = new ExitFacts(false, ctx.Duty.IsInDuty, atEntrance, !ctx.Duty.IsInDuty && exit != null,
            Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat], ui.Kind,
            ctx.Duty.IsInDuty && exit != null);
        var action = ExitUiPolicy.Decide(facts, returnToEntrance);
        string revision = $"{facts.InDuty}:{facts.AtEntrance}:{facts.RestExitPresent}:{facts.DutyExitPresent}:{ui.Revision}";
        _dispatch.Observe(revision, now);
        if (action == ExitAction.Complete)
        {
            _navigation?.Cancel();
            Outcome = ExitOutcome.Completed;
            SetStatus(returnToEntrance ? "已回到入口 NPC。" : "已離開副本。");
            return Outcome;
        }
        if (action == ExitAction.Wait)
        {
            _navigation?.Cancel();
            SetStatus(ui.Kind is ExitUiKind.Unknown or ExitUiKind.Incomplete
                ? $"等待可識別的退本 UI：{ui.Revision}" : "等待出口或可退本狀態。");
            return Outcome;
        }
        if (!_dispatch.CanDispatch(now))
        {
            if (_dispatch.Exhausted) SetStatus("退本操作尚未生效；等待新的畫面或狀態，不再重送。");
            return Outcome;
        }
        if (action is ExitAction.RequestDutyExit or ExitAction.ApproachRestExit or ExitAction.ApproachDutyExit &&
            _lastRequest != long.MinValue && now - _lastRequest < InteractionTiming.GetRetryMilliseconds()) return Outcome;
        if (action is ExitAction.ApproachRestExit or ExitAction.ApproachDutyExit)
        {
            var player = Service.LocalPlayer;
            if (player == null || exit == null || !exit.IsTargetable) return Outcome;
            if (!RestExitRange.HasArrived(player.Position, exit.Position))
            {
                var nav = _navigation!.Navigate(exit.Position, player.Position, RestExitRange);
                SetStatus($"前往{(ctx.Duty.IsInDuty ? "終局" : "休息區")}出口：{nav}");
                return Outcome;
            }
        }
        _navigation?.Cancel();
        var current = ReadUi(ctx.Duty.IsInDuty);
        if (current.Revision != ui.Revision || current.Kind != ui.Kind) return Outcome;
        bool sent = false;
        switch (action)
        {
            case ExitAction.LeaveResult:
                var result = (AtkUnitBase*)current.Address;
                var node = result->UldManager.SearchNodeById(25);
                if (node != null)
                {
                    var evt = node->AtkEventManager.Event;
                    for (int i = 0; evt != null && i < 64; i++, evt = evt->NextEvent)
                        if (evt->State.EventType == AtkEventType.ButtonClick)
                        { result->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt); sent = true; break; }
                }
                break;
            case ExitAction.RequestDutyExit:
                sent = LeaveDutyHelper.TryRequestLeaveDuty();
                break;
            case ExitAction.ConfirmDutyExit:
            case ExitAction.ConfirmRestExit:
                sent = DeepDungeonUi.Fire((AtkUnitBase*)current.Address, 0);
                break;
            case ExitAction.ApproachRestExit:
            case ExitAction.ApproachDutyExit:
                var target = TargetSystem.Instance();
                if (target != null && exit != null && exit.IsTargetable)
                {
                    var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)exit.Address;
                    target->Target = native;
                    target->InteractWithObject(native);
                    sent = true;
                }
                break;
        }
        _dispatch.Dispatched(now);
        if (sent) _lastRequest = now;
        SetStatus($"退本操作 {action}：{(sent ? "已送出，等待結果" : "未送出")}");
        ctx.RecordDiagnostic?.Invoke("exit-action", new { action = action.ToString(), ui.Kind, ui.Revision, sent });
        return Outcome;
    }

    private unsafe (ExitUiKind Kind, nint Address, string Revision) ReadUi(bool inDuty)
    {
        if (DeepDungeonUi.TryGetAddon("SelectYesno", out var yesno))
        {
            string identity = $"SelectYesno:{yesno->Id}:{(nint)yesno:X}";
            if (!yesno->IsAddonAndNodesReady() ||
                !DeepDungeonUi.TryGetConfirmationPromptText(yesno, out var prompt, out _))
                return (ExitUiKind.Incomplete, (nint)yesno, identity);
            string text = PtEntryText.Normalize(prompt);
            var button = ((AddonSelectYesno*)yesno)->YesButton;
            var kind = button == null || !button->IsEnabled ? ExitUiKind.Incomplete
                : text.Length > 0 && text == _dutyPrompt ? ExitUiKind.DutyConfirmation
                : text.Length > 0 && _restPrompts.Contains(text) ? ExitUiKind.RestConfirmation : ExitUiKind.Unknown;
            return (kind, (nint)yesno, identity + ":" + text);
        }
        if (!inDuty)
        {
            foreach (var name in BlockingWindows)
                if (DeepDungeonUi.TryGetAddon(name, out var addon))
                    return (ExitUiKind.Unknown, (nint)addon, $"{name}:{addon->Id}:{(nint)addon:X}");
        }
        if (DeepDungeonUi.TryGetAddon("DeepDungeonResult", out var result))
        {
            string identity = $"DeepDungeonResult:{result->Id}:{(nint)result:X}";
            if (!result->IsAddonAndNodesReady()) return (ExitUiKind.Incomplete, (nint)result, identity);
            var titleNode = result->UldManager.SearchNodeById(2);
            var title = titleNode == null ? null : titleNode->GetAsAtkTextNode();
            var buttonNode = result->UldManager.SearchNodeById(25);
            var button = buttonNode == null ? null : buttonNode->GetAsAtkComponentButton();
            if (title == null || button == null || button->ButtonTextNode == null)
                return (ExitUiKind.Incomplete, (nint)result, identity);
            string titleText = PtEntryText.Normalize(title->NodeText.ExtractText());
            string buttonText = PtEntryText.Normalize(button->ButtonTextNode->NodeText.ExtractText());
            var kind = !button->IsEnabled ? ExitUiKind.Incomplete
                : _resultTitle.Length > 0 && titleText == _resultTitle && buttonText == _resultExit
                    ? ExitUiKind.Result : ExitUiKind.Unknown;
            return (kind, (nint)result, identity + ":" + titleText + ":" + buttonText);
        }
        return (ExitUiKind.None, 0, "none");
    }

    private static global::Dalamud.Game.ClientState.Objects.Types.IGameObject? FindResultExit(RunContext context)
    {
        if (!context.TerminalRoomObserved || context.TerminalRewardsRequired || !context.DutyCompletionObserved ||
            !Potd200RewardPolicy.IsTerminal(context.Duty.DungeonId, context.Duty.Floor)) return null;
        foreach (var obj in Service.GameObjects)
            if (obj.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj &&
                obj.BaseId == Potd200RewardPolicy.ExitBaseId && obj.IsTargetable) return obj;
        return null;
    }

    private static global::Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNearestRestExit()
    {
        var player = Service.LocalPlayer;
        global::Dalamud.Game.ClientState.Objects.Types.IGameObject? bestTargetable = null;
        global::Dalamud.Game.ClientState.Objects.Types.IGameObject? bestAny = null;
        float bestTargetableDistSq = float.MaxValue;
        float bestAnyDistSq = float.MaxValue;

        foreach (var obj in Service.GameObjects)
        {
            if (obj.BaseId is not (PtRestExitDataId or PotdRestExitDataId or HohRestExitDataId or EoRestExitDataId))
                continue;

            if (player == null)
            {
                if (obj.IsTargetable) return obj;
                bestAny ??= obj;
                continue;
            }

            float distSq = Vector3.DistanceSquared(player.Position, obj.Position);
            if (obj.IsTargetable && distSq < bestTargetableDistSq)
            {
                bestTargetable = obj;
                bestTargetableDistSq = distSq;
            }
            if (distSq < bestAnyDistSq)
            {
                bestAny = obj;
                bestAnyDistSq = distSq;
            }
        }

        return bestTargetable ?? bestAny;
    }

    private bool IsAtEntrance()
    {
        if (_entranceNpcBaseId != 0)
            return NpcInteractionGuard.FindByBaseId(_entranceNpcBaseId) is { IsTargetable: true };
        foreach (var dungeon in DungeonCatalog.All)
        {
            if (dungeon.NpcDataId != 0 && NpcInteractionGuard.FindByBaseId(dungeon.NpcDataId) is { IsTargetable: true })
                return true;
        }
        return false;
    }

    private void SetStatus(string status)
    {
        if (_context != null) _context.StatusLine = status;
        if (status == _status) return;
        _status = status;
        Service.Log.Info($"[DungeonExit] {status}");
    }
    public void Dispose() { _navigation?.Cancel(); _context = null; }
}
