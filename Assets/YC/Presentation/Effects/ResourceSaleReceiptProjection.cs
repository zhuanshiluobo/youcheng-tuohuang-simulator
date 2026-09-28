using System;
using System.Collections.Generic;
using YC.Domain.Effects;
using YC.Domain.State;

namespace YC.Presentation
{
    /// <summary>读取已同步的出售结果；不从余额变化或数量草稿推算成交。</summary>
    public static class ResourceSaleReceiptProjection
    {
        public sealed class Receipt
        {
            public string EffectId;
            public long Revenue;
        }

        public static Receipt Latest(GameStateView view, int playerId)
        {
            if (view == null || view.Effects == null || playerId < 0) return null;
            var receipts = new Dictionary<string, Receipt>(StringComparer.Ordinal);
            foreach (var effect in view.Effects)
            {
                if (effect == null || string.IsNullOrEmpty(effect.EffectId) ||
                    effect.PlayerId != playerId || effect.EffectTypeId != LuaDomainEffectTypeIds.ResourceSell ||
                    effect.Status != EffectNodeStatus.Completed ||
                    !GameStateVisibilityPolicy.IsVisible(effect.Visibility, effect.PlayerId, GameStateViewer.Player(playerId)) ||
                    effect.NormalizedResult == null || effect.NormalizedResult.Kind != NormalizedValueKind.Integer ||
                    effect.NormalizedResult.IntegerValue < 0) continue;
                receipts[effect.EffectId] = new Receipt { EffectId = effect.EffectId, Revenue = effect.NormalizedResult.IntegerValue };
            }

            // 完成事件的顺序由内核产生；节点创建顺序不能代表成交先后。
            Receipt latest = null;
            var latestRevision = -1;
            if (view.Events != null)
            {
                foreach (var completed in view.Events)
                {
                    if (completed == null || completed.PlayerId != playerId || completed.EventType != "EffectCompleted" ||
                        completed.StateRevision < latestRevision ||
                        !GameStateVisibilityPolicy.IsVisible(completed.Visibility, completed.PlayerId, GameStateViewer.Player(playerId)) ||
                        Text(completed.Payload, "effectTypeId") != LuaDomainEffectTypeIds.ResourceSell ||
                        Text(completed.Payload, "outcome") != "completed" ||
                        !receipts.TryGetValue(Text(completed.Payload, "effectId"), out var receipt)) continue;
                    latest = receipt;
                    latestRevision = completed.StateRevision;
                }
            }
            // 单笔记录不需要推断相对次序；多笔缺少完成事件时不伪造“最近”。
            if (latest == null && receipts.Count == 1)
                foreach (var receipt in receipts.Values) latest = receipt;
            return latest;
        }

        private static string Text(NormalizedValue value, string key)
        {
            if (value == null || value.Kind != NormalizedValueKind.Object || value.Properties == null) return string.Empty;
            foreach (var item in value.Properties)
                if (item != null && item.Name == key && item.Value != null && item.Value.Kind == NormalizedValueKind.String)
                    return item.Value.StringValue ?? string.Empty;
            return string.Empty;
        }
    }
}
