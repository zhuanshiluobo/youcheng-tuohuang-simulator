using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    [Serializable] public sealed class RoundCandidateDefinition
    {
        public string CandidateId = "", EntityDefinitionId = "", FaceId = "", SourceId = "", Artwork = "", ArtworkSha256 = "";
        public int DisplayOrder;
        public List<string> RuleRefs = new List<string>(), ImmediateHandlerRefs = new List<string>();
    }
    [Serializable] public sealed class RoundChoiceDefinition
    {
        public string GroupId = "", PreviewCandidateId = "", UnselectedEntityDestination = "reserve";
        public int RoundNumber;
        public List<RoundCandidateDefinition> Candidates = new List<RoundCandidateDefinition>();
    }
    [Serializable] public sealed class EntrepreneurComponentRecipe
    {
        public string EntryId = "", DefinitionId = "", TypeId = "", DestinationRole = "", CarrierLocalZoneId = "", SourceRole = "reserve";
        public int Quantity;
    }
    [Serializable] public sealed class EntrepreneurDefinition
    {
        public string DefinitionId = "", Version = "1.0.0", SourceId = "", DisplayName = "", Artwork = "";
        public List<string> SetupHandlerRefs = new List<string>();
        public List<RoundChoiceDefinition> RoundGroups = new List<RoundChoiceDefinition>();
        public List<EntrepreneurComponentRecipe> Components = new List<EntrepreneurComponentRecipe>();
    }
    [Serializable] public sealed class EntrepreneurImmediateEntry
    {
        public string EntryId = "", HandlerRef = "", OwnerEffectId = "", SourceKey = "", EffectRootId = "", Status = "pending", ReceiptId = "";
    }
    [Serializable] public sealed class RoundCandidateState
    {
        public string CandidateId = "", EntityItemId = "", FaceId = "", DefinitionId = "";
        public int DisplayOrder;
    }
    [Serializable] public sealed class RoundChoiceState
    {
        public string GroupId = "", RequestId = "", OwnerEffectId = "", SelectedCandidateId = "", ActiveFaceId = "";
        public int RoundNumber;
        public List<RoundCandidateState> Candidates = new List<RoundCandidateState>();
        public List<EntrepreneurImmediateEntry> ImmediateEntries = new List<EntrepreneurImmediateEntry>();
    }
    [Serializable] public sealed class EntrepreneurPlayerSetup
    {
        public int PlayerId;
        public string DefinitionId = "", AbilityItemId = "", OwnerEffectId = "", Stage = "allocated", SetupReceiptId = "";
        public List<string> AllocatedItemIds = new List<string>();
        public List<RoundChoiceState> RoundGroups = new List<RoundChoiceState>();
        public List<EntrepreneurImmediateEntry> SetupEntries = new List<EntrepreneurImmediateEntry>();
    }
    [Serializable] public sealed class EnterpriseCandidateAssignment
    {
        public int PlayerId;
        public List<string> CandidateItemIds = new List<string>();
    }
    [Serializable] public sealed class EntrepreneurSetupState
    {
        public int ProtocolVersion = 1, SetupProtocolVersion = 3, CurrentIndex;
        public string ContentVersion = "", OwnerEffectId = "", Stage = "preview";
        public List<int> PlayerOrder = new List<int>();
        public List<string> RemainingEntrepreneurs = new List<string>();
        public List<EnterpriseCandidateAssignment> EnterpriseAssignments = new List<EnterpriseCandidateAssignment>();
        public List<EntrepreneurPlayerSetup> Players = new List<EntrepreneurPlayerSetup>();
    }
    [Serializable] public sealed class EntrepreneurRoundSlotView
    {
        public string GroupId = "", DisplayCandidateId = "", DisplayFaceId = "", Artwork = "", SelectedCandidateId = "", ActiveFaceId = "";
        public string DisplayState = "unselected_preview", PreviewDiagnostic = "";
        public int RoundNumber;
        public List<RoundCandidateDefinition> Candidates = new List<RoundCandidateDefinition>();
    }
    [Serializable] public sealed class EntrepreneurPlayerView
    {
        public int PlayerId;
        public string DefinitionId = "", AbilityItemId = "", SetupStage = "";
        public List<EntrepreneurRoundSlotView> RoundSlots = new List<EntrepreneurRoundSlotView>();
    }
    [Serializable] public sealed class EntrepreneurStateView
    {
        public string Stage = "";
        public int ChoosingPlayerId = -1;
        public List<string> OwnEnterpriseCandidateItemIds = new List<string>(), RemainingEntrepreneurDefinitionIds = new List<string>();
        public List<EnterpriseCandidateCardView> OwnEnterpriseCandidates = new List<EnterpriseCandidateCardView>();
        public List<EntrepreneurPlayerView> Players = new List<EntrepreneurPlayerView>();
    }
    /// <summary>仅本人已分配候选的显示身份；不携带隐藏物品状态、区域或其它候选。</summary>
    [Serializable] public sealed class EnterpriseCandidateCardView
    {
        public string ItemId = "", DefinitionId = "";
    }
    public static class EntrepreneurContent
    {
        public const string Version = "1.0.0", Specification = "entrepreneur-content.v1";
        public static void ValidateDefinitions(IReadOnlyList<EntrepreneurDefinition> definitions)
        {
            if (definitions == null || definitions.Any(d => d == null || string.IsNullOrWhiteSpace(d.DefinitionId) || string.IsNullOrWhiteSpace(d.Version)) ||
                definitions.Select(d => d.DefinitionId).Distinct().Count() != definitions.Count) throw new InvalidOperationException("entrepreneur_definitions_invalid");
            foreach (var d in definitions)
            {
                if (d.RoundGroups == null || d.Components == null || d.SetupHandlerRefs == null || d.RoundGroups.Count != 4 ||
                    !d.RoundGroups.Select(g => g.RoundNumber).OrderBy(n => n).SequenceEqual(new[] { 1, 3, 5, 7 }) ||
                    d.RoundGroups.Select(g => g.GroupId).Distinct().Count() != 4) throw new InvalidOperationException("entrepreneur_groups_invalid:" + d.DefinitionId);
                foreach (var g in d.RoundGroups)
                {
                    if (string.IsNullOrWhiteSpace(g.GroupId) || g.Candidates == null || g.Candidates.Count < 1 ||
                        g.Candidates.Any(c => c == null || string.IsNullOrWhiteSpace(c.CandidateId) || string.IsNullOrWhiteSpace(c.EntityDefinitionId) || string.IsNullOrWhiteSpace(c.FaceId) || c.RuleRefs == null || c.ImmediateHandlerRefs == null) ||
                        g.Candidates.Select(c => c.CandidateId).Distinct().Count() != g.Candidates.Count || g.Candidates.Select(c => c.DisplayOrder).Distinct().Count() != g.Candidates.Count ||
                        g.PreviewCandidateId.Length > 0 && !g.Candidates.Any(c => c.CandidateId == g.PreviewCandidateId) ||
                        !new[] { "reserve", "retired" }.Contains(g.UnselectedEntityDestination)) throw new InvalidOperationException("entrepreneur_candidates_invalid:" + g.GroupId);
                }
                if (d.Components.Any(c => c == null || string.IsNullOrEmpty(c.EntryId) || string.IsNullOrEmpty(c.DefinitionId) || ItemTypeRegistry.CreateBuiltIn().Find(c.TypeId) == null || c.Quantity < 1 ||
                    (c.DestinationRole != "supply" && c.DestinationRole != "ability_card")) || d.Components.Select(c => c.EntryId).Distinct().Count() != d.Components.Count)
                    throw new InvalidOperationException("entrepreneur_components_invalid:" + d.DefinitionId);
            }
        }
        public static EntrepreneurStateView Project(GameState state, GameStateViewer viewer)
        {
            var result = new EntrepreneurStateView(); var setup = state.EntrepreneurSetup;
            if (setup == null || state.RuleConfiguration?.RuleModeId != "entrepreneur") return result;
            result.Stage = setup.Stage;
            if (setup.CurrentIndex < setup.PlayerOrder.Count && setup.Stage != "complete") result.ChoosingPlayerId = setup.PlayerOrder[setup.CurrentIndex];
            result.RemainingEntrepreneurDefinitionIds.AddRange(setup.RemainingEntrepreneurs);
            if (viewer.Role == GameStateViewerRole.Player && state.FindPlayer(viewer.PlayerId) != null)
            {
                var assignment = setup.EnterpriseAssignments.SingleOrDefault(a => a.PlayerId == viewer.PlayerId);
                if (assignment != null)
                {
                    if (assignment.CandidateItemIds == null || assignment.CandidateItemIds.Any(string.IsNullOrWhiteSpace) ||
                        assignment.CandidateItemIds.Distinct(StringComparer.Ordinal).Count() != assignment.CandidateItemIds.Count)
                        throw new InvalidOperationException("entrepreneur_candidate_projection_assignment_invalid");
                    foreach (string itemId in assignment.CandidateItemIds)
                    {
                        var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == itemId);
                        if (item == null || item.PhysicalKind != ItemPhysicalKind.Card || item.TypeId != "core.board_card" || string.IsNullOrWhiteSpace(item.DefinitionId))
                            throw new InvalidOperationException("entrepreneur_candidate_projection_item_invalid:" + itemId);
                        result.OwnEnterpriseCandidateItemIds.Add(itemId);
                        result.OwnEnterpriseCandidates.Add(new EnterpriseCandidateCardView { ItemId = itemId, DefinitionId = item.DefinitionId });
                    }
                }
            }
            foreach (var p in setup.Players)
            {
                var d = state.ContentDefinitions.Entrepreneurs.Single(x => x.DefinitionId == p.DefinitionId);
                var v = new EntrepreneurPlayerView { PlayerId = p.PlayerId, DefinitionId = p.DefinitionId, AbilityItemId = p.AbilityItemId, SetupStage = p.Stage };
                foreach (var g in d.RoundGroups.OrderBy(g => g.RoundNumber))
                {
                    var actual = p.RoundGroups.Single(x => x.GroupId == g.GroupId);
                    var display = g.Candidates.Find(c => c.CandidateId == (actual.SelectedCandidateId.Length > 0 ? actual.SelectedCandidateId : g.PreviewCandidateId));
                    v.RoundSlots.Add(new EntrepreneurRoundSlotView { GroupId = g.GroupId, RoundNumber = g.RoundNumber,
                        SelectedCandidateId = actual.SelectedCandidateId, ActiveFaceId = actual.ActiveFaceId,
                        DisplayCandidateId = display?.CandidateId ?? "", DisplayFaceId = display?.FaceId ?? "", Artwork = display?.Artwork ?? "",
                        DisplayState = actual.SelectedCandidateId.Length > 0 ? "selected" : "unselected_preview",
                        PreviewDiagnostic = display == null ? "diy_preview_candidate_missing:" + g.GroupId : "",
                        Candidates = GameStateCloneService.DeepClone(g.Candidates.OrderBy(c => c.DisplayOrder).ToList()) });
                }
                result.Players.Add(v);
            }
            return result;
        }
    }
}
