using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

/// <summary>One entry/exit attempt. Session owns continuation, prepared source and climb progress.</summary>
internal sealed class FarmingScenario(FarmingSession session) : IScenario
{
    private readonly DungeonData _dungeon = DungeonCatalog.TryGetByDungeonId(session.Plan.DungeonId, out var d)
        ? d
        : DungeonCatalog.PilgrimsTraverse;
    private RunContext? _context;
    private DungeonEntryController? _entry;
    private DungeonEntryController? _verification;
    private DungeonExitController? _exit;
    private GenericDeleteSaveFlow? _delete;
    private GenericDeleteSaveFlow? _entryReset;
    private SaveSlotSnapshot? _resetSource;
    private bool _creatingSave, _entered, _restDone, _complete, _recoverFailure, _aborted;
    private int _entryFloor;

    public string Name => $"{DungeonShortLabel(session.Plan.DungeonId)} {session.Plan.DisplayName} ({(session.Plan.SaveUse == SaveUse.Prepared ? session.Source.Description : $"{session.NextFloor} 層")})";
    public bool IsComplete => _complete;
    public bool ShouldLoop => !_aborted;
    public bool CountsAsCycle { get; private set; }
    public bool RecoversDutyFailure => _recoverFailure;
    public bool RequiresDutyCompletionEvent => !session.Plan.ReusesSave && !_recoverFailure && !_aborted;

    public void Initialize(RunContext context)
    {
        _context = context;
        context.ResetAttemptState();
        context.FarmingPlan = session.Plan;
        var plan = session.Plan;
        context.RunOptions.Set(new RunOptions
        {
            OpenGold = session.OpenGold,
            OpenSilver = session.OpenSilver,
            OpenBronze = session.OpenBronze,
            BandedEnabled = session.BandedEnabled,
            HarvestChestsRequired = plan.Mode == FarmingMode.Aetherpool,
            DiscoveryOnly = plan.Mode == FarmingMode.HoardDiscovery,
            LeaveMode = LeaveMode.AfterFinishDungeon,
            RequireValidatedAbandonPrompt = true
        });
        // Entry/attachment is selected from fresh observations in Update.
        // Until then no floor controller may act on this attempt.
        context.EntryReadyForDuty = false;
        if (plan.DungeonId == 4 && !plan.ReusesSave && plan.StartFloor <= 21)
        {
            try { context.PromeRadarMovement = new PromeRadarFarmingMovement(context); }
            catch (Exception ex)
            {
                context.StatusLine = $"PT21–30 MP 聯動初始化失敗：{ex.Message}";
                context.StatusIsError = true;
                Service.Log.Error($"[Farming] PromeRadar movement initialization failed: {ex}");
            }
        }
    }

    public void Update(IFramework framework)
    {
        var ctx = _context;
        if (ctx == null || _complete) return;
        if (ctx.StatusIsError) { _complete = true; return; }
        if (ctx.TerminalReviewRequested)
        {
            _aborted = true;
            _complete = true;
            return;
        }
        if (!_entered && !TryEnterDuty(ctx, framework)) return;
        if (ctx.Duty.StateReadFailed || ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning) return;
        ObserveDutyCompletion(ctx);
        if (session.Plan.HoldAfterClear && ctx.DutyCompletionObserved && !ctx.DutyFailureObserved)
        {
            ctx.EntryReadyForDuty = false;
            ctx.Navigator.CancelAll();
            ctx.StatusLine = "王戰已通關；保留副本現場，未退本、未清理存檔。";
            _aborted = true;
            _complete = true;
            Service.Log.Info("[Farming] HoldAfterClear: retaining duty and owned save for exit validation");
            return;
        }
        if (ctx.Duty.IsInDuty)
        {
            if (ctx.AttemptAborted)
            {
                _aborted = true;
                if (ctx.TryHoldFailedAttempt(ctx.AttemptAbortReason)) return;
                RequestLeave();
                return;
            }
            if (ctx.DutyFailureObserved)
            {
                if (ctx.TryHoldFailedAttempt("攻略失敗")) return;
                if (!session.Plan.ReusesSave)
                {
                    _recoverFailure = true;
                    RequestLeave();
                }
                return;
            }
            if (ctx.DutyCompletionObserved && !ctx.TerminalRewardsRequired && !session.Plan.ReusesSave)
            {
                RequestLeave();
                return;
            }
            if (session.Plan.ReusesSave && (ctx.Duty.IsBossFloor || ctx.Duty.Floor >= 30 || ctx.HarvestComplete))
            {
                if (!ctx.HarvestComplete)
                {
                    _aborted = true;
                    ctx.AttemptAborted = true;
                    ctx.AttemptAbortReason = "已到存檔保護邊界；停止速刷，不計完成輪數。";
                }
                else ctx.HarvestComplete = true;
                RequestLeave();
            }
            return;
        }

        if (ctx.TerminalRewardsRequired && ctx.DutyCompletionObserved && !ctx.DutyFailureObserved)
        {
            ctx.StatusLine = ctx.TerminalRoomObserved
                ? "最終獎勵房尚未完成驗收；FSD 已停止，請檢查終局獎勵紀錄。"
                : "尚未觀測到最終獎勵房；FSD 已停止並保留現場，請檢查終局物件與獎勵。";
            ctx.StatusIsError = true;
            _complete = true;
            return;
        }
        _exit ??= new(returnToEntrance: true, entranceNpcBaseId: _dungeon.NpcDataId);
        if (!_restDone)
        {
            if (!_exit.IsPrepared) _exit.Prepare(ctx);
            if (_exit.Update() != ExitOutcome.Completed) return;
            _restDone = true;
        }
        _aborted |= ctx.AttemptAborted;
        var result = new FarmingAttemptResult(
            _entryFloor, ctx.DutyCompletionObserved, ctx.DutyFailureObserved, ctx.HarvestComplete, _aborted);
        if (FarmingSessionPolicy.ReachedStopBoundary(session.Plan, result))
        {
            _aborted = true; // Stop the host before it can construct the next entry attempt.
            _complete = true;
            ctx.StatusLine = $"已通過 {session.Plan.StopAfterFloor} 層；停止並保留存檔，等待驗收。";
            Service.Log.Info($"[Farming] Progression stop boundary reached: cleared={_entryFloor + 9}, next={_entryFloor + 10}, ownedSlot={session.OwnedSlot}");
            return;
        }
        var decision = FarmingSessionPolicy.Decide(session.Plan, result);
        if (session.Plan.ReusesSave)
        {
            _verification ??= new(_dungeon, session.Source.ExpectedFloor ?? 0, prepared: session.Source, verifyOnly: true);
            if (!_verification.IsPrepared) _verification.Prepare(ctx);
            if (_verification.Update().Outcome == EntryOutcome.Pending) return;
            if (ctx.StatusIsError) { _complete = true; return; }
        }
        if (decision.DeleteOwnedSave)
        {
            if (session.OwnedSlot < 0)
            {
                ctx.StatusLine = "無法識別本任務建立的存檔；停止清理。";
                ctx.StatusIsError = true;
                _complete = true;
                return;
            }
            if (_delete == null)
            {
                _delete = new GenericDeleteSaveFlow(_dungeon,
                    session.OwnedSlot, slots => ValidateOwnedSave(slots));
                _delete.Prepare(ctx);
            }
            if (!_delete.Update(framework)) return;
            if (ctx.StatusIsError) { _complete = true; return; }
            session.ConfirmOwnedSaveDeleted();
            ctx.RecordDiagnostic?.Invoke("farming-owned-save-reset-completed", new { entryFloor = _entryFloor, failed = ctx.DutyFailureObserved });
        }
        CountsAsCycle = decision.CountCycle;
        _recoverFailure = decision.RecoverFailure;
        session.CompleteAttempt(decision, ctx.HoardDiscoveries);
        ctx.RecordDiagnostic?.Invoke("farming-attempt-finished", new
        {
            entryFloor = _entryFloor, completed = ctx.DutyCompletionObserved, failed = ctx.DutyFailureObserved,
            decision.CountCycle, decision.RecoverFailure, decision.NextFloor, decision.DeleteOwnedSave, decision.Error,
            failures = session.Failures
        });
        if (decision.Error.Length > 0)
        {
            ctx.StatusLine = ctx.AttemptAbortReason + decision.Error + " FSD 已停止。";
            ctx.StatusIsError = true;
        }
        else
            ctx.StatusLine = decision.RecoverFailure ? $"攻略失敗 {session.Failures} 次；從第 {session.NextFloor} 層重開。"
                : decision.CountCycle ? "本輪完成；存檔收尾已驗證。"
                : $"本組通關；將接續 {session.NextFloor} 層。";
        Service.Log.Info($"[Farming] mode={session.Plan.Mode}, entry={_entryFloor}, clear={ctx.DutyCompletionObserved}, failure={ctx.DutyFailureObserved}, harvest={ctx.HarvestComplete}, discoveries={ctx.HoardDiscoveries}, aborted={_aborted}, reason={ctx.AttemptAbortReason}, cycle={CountsAsCycle}, next={session.NextFloor}, deleteOwned={decision.DeleteOwnedSave}, source={session.Source.Description}");
        _complete = true;
    }

    private bool TryEnterDuty(RunContext ctx, IFramework framework)
    {
        if (_entryReset != null)
        {
            if (_resetSource is not { } resetSource)
            {
                ctx.StatusLine = "重置來源已遺失；停止，不改選其他存檔。";
                ctx.StatusIsError = true;
                _complete = true;
                return false;
            }
            if (!_entryReset.Update(framework)) return false;
            if (ctx.StatusIsError) { _complete = true; return false; }
            ctx.RecordDiagnostic?.Invoke("entry-save-reset-completed", new { slot = resetSource.Index, nextFloor = session.NextFloor });
            Service.Log.Info($"[Farming] Entry slot {resetSource.Index + 1} verified empty; continuing fresh entry at floor {session.NextFloor}.");
            _entryReset.Dispose();
            _entryReset = null;
            _entry?.Dispose();
            _entry = null;
        }
        // An entry controller owns its selected source and UI confirmation.
        // Do not bypass it by attaching when its own entry arrives in-game.
        if (_entry == null)
        {
            var facts = new FarmingEntryFacts(
                !ctx.Duty.StateReadFailed && !ctx.Duty.IsTransitioning && !DutyState.IsClientTransitioning,
                ctx.Duty.IsInDuty, ctx.Duty.DungeonId, ctx.Duty.Floor);
            var decision = FarmingEntryPolicy.Decide(facts, _dungeon.DutyDungeonId, session.NextFloor, session.OwnedSlot);
            ctx.StatusLine = decision.Reason;
            if (decision.Action == FarmingEntryAction.Wait) return false;
            ctx.RecordDiagnostic?.Invoke("farming-entry-observed", new
            {
                action = decision.Action.ToString(), facts, expectedDungeon = _dungeon.DutyDungeonId,
                entryFloor = session.NextFloor, ownedSlot = session.OwnedSlot
            });
            if (decision.Action == FarmingEntryAction.Conflict)
            {
                ctx.StatusIsError = true;
                return false;
            }
            if (decision.Action == FarmingEntryAction.Attach)
            {
                BeginDuty(ctx, session.NextFloor);
                ctx.PreparedSaveDescription = $"原副本接續，存檔 {session.OwnedSlot + 1}，起點 {_entryFloor} 層";
                ctx.StatusLine = $"原副本接續：第 {ctx.Duty.Floor} 層。";
                Service.Log.Info($"[Farming] Resumed existing duty: floor={ctx.Duty.Floor}, entryFloor={_entryFloor}, ownedSlot={session.OwnedSlot}, holdOnFailure={session.Plan.HoldOnFailure}");
                return true;
            }
            if (session.Plan.SaveUse == SaveUse.Prepared || session.OwnedSlot >= 0)
            {
                var source = session.PrepareSource();
                _entry = new(_dungeon, source.ExpectedFloor ?? 0, prepared: source);
            }
            else
            {
                _creatingSave = true;
                _entry = new(_dungeon, session.NextFloor, preferredSlot: _resetSource?.Index ?? 0,
                    resetWhenFull: session.Plan.Mode == FarmingMode.Hoard && _resetSource == null);
            }
            _entry.Prepare(ctx);
        }
        var entry = _entry.Update();
        if (entry.Outcome == EntryOutcome.ResetRequired)
        {
            if (session.Plan.Mode != FarmingMode.Hoard || session.Plan.SaveUse != SaveUse.Create || _resetSource != null || _entry.ResetSource == null)
            {
                ctx.StatusLine = "入場重置條件不符；停止，不改選其他存檔。";
                ctx.StatusIsError = true;
                return false;
            }
            _resetSource = _entry.ResetSource;
            var source = _resetSource.Value;
            ctx.RecordDiagnostic?.Invoke("entry-save-reset-started", new { slot = source.Index, source.StartFloor, source.Progress, nextFloor = session.NextFloor });
            Service.Log.Info($"[Farming] No empty slot; resetting pinned slot {source.Index + 1} ({source.Progress}) before fresh entry at floor {session.NextFloor}.");
            _entryReset = new GenericDeleteSaveFlow(_dungeon, source.Index, slots => SaveSlotResetPolicy.Validate(slots, source));
            _entryReset.Prepare(ctx);
            return false;
        }
        if (entry.Outcome != EntryOutcome.Entered) return false;
        if (ctx.Duty.DungeonId != _dungeon.DutyDungeonId || ctx.Duty.Floor != entry.Floor)
        {
            ctx.StatusLine = "入場地宮／樓層與來源不符；請手動退本並檢查存檔。";
            ctx.StatusIsError = true;
            return false;
        }
        if (_creatingSave && !session.BindCreatedSave(entry.Slot))
        {
            ctx.StatusLine = "入場來源未由本任務建立；停止自動化，請手動退本。";
            ctx.StatusIsError = true;
            return false;
        }
        BeginDuty(ctx, entry.Floor);
        return true;
    }

    private void BeginDuty(RunContext ctx, int entryFloor)
    {
        _entryFloor = entryFloor;
        _entered = true;
        ctx.TerminalRewardsRequired =
            _entryFloor == 91 && ResultAltarPolicy.TryGet(_dungeon.DungeonId, 100, out _) ||
            _entryFloor == 191 && Potd200RewardPolicy.IsTerminal(_dungeon.DungeonId, 200);
        ctx.EntryReadyForDuty = true;
    }

    private void ObserveDutyCompletion(RunContext ctx)
    {
        // This result can predate FSD attachment. Use the same current evidence
        // for normal entry and attachment, then retain it through exit/cleanup.
        if (ctx.DutyCompletionObserved || ctx.DutyFailureObserved || !ctx.Duty.IsInDuty ||
            ctx.Duty.DungeonId != _dungeon.DutyDungeonId || ctx.Duty.Floor < _entryFloor ||
            ctx.Duty.Floor >= _entryFloor + 10 || !ctx.Duty.IsBossFloor || !ctx.Duty.PassageOpen ||
            Service.DutyState.IsDutyStarted ||
            Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] ||
            Service.LocalPlayer is not { IsDead: false }) return;
        ctx.MarkDutyCompleted();
        ctx.RecordDiagnostic?.Invoke("observed-completed-duty", new
        {
            floor = ctx.Duty.Floor, passageOpen = ctx.Duty.PassageOpen, ownedSlot = session.OwnedSlot
        });
    }

    internal FarmingSession Session => session;
    private static string DungeonShortLabel(uint dungeonId) => dungeonId switch
    {
        1 => "死宮",
        2 => "天宮",
        _ => "PT"
    };
    private string? ValidateOwnedSave(IReadOnlyList<SaveSlotSnapshot> slots) =>
        OwnedSavePolicy.ValidateForDeletion(slots, session.OwnedSlot, _entryFloor, session.Source.Pinned);
    private void RequestLeave()
    {
        if (_context == null || _context.RunOptions.Current.LeaveMode == LeaveMode.Immediate) return;
        _context.RecordDiagnostic?.Invoke("farming-duty-exit-requested", new
        {
            floor = _context.Duty.Floor, completed = _context.DutyCompletionObserved,
            failed = _context.DutyFailureObserved, aborted = _context.AttemptAborted
        });
        _context.RunOptions.Update(o => o.LeaveMode = LeaveMode.Immediate);
    }
    public void Dispose()
    {
        _entry?.Dispose();
        _verification?.Dispose();
        _exit?.Dispose();
        _entryReset?.Dispose();
        _delete?.Dispose();
        _context = null;
    }
}
