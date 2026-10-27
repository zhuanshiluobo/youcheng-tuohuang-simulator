using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Domain.Travel;

// 有序执行归YC.Application所有；Domain的新提交入口保持internal，普通调用方不能选择放宽路径。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("YC.Application")]

namespace YC.Domain.Rules
{
    public sealed class InteractionRequestAuthority
    {
        public string InteractionId { get; }
        public string OwnerEffectId { get; }
        public string OperationWindowId { get; }
        public int WindowGeneration { get; }
        public int PlayerId { get; }
        public string Profile { get; }
        public string Digest { get; }
        internal InteractionRequestAuthority(InteractionRequest request, OperationCommandContext context, string profile, string digest)
        { InteractionId = request.GetStableInteractionId(); OwnerEffectId = request.OwnerEffectId; PlayerId = request.AnsweringPlayerId;
          OperationWindowId = context.OperationWindowId; WindowGeneration = context.WindowGeneration; Profile = profile; Digest = digest; }
    }

    /// <summary>只读三类并发请求的语义合同；不提交、不刷新请求、不消耗随机或执行Lua。</summary>
    public static class InteractionRequestValidity
    {
        public const int ContractVersion = 1;
        public const string VersionParameter = "requestContractVersion";
        public const string DigestParameter = "expectedRequestContractDigest";
        internal const string AcceptedVersionParameter = "acceptedRequestContractVersion";
        internal const string AcceptedDigestParameter = "acceptedRequestContractDigest";
        internal const string AcceptedCommandParameter = "acceptedRequestCommandId";
        internal const string AcceptanceRecord = "requestContractAcceptance";
        public const string NormalCover = "normal_cover";
        public const string InitialEnterprise = "initial_enterprise";
        public const string NormalHarvest = "normal_harvest";

        public static bool TryReadMarker(GameCommand command, out bool present, out string digest, out ValidationResult validation)
        {
            present = false; digest = null; validation = ValidationResult.Success;
            if (command?.Parameters == null) return true;
            bool version = command.Parameters.TryGetValue(VersionParameter, out var value);
            bool hash = command.Parameters.TryGetValue(DigestParameter, out digest);
            present = version || hash;
            if (!present) return true;
            if (command.Kind != GameCommandKind.AnswerInteraction || !version || !hash || value != "1" ||
                digest == null || digest.Length != 64 || digest.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
            { validation = Invalid("request_contract_marker_invalid"); return false; }
            return true;
        }

        public static bool TryReadRequestAuthority(GameState state, GameCommand command,
            out InteractionRequestAuthority authority, out ValidationResult validation)
        {
            authority = null; validation = Invalid("request_contract_profile_not_supported");
            if (state?.RuleConfiguration?.PlayerStageSchedulingMode != PlayerStageSchedulingMode.Concurrent ||
                state.EffectRuntime?.Status != EffectRuntimeStatus.Active || command?.Kind != GameCommandKind.AnswerInteraction) return false;
            string id = Parameter(command, "interactionId");
            if (id.Length == 0) id = command.SourceId;
            if (string.IsNullOrEmpty(id)) id = command.TargetId;
            var request = state.EffectRuntime.InteractionRequests.Find(r => r.Status == "open" && r.GetStableInteractionId() == id && r.AnsweringPlayerId == command.PlayerId);
            var owner = request == null ? null : state.EffectRuntime.EffectNodes.Find(n => n.EffectId == request.OwnerEffectId);
            if (owner == null || Terminal(owner.Status) || owner.PlayerId != command.PlayerId || state.FindPlayer(command.PlayerId) == null) return false;
            bool potentialProfile = owner.EffectTypeId == CharacterCoverEffectExecutor.EffectTypeId && request.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId &&
                    ItemTransferEffectExecutor.Number(owner.NormalizedArguments, "apiVersion") < 2 ||
                owner.EffectTypeId == ExpansionSetupFlow.EnterpriseSelectionTypeId && request.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction ||
                owner.EffectTypeId == RouteResourceSelectionEffectExecutor.TypeId && request.InteractionTypeId == "route_resource.selection" &&
                    ItemTransferEffectExecutor.Number(owner.NormalizedArguments, "purpose") == (int)RouteSelectionPurpose.NormalHarvest;
            if (!potentialProfile) return false;
            // 仅决定不降级为legacy；授权仍须通过下方真实主链/组/任务检查。
            validation = Invalid("request_contract_authority_changed");
            string profile; EffectNodeRuntimeState groupOwner, task;
            if (owner.EffectTypeId == CharacterCoverEffectExecutor.EffectTypeId && request.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId &&
                state.Phase == GamePhase.CharacterCover && ItemTransferEffectExecutor.Number(owner.NormalizedArguments, "apiVersion") < 2 &&
                TryGroup(state, owner, "character_cover_players", out groupOwner, out task) && task == owner &&
                RoundExecutionService.IsParallelCoverExecution(groupOwner) && ActiveMain(state, groupOwner, RoundMainlineNodeTypeIds.CharacterCover))
                profile = NormalCover;
            else if (owner.EffectTypeId == ExpansionSetupFlow.EnterpriseSelectionTypeId && request.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction &&
                TryGroup(state, owner, "initial_enterprise_selections", out groupOwner, out task) && task == owner &&
                groupOwner.EffectTypeId == ExpansionSetupFlow.PreparationTypeId && ItemTransferEffectExecutor.Number(groupOwner.NormalizedArguments, "setupProtocolVersion") == 2 &&
                groupOwner.FlowStage == "awaiting_initial_enterprise_tasks" && ActiveAncestor(state, groupOwner, RoundMainlineNodeTypeIds.PlayerEntranceSetup))
                profile = InitialEnterprise;
            else if (owner.EffectTypeId == RouteResourceSelectionEffectExecutor.TypeId && request.InteractionTypeId == "route_resource.selection" &&
                ItemTransferEffectExecutor.Number(owner.NormalizedArguments, "purpose") == (int)RouteSelectionPurpose.NormalHarvest &&
                state.Phase == GamePhase.ResourceCollection && TryGroup(state, owner, "collection_players", out groupOwner, out task) && task == owner &&
                ActiveMain(state, groupOwner, RoundMainlineNodeTypeIds.Collection))
                profile = NormalHarvest;
            else return false;
            validation = Invalid("request_contract_authority_changed");
            if (task.PlayerId != command.PlayerId || Terminal(task.Status) || !OperationWindowService.TryResolveCommandContext(state, command, out var context, out validation)) return false;
            validation = Invalid("request_contract_authority_changed");
            if (request.Visibility != GameStateVisibilityPolicy.Owner || request.CandidateIds == null || request.CandidateIds.Count == 0 ||
                request.CandidateIds.Distinct(StringComparer.Ordinal).Count() != request.CandidateIds.Count) return false;
            if (profile == NormalCover)
            {
                var main = state.EffectRuntime.MainNodes.Single(m => m.ExecutionEffectId == groupOwner.EffectId && m.NodeId == state.EffectRuntime.ActiveMainNodeId);
                var group = groupOwner.ChildSchedulingGroups.Single(g => g.StableGroupKey == "character_cover_players");
                if (owner.FlowStage != "awaiting_card" || request.MinSelections != 1 || request.MaxSelections != 1 || request.AllowDecline ||
                    ItemTransferEffectExecutor.Text(owner.NormalizedArguments, "mainNodeId") != main.NodeId ||
                    ItemTransferEffectExecutor.Number(owner.NormalizedArguments, "playerId", -1) != command.PlayerId ||
                    ItemTransferEffectExecutor.Number(owner.NormalizedArguments, "coverIndex", -1) != group.ChildEffectIds.IndexOf(owner.EffectId) ||
                    main.CompletedPlayerIds.Contains(command.PlayerId)) return false;
            }
            var fields = new StringBuilder();
            void Text(string key, object value)
            { string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; fields.Append(key).Append(':').Append(text.Length).Append(':').Append(text); }
            void Ids(string key, IEnumerable<string> values) { foreach (string value in values ?? Array.Empty<string>()) Text(key, value); }
            Text("format", "interaction.request-contract.sha256.v1"); Text("game", state.GameId); Text("content", state.ContentPackHash);
            Text("profile", profile); Text("map", state.MapId); Text("round", state.Round); Text("phase", state.Phase);
            Text("window", context.OperationWindowId); Text("generation", context.WindowGeneration); Text("windowKind", context.WindowKind);
            Text("main", context.SourceMainNodeId); Text("group", groupOwner.EffectId); Text("task", task.EffectId);
            var schedulingGroup = groupOwner.ChildSchedulingGroups.Single(g => g.StableGroupKey ==
                (profile == NormalCover ? "character_cover_players" : profile == InitialEnterprise ? "initial_enterprise_selections" : "collection_players"));
            Text("groupId", schedulingGroup.GroupId); Text("groupKey", schedulingGroup.StableGroupKey); Text("groupStart", schedulingGroup.StartIndex);
            Text("ownerSource", owner.SourceId); Text("ownerParent", owner.ParentEffectId); Text("ownerVersion", owner.DefinitionVersion);
            Text("request", request.GetStableInteractionId()); Text("owner", request.OwnerEffectId); Text("source", request.SourceNodeId);
            Text("player", request.AnsweringPlayerId); Text("type", request.InteractionTypeId); Text("schema", request.AnswerSchema);
            Text("minimum", request.MinSelections); Text("maximum", request.MaxSelections); Text("decline", request.AllowDecline);
            Text("visibility", request.Visibility); Text("candidateSet", request.CandidateSetId); Text("candidateVersion", request.CandidateSetVersion);
            Text("resolution", request.CandidateResolutionId); Ids("candidate", request.CandidateIds);
            Text("stage", owner.FlowStage); Text("arguments", owner.NormalizedArguments?.ToDeterministicString());
            var parameters = request.PromptParameters?.Clone();
            if (profile == NormalHarvest && parameters?.Properties != null)
                parameters.Properties.RemoveAll(e => e.Name == "requestRevision" || e.Name == "selectionDiagnostic");
            Text("parameters", parameters?.ToDeterministicString());
            if (!string.IsNullOrEmpty(request.CandidateResolutionId))
            {
                var resolution = state.EffectRuntime.CandidateResolutions.Find(c => c.CandidateSetId == request.CandidateResolutionId && c.Version == request.CandidateSetVersion);
                if (resolution == null || !resolution.FinalCandidateIds.SequenceEqual(request.CandidateIds)) { validation = Invalid("request_candidate_resolution_changed"); return false; }
                Text("kind", resolution.CandidateKind); Text("candidateOwner", resolution.SourceEffectId); Text("executingPlayer", resolution.ExecutingPlayerId);
                Ids("pool", resolution.CandidatePoolIds); Ids("default", resolution.DefaultCandidateIds);
            }
            if (profile == InitialEnterprise)
            {
                var group = groupOwner.ChildSchedulingGroups.Single(g => g.StableGroupKey == "initial_enterprise_selections");
                int count = group.ChildEffectIds.Count, draw = count == 2 ? 4 : 2, keep = count == 2 ? 2 : 1;
                var candidates = ReadIds(ItemTransferEffectExecutor.Value(owner.NormalizedArguments, "candidateEnterpriseItemIds"));
                var assigned = ReadIds(ItemTransferEffectExecutor.Value(groupOwner.ContinuationData, "candidateEnterpriseItemIds"))
                    .Skip(group.ChildEffectIds.IndexOf(owner.EffectId) * draw).Take(draw).ToArray();
                if (owner.FlowStage != "awaiting_selection" || request.AllowDecline ||
                    state.SetupRecipe?.EnterpriseEntrance != true || count < 2 || count > 4 || ItemTransferEffectExecutor.Number(owner.NormalizedArguments, "keepCount") != keep ||
                    !candidates.SequenceEqual(assigned) || request.MinSelections != keep || request.MaxSelections != keep ||
                    !request.CandidateIds.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(candidates.OrderBy(x => x, StringComparer.Ordinal)))
                { validation = Invalid("enterprise_request_assignment_changed"); return false; }
                Text("keep", keep); Ids("assigned", assigned);
            }
            if (profile == NormalCover || profile == InitialEnterprise)
            {
                foreach (string candidate in request.CandidateIds)
                {
                    var item = state.ItemZones.Items.Find(i => i.ItemId == candidate);
                    if (ItemZoneLegacyProjection.IsActive(state))
                    {
                        if (item == null) { validation = Invalid("request_candidate_item_missing"); return false; }
                        Text("item", item.ItemId); Text("definition", item.DefinitionId); Text("itemType", item.TypeId); Text("zone", item.ZoneId);
                        Text("controllerKind", item.ControllerSubject?.Kind); Text("controller", item.ControllerSubject?.PlayerId);
                        Text("controllerCompany", item.ControllerSubject?.CompanyId);
                        Text("ownerKind", item.OwnerSubject?.Kind); Text("itemOwner", item.OwnerSubject?.PlayerId);
                        Text("ownerCompany", item.OwnerSubject?.CompanyId);
                        var zone = state.ItemZones.Zones.Find(z => z.ZoneId == item.ZoneId);
                        Text("zoneTemplate", zone?.TemplateId); Text("zoneLocal", zone?.LocalZoneId); Text("zoneOwner", zone?.OwnerPlayerId);
                        if (profile == NormalCover && (item.ControllerSubject?.Kind != ItemSubjectKind.Player || item.ControllerSubject.PlayerId != command.PlayerId ||
                            zone?.LocalZoneId != "hand" || zone.OwnerPlayerId != command.PlayerId ||
                            !ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(item.TypeId, "core.character_card") ||
                            !ItemTypeRegistry.CreateBuiltIn().HasOperationProfile(item.TypeId, "core.character_card_use")))
                        { validation = Invalid("cover_card_source_changed"); return false; }
                        if (profile == InitialEnterprise && (item.TypeId != "core.board_card" || item.PhysicalKind != ItemPhysicalKind.Card ||
                            state.ContentDefinitions?.EnterpriseDefinitionIds?.Contains(item.DefinitionId) != true || zone == null ||
                            zone.OwnerPlayerId > 0 || item.ControllerSubject?.Kind == ItemSubjectKind.Player))
                        { validation = Invalid("enterprise_candidate_source_changed"); return false; }
                    }
                    else if (profile == NormalCover && !state.FindPlayer(command.PlayerId).HandCardIds.Contains(candidate))
                    { validation = Invalid("cover_card_source_changed"); return false; }
                }
                if (profile == NormalCover)
                {
                    var player = state.FindPlayer(command.PlayerId);
                    Text("covered", player.CoveredCharacterCardId);
                    if (!string.IsNullOrEmpty(player.CoveredCharacterCardId) || state.ItemZones.Items.Any(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "covered" && z.OwnerPlayerId == command.PlayerId)))
                    { validation = Invalid("cover_already_completed"); return false; }
                }
            }
            if (profile == NormalHarvest)
            {
                var routeAuthority = new RouteSelectionSessionAuthority();
                var read = routeAuthority.TryRestore(state, owner, out var session, out var binding);
                if (!read.IsValid || session.Purpose != RouteSelectionPurpose.NormalHarvest || session.Stage != RouteSelectionStage.Selecting || binding == null ||
                    session.SchemaVersion == YC.Domain.Harvest.CollectionPlanService.ProtocolVersion && session.CollectionFreeLocations == null ||
                    binding.CandidateSetId != request.CandidateSetId || binding.CandidateSetVersion != request.CandidateSetVersion ||
                    binding.ScopeKey != RouteSelectionSessionAuthority.Text(request.PromptParameters, "scopeKey") ||
                    binding.FrozenFactsDigest != RouteSelectionSessionAuthority.Text(request.PromptParameters, "factsDigest"))
                { validation = Invalid("collection_request_binding_changed"); return false; }
                var facts = routeAuthority.TryValidateNormalHarvestRequest(state, owner, request, null);
                if (!facts.IsValid) { validation = facts; return false; }
                var budgets = ItemTransferEffectExecutor.Value(groupOwner.ContinuationData, "collectionStartGoldVoucherAmounts");
                var originalBudget = budgets?.Items?.Find(v => ItemTransferEffectExecutor.Number(v, "playerId", -1) == command.PlayerId);
                if (ItemTransferEffectExecutor.Text(owner.NormalizedArguments, "budgetOwnerEffectId") != groupOwner.EffectId ||
                    originalBudget == null || ItemTransferEffectExecutor.Number(originalBudget, "amount", -1) != session.StartGoldVoucherAmount ||
                    session.BudgetBindingId != StableIdFactory.Create("route_selection_budget", owner.EffectId, groupOwner.EffectId))
                { validation = Invalid("collection_start_budget_changed"); return false; }
                Text("budget", session.BudgetBindingId); Text("startGold", session.StartGoldVoucherAmount);
                Text("scope", binding.ScopeKey); Text("facts", binding.FrozenFactsDigest);
            }
            using (var sha = SHA256.Create())
                authority = new InteractionRequestAuthority(request, context, profile,
                    BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(fields.ToString()))).Replace("-", "").ToLowerInvariant());
            validation = ValidationResult.Success; return true;
        }

        public static bool TryValidateAnswer(GameState state, GameCommand command, string expectedDigest, out ValidationResult validation)
        {
            if (!TryReadRequestAuthority(state, command, out var authority, out validation)) return false;
            if (authority.Digest != expectedDigest) { validation = Invalid("request_contract_changed", CommandErrorCode.StateRevisionMismatch); return false; }
            var request = state.EffectRuntime.InteractionRequests.Find(r => r.GetStableInteractionId() == authority.InteractionId && r.Status == "open");
            if (!TryBuildAnswer(command, out var answer, out var diagnostic) || !answer.TryValidate(out diagnostic) || !EffectTreeExecutor.TryValidateInteractionAnswerOnState(state, request, answer, out diagnostic))
            { validation = Invalid(diagnostic); return false; }
            if (authority.Profile == NormalHarvest)
            {
                var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == authority.OwnerEffectId);
                validation = new RouteSelectionSessionAuthority().TryValidateNormalHarvestRequest(state, owner, request, command.OptionIds);
                return validation.IsValid;
            }
            if (authority.Profile == NormalCover && ItemZoneLegacyProjection.IsActive(state))
            {
                var ids = answer.Kind == NormalizedValueKind.Array ? answer.Items.Select(v => v.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v.StringValue).ToArray() :
                    new[] { answer.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : answer.StringValue };
                var item = state.ItemZones.Items.Find(i => i.ItemId == ids.Single());
                var destination = state.ItemZones.Zones.Find(z => z.LocalZoneId == "covered" && z.OwnerPlayerId == command.PlayerId);
                if (item == null || destination == null) { validation = Invalid("cover_card_source_changed"); return false; }
                var transfer = new ItemTransferService().Validate(state, new ItemTransferRequest {
                    ExecutingPlayerId = command.PlayerId, ItemIds = new List<string> { item.ItemId }, RequiredCount = 1,
                    ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId }, Destination = new ZoneRef { ZoneId = destination.ZoneId },
                    InsertionKind = "unordered", OperationProfileId = "core.character_card_transfer" });
                if (!transfer.IsValid) { validation = Invalid(transfer.ReasonCode); return false; }
            }
            validation = ValidationResult.Success; return true;
        }

        public static bool TryBuildAnswer(GameCommand command, out NormalizedValue answer, out string diagnostic)
        {
            answer = null; diagnostic = "interaction_answer_missing";
            string value = Parameter(command, "answerValue");
            if (value.Length > 0) { answer = bool.TryParse(value, out bool boolean) ? NormalizedValue.CreateBoolean(boolean) : NormalizedValue.CreateString(value); return true; }
            if (command.OptionIds?.Count > 0)
            {
                if (command.OptionIds.Any(string.IsNullOrEmpty)) return false;
                answer = NormalizedValue.CreateArray(command.OptionIds.Select(id => NormalizedValue.CreateStableReference("candidate", id)).ToList()); return true;
            }
            if (!string.IsNullOrEmpty(command.TargetId)) { answer = NormalizedValue.CreateStableReference("candidate", command.TargetId); return true; }
            return false;
        }

        public static bool IsNormalHarvestOwner(GameState state, EffectNodeRuntimeState owner)
            => state?.RuleConfiguration?.PlayerStageSchedulingMode == PlayerStageSchedulingMode.Concurrent && state.Phase == GamePhase.ResourceCollection &&
                owner != null && !Terminal(owner.Status) && owner.EffectTypeId == RouteResourceSelectionEffectExecutor.TypeId &&
                ItemTransferEffectExecutor.Number(owner.NormalizedArguments, "purpose") == (int)RouteSelectionPurpose.NormalHarvest &&
                TryGroup(state, owner, "collection_players", out var groupOwner, out var task) && task == owner && task.PlayerId == owner.PlayerId &&
                !Terminal(task.Status) && ActiveMain(state, groupOwner, RoundMainlineNodeTypeIds.Collection);

        private static bool TryGroup(GameState state, EffectNodeRuntimeState owner, string key, out EffectNodeRuntimeState groupOwner, out EffectNodeRuntimeState task)
        {
            groupOwner = null; task = owner;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (task != null && visited.Add(task.EffectId))
            {
                string parentId = task.ParentEffectId;
                var parent = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == parentId);
                if (parent == null || Terminal(parent.Status) || !parent.ChildEffectIds.Contains(task.EffectId)) return false;
                var groups = parent.ChildSchedulingGroups.Where(g => g.StableGroupKey == key).ToArray();
                if (groups.Length == 1)
                {
                    var group = groups[0];
                    if (group.OwnerEffectId != parent.EffectId || group.ProtocolVersion != 1 || group.Mode != EffectChildSchedulingMode.Independent ||
                        !group.ChildEffectIds.Contains(task.EffectId) || group.ChildEffectIds.Distinct().Count() != group.ChildEffectIds.Count || group.StartIndex < 0 ||
                        !parent.ChildEffectIds.Skip(group.StartIndex).Take(group.ChildEffectIds.Count).SequenceEqual(group.ChildEffectIds)) return false;
                    var main = state.EffectRuntime.MainNodes.Find(m => m.NodeId == state.EffectRuntime.ActiveMainNodeId);
                    if (main == null) return false;
                    var order = new TurnOrderService().GetTurnOrder(state, main.StartPlayerId);
                    var children = group.ChildEffectIds.Select(id => state.EffectRuntime.EffectNodes.Find(n => n.EffectId == id)).ToArray();
                    string type = key == "character_cover_players" ? CharacterCoverEffectExecutor.EffectTypeId :
                        key == "initial_enterprise_selections" ? ExpansionSetupFlow.EnterpriseSelectionTypeId : RouteResourceSelectionEffectExecutor.TypeId;
                    if (children.Any(n => n == null || n.ParentEffectId != parent.EffectId || n.EffectTypeId != type || n.Visibility != GameStateVisibilityPolicy.Owner) ||
                        children.Select(n => n.PlayerId).Distinct().Count() != children.Length ||
                        !order.Where(p => children.Any(n => n.PlayerId == p)).SequenceEqual(children.Select(n => n.PlayerId)) ||
                        key != "collection_players" && children.Length != order.Count) return false;
                    groupOwner = parent; return true;
                }
                task = parent;
            }
            return false;
        }
        private static bool ActiveMain(GameState state, EffectNodeRuntimeState execution, string type)
            => state.EffectRuntime.MainNodes.Any(m => m.NodeId == state.EffectRuntime.ActiveMainNodeId && m.ExecutionEffectId == execution.EffectId && m.NodeTypeId == type && !Terminal(m.Status));
        private static bool ActiveAncestor(GameState state, EffectNodeRuntimeState node, string type)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (node != null && visited.Add(node.EffectId))
            { if (ActiveMain(state, node, type)) return true; node = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == node.ParentEffectId); }
            return false;
        }
        private static bool Terminal(EffectNodeStatus status) => status == EffectNodeStatus.Completed || status == EffectNodeStatus.Failed || status == EffectNodeStatus.Faulted;
        private static string[] ReadIds(NormalizedValue value) => value?.Kind == NormalizedValueKind.Array ? value.Items.Select(v => v.Kind == NormalizedValueKind.StableReference ? v.ReferenceId : v.StringValue).ToArray() : Array.Empty<string>();
        private static string Parameter(GameCommand command, string key) => command?.Parameters != null && command.Parameters.TryGetValue(key, out var value) ? value ?? "" : "";
        private static ValidationResult Invalid(string reason, CommandErrorCode code = CommandErrorCode.InvalidTarget) => ValidationResult.Failure(code, reason);
    }
}
