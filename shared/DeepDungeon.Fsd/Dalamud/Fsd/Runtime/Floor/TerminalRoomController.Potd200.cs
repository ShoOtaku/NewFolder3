using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Enums;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class TerminalRoomController
{
    private ulong _potdRewardId;
    private uint _potdRewardEntityId;
    private TerminalRewardProfile _potdRewardProfile;
    private Vector3 _potdRewardPosition;
    private int _potdRewardCountBefore;
    private DateTime _potdRewardBoundAt, _potdRewardDispatchedAt, _potdAtRewardRegion;

    private void UpdatePotd200(DateTime now)
    {
        var player = Service.LocalPlayer;
        if (player == null) return;
        // A receipt survives actor despawn/untargetability and is scoped to our dispatch.
        if (_potdRewardDispatchedAt != default)
        {
            if (DeepDungeonLootTracker.TryGetItemCount(_potdRewardProfile.ItemId, out var count, out _) &&
                count > _potdRewardCountBefore)
            {
                _resultOpened.Add(_potdRewardId);
                RecordReplayEvent("result-reward-received", new
                {
                    actorId = _potdRewardId, _potdRewardProfile.BaseId, _potdRewardProfile.ItemId,
                    before = _potdRewardCountBefore, after = count
                });
                _potdRewardId = 0;
                _potdRewardDispatchedAt = default;
            }
            else if ((now - _potdRewardDispatchedAt).TotalSeconds > 60)
            {
                StopResultReview("200層：尚未確認獎勵入袋；已保留現場。", now);
                return;
            }
        }

        IGameObject? reward = null;
        float nearest = float.MaxValue;
        foreach (var obj in Service.GameObjects)
        {
            if (obj.ObjectKind != ObjectKind.EventObj || !Potd200RewardPolicy.TryGet(obj.BaseId, out _)) continue;
            if (_potdRewardId != 0)
            {
                if (obj.GameObjectId == _potdRewardId && obj.EntityId == _potdRewardEntityId &&
                    obj.BaseId == _potdRewardProfile.BaseId) reward = obj;
            }
            else if (obj.IsTargetable && !_resultOpened.Contains(obj.GameObjectId))
            {
                float distance = Vector3.DistanceSquared(player.Position, obj.Position);
                if (distance < nearest) { nearest = distance; reward = obj; }
            }
        }
        if (_potdRewardId == 0 && reward != null)
        {
            _potdRewardId = reward.GameObjectId;
            _potdRewardEntityId = reward.EntityId;
            _potdRewardPosition = reward.Position;
            Potd200RewardPolicy.TryGet(reward.BaseId, out _potdRewardProfile);
            _potdRewardBoundAt = now;
            RecordReplayEvent("result-reward-bound", new
            {
                reward.GameObjectId, reward.EntityId, reward.BaseId, _potdRewardProfile.ItemId,
                position = new { reward.Position.X, reward.Position.Y, reward.Position.Z }
            });
        }
        if (_potdRewardId != 0)
        {
            if (_potdRewardDispatchedAt != default)
            {
                _navHelper.Cancel();
                Status = _ctx.StatusLine = "200層：等待獎勵入袋確認";
                return;
            }
            if ((now - _potdRewardBoundAt).TotalSeconds > 180)
            {
                StopResultReview("200層：獎勵物件接近逾時；已保留現場。", now);
                return;
            }
            {
                // Preserve a real observed target through streaming loss; forgetting
                // it could wrongly complete collection after an earlier reward.
                if (reward != null) _potdRewardPosition = reward.Position;
                var nav = _navHelper.Navigate(_potdRewardPosition, player.Position, 2.5f);
                Status = _ctx.StatusLine = $"200層：前往獎勵 {_potdRewardProfile.BaseId}";
                if (nav is NavigationState.Failed or NavigationState.StuckGiveUp)
                    StopResultReview("200層：獎勵導航失敗；已保留現場。", now);
                if (reward == null || !reward.IsTargetable || nav != NavigationState.Arrived || now < _resultNextInteract ||
                    Service.Condition[ConditionFlag.OccupiedInEvent] ||
                    Service.Condition[ConditionFlag.OccupiedInQuestEvent] ||
                    Service.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return;
                if (!DeepDungeonLootTracker.TryGetItemCount(_potdRewardProfile.ItemId,
                    out int before, out _)) return;
                _resultNextInteract = now.Add(InteractionTiming.GetRetryInterval());
                GameInteraction.InteractWith(reward, 3f, false, out var failure, out bool sent);
                if (sent)
                {
                    _potdRewardCountBefore = before;
                    _potdRewardDispatchedAt = now;
                    _navHelper.Cancel();
                }
                RecordReplayEvent("result-reward-interaction", new
                { reward.GameObjectId, reward.BaseId, _potdRewardProfile.ItemId, before, sent, failure });
                return;
            }
        }
        if (_resultOpened.Count > 0)
        {
            // All currently observed reward actors are collected. The existing exit
            // controller prefers the native exit point when it is loaded.
            _navHelper.Cancel();
            _ctx.Navigator.CancelAll();
            _ctx.TerminalRewardsRequired = false;
            if (!_ctx.DutyCompletionObserved) _ctx.MarkDutyCompleted();
            Status = _ctx.StatusLine = "200層：獎勵已入袋，前往退出點";
            return;
        }

        // Missing objects at the entrance are expected: the result scene spans
        // over 200m and 55m in height. Start the load wait only at the far region.
        var navigation = _navHelper.Navigate(Potd200RewardPolicy.Approach, player.Position,
            new NavigationRange(8f, NavigationDistanceMetric.Spatial));
        Status = _ctx.StatusLine = "200層：沿終局階梯前往獎勵區，等待物件載入";
        if (navigation is NavigationState.Failed or NavigationState.StuckGiveUp)
        {
            StopResultReview("200層：終局階梯導航失敗；已保留現場。", now);
            return;
        }
        if (navigation != NavigationState.Arrived) { _potdAtRewardRegion = default; return; }
        if (_potdAtRewardRegion == default) _potdAtRewardRegion = now;
        if ((now - _potdAtRewardRegion).TotalSeconds > 45)
            StopResultReview("200層：已到獎勵區，仍未觀察到可領取獎勵；已保留現場。", now);
    }
}
