using global::Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private string? _farmingError;
    private static readonly string[] FarmingModeLabels =
        ["挖寶藏", "強化／陶片", "寶藏發現成就", "死靈術士", "自由探索"];
    private static readonly string[] HoardFloorsetLabels = ["21–30 層", "31–40 層"];
    private static readonly int[] ExplorationStartFloors = [1, 21, 31, 51, 71];
    private static readonly string[] ExplorationStartFloorLabels = ["1 層", "21 層", "31 層", "51 層", "71 層"];
    private static readonly string[] ProgressionSaveSlotLabels = ["存檔槽位 1", "存檔槽位 2"];
    private static readonly string[] FarmingDungeonLabels = ["朝聖交錯路", "死者宮殿", "天之御柱"];

    private bool IsFixedFloorsetHoard => _configuration.Farming.DungeonId == 4 &&
        _fsfScenarioIndex is 0 or 1 &&
        _configuration.Farming.Mode == FarmingMode.Hoard &&
        _configuration.Farming.GetPreferences(_configuration).BandedEnabled == true;

    private string? GetSelectedDetailedMapScenarioKey() =>
        IsFixedFloorsetHoard
            ? DetailedMapCatalogManager.GetScenarioKey(_fsfScenarioIndex)
            : null;

    private void DrawFarmingSelection(bool running)
    {
        var settings = _configuration.Farming;
        int dungeon = settings.DungeonId switch
        {
            1 => 1,
            2 => 2,
            _ => 0
        };
        ImGui.BeginDisabled(running);
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("地宮##fsdDungeon", ref dungeon, FarmingDungeonLabels, FarmingDungeonLabels.Length))
        {
            settings.DungeonId = dungeon switch
            {
                1 => 1u,
                2 => 2u,
                _ => 4u
            };
            if (settings.DungeonId != 4)
            {
                _fsfScenarioIndex = 1;
                _configuration.NecromancerFsdScenarioIndex = 1;
            }
            _configuration.Save();
            _farmingError = null;
        }
        ImGui.EndDisabled();

        bool pt = settings.DungeonId == 4;
        var effectiveMode = pt ? settings.Mode : FarmingMode.DeepProgression;
        int mode = (int)effectiveMode;
        ImGui.BeginDisabled(running || !pt);
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("模式##fsdMode", ref mode, FarmingModeLabels, FarmingModeLabels.Length) && pt)
        {
            settings.Mode = (FarmingMode)mode;
            _configuration.Save();
            _farmingError = null;
        }
        ImGui.EndDisabled();

        ImGui.BeginDisabled(running);
        if (effectiveMode == FarmingMode.DeepProgression)
        {
            int slot = _configuration.NecromancerSaveSlot;
            if (ImGui.Combo("死靈術士存檔##fsdProgressionSave", ref slot, ProgressionSaveSlotLabels, 2))
            {
                _configuration.NecromancerSaveSlot = slot;
                _configuration.Save();
            }
            ImGui.TextWrapped("只使用指定槽位；有存檔就驗證後接續，空槽從第1層新建。確認敗北才清理，登頂保留存檔。副本內接管不能證明來源時不自動刪檔。");
        }
        else if (effectiveMode == FarmingMode.FreeExploration)
        {
            int floor = Array.IndexOf(ExplorationStartFloors, settings.ExplorationStartFloor);
            if (ImGui.Combo("起始樓層##fsdExplorationStart", ref floor, ExplorationStartFloorLabels, ExplorationStartFloors.Length))
            {
                settings.ExplorationStartFloor = ExplorationStartFloors[floor];
                _configuration.Save();
            }
            ImGui.TextWrapped("使用空槽新建，每10層接續至100層；只清理本輪建立的存檔。");
        }
        else if (effectiveMode == FarmingMode.Hoard)
        {
            int floor = _fsfScenarioIndex;
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("起始層段##fsdFloorset", ref floor, HoardFloorsetLabels, 2))
            {
                _fsfScenarioIndex = floor;
                _configuration.NecromancerFsdScenarioIndex = floor;
                _configuration.Save();
            }
        }
        if (effectiveMode == FarmingMode.Hoard)
            ImGui.TextWrapped("優先使用空槽；兩槽皆滿才校驗並重置存檔 1。同次入場最多重置一次。");
        else if (effectiveMode == FarmingMode.Aetherpool)
            ImGui.TextWrapped("準備1份較多跳層松杜香的任意起點層存檔");
        else if (effectiveMode == FarmingMode.HoardDiscovery)
            ImGui.TextWrapped("準備1份有至少1個感知寶藏的任意起點層存檔");
        ImGui.EndDisabled();
    }

    private void DrawFarmingOptions()
    {
        ImGui.Separator();
        ImGui.Text("FSD 選項");
        ImGui.Indent();
        ImGui.TextDisabled($"寶藏拾取：本次 {_hoardStatistics.SessionPickups}，累計 {_hoardStatistics.LifetimePickups?.ToString() ?? "匯入中"}");
        bool running = _ddHost?.FsdActive == true;
        var activePlan = running ? _ddHost?.Context?.FarmingPlan : null;
        bool pt = (activePlan?.DungeonId ?? _configuration.Farming.DungeonId) == 4;
        var mode = activePlan?.Mode ?? (pt ? _configuration.Farming.Mode : FarmingMode.DeepProgression);
        var preferences = _configuration.Farming.GetPreferences(_configuration, mode);
        var options = running ? _ddHost?.RunOptionsProvider?.Current : null;
        bool hoard = options?.BandedEnabled ?? (preferences.BandedEnabled == true);
        bool gold = options?.OpenGold ?? preferences.OpenGold;
        bool silver = options?.OpenSilver ?? preferences.OpenSilver;
        bool bronze = options?.OpenBronze ?? preferences.OpenBronze;
        bool changed;
        ImGui.BeginDisabled(ProgressionPauseLocked);
        ImGui.BeginDisabled(FarmingTargetPolicy.LocksHoard(mode));
        changed = ImGui.Checkbox("探索埋藏的寶藏", ref hoard);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(FarmingTargetPolicy.LocksGold(mode));
        changed |= ImGui.Checkbox("開啟金寶箱", ref gold);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(FarmingTargetPolicy.LocksSilverBronze(mode) ||
            mode == FarmingMode.Aetherpool && silver && !bronze);
        changed |= ImGui.Checkbox("開啟銀寶箱", ref silver);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(FarmingTargetPolicy.LocksSilverBronze(mode) ||
            mode == FarmingMode.Aetherpool && bronze && !silver);
        changed |= ImGui.Checkbox("開啟銅寶箱", ref bronze);
        ImGui.EndDisabled();
        if (changed)
        {
            preferences.BandedEnabled = hoard;
            preferences.OpenGold = gold; preferences.OpenSilver = silver; preferences.OpenBronze = bronze;
            _ddHost?.SetFarmingTargets(hoard, gold, silver, bronze);
            _configuration.Save();
            _farmingError = null;
        }
        ImGui.BeginDisabled(mode == FarmingMode.HoardDiscovery);
        bool aggressiveChestInteraction = _configuration.AggressiveChestInteraction;
        if (ImGui.Checkbox("我開箱會卡住很久##aggressiveChestInteraction", ref aggressiveChestInteraction))
        {
            _configuration.AggressiveChestInteraction = aggressiveChestInteraction;
            _configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32f);
            ImGui.TextUnformatted("啟用該項目會增加開箱的力度, 如幀數較低或戰鬥時很難成功開箱, 請啟用以增加對輸出或其他外掛搶佔成功率. 若沒有遇到這個問題, 請不要啟用");
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.EndDisabled();
        ImGui.Spacing();
        ImGui.BeginDisabled(mode is FarmingMode.Aetherpool or FarmingMode.HoardDiscovery);
        bool pause = _configuration.PauseAfterCurrentFloorset;
        if (ImGui.Checkbox("完成當前10層後停止並留檔##fsdPauseAfterFloorset", ref pause))
        {
            _configuration.PauseAfterCurrentFloorset = pause;
            _configuration.Save();
        }
        ImGui.EndDisabled();
        if (activePlan?.AttachCurrentDuty != true && mode != FarmingMode.DeepProgression)
            DrawCycleOptions(preferences);
        ImGui.EndDisabled();
        if (running)
        {
            if (ProgressionPauseLocked && _ddHost is { } active && !string.IsNullOrEmpty(active.CurrentStatus))
                ImGui.TextWrapped(active.CurrentStatus);
            ImGui.TextDisabled($"完成循環：{_ddHost!.CompletedLoops}/{(_ddHost.Infinite ? "∞" : _ddHost.TargetLoops)}");
            if (mode == FarmingMode.DeepProgression)
                ImGui.TextDisabled($"失敗重試：{_ddHost.FarmingFailures}");
            if (mode == FarmingMode.HoardDiscovery)
                ImGui.TextDisabled($"寶藏發現：{_ddHost.HoardDiscoveries}");
            if (_ddHost.Context?.FarmingPlan?.SaveUse == SaveUse.Prepared)
                ImGui.TextDisabled(_ddHost.PreparedSaveDescription);
        }
        ImGui.Unindent();
        string? error = _farmingError ?? (_ddHost is { FsdActive: false, LastStatusIsError: true }
            ? _ddHost.LastStatus : null);
        if (!string.IsNullOrEmpty(error))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f));
            ImGui.TextWrapped(error);
            ImGui.PopStyleColor();
        }
    }

    private void DrawCycleOptions(FarmingPreferences preferences)
    {
        ImGui.Text("結束 FSD...");
        bool infinite = _ddHost?.FsdActive == true ? _ddHost.Infinite : preferences.Infinite;
        int cycles = _ddHost?.FsdActive == true ? _ddHost.TargetLoops : preferences.Cycles;
        bool changed = ImGui.Checkbox("無限循環##fsf_inf", ref infinite);
        ImGui.SameLine();
        ImGui.BeginDisabled(infinite);
        ImGui.SetNextItemWidth(90);
        changed |= ImGui.InputInt("循環次數##fsf_loops", ref cycles);
        ImGui.EndDisabled();
        if (changed)
        {
            cycles = Math.Max(1, cycles);
            preferences.Cycles = cycles;
            preferences.Infinite = infinite;
            _configuration.Save();
            _ddHost?.SetCycleTargets(cycles, infinite);
        }
    }

    public FsdControlResult StartSelectedFarming(FsdStartRequest request)
    {
        if (DeepDungeonHelper.IsInDeepDungeon() && request is { StopAfterFloor: null, DiagnosticStartFloor: null,
                ResumeSlot: null, HoldOnFailure: false, HoldAfterClear: false } &&
            request.Scenario != FsdStartScenario.ControlledSurvey)
            return StartCurrentDutyFarming();
        var settings = _configuration.Farming;
        uint dungeonId = settings.DungeonId is 1 or 2 ? settings.DungeonId : 4u;
        bool pt = dungeonId == 4;
        if (request.Scenario == FsdStartScenario.ControlledSurvey)
            return new FsdControlResult(false, "妖宮受控採集已移除。");
        var effectiveMode = pt ? settings.Mode : FarmingMode.DeepProgression;
        var p = settings.GetPreferences(_configuration, effectiveMode);
        bool prepared = effectiveMode is FarmingMode.Aetherpool or FarmingMode.HoardDiscovery;
        int startFloor = effectiveMode switch { FarmingMode.DeepProgression => 1,
            FarmingMode.FreeExploration => settings.ExplorationStartFloor, _ => _fsfScenarioIndex == 0 ? 21 : 31 };
        var farming = new FsdFarmingRequest(effectiveMode, prepared ? SaveUse.Prepared : SaveUse.Create,
            startFloor, p.Cycles, p.Infinite,
            new FarmingTargets(p.BandedEnabled == true, p.OpenGold, p.OpenSilver, p.OpenBronze),
            request, DungeonId: dungeonId);
        bool started = TryStartFarming(farming, out string error);
        return new FsdControlResult(started, error);
    }

    private FsdControlResult StartCurrentDutyFarming()
    {
        if (!TryGetCurrentDutyForAttachment(out var dungeon, out var duty, out var entryFloor, out var error))
            return new FsdControlResult(false, error);

        var mode = FarmingEntryPolicy.ResolveAttachmentMode(dungeon.DungeonId, entryFloor, _configuration.Farming.Mode);
        var preferences = _configuration.Farming.GetPreferences(_configuration, mode);
        bool prepared = mode is FarmingMode.Aetherpool or FarmingMode.HoardDiscovery;
        int validatedStart = mode == FarmingMode.Hoard ? entryFloor : 1;
        if (!FarmingPlan.TryCreate(mode, prepared ? SaveUse.Prepared : SaveUse.Create, validatedStart, 1, false,
                preferences.BandedEnabled == true, preferences.OpenGold, preferences.OpenSilver, preferences.OpenBronze,
                out var plan, out error, dungeon.DungeonId))
            return new FsdControlResult(false, error);

        var session = new FarmingSession(plan! with
        {
            StartFloor = entryFloor,
            AttachCurrentDuty = true,
            ProgressionSaveSlot = null
        });
        if (!TryStartFsd(() => new FarmingScenario(session), 1, false,
                GetFarmingDetailedMapKey(session.Plan), out error))
            return new FsdControlResult(false, error);

        _farmingError = null;
        return new FsdControlResult(true, Scenario: $"接管 {dungeon.Name} 第 {duty.Floor} 層");
    }

    private bool TryGetCurrentDutyForAttachment(out DungeonData dungeon, out DutyState duty,
        out int entryFloor, out string error)
    {
        duty = _dutyState!;
        entryFloor = 0;
        uint territory = Service.ClientState.TerritoryType;
        if (!DungeonCatalog.TryGetByTerritoryId(territory, out dungeon) ||
            dungeon.DungeonId is not (1 or 2 or 4))
        {
            error = "FSD 不支援目前地宮。";
            return false;
        }

        if (duty is not { IsInDuty: true, StateReadFailed: false, IsTransitioning: false } ||
            DutyState.IsClientTransitioning || !duty.IsPlayerPositionStable() ||
            duty.CurrentFloorKind == DeepDungeonFloorKind.Unknown || duty.DungeonId != dungeon.DutyDungeonId ||
            !DeepDungeonHelper.TryGetFloorRangeForCurrentTerritory(out int firstFloor, out int lastFloor) ||
            duty.Floor < firstFloor || duty.Floor > lastFloor)
        {
            error = "等待過圖及目前副本樓層資料穩定。";
            return false;
        }

        if (Service.LocalPlayer is not { IsDead: false })
        {
            error = "請在角色復活後接管副本。";
            return false;
        }

        entryFloor = firstFloor;
        error = string.Empty;
        return true;
    }

    private static string? GetFarmingDetailedMapKey(FarmingPlan plan)
    {
        if (!plan.ShowsDetailedMap)
            return null;
        return plan.StartFloor == 21
            ? DetailedMapScenarioCatalog.PilgrimsTraverse21To30.Key
            : DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key;
    }

    public bool TryStartFarming(FsdFarmingRequest request, out string error)
    {
        var (mode, saveUse, startFloor, cycles, infinite, targets, checkpointOptions, dungeonId) = request;
        if (mode == FarmingMode.DeepProgression && dungeonId is 1 or 2 &&
            Service.LocalPlayer?.ClassJob.RowId is not (23 or 31 or 38))
        {
            error = "死宮／天宮死靈術士模式僅支援機工士、吟遊詩人、舞者。請切換職業後再啟動。";
            return false;
        }
        var (hoard, gold, silver, bronze) = targets;
        var (stopAfterFloor, diagnosticStartFloor, resumeSlot, holdOnFailure, _) = checkpointOptions ?? new FsdStartRequest();
        bool holdAfterClear = checkpointOptions?.HoldAfterClear == true;
        int maxFloor = dungeonId == 1 ? 200 : 100;
        int terminalEntry = maxFloor - 9;
        bool hoardReview = holdAfterClear && dungeonId == 4 && mode == FarmingMode.Hoard &&
            diagnosticStartFloor is 21 or 31 && !stopAfterFloor.HasValue;
        if (stopAfterFloor.HasValue && (mode != FarmingMode.DeepProgression ||
            stopAfterFloor.Value < 10 || stopAfterFloor.Value > maxFloor || stopAfterFloor.Value % 10 != 0))
        {
            error = $"停止層數只支援深層攻略模式，且必須為 10 至 {maxFloor} 的十層邊界。";
            return false;
        }
        if (!FarmingPlan.TryCreate(mode, saveUse, startFloor, cycles, infinite, hoard, gold, silver, bronze,
                out var plan, out error, dungeonId)) return false;
        if (!hoardReview && (holdOnFailure || resumeSlot.HasValue) && (mode != FarmingMode.DeepProgression ||
            (!stopAfterFloor.HasValue && (!diagnosticStartFloor.HasValue || resumeSlot.HasValue && !holdOnFailure)) ||
            (resumeSlot.HasValue && (resumeSlot.Value < 0 || !diagnosticStartFloor.HasValue))))
        {
            error = "保留現場須指定深層測試起點或停止層；續用槽位須指定起點，並設定停止層或保留失敗現場。";
            return false;
        }
        bool validDiagnosticStart = dungeonId switch
        {
            1 => diagnosticStartFloor is 1 or 51,
            2 => diagnosticStartFloor is 1 or 21,
            _ => diagnosticStartFloor is 1 or 21 or 31 or 51 or 71
        };
        if (!hoardReview && diagnosticStartFloor is { } checkpoint && (mode != FarmingMode.DeepProgression ||
            (resumeSlot.HasValue ? checkpoint < 1 || checkpoint > terminalEntry || checkpoint % 10 != 1
                : !validDiagnosticStart) ||
            stopAfterFloor is { } endFloor && endFloor < checkpoint + 9))
        {
            error = "新建測試起點與地宮設定不符；指定續用槽位須為十層組起點，且停止層數如有指定須為組末或之後。";
            return false;
        }
        plan = plan! with { StopAfterFloor = stopAfterFloor, StartFloor = diagnosticStartFloor ?? plan.StartFloor,
            HoldOnFailure = holdOnFailure,
            HoldAfterClear = holdAfterClear };
        if (mode == FarmingMode.DeepProgression && !stopAfterFloor.HasValue && !diagnosticStartFloor.HasValue &&
            !resumeSlot.HasValue && !holdOnFailure && !holdAfterClear)
            plan = plan with { ProgressionSaveSlot = _configuration.NecromancerSaveSlot };
        if (_dutyState is { IsInDuty: true } duty &&
            ((!holdOnFailure && !hoardReview) || !resumeSlot.HasValue || resumeSlot is < 0 or > 1 ||
            duty.IsTransitioning || duty.DungeonId != dungeonId ||
            duty.Floor < plan.StartFloor || duty.Floor >= plan.StartFloor + 10))
        {
            error = "原副本接續只支援保留現場的測試，須指定來源槽位與目前所在層段起點。";
            return false;
        }
        var session = new FarmingSession(plan, resumeSlot ?? -1);
        string? key = plan!.ShowsDetailedMap ? startFloor == 21
            ? DetailedMapScenarioCatalog.PilgrimsTraverse21To30.Key
            : DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key : null;
        bool started = TryStartFsd(() => new FarmingScenario(session), plan.UsesSelectedProgressionSave ? 1 : cycles,
            !plan.UsesSelectedProgressionSave && infinite, key, out error);
        if (started)
        {
            _farmingError = null;
            _configuration.Farming.DungeonId = dungeonId;
            if (dungeonId == 4)
                _configuration.Farming.Mode = mode;
            if (mode == FarmingMode.FreeExploration) _configuration.Farming.ExplorationStartFloor = startFloor;
            _fsfScenarioIndex = dungeonId == 4 && mode == FarmingMode.Hoard && startFloor == 21 ? 0 : 1;
            _configuration.NecromancerFsdScenarioIndex = _fsfScenarioIndex;
            var p = _configuration.Farming.GetPreferences(_configuration, mode);
            p.BandedEnabled = hoard; p.OpenGold = gold; p.OpenSilver = silver; p.OpenBronze = bronze;
            p.Cycles = cycles; p.Infinite = infinite;
            _configuration.Save();
        }
        return started;
    }
}
