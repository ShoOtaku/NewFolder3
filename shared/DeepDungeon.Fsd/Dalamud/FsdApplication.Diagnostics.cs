namespace DeepDungeon.Fsd.Dalamud;

public sealed partial class FsdApplication
{
    /// <summary>Reads the last immutable diagnostic sample and current heartbeat age from any thread.</summary>
    public string GetDiagnosticSnapshot() => _module.GetDiagnosticSnapshot();

    /// <summary>Reads bounded diagnostic events by sequence; never invokes game operations.</summary>
    public string GetDiagnosticEvents(string query) => _module.GetDiagnosticEvents(query);

    /// <summary>Reads a support report containing the snapshot and recent event evidence.</summary>
    public string GetDiagnosticReport() => _module.GetDiagnosticReport();
}
