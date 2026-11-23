using System.Text.Json;
using Dalamud.Plugin;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

/// <summary>Owns the MP switch only during FSD's Palace/Heaven-on-High run.</summary>
internal sealed class PromeRadarFloorMovement : IDisposable
{
    internal readonly record struct Observation(bool Available, bool Enabled, bool MovementActive, string BuildMvid);

    private readonly Func<Observation> observe;
    private readonly Action<bool> request;
    private readonly Action<string, object> record;
    private readonly Func<DateTime> clock;
    private bool? desired;
    private bool? original;
    private bool? written;
    private string? buildMvid;
    private bool pending;
    private DateTime requestedAt;
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
        observe = () =>
        {
            if (!enabled.HasFunction && !active.HasFunction && !state.HasFunction && !setter.HasAction)
                return default;
            if (!enabled.HasFunction || !active.HasFunction || !state.HasFunction || !setter.HasAction)
                throw new InvalidOperationException("MP 智能走位接口未完整注册。");
            var snapshot = JsonSerializer.Deserialize<PromeRadarMovementState>(state.InvokeFunc());
            if (snapshot == null || !snapshot.IsCurrent(Service.ClientState.TerritoryType, DateTime.UtcNow) ||
                string.IsNullOrWhiteSpace(snapshot.BuildMvid))
                throw new InvalidOperationException("MP 智能走位状态尚未就绪或已过期。");
            return new(true, enabled.InvokeFunc(), active.InvokeFunc(), snapshot.BuildMvid);
        };
        request = setter.InvokeAction;
        this.record = record;
        clock = () => DateTime.UtcNow;
    }

    internal PromeRadarFloorMovement(Func<Observation> observe, Action<bool> request,
        Action<string, object> record, Func<DateTime> clock)
    {
        this.observe = observe;
        this.request = request;
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

        if (desired == null && original == null) return true;
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
        if (desired == null && original == null) return true;
        original ??= current.Enabled;
        var target = desired ?? original.Value;
        // A user change after our confirmed write owns the eventual restore.
        if (desired == null && !pending && written.HasValue && current.Enabled != written.Value)
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
        ClearWait();
        if (desired == null)
            ForgetProvider();
        return true;
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
        buildMvid = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (original is not { } restore) return;
        var current = observe();
        if (current.Available && current.BuildMvid == buildMvid &&
            (pending || written.HasValue && current.Enabled == written.Value) &&
            (current.Enabled != restore || pending && written != restore))
        {
            request(restore);
            record("mp-floor-movement-restore-requested", new { enabled = restore, buildMvid });
        }
    }
}
