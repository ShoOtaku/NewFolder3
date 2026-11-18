namespace DeepDungeon.Fsd.Dalamud.Runtime;

public sealed partial class RunHost
{
    internal string DiagnosticGate { get; private set; } = "inactive";
    internal long DiagnosticAttemptGeneration => _activeDutyAttemptGeneration;
    private readonly Dictionary<string, (DateTime At, string Error, int Count)> diagnosticErrors = new();

    private string GetScenarioDiagnosticGate()
    {
        if (_context == null) return "context-unavailable";
        if (_context.StatusIsError) return "scenario-error";
        if (!_context.Duty.IsInDuty) return "outside-duty-scenario";
        if (!_context.EntryReadyForDuty) return "waiting-entry-ready";
        return "scenario-update";
    }

    private void ReportRuntimeError(string source, Exception error)
    {
        string detail = error.ToString();
        var now = DateTime.UtcNow;
        diagnosticErrors.TryGetValue(source, out var previous);
        int count = previous.Count + 1;
        if (previous.Error == detail && now - previous.At < TimeSpan.FromSeconds(5))
        {
            diagnosticErrors[source] = (previous.At, detail, count);
            return;
        }
        diagnosticErrors[source] = (now, detail, count);
        _floorController.RecordReplayEvent("runtime-error", new { source, error = detail, count,
            scenario = CurrentScenarioName, gate = DiagnosticGate, status = CurrentStatus });
        Service.Log.Error($"[FSD.Diagnostics] {source} failed (count={count}, gate={DiagnosticGate}): {detail}");
    }
}
