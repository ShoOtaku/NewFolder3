namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

public sealed partial class FloorPhaseController
{
    internal Action<string, object>? DiagnosticSink { get; set; }
    internal string DiagnosticGate { get; private set; } = "not-updated";
    internal long DiagnosticGeneration => _floorRuntime?.Generation ?? 0;
    internal string DiagnosticTaskPhase => _floorRuntime?.CurrentTaskPhase.ToString() ?? "none";
    internal int DiagnosticPlayerRoom => _floorRuntime?.DiagnosticPlayerRoom ?? -1;
    internal int DiagnosticCompletedRooms => _floorRuntime?.Executor?.GetDebugSnapshot().CompletedRooms.Count ?? 0;
    internal bool DiagnosticMovementRequested => _navHelper?.HasActiveTarget == true || _floorRuntime?.DiagnosticMovementRequested == true;
    private DateTime lastDiagnosticErrorAt;
    private string lastDiagnosticError = "";
    private long diagnosticErrorCount;
    private long recorderErrorCount;

    internal object CaptureDiagnostics() => new
    {
        gate = DiagnosticGate, phase = _phase.ToString(), status = _status,
        generation = DiagnosticGeneration, taskPhase = DiagnosticTaskPhase,
        objective = CurrentObjectiveDecision.PrimaryObjective.ToString(),
        channels = new { movement = CurrentObjectiveDecision.Channels.Movement.ToString(),
            combat = CurrentObjectiveDecision.Channels.Combat.ToString(),
            interaction = CurrentObjectiveDecision.Channels.Interaction.ToString(),
            transition = CurrentObjectiveDecision.Channels.Transition.ToString() },
        hoardEvidenceWait = new { eventType = _activeHoardEvidenceWaitEventType,
            state = _activeHoardEvidenceWaitState,
            sinceUtc = _activeHoardEvidenceWaitStartedAt == DateTime.MinValue ? (DateTime?)null : _activeHoardEvidenceWaitStartedAt },
        nativeIntuitionSampleAvailable = _nativeIntuitionSampleAvailable,
        navigation = _navHelper?.CaptureDiagnostics(),
        driver = new { staging = _navDriver?.IsStaging, stage = _navDriver?.StageLabel, retries = _navDriver?.StuckRetryCount },
        runtime = _floorRuntime?.CaptureDiagnostics(), diagnosticErrorCount, recorderErrorCount,
        lastDiagnosticError
    };

    private void ReportDiagnosticError(string source, Exception error)
    {
        diagnosticErrorCount++;
        string detail = $"{source}: {error}";
        var now = DateTime.UtcNow;
        if (detail == lastDiagnosticError && now - lastDiagnosticErrorAt < TimeSpan.FromSeconds(5)) return;
        lastDiagnosticError = detail;
        lastDiagnosticErrorAt = now;
        // Do not recurse through RecordReplayEvent when its writer or observer failed.
        if (source == "floor-update")
            RecordReplayEvent("runtime-error", new { source, error = detail, count = diagnosticErrorCount, gate = DiagnosticGate });
        Service.Log.Error($"[FSD.Diagnostics] {detail}; count={diagnosticErrorCount}");
    }
}
