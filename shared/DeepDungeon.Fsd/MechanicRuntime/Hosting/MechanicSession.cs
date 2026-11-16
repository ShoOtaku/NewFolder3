using BossMod;

namespace MasterPlayer.MechanicRuntime;

public sealed class MechanicSession : IDisposable
{
    public WorldState World { get; }
    public BossModuleManager BossModules { get; }
    public ZoneModuleManager ZoneModules { get; }
    private readonly EncounterDutyAdapters _dutyAdapters;
    public ExecutionRestrictions GetExecutionRestrictions(Actor player) => _dutyAdapters.GetRestrictions(player);
    private bool _disposed;

    public MechanicSession(string gameVersion)
    {
        if (!MechanicRuntimeEnvironment.IsInitialized)
            throw new InvalidOperationException("Initialize BMR game data and configuration before creating a session.");
        World = new((ulong)TimeSpan.TicksPerSecond, gameVersion);
        BossModules = new(World);
        ZoneModules = new(World);
        _dutyAdapters = new(this);
    }

    public void UpdateHints(AIHintsBuilder? builder, AIHints hints, int slot, bool moveImminent)
    {
        var module = BossModules.ActiveModule;
        if (builder != null)
            builder.Update(hints, slot, moveImminent);
        else
        {
            hints.Clear();
            if (World.Party[slot] is { } player)
            {
                if (module != null) module.CalculateAIHints(slot, player, PartyRolesConfig.Assignment.Unassigned, hints);
                else ZoneModules.ActiveModule?.CalculateAIHints(slot, player, hints);
            }
            hints.Normalize();
        }
    }

    public void BeginFrame(DateTime timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = World.CurrentTime;
        if (timestamp == default || (previous != default && timestamp < previous))
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Seeking backwards requires a new world session.");
        var delta = previous == default ? 0 : (float)(timestamp - previous).TotalSeconds;
        World.Execute(new WorldState.OpFrameStart(new(timestamp, (ulong)timestamp.Ticks,
            World.Frame.Index + 1, delta, delta, 1), default, World.Client.GaugePayload, World.Client.CameraAzimuth));
    }

    public void Execute(WorldState.Operation operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        World.Execute(operation);
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ZoneModules.ActiveModule?.Update();
        BossModules.Update();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _dutyAdapters.Dispose();
        List<Exception> errors = [];
        try { ZoneModules.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        finally { ZoneModules.ActiveModule = null; }
        try { BossModules.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0)
            throw new AggregateException("BMR session cleanup failed.", errors);
    }
}
