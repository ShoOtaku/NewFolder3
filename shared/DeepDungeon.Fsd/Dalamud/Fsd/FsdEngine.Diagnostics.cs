using System.Text.Json;
using System.Text.Json.Serialization;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud.moveHelper;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private readonly FsdDiagnosticJournal diagnostics = new();
    private static readonly JsonSerializerOptions DiagnosticJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private DateTime nextDiagnosticSample;
    private DateTime nextDiagnosticErrorLog;

    internal string GetDiagnosticSnapshot() => diagnostics.GetSnapshot();
    internal string GetDiagnosticEvents(string query) => diagnostics.GetEvents(query);
    internal string GetDiagnosticReport() => diagnostics.GetReport();
    internal void RecordDiagnosticEvent(string type, object data) => diagnostics.Record(type, data);
    internal void RecordDiagnosticFailure(string source, Exception error) => diagnostics.RecordFailure(source, error);

    private void ObserveDiagnostics()
    {
        var now = DateTime.UtcNow;
        if (now < nextDiagnosticSample) return;
        nextDiagnosticSample = now.AddSeconds(1);
        try
        {
            var host = _ddHost;
            var floor = host?.FloorController;
            var duty = host?.Context?.Duty ?? _dutyState;
            var player = Service.LocalPlayer;
            var target = Service.TargetManager.Target as IBattleChara;
            string gate = "inactive";
            string status = host?.CurrentStatus ?? "";
            if (IsRunActive)
            {
                gate = host?.DiagnosticGate ?? "host-unavailable";
                if (gate == "floor-update")
                {
                    gate = floor?.DiagnosticGate ?? "floor-unavailable";
                    if (host?.CurrentStatusIsError != true)
                        status = floor?.Status ?? status;
                }
            }
            bool navigationRunning = false, pending = false;
            string? navigationError = null;
            try
            {
                navigationRunning = VNav.Path.IsRunning();
                pending = VNav.SimpleMove.PathfindInProgress();
            }
            catch (Exception error) { navigationError = error.ToString(); }
            var sample = new FsdDiagnosticSample
            {
                CapturedAtUtc = now, Active = IsRunActive,
                Scenario = host?.CurrentScenarioName ?? "",
                Gate = gate,
                Phase = floor?.CurrentPhase.ToString() ?? "",
                TaskPhase = floor?.DiagnosticTaskPhase ?? "",
                Objective = floor?.CurrentObjectiveDecision.PrimaryObjective.ToString() ?? "",
                Status = status,
                StatusIsError = host?.CurrentStatusIsError == true,
                DungeonId = duty?.DungeonId ?? 0, Floor = duty?.Floor ?? 0,
                FloorGeneration = floor?.DiagnosticGeneration ?? 0,
                AttemptGeneration = host?.DiagnosticAttemptGeneration ?? 0,
                InDuty = duty?.IsInDuty == true,
                Transitioning = duty?.IsTransitioning == true || Service.Condition[ConditionFlag.BetweenAreas]
                    || Service.Condition[ConditionFlag.BetweenAreas51],
                InCombat = Service.Condition[ConditionFlag.InCombat],
                PlayerAvailable = player != null, PlayerDead = player?.IsDead == true,
                Casting = player?.IsCasting == true,
                X = Finite(player?.Position.X), Y = Finite(player?.Position.Y), Z = Finite(player?.Position.Z),
                TargetId = target?.GameObjectId ?? 0, TargetHp = target?.CurrentHp,
                PlayerRoom = floor?.DiagnosticPlayerRoom ?? -1,
                CompletedRooms = floor?.DiagnosticCompletedRooms ?? 0,
                HoardDiscoveries = host?.HoardDiscoveries ?? 0,
                NavigationRequested = floor?.DiagnosticMovementRequested == true || navigationRunning || pending,
                NavigationRunning = navigationRunning, PathfindPending = pending,
                RecorderPath = floor?.RunRecorderPath ?? "",
                Details = JsonSerializer.SerializeToElement(new
                {
                    hostIdentity = _hostIdentity, hostVersion = _hostVersion,
                    engineBuildMvid = typeof(FsdEngine).Module.ModuleVersionId,
                    coreBuildMvid = typeof(FsdDiagnosticJournal).Module.ModuleVersionId,
                    mechanicBuildMvid = typeof(MasterPlayer.MechanicRuntime.CastMovementPolicy).Module.ModuleVersionId,
                    entryReady = host?.Context?.EntryReadyForDuty,
                    dutyStateReadError = duty?.LastStateReadError,
                    completedLoops = host?.CompletedLoops, targetLoops = host?.TargetLoops,
                    playerHp = player?.CurrentHp, playerMaxHp = player?.MaxHp,
                    targetMaxHp = target?.MaxHp, navigationReadError = navigationError,
                    floor = floor?.CaptureDiagnostics(), rotationFailure = _rotationControl.FailedProvider?.ToString(),
                    movement = new
                    {
                        automatic = Runtime.Navigation.MovementCoordinator.Shared.AutomaticActive,
                        directRequired = Runtime.Navigation.MovementCoordinator.Shared.DirectRequired,
                        status = Runtime.Navigation.MovementCoordinator.Shared.Status,
                        lastError = Runtime.Navigation.MovementCoordinator.Shared.LastError,
                        mpBlocked = Runtime.Navigation.PromeRadarMovementGate.IsBlocked,
                        mpHandoffComplete = Runtime.Navigation.PromeRadarMovementGate.HandoffComplete
                    },
                    directMovement = new { owner = Utilities.DirectMovementController.ActiveOwner,
                        inputHooksReady = Utilities.DirectMovementController.InputHooksReady,
                        initializationFailure = Utilities.DirectMovementController.InitializationFailure }
                }, DiagnosticJson)
            };
            if (!diagnostics.Publish(sample)) return;
            string json = diagnostics.GetSnapshot();
            using var parsed = JsonDocument.Parse(json);
            // The existing per-run JSONL recorder owns durable support evidence.
            floor?.RecordReplayEvent("diagnostic-heartbeat", parsed.RootElement);
            string assessment = parsed.RootElement.GetProperty("state").GetProperty("assessment").GetString() ?? "";
            if (assessment.Contains("no-progress") || assessment.Contains("no-observed-progress")
                || assessment.Contains("no-displacement") || assessment.Contains("overdue") || sample.StatusIsError)
                Service.Log.Warning($"[FSD.Diagnostics] {json}");
            else if (sample.Active)
                Service.Log.Info($"[FSD.Diagnostics] {json}");
        }
        catch (Exception error)
        {
            // Diagnostic collection must not interrupt the control loop.
            diagnostics.RecordFailure("diagnostic-sample", error);
            if (now >= nextDiagnosticErrorLog)
            {
                nextDiagnosticErrorLog = now.AddSeconds(5);
                Service.Log.Error($"[FSD.Diagnostics] Snapshot collection failed: {error}");
            }
        }
    }

    private static float? Finite(float? value) => value.HasValue && float.IsFinite(value.Value) ? value : null;
}
