using System.Text.Json;
using Dalamud.Plugin;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

/// <summary>Owns MP floor delegation and the movement switch during FSD's Palace/Heaven-on-High run.</summary>
internal sealed class PromeRadarFloorMovement : IDisposable
{
    internal readonly record struct Observation(bool Available, bool Enabled, bool MovementActive, string BuildMvid,
        bool SessionActive);

    private readonly Func<Observation> observe;
    private readonly Action<bool> request;
    private readonly Action<bool> requestSession;
    private readonly Action<string, object> record;
    private readonly Func<DateTime> clock;
    private bool? desired;
    private bool? original;
    private bool? written;
    private string? buildMvid;
    private bool pending;
    private DateTime requestedAt;
    private bool sessionOwned;
    private bool sessionPending;
    private bool sessionWritten;
    private DateTime sessionRequestedAt;
    private DateTime waitingSince;
    private string? waitingReason;
    private bool disposed;

    internal bool SuppressControl => desired == false;

    internal PromeRadarFloorMovement(IDalamudPluginInterface pluginInterface, Action<string, object> record)
    {
        var enabled = pluginInterface.GetIpcSubscriber<bool>("PromeRadar.GetSmartMovementEnabled");
        var active = pluginInterface.GetIpcSubscriber<bool>("PromeRadar.IsMechanicMovementActive");
        var state = pluginInterface.GetIpcSubscriber<string>("PromeRadar.GetState");
        var setter = pluginInterface.GetIpcSubscriber<bool, object>("PromeRadar.SetSmartMovementEnabled");
        var session = pluginInterface.GetIpcSubscriber<bool, object>("PromeRadar.SetFsdSessionActive");
        observe = () =>
        {
            if (!enabled.HasFunction && !active.HasFunction && !state.HasFunction && !setter.HasAction && !session.HasAction)
                return default;
            if (!enabled.HasFunction || !active.HasFunction || !state.HasFunction || !setter.HasAction || !session.HasAction)
                throw new InvalidOperationException("MP 智能走位接口未完整注册。");
            var snapshot = JsonSerializer.Deserialize<PromeRadarMovementState>(state.InvokeFunc());
            if (snapshot == null || !snapshot.IsCurrent(Service.ClientState.TerritoryType, DateTime.UtcNow) ||
                string.IsNullOrWhiteSpace(snapshot.BuildMvid))
                throw new InvalidOperationException("MP 智能走位状态尚未就绪或已过期。");
            return new(true, enabled.InvokeFunc(), active.InvokeFunc(), snapshot.BuildMvid, snapshot.FsdSessionActive);
        };
        request = setter.InvokeAction;
        requestSession = session.InvokeAction;
        this.record = record;
        clock = () => DateTime.UtcNow;
    }

    internal PromeRadarFloorMovement(Func<Observation> observe, Action<bool> request, Action<bool> requestSession,
        Action<string, object> record, Func<DateTime> clock)
    {
        this.observe = observe;
        this.request = request;
        this.requestSession = requestSession;
        this.record = record;
        this.clock = clock;
    }

    internal bool Update(bool runActive, in DeepDungeonStateSnapshot floor)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Keep the last intent through loading/death observations. A transient
        // unknown floor must not re-enable MP in the middle of a spin encounter.
        if (runActive && (!floor.IsValid || floor.IsTransitioning))
            return original == null && desired == null;
        if (runActive && floor.IsInDuty && floor.DungeonId == 4)
        {
            // PT owns its own session and switch lifecycle. Do not release a
            // newly acquired PT session using this previous floor policy.
            desired = null;
            ForgetProvider();
            ClearWait();
            return true;
        }
        var previousDesired = desired;
        if (!runActive)
            desired = null;
        else if (floor.IsInDuty && floor.DungeonId is 1 or 2)
        {
            if (floor.FloorKind == DeepDungeonFloorKind.Mob)
                desired = true;
            else if (DeepDungeonBossPolicy.UsesSpin(floor.DungeonId, floor.Floor))
                desired = false;
            else
                desired = null;
        }
        else if (!floor.IsInDuty && floor.DungeonId is 0 or 1 or 2)
        {
            // Preserve the switch lease between floorsets while the run is active.
            return true;
        }
        else
            desired = null;
        if (previousDesired != desired) ClearWait();

        var wantsSession = runActive && floor.IsInDuty && floor.DungeonId is 1 or 2;
        if (desired == null && original == null && !sessionOwned && !wantsSession) return true;
        Observation current;
        try { current = observe(); }
        catch (Exception error) { return Wait(error.Message); }
        if (!current.Available)
        {
            // An unloaded MP cannot compete with spin. Do not carry a switch
            // restoration into a different provider instance after a reload.
            ForgetProvider();
            if (desired != true)
            {
                ClearWait();
                return true;
            }
            return Wait("等待 MP 上线以处理普通层走位。");
        }
        if (buildMvid != current.BuildMvid)
        {
            ForgetProvider();
            buildMvid = current.BuildMvid;
        }
        if (desired == null && original == null && !sessionOwned && !wantsSession) return true;
        original ??= current.Enabled;
        bool sessionReady;
        try { sessionReady = UpdateSession(current, wantsSession); }
        catch (Exception error) { return Wait($"MP 会话请求失败：{error.Message}"); }
        var target = desired ?? original.Value;
        // A user change after our confirmed write owns the eventual restore.
        if (desired == null && sessionReady && !pending && written.HasValue && current.Enabled != written.Value)
        {
            ForgetProvider();
            ClearWait();
            return true;
        }

        var now = clock();
        // Queue a compensating request even if the cached state already equals
        // the new target: an opposite request may still be waiting inside MP.
        if (current.Enabled != target || pending && written != target)
        {
            if (!pending || written != target || now - requestedAt >= TimeSpan.FromSeconds(1))
            {
                written = target;
                pending = true;
                requestedAt = now;
                try { request(target); }
                catch (Exception error) { return Wait($"MP 开关请求失败：{error.Message}"); }
                record("mp-floor-movement-requested", new
                {
                    enabled = target, restoring = desired == null,
                    dungeonId = floor.DungeonId, floor = floor.Floor, buildMvid
                });
            }
            return Wait(target ? "等待 MP 开启普通层智能走位。" : "等待 MP 关闭 spin 层智能走位。");
        }
        if (!target && current.MovementActive)
            return Wait("等待 MP 释放移动控制后启动 spin。");

        pending = false;
        if (!sessionReady)
            return Wait(wantsSession ? "等待 MP 确认普通楼层由 FSD 负责。" : "等待 MP 释放 FSD 会话。");
        ClearWait();
        if (desired == null && !wantsSession)
            ForgetProvider();
        return true;
    }

    private bool UpdateSession(in Observation current, bool active)
    {
        if (!active && !sessionOwned) return true;
        if (current.SessionActive == active && (!sessionPending || sessionWritten == active))
        {
            sessionPending = false;
            sessionOwned = active;
            return true;
        }
        var now = clock();
        if (!sessionPending || sessionWritten != active || now - sessionRequestedAt >= TimeSpan.FromSeconds(1))
        {
            // Remember queued ownership before invoking IPC: stop/dispose must
            // compensate even an action that enqueued and then threw.
            sessionOwned = true;
            sessionPending = true;
            sessionWritten = active;
            sessionRequestedAt = now;
            requestSession(active);
            record("mp-floor-session-requested", new { active, buildMvid });
        }
        return false;
    }

    private bool Wait(string reason)
    {
        var now = clock();
        if (waitingSince == default) waitingSince = now;
        if (waitingReason != reason)
        {
            waitingReason = reason;
            record("mp-floor-movement-wait", new { reason, spin = SuppressControl });
        }
        if (now - waitingSince >= TimeSpan.FromSeconds(10))
            throw new InvalidOperationException($"MP 楼层走位交接逾时：{reason}");
        return false;
    }

    private void ClearWait()
    {
        if (waitingReason != null)
            record("mp-floor-movement-ready", new { enabled = desired, buildMvid });
        waitingSince = default;
        waitingReason = null;
    }

    private void ForgetProvider()
    {
        original = null;
        written = null;
        pending = false;
        sessionOwned = false;
        sessionPending = false;
        buildMvid = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!original.HasValue && !sessionOwned) return;
        var current = observe();
        if (!current.Available || current.BuildMvid != buildMvid) return;
        if (sessionOwned)
        {
            requestSession(false);
            record("mp-floor-session-release-requested", new { buildMvid });
        }
        if (original is not { } restoreSwitch) return;
        if (current.Available && current.BuildMvid == buildMvid &&
            (pending || written.HasValue && current.Enabled == written.Value) &&
            (current.Enabled != restoreSwitch || pending && written != restoreSwitch))
        {
            request(restoreSwitch);
            record("mp-floor-movement-restore-requested", new { enabled = restoreSwitch, buildMvid });
        }
    }
}
