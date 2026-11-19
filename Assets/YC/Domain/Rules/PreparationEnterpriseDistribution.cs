using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;

namespace YC.Domain.Rules
{
    [Serializable]
    public sealed class PreparationEnterpriseAssignment
    {
        public int PlayerId;
        public bool Confirmed;
        public List<string> CandidateItemIds = new List<string>();
        public List<string> RequiredItemIds = new List<string>();
        public List<string> SelectedItemIds = new List<string>();
    }

    [Serializable]
    public sealed class PreparationAutomaticEnterprise
    {
        public string ItemId = string.Empty;
        public int InitialOwnerPlayerId = -1;
    }

    /// <summary>只保存实例 ID 与席位，不从定义 ID 重新生成或重新分发。</summary>
    [Serializable]
    public sealed class PreparationEnterpriseDistributionRecord
    {
        public int Version = 1;
        public string OwnerEffectId = string.Empty, ModeId = string.Empty, ContentHash = string.Empty;
        public List<int> PlayerOrder = new List<int>();
        public List<PreparationEnterpriseAssignment> Assignments = new List<PreparationEnterpriseAssignment>();
        public List<PreparationAutomaticEnterprise> Automatic = new List<PreparationAutomaticEnterprise>();
        public List<string> UnassignedItemIds = new List<string>();
        public List<string> ExtraItemIds = new List<string>();
    }

    /// <summary>准备流程的权威进度；旧存档没有该状态，继续使用旧协议。</summary>
    [Serializable]
    public sealed class PreparationRuntimeState
    {
        public int Version = 1;
        public string OwnerEffectId = string.Empty, ModeId = string.Empty, ContentHash = string.Empty, Stage = string.Empty;
        public PreparationEnterprisePlan EffectiveEnterprise;
        public PreparationEnterpriseDistributionRecord Distribution;
    }

    public static class PreparationEnterpriseDistribution
    {
        public static PreparationEnterpriseDistributionRecord Create(PreparationEnterprisePlan plan, string ownerEffectId,
            string modeId, string contentHash, IReadOnlyList<int> order, IEnumerable<ItemInstanceState> eligible, Func<int, int> next)
        {
            if (plan == null || order == null || eligible == null || next == null) throw new ArgumentNullException();
            plan.Validate();
            if (string.IsNullOrWhiteSpace(ownerEffectId) || string.IsNullOrWhiteSpace(modeId) || string.IsNullOrWhiteSpace(contentHash) ||
                order.Count < 2 || order.Count > 4 || order.Any(id => id < 1) || order.Distinct().Count() != order.Count)
                throw new InvalidOperationException("preparation_distribution_identity_invalid");
            var count = plan.PlayerCounts.SingleOrDefault(rule => rule.PlayerCount == order.Count)
                ?? throw new InvalidOperationException("preparation_distribution_player_count_missing");
            var all = eligible.ToList();
            if (all.Any(item => item == null || string.IsNullOrEmpty(item.ItemId) || string.IsNullOrEmpty(item.DefinitionId) ||
                    item.TypeId != "core.board_card" || item.PhysicalKind != ItemPhysicalKind.Card) ||
                all.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != all.Count)
                throw new InvalidOperationException("preparation_distribution_items_invalid");
            void Shuffle<T>(IList<T> items)
            {
                for (int i = items.Count - 1; i > 0; i--)
                {
                    int pick = next(i + 1);
                    if (pick < 0 || pick > i) throw new InvalidOperationException("preparation_distribution_random_invalid");
                    T value = items[i]; items[i] = items[pick]; items[pick] = value;
                }
            }
            var policy = YC.Domain.State.GameStateCloneService.DeepClone(plan.Pool);
            if (policy.Kind == "requiredRandom")
                foreach (string id in plan.Fixed.Select(item => item.DefinitionId))
                    if (!policy.RequiredDefinitionIds.Contains(id)) policy.RequiredDefinitionIds.Add(id);
            var pool = PreparationPoolResolver.Resolve(policy, all, item => item.DefinitionId, Shuffle, "enterprise");
            var byDefinition = pool.ToDictionary(item => item.DefinitionId, StringComparer.Ordinal);
            foreach (var fixedItem in plan.Fixed)
                if (!byDefinition.ContainsKey(fixedItem.DefinitionId))
                    throw new InvalidOperationException("preparation_distribution_fixed_missing:" + fixedItem.DefinitionId);
            var fixedIds = new HashSet<string>(plan.Fixed.Select(item => byDefinition[item.DefinitionId].ItemId), StringComparer.Ordinal);
            var remaining = pool.Where(item => !fixedIds.Contains(item.ItemId)).ToList();
            Shuffle(remaining);
            var record = new PreparationEnterpriseDistributionRecord { OwnerEffectId = ownerEffectId, ModeId = modeId,
                ContentHash = contentHash, PlayerOrder = order.ToList() };
            foreach (var fixedItem in plan.Fixed.Where(item => !item.RequireConfirmation))
                record.Automatic.Add(new PreparationAutomaticEnterprise { ItemId = byDefinition[fixedItem.DefinitionId].ItemId,
                    InitialOwnerPlayerId = fixedItem.OwnerOrderIndex < 0 ? -1 : order[fixedItem.OwnerOrderIndex] });
            int cursor = 0;
            for (int i = 0; i < order.Count; i++)
            {
                var assignment = new PreparationEnterpriseAssignment { PlayerId = order[i] };
                assignment.RequiredItemIds.AddRange(plan.Fixed.Where(item => item.RequireConfirmation && item.OwnerOrderIndex == i)
                    .Select(item => byDefinition[item.DefinitionId].ItemId));
                assignment.CandidateItemIds.AddRange(assignment.RequiredItemIds);
                int additional = count.Draw - assignment.CandidateItemIds.Count;
                if (additional < 0 || cursor > remaining.Count - additional)
                    throw new InvalidOperationException("preparation_distribution_insufficient_candidates");
                assignment.CandidateItemIds.AddRange(remaining.Skip(cursor).Take(additional).Select(item => item.ItemId));
                cursor += additional;
                record.Assignments.Add(assignment);
            }
            record.UnassignedItemIds.AddRange(remaining.Skip(cursor).Select(item => item.ItemId));
            if (record.UnassignedItemIds.Count < count.ExtraRandom)
                throw new InvalidOperationException("preparation_distribution_insufficient_extra");
            record.ExtraItemIds.AddRange(record.UnassignedItemIds.Take(count.ExtraRandom));
            if (!Validate(plan, record, all, ownerEffectId, modeId, contentHash, order, out string diagnostic))
                throw new InvalidOperationException(diagnostic);
            return record;
        }

        public static bool TryConfirm(PreparationEnterprisePlan plan, PreparationEnterpriseDistributionRecord record,
            IEnumerable<ItemInstanceState> eligible, string expectedOwner, string expectedMode, string expectedHash,
            IReadOnlyList<int> expectedOrder, int player, IEnumerable<string> selected, out string diagnostic)
        {
            diagnostic = "preparation_confirmation_invalid";
            if (record == null || selected == null || !Validate(plan, record, eligible, expectedOwner, expectedMode, expectedHash, expectedOrder, out _)) return false;
            var assignment = record.Assignments.SingleOrDefault(item => item.PlayerId == player);
            var count = plan.PlayerCounts.Single(rule => rule.PlayerCount == record.PlayerOrder.Count);
            var choices = selected.ToList();
            if (assignment == null || assignment.Confirmed || choices.Count != count.Keep ||
                choices.Distinct(StringComparer.Ordinal).Count() != choices.Count ||
                choices.Any(id => !assignment.CandidateItemIds.Contains(id)) || assignment.RequiredItemIds.Any(id => !choices.Contains(id))) return false;
            assignment.SelectedItemIds = choices;
            assignment.Confirmed = true;
            diagnostic = string.Empty;
            return true;
        }

        public static bool Validate(PreparationEnterprisePlan plan, PreparationEnterpriseDistributionRecord record,
            IEnumerable<ItemInstanceState> eligible, string expectedOwner, string expectedMode, string expectedHash,
            IReadOnlyList<int> expectedOrder, out string diagnostic)
        {
            diagnostic = "preparation_distribution_invalid";
            if (plan == null || record == null || eligible == null || record.Version != 1 ||
                string.IsNullOrWhiteSpace(expectedOwner) || string.IsNullOrWhiteSpace(expectedMode) || string.IsNullOrWhiteSpace(expectedHash) || expectedOrder == null ||
                record.OwnerEffectId != expectedOwner || record.ModeId != expectedMode || record.ContentHash != expectedHash ||
                record.PlayerOrder == null || record.Assignments == null || record.Automatic == null ||
                record.UnassignedItemIds == null || record.ExtraItemIds == null) return false;
            try
            {
                plan.Validate();
                var order = record.PlayerOrder;
                var count = plan.PlayerCounts.SingleOrDefault(rule => rule.PlayerCount == order.Count);
                if (count == null || !order.SequenceEqual(expectedOrder) || order.Any(id => id < 1) || order.Distinct().Count() != order.Count ||
                    record.Assignments.Count != order.Count || record.Assignments.Any(item => item == null) ||
                    !record.Assignments.Select(item => item.PlayerId).SequenceEqual(order) || record.Automatic.Any(item => item == null)) return false;
                var all = eligible.ToList();
                if (all.Any(item => item == null || string.IsNullOrEmpty(item.ItemId) || string.IsNullOrEmpty(item.DefinitionId) ||
                        item.TypeId != "core.board_card" || item.PhysicalKind != ItemPhysicalKind.Card) ||
                    all.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != all.Count ||
                    all.Select(item => item.DefinitionId).Distinct(StringComparer.Ordinal).Count() != all.Count) return false;
                var byId = all.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
                var owned = new List<string>();
                for (int i = 0; i < order.Count; i++)
                {
                    var assignment = record.Assignments[i];
                    if (assignment.CandidateItemIds == null || assignment.RequiredItemIds == null || assignment.SelectedItemIds == null ||
                        assignment.CandidateItemIds.Count != count.Draw || assignment.CandidateItemIds.Distinct(StringComparer.Ordinal).Count() != count.Draw ||
                        assignment.RequiredItemIds.Distinct(StringComparer.Ordinal).Count() != assignment.RequiredItemIds.Count ||
                        assignment.RequiredItemIds.Any(id => !assignment.CandidateItemIds.Contains(id)) ||
                        !assignment.Confirmed && assignment.SelectedItemIds.Count != 0 ||
                        assignment.Confirmed && (assignment.SelectedItemIds.Count != count.Keep ||
                            assignment.SelectedItemIds.Distinct(StringComparer.Ordinal).Count() != count.Keep ||
                            assignment.SelectedItemIds.Any(id => !assignment.CandidateItemIds.Contains(id)) ||
                            assignment.RequiredItemIds.Any(id => !assignment.SelectedItemIds.Contains(id)))) return false;
                    var requiredDefinitions = plan.Fixed.Where(item => item.RequireConfirmation && item.OwnerOrderIndex == i).Select(item => item.DefinitionId).ToArray();
                    if (assignment.RequiredItemIds.Any(id => !byId.ContainsKey(id)) ||
                        !assignment.RequiredItemIds.Select(id => byId[id].DefinitionId).SequenceEqual(requiredDefinitions)) return false;
                    owned.AddRange(assignment.CandidateItemIds);
                }
                var automatic = plan.Fixed.Where(item => !item.RequireConfirmation).ToArray();
                if (record.Automatic.Count != automatic.Length) return false;
                for (int i = 0; i < automatic.Length; i++)
                    if (!byId.TryGetValue(record.Automatic[i].ItemId, out var item) || item.DefinitionId != automatic[i].DefinitionId ||
                        record.Automatic[i].InitialOwnerPlayerId != (automatic[i].OwnerOrderIndex < 0 ? -1 : order[automatic[i].OwnerOrderIndex])) return false;
                owned.AddRange(record.Automatic.Select(item => item.ItemId));
                owned.AddRange(record.UnassignedItemIds);
                if (owned.Any(id => !byId.ContainsKey(id)) || owned.Distinct(StringComparer.Ordinal).Count() != owned.Count ||
                    record.ExtraItemIds.Count != count.ExtraRandom || record.ExtraItemIds.Distinct(StringComparer.Ordinal).Count() != record.ExtraItemIds.Count ||
                    record.ExtraItemIds.Any(id => !record.UnassignedItemIds.Contains(id))) return false;
                var poolDefinitions = owned.Select(id => byId[id].DefinitionId).ToList();
                if (plan.Pool.Kind == "all" && poolDefinitions.Count != all.Count ||
                    plan.Pool.Kind == "fixed" && !new HashSet<string>(poolDefinitions).SetEquals(plan.Pool.CandidateDefinitionIds) ||
                    plan.Pool.Kind == "requiredRandom" && (poolDefinitions.Count != plan.Pool.Count ||
                        plan.Pool.RequiredDefinitionIds.Any(id => !poolDefinitions.Contains(id)) ||
                        plan.Pool.CandidateDefinitionIds.Count > 0 && poolDefinitions.Any(id => !plan.Pool.CandidateDefinitionIds.Contains(id)))) return false;
                diagnostic = string.Empty;
                return true;
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is NullReferenceException)
            { return false; }
        }
    }
}
