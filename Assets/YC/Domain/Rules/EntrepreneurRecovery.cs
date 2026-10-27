using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Rules
{
    public static class EntrepreneurRecovery
    {
        private static bool Fields(JToken value, Type type) => value is JObject obj && type.GetFields().All(f => obj[f.Name] != null && obj[f.Name].Type != JTokenType.Null);
        public static bool HasRawFields(JToken state)
        {
            if ((string)state?["RuleConfiguration"]?["RuleModeId"] != "entrepreneur") return state?["EntrepreneurSetup"] == null || state["EntrepreneurSetup"].Type == JTokenType.Null;
            if (state["ContentDefinitions"]?["Entrepreneurs"] is not JArray frozen || !frozen.All(d => Fields(d, typeof(EntrepreneurDefinition)))) return false;
            foreach (var definition in frozen)
            {
                if (definition["RoundGroups"] is not JArray groups || definition["Components"] is not JArray components || components.Any(c => !Fields(c, typeof(EntrepreneurComponentRecipe)))) return false;
                foreach (var group in groups)
                    if (!Fields(group, typeof(RoundChoiceDefinition)) || group["Candidates"] is not JArray candidates || candidates.Any(c => !Fields(c, typeof(RoundCandidateDefinition)))) return false;
            }
            var setup = state["EntrepreneurSetup"];
            // 尚未抽取企业时可为空；已启动的选择树不能丢失整个记录。
            if (setup == null || setup.Type == JTokenType.Null)
                return !((state["EffectRuntime"]?["EffectNodes"] as JArray)?.Any(n => (string)n["EffectTypeId"] == EntrepreneurFlow.SetupTypeId && (string)n["FlowStage"] != "") ?? false);
            if (!Fields(setup, typeof(EntrepreneurSetupState)) || setup["Players"] is not JArray players || setup["EnterpriseAssignments"] is not JArray assignments) return false;
            if (assignments.Any(a => !Fields(a, typeof(EnterpriseCandidateAssignment)))) return false;
            foreach (var player in players)
            {
                if (!Fields(player, typeof(EntrepreneurPlayerSetup)) || player["RoundGroups"] is not JArray groups || player["SetupEntries"] is not JArray entries || entries.Any(e => !Fields(e, typeof(EntrepreneurImmediateEntry)))) return false;
                foreach (var group in groups)
                    if (!Fields(group, typeof(RoundChoiceState)) || group["Candidates"] is not JArray candidates || candidates.Any(c => !Fields(c, typeof(RoundCandidateState))) ||
                        group["ImmediateEntries"] is not JArray immediate || immediate.Any(e => !Fields(e, typeof(EntrepreneurImmediateEntry)))) return false;
            }
            return state["ContentDefinitions"]?["Entrepreneurs"] is JArray definitions && definitions.All(d => Fields(d, typeof(EntrepreneurDefinition)));
        }
        public static bool TryValidateEntrepreneurState(GameState state, EffectRegistry registry, out string diagnostic)
        {
            diagnostic = "entrepreneur_selection.v1:state_invalid";
            var setup = state.EntrepreneurSetup;
            if (setup == null)
            {
                if (state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == EntrepreneurFlow.SetupTypeId && n.FlowStage.Length > 0)) return false;
                diagnostic = ""; return true;
            }
            try
            {
                if (!EntrepreneurFlow.Enabled(state) || setup.ProtocolVersion != 1 || setup.SetupProtocolVersion != 3 || setup.ContentVersion != state.ContentPackHash ||
                    !new[] { "preview", "choosing", "complete" }.Contains(setup.Stage) || setup.CurrentIndex < 0 || setup.CurrentIndex > state.Players.Count ||
                    setup.PlayerOrder == null || setup.Players == null || setup.EnterpriseAssignments == null || setup.RemainingEntrepreneurs == null) return false;
                EntrepreneurContent.ValidateDefinitions(state.ContentDefinitions.Entrepreneurs);
                if (registry?.ContentDefinitions?.Entrepreneurs?.Count > 0 && !JToken.DeepEquals(JToken.FromObject(state.ContentDefinitions.Entrepreneurs), JToken.FromObject(registry.ContentDefinitions.Entrepreneurs)))
                { diagnostic = "entrepreneur_selection.v1:frozen_definitions_mismatch"; return false; }
                var owner = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == setup.OwnerEffectId && n.EffectTypeId == EntrepreneurFlow.SetupTypeId);
                var parent = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == owner?.ParentEffectId && n.EffectTypeId == ExpansionSetupFlow.PreparationTypeId);
                if (owner == null || parent == null || !setup.PlayerOrder.SequenceEqual(setup.EnterpriseAssignments.Select(a => a.PlayerId).Reverse()) ||
                    setup.Players.Select(p => p.PlayerId).Distinct().Count() != setup.Players.Count || setup.Players.Select(p => p.DefinitionId).Distinct().Count() != setup.Players.Count ||
                    !setup.Players.Select(p => p.PlayerId).SequenceEqual(setup.PlayerOrder.Take(setup.Players.Count)) || setup.EnterpriseAssignments.Count != setup.PlayerOrder.Count) return false;
                int draw = state.Players.Count == 2 ? 4 : 2;
                var original = Value(parent.ContinuationData, "candidateEnterpriseItemIds").Items.Select(v => v.StringValue).ToList();
                foreach (var assignment in setup.EnterpriseAssignments)
                {
                    int index = setup.EnterpriseAssignments.IndexOf(assignment);
                    if (index < 0 || !assignment.CandidateItemIds.SequenceEqual(original.Skip(index * draw).Take(draw))) return false;
                }
                if (setup.EnterpriseAssignments.Select(a => a.PlayerId).Distinct().Count() != setup.PlayerOrder.Count ||
                    !setup.RemainingEntrepreneurs.OrderBy(x => x).SequenceEqual(state.ContentDefinitions.Entrepreneurs.Select(d => d.DefinitionId).Except(setup.Players.Select(p => p.DefinitionId)).OrderBy(x => x))) return false;
                foreach (var player in setup.Players)
                {
                    var definition = state.ContentDefinitions.Entrepreneurs.Single(d => d.DefinitionId == player.DefinitionId);
                    var ability = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == player.AbilityItemId && i.DefinitionId == definition.DefinitionId && i.OwnerSubject.PlayerId == player.PlayerId && i.ControllerSubject.PlayerId == player.PlayerId);
                    var playerNode = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == player.OwnerEffectId && n.EffectTypeId == EntrepreneurFlow.PlayerTypeId && n.PlayerId == player.PlayerId && n.ParentEffectId == owner.EffectId);
                    if (ability == null || playerNode == null || state.ItemZones.Zones.Single(z => z.ZoneId == ability.ZoneId).TemplateId != "core.zone.entrepreneur_active" ||
                        !new[] { "allocated", "complete" }.Contains(player.Stage) || player.Stage == "complete" && player.SetupReceiptId != StableIdFactory.Create("entrepreneur-setup", playerNode.EffectId, player.DefinitionId) ||
                        player.AllocatedItemIds.Distinct().Count() != player.AllocatedItemIds.Count || player.AllocatedItemIds.Any(id => !state.ItemZones.Items.Any(i => i.ItemId == id && i.OwnerSubject.PlayerId == player.PlayerId)) || player.RoundGroups.Count != 4) return false;
                    int expectedItems = 1 + definition.RoundGroups.SelectMany(g => g.Candidates).Select(f => f.EntityDefinitionId).Distinct().Count() + definition.Components.Sum(c => c.Quantity);
                    if (player.AllocatedItemIds.Count != expectedItems || !player.AllocatedItemIds.Contains(player.AbilityItemId)) return false;
                    if (!ValidateEntries(state, registry, playerNode, player.SetupEntries, definition.SetupHandlerRefs, player.AbilityItemId)) return false;
                    foreach (var group in player.RoundGroups)
                    {
                        var frozen = definition.RoundGroups.SingleOrDefault(g => g.GroupId == group.GroupId && g.RoundNumber == group.RoundNumber);
                        if (frozen == null || group.Candidates.Count != frozen.Candidates.Count || group.Candidates.Select(c => c.CandidateId).Distinct().Count() != group.Candidates.Count) return false;
                        foreach (var candidate in group.Candidates)
                        {
                            var face = frozen.Candidates.SingleOrDefault(f => f.CandidateId == candidate.CandidateId && f.FaceId == candidate.FaceId && f.EntityDefinitionId == candidate.DefinitionId && f.DisplayOrder == candidate.DisplayOrder);
                            if (face == null || !state.ItemZones.Items.Any(i => i.ItemId == candidate.EntityItemId && i.DefinitionId == candidate.DefinitionId && i.OwnerSubject.PlayerId == player.PlayerId)) return false;
                        }
                        if (group.Candidates.GroupBy(c => c.DefinitionId).Any(g => g.Select(c => c.EntityItemId).Distinct().Count() != 1)) return false;
                        if (group.SelectedCandidateId.Length == 0)
                        {
                            if (group.ActiveFaceId.Length > 0 || group.ImmediateEntries.Count > 0 || group.Candidates.Any(c => state.ItemZones.Items.Single(i => i.ItemId == c.EntityItemId).LocalState.Properties.Any(p => p.Name == "sourceSelectionReceipt"))) return false;
                            continue;
                        }
                        var selected = group.Candidates.SingleOrDefault(c => c.CandidateId == group.SelectedCandidateId && c.FaceId == group.ActiveFaceId);
                        var selecting = state.EffectRuntime.EffectNodes.SingleOrDefault(n => n.EffectId == group.OwnerEffectId && n.EffectTypeId == EntrepreneurFlow.FaceTypeId && n.PlayerId == player.PlayerId);
                        if (selected == null || selecting == null || !AbilitySourceSelection.HasSelectedFaceReceipt(state, state.ItemZones.Items.Single(i => i.ItemId == selected.EntityItemId), selected.FaceId, player.PlayerId) ||
                            !state.EffectRuntime.InteractionRequests.Any(r => r.GetStableInteractionId() == group.RequestId && r.OwnerEffectId == selecting.EffectId && r.AnsweringPlayerId == player.PlayerId && r.Status == "answered")) return false;
                        if (!ValidateEntries(state, registry, selecting, group.ImmediateEntries, frozen.Candidates.Single(c => c.CandidateId == group.SelectedCandidateId).ImmediateHandlerRefs, selected.EntityItemId)) return false;
                    }
                }
                if (setup.Stage == "complete" && (setup.CurrentIndex != setup.PlayerOrder.Count || setup.Players.Count != setup.PlayerOrder.Count || setup.Players.Any(p => p.Stage != "complete"))) return false;
                diagnostic = ""; return true;
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is NullReferenceException)
            { diagnostic = "entrepreneur_selection.v1:" + e.Message; return false; }
        }
        private static bool ValidateEntries(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, List<EntrepreneurImmediateEntry> entries, List<string> handlers, string itemId)
        {
            if (entries == null || entries.Count != handlers.Count || entries.Select(e => e.EntryId).Distinct().Count() != entries.Count || !entries.Select(e => e.HandlerRef).SequenceEqual(handlers)) return false;
            foreach (var e in entries)
            {
                int index = entries.IndexOf(e);
                if (e.OwnerEffectId != owner.EffectId || e.EntryId != StableIdFactory.Create("entrepreneur-entry", owner.EffectId, e.HandlerRef, index.ToString(System.Globalization.CultureInfo.InvariantCulture)) ||
                    registry != null && registry.HandlerHash(e.HandlerRef).Length == 0 || !new[] { "pending", "scheduled", "completed", "failed" }.Contains(e.Status)) return false;
                var actual = state.EffectRuntime.EffectNodes.Where(n => n.ParentEffectId == owner.EffectId && Text(n.NormalizedArguments, "entryId") == e.EntryId).ToList();
                if (e.Status == "pending") { if (actual.Count != 0 || e.ReceiptId.Length > 0 || e.SourceKey.Length > 0) return false; continue; }
                var item = state.ItemZones.Items.Single(i => i.ItemId == itemId);
                if (!state.ContentDefinitions.AbilitySources.Any(d => d.DefinitionId == item.DefinitionId && d.Kind == "setup" && d.HandlerRef == e.HandlerRef &&
                    (d.ActiveFaceId.Length == 0 || d.ActiveFaceId == Text(item.LocalState, "activeFaceId")) &&
                    e.SourceKey == StableIdFactory.Create("ability-source", item.ItemId, d.AbilityId, d.ActiveFaceId, d.DeclarationVersion, owner.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)))) return false;
                if (actual.Count != 1 || actual[0].EffectTypeId != EntrepreneurFlow.EntryTypeId || actual[0].PlayerId != owner.PlayerId ||
                    Text(actual[0].NormalizedArguments, "handlerRef") != e.HandlerRef || Text(actual[0].NormalizedArguments, "sourceKey") != e.SourceKey || e.EffectRootId.Length > 0 && e.EffectRootId != actual[0].EffectId) return false;
                if (e.Status == "completed" || e.Status == "failed")
                    if (actual[0].Status != (e.Status == "completed" ? EffectNodeStatus.Completed : EffectNodeStatus.Failed) || e.ReceiptId != StableIdFactory.Create("entrepreneur-entry-receipt", e.EntryId, actual[0].Status.ToString())) return false;
            }
            return true;
        }
    }
}
