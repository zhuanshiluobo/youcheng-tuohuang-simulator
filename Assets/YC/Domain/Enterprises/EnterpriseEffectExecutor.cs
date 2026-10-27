using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Enterprises
{
    public static class EnterpriseEffectExecutor
    {
        public const string ChangeLevelTypeId = "effect.enterprise.change_level";
        public const string UpgradeAllPlayersTypeId = "effect.enterprise.upgrade_all_players";
        internal const string IncompleteTypeId = "effect.enterprise.incomplete";
        public const string LevelChangedEvent = "enterprise.level.changed", DepartmentChangedEvent = "enterprise.department.changed";

        public static void Register(EffectRegistry registry, EnterpriseDefinitions definitions = null)
        {
            if (definitions != null) registry.EnterpriseDefinitions = definitions;
            foreach (string typeId in new[] { LuaDomainEffectTypeIds.UpgradeEnterprise, ChangeLevelTypeId, LuaDomainEffectTypeIds.SwitchDepartment })
                if (!registry.TryGet(typeId, out _)) registry.Register(new EffectRegistration(typeId,
                    typeId == LuaDomainEffectTypeIds.SwitchDepartment ? (Func<EffectExecutionContext, EffectStepResult>)SwitchDepartment : typeId == LuaDomainEffectTypeIds.UpgradeEnterprise ? Upgrade : ChangeLevel, EffectExecutorKind.IntrinsicFlow));
            if (!registry.TryGet(UpgradeAllPlayersTypeId, out _))
                registry.Register(new EffectRegistration(UpgradeAllPlayersTypeId, UpgradeAllPlayers, EffectExecutorKind.IntrinsicFlow, "v1"));
            if (!registry.TryGet(IncompleteTypeId, out _)) registry.Register(IncompleteTypeId, context => SkipIncomplete(context, new[] { "abilityEffects" }));
            foreach (string eventType in new[] { LevelChangedEvent, DepartmentChangedEvent, "EnterpriseSpecialEffectActivated" })
            {
                string subscriptionId = "enterprise.resolve:" + eventType;
                if (!registry.HasEventHandler(subscriptionId)) registry.RegisterEventHandler(new EffectEventHandlerRegistration(subscriptionId, eventType, "", subscriptionId,
                    context => registry.EnterpriseDefinitions.ResolveAbility(Text(context.Event.Payload, "abilityId"), context), EffectHandlerRole.Primary));
            }
            EnterpriseSpecialSelectionExecutor.Register(registry);
            EnterpriseDistinctUpgradeFlow.Register(registry);
        }
        public static EffectSpec UpgradeSpec(int playerId, string enterpriseItemId, int levels) => EffectSpec.Create(LuaDomainEffectTypeIds.UpgradeEnterprise,
            Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(playerId.ToString(CultureInfo.InvariantCulture)), "enterpriseItemId", NormalizedValue.CreateString(enterpriseItemId), "levels", NormalizedValue.CreateInteger(levels)), playerId);
        internal static EffectSpec IncompleteSpec(int playerId, string abilityId) => new EffectSpec(IncompleteTypeId) { PlayerId = playerId, SourceId = abilityId };

        public static bool IsEnterpriseType(string typeId) => new[] { EnterpriseDistinctUpgradeFlow.TypeId, LuaDomainEffectTypeIds.UpgradeEnterprise, ChangeLevelTypeId, UpgradeAllPlayersTypeId, LuaDomainEffectTypeIds.SwitchDepartment,
            LuaDomainEffectTypeIds.ActivateEnterpriseSpecial, EnterpriseSpecialSelectionExecutor.UpgradeThenActivateTypeId, EnterpriseSpecialSelectionExecutor.ActivateAllTypeId }.Contains(typeId);
        public static string[] AllowedFields(string typeId)
        {
            if (typeId == EnterpriseDistinctUpgradeFlow.TypeId) return new[] { "apiVersion", "player", "count", "levels" };
            if (typeId == LuaDomainEffectTypeIds.UpgradeEnterprise) return new[] { "apiVersion", "player", "enterpriseItemId", "enterpriseItemIds", "levels" };
            if (typeId == UpgradeAllPlayersTypeId) return new[] { "apiVersion", "player", "enterpriseItemId", "enterpriseItemIds", "levels" };
            if (typeId == ChangeLevelTypeId) return new[] { "apiVersion", "player", "enterpriseItemId", "levelDelta", "resolveArrivalReward" };
            if (typeId == LuaDomainEffectTypeIds.SwitchDepartment) return new[] { "apiVersion", "player", "enterpriseItemId", "departmentItemId" };
            if (typeId == EnterpriseSpecialSelectionExecutor.UpgradeThenActivateTypeId) return new[] { "apiVersion", "player", "levels", "enterpriseItemIds" };
            if (typeId == EnterpriseSpecialSelectionExecutor.ActivateAllTypeId) return new[] { "apiVersion", "player", "unlockedByPlayer", "minLevel", "maxLevel", "enterpriseItemIds" };
            return new[] { "apiVersion", "player", "unlockedByPlayer", "minLevel", "maxLevel", "enterpriseItemIds", "specialId", "candidateIds" };
        }
        public static List<string> MissingFields(string typeId, NormalizedValue args)
        {
            if (Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Value(args, "apiVersion").IntegerValue != 1 || args.Properties.Any(entry => !AllowedFields(typeId).Concat(new[] { "kind", "effectTypeId" }).Contains(entry.Name))) return new List<string>();
            var required = new List<string> { "player" };
            if (typeId == EnterpriseDistinctUpgradeFlow.TypeId) required.AddRange(new[] { "count", "levels" });
            if ((typeId == LuaDomainEffectTypeIds.UpgradeEnterprise || typeId == UpgradeAllPlayersTypeId) && Value(args, "enterpriseItemIds") == null || typeId == ChangeLevelTypeId || typeId == LuaDomainEffectTypeIds.SwitchDepartment) required.Add("enterpriseItemId");
            if (typeId == LuaDomainEffectTypeIds.UpgradeEnterprise || typeId == UpgradeAllPlayersTypeId || typeId == EnterpriseSpecialSelectionExecutor.UpgradeThenActivateTypeId) required.Add("levels");
            if (typeId == ChangeLevelTypeId) required.AddRange(new[] { "levelDelta", "resolveArrivalReward" });
            if (typeId == LuaDomainEffectTypeIds.ActivateEnterpriseSpecial || typeId == EnterpriseSpecialSelectionExecutor.ActivateAllTypeId) required.Add("unlockedByPlayer");
            return required.Where(key => Value(args, key) == null).ToList();
        }
        public static string ValidateArguments(string typeId, NormalizedValue args)
        {
            if (args?.Kind != NormalizedValueKind.Object || args.Properties.Any(entry => !AllowedFields(typeId).Concat(new[] { "kind", "effectTypeId" }).Contains(entry.Name))) return "invalid_enterprise_arguments";
            if (Value(args, "apiVersion")?.Kind != NormalizedValueKind.Integer || Value(args, "apiVersion").IntegerValue != 1) return "enterprise_schema_unsupported";
            if ((typeId == LuaDomainEffectTypeIds.UpgradeEnterprise || typeId == UpgradeAllPlayersTypeId) && Value(args, "enterpriseItemId") != null && Value(args, "enterpriseItemIds") != null) return "enterprise_upgrade_target_ambiguous";
            if (typeId == EnterpriseDistinctUpgradeFlow.TypeId && (Value(args, "count")?.Kind != NormalizedValueKind.Integer || Number(args, "count") < 1 || Number(args, "count") > 16)) return "enterprise_distinct_count_invalid";
            foreach (var entry in args.Properties)
            {
                if (new[] { "player", "unlockedByPlayer" }.Contains(entry.Name) && (!int.TryParse(entry.Value.StringValue, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || entry.Value.Kind != NormalizedValueKind.String || id <= 0)) return "invalid_player";
                if (new[] { "levels", "levelDelta", "minLevel", "maxLevel" }.Contains(entry.Name) && (entry.Value.Kind != NormalizedValueKind.Integer || entry.Value.IntegerValue < int.MinValue || entry.Value.IntegerValue > int.MaxValue ||
                    (entry.Name == "levels" ? entry.Value.IntegerValue <= 0 : entry.Name == "levelDelta" ? entry.Value.IntegerValue == 0 : entry.Value.IntegerValue < 1 || entry.Value.IntegerValue > 5))) return "invalid_enterprise_level_argument";
                if (new[] { "enterpriseItemId", "departmentItemId", "specialId" }.Contains(entry.Name) && (entry.Value.Kind != NormalizedValueKind.String || string.IsNullOrWhiteSpace(entry.Value.StringValue))) return "invalid_enterprise_target";
                if (entry.Name == "resolveArrivalReward" && entry.Value.Kind != NormalizedValueKind.Boolean) return "invalid_enterprise_arguments";
                if ((entry.Name == "enterpriseItemIds" || entry.Name == "candidateIds") && (entry.Value.Kind != NormalizedValueKind.Array || entry.Value.Items.Any(value => value.Kind != NormalizedValueKind.String || string.IsNullOrWhiteSpace(value.StringValue)) || entry.Value.Items.Select(value => value.StringValue).Distinct().Count() != entry.Value.Items.Count)) return "invalid_enterprise_target";
            }
            if (Number(args, "minLevel", 1) > Number(args, "maxLevel", 5)) return "invalid_enterprise_level_range";
            if (typeId == UpgradeAllPlayersTypeId && Value(args, "levels") != null && Number(args, "levels") != 1) return "enterprise_all_players_levels_rejected";
            if (Value(args, "specialId") != null && Value(args, "enterpriseItemIds")?.Items.Count != 1) return "enterprise_special_binding_required";
            if (Value(args, "specialId") != null && !EnterpriseSpecialSelectionExecutor.IsInCandidateScope(args,
                StableIdFactory.Create("candidate", Value(args, "enterpriseItemIds").Items[0].StringValue, Text(args, "specialId")))) return "enterprise_special_not_in_scope";
            return "";
        }
        internal static EffectStepResult CheckArguments(EffectExecutionContext context)
        {
            var args = context.Node.NormalizedArguments;
            if (Value(args, "authorizationEffectId") != null)
            {
                if (!TryValidateUpgradeAuthorizations(context.State, out _) || !IsAuthorizedUpgradeChild(context.State, context.Node)) throw new InvalidOperationException("enterprise_execution_permission_denied");
                args = args.Clone(); args.Properties.RemoveAll(entry => entry.Name == "authorizationEffectId");
            }
            string error = ValidateArguments(context.Node.EffectTypeId, args);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            var missing = MissingFields(context.Node.EffectTypeId, args);
            if (missing.Count > 0) return SkipIncomplete(context, missing);
            if (int.Parse(Text(context.Node.NormalizedArguments, "player"), CultureInfo.InvariantCulture) != context.Node.PlayerId || context.State.FindPlayer(context.Node.PlayerId) == null)
                throw new InvalidOperationException("enterprise_execution_permission_denied");
            return null;
        }

        private static List<int> PlayerOrder(GameState state, int initiatingPlayer)
        {
            var order = state.EffectRuntime.PlayerOrderSnapshot.Count > 0
                ? state.EffectRuntime.PlayerOrderSnapshot.ToList() : state.Players.Select(player => player.PlayerId).ToList();
            if (order.Count < 2 || order.Count > 4 || order.Distinct().Count() != order.Count ||
                !order.OrderBy(id => id).SequenceEqual(state.Players.Select(player => player.PlayerId).OrderBy(id => id)) || !order.Contains(initiatingPlayer))
                throw new InvalidOperationException("enterprise_player_order_invalid");
            int first = order.IndexOf(initiatingPlayer);
            return Enumerable.Range(0, order.Count).Select(index => order[(first + index) % order.Count]).ToList();
        }
        private static List<int> AuthorizedPlayers(EffectNodeRuntimeState node) => Value(node.ContinuationData, "authorizedPlayerIds")?.Items
            .Select(value => (int)value.IntegerValue).ToList() ?? new List<int>();
        private static string UpgradeTarget(EffectNodeRuntimeState node) => Text(node.NormalizedArguments, "enterpriseItemId", Text(node.ContinuationData, "selectedEnterpriseItemId"));
        private static bool IsAuthorizedUpgradeChild(GameState state, EffectNodeRuntimeState child)
        {
            var parent = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == child.ParentEffectId);
            if (parent?.EffectTypeId != UpgradeAllPlayersTypeId || child.EffectTypeId != LuaDomainEffectTypeIds.UpgradeEnterprise ||
                Text(child.NormalizedArguments, "authorizationEffectId") != parent.EffectId || child.SourceId != parent.SourceId) return false;
            int index = parent.ChildEffectIds.IndexOf(child.EffectId);
            var targets = AuthorizedPlayers(parent);
            return Number(parent.ContinuationData, "initiatingPlayerId", -1) == parent.PlayerId && index >= 0 && index < targets.Count &&
                index < Number(parent.ContinuationData, "nextIndex", -1) && child.PlayerId == targets[index] &&
                Text(child.NormalizedArguments, "player") == targets[index].ToString(CultureInfo.InvariantCulture) && Number(child.NormalizedArguments, "levels") == 1 &&
                Text(child.NormalizedArguments, "enterpriseItemId") == UpgradeTarget(parent);
        }
        public static bool TryValidateUpgradeAuthorizations(GameState state, out string reason)
        {
            reason = "enterprise_upgrade_authorization_invalid";
            foreach (var parent in state.EffectRuntime.EffectNodes.Where(node => node.EffectTypeId == UpgradeAllPlayersTypeId))
            {
                if (Value(parent.NormalizedArguments, "enterpriseItemIds") != null && Value(parent.ContinuationData, "selectedEnterpriseItemId") == null)
                {
                    if (ValidateArguments(parent.EffectTypeId, parent.NormalizedArguments).Length > 0 || MissingFields(parent.EffectTypeId, parent.NormalizedArguments).Count > 0 ||
                        Text(parent.NormalizedArguments, "player") != parent.PlayerId.ToString(CultureInfo.InvariantCulture) || state.FindPlayer(parent.PlayerId) == null ||
                        !new[] { "", "choose_enterprise", "finished" }.Contains(parent.FlowStage) || parent.ChildEffectIds.Count > 0 ||
                        new[] { "initiatingPlayerId", "authorizedPlayerIds", "authorizedEnterpriseDefinitionId", "nextIndex", "completedCount" }.Any(key => Value(parent.ContinuationData, key) != null)) return false;
                    continue;
                }
                if (parent.FlowStage.Length == 0)
                {
                    if (parent.ChildEffectIds.Count > 0 || Value(parent.ContinuationData, "authorizedPlayerIds") != null) return false;
                    continue;
                }
                if (ValidateArguments(parent.EffectTypeId, parent.NormalizedArguments).Length > 0 || MissingFields(parent.EffectTypeId, parent.NormalizedArguments).Count > 0 ||
                    Text(parent.NormalizedArguments, "player") != parent.PlayerId.ToString(CultureInfo.InvariantCulture) || state.FindPlayer(parent.PlayerId) == null) return false;
                var ids = Value(parent.ContinuationData, "authorizedPlayerIds");
                var initiating = Value(parent.ContinuationData, "initiatingPlayerId");
                var next = Value(parent.ContinuationData, "nextIndex"); var completed = Value(parent.ContinuationData, "completedCount");
                if (ids?.Kind != NormalizedValueKind.Array || ids.Items.Any(value => value.Kind != NormalizedValueKind.Integer || value.IntegerValue < 1 || value.IntegerValue > int.MaxValue) ||
                    initiating?.Kind != NormalizedValueKind.Integer || initiating.IntegerValue != parent.PlayerId || next?.Kind != NormalizedValueKind.Integer || completed?.Kind != NormalizedValueKind.Integer ||
                    !new[] { "awaiting_player", "finished" }.Contains(parent.FlowStage)) return false;
                var targets = AuthorizedPlayers(parent);
                try { if (!targets.SequenceEqual(PlayerOrder(state, parent.PlayerId))) return false; }
                catch (InvalidOperationException) { return false; }
                int sent = Number(parent.ContinuationData, "nextIndex", -1), done = Number(parent.ContinuationData, "completedCount", -1);
                if (done < 0 || sent < done || sent > targets.Count || sent - done > 1 || parent.ChildEffectIds.Count != sent ||
                    parent.FlowStage == "finished" && (done != targets.Count || sent != done)) return false;
                var enterprise = state.ItemZones.Items.Find(item => item.ItemId == UpgradeTarget(parent));
                if (enterprise == null || enterprise.DefinitionId != Text(parent.ContinuationData, "authorizedEnterpriseDefinitionId")) return false;
                for (int index = 0; index < sent; index++)
                {
                    var child = state.EffectRuntime.EffectNodes.Find(node => node.EffectId == parent.ChildEffectIds[index]);
                    if (child == null || !IsAuthorizedUpgradeChild(state, child) || index < done && child.Status != EffectNodeStatus.Completed) return false;
                    var args = child.NormalizedArguments.Clone(); args.Properties.RemoveAll(entry => entry.Name == "authorizationEffectId");
                    if (ValidateArguments(child.EffectTypeId, args).Length > 0 || MissingFields(child.EffectTypeId, args).Count > 0) return false;
                    if (child.PendingOutcome == EffectPendingOutcome.Completed || child.Status == EffectNodeStatus.Completed)
                    {
                        var oldLevel = Value(child.NormalizedResult, "oldLevel"); var newLevel = Value(child.NormalizedResult, "newLevel");
                        var changed = Value(child.NormalizedResult, "changed"); var arrivals = Value(child.NormalizedResult, "arrivalEventIds");
                        // 到达响应可再次改变等级；仅核验来源结果与收据，不把最终等级锁死为旧等级+1。
                        if (child.FlowStage != "finished" || oldLevel?.Kind != NormalizedValueKind.Integer || oldLevel.IntegerValue < 0 || oldLevel.IntegerValue > 5 ||
                            oldLevel.IntegerValue != Number(child.ContinuationData, "oldLevel", -1) || newLevel?.Kind != NormalizedValueKind.Integer || newLevel.IntegerValue < 0 || newLevel.IntegerValue > 5 ||
                            changed?.Kind != NormalizedValueKind.Boolean || changed.BooleanValue != (child.EnterpriseArrivalReceipts.Count > 0) ||
                            arrivals?.Kind != NormalizedValueKind.Array || arrivals.Items.Any(value => value.Kind != NormalizedValueKind.String) ||
                            !arrivals.Items.Select(value => value.StringValue).SequenceEqual(child.EnterpriseArrivalReceipts.Select(receipt => receipt.EventId))) return false;
                    }
                }
                if (parent.FlowStage == "finished")
                {
                    var resultInitiator = Value(parent.NormalizedResult, "initiatingPlayerId");
                    var resultPlayers = Value(parent.NormalizedResult, "completedPlayerIds"); var changed = Value(parent.NormalizedResult, "changed");
                    bool anyChanged = parent.ChildEffectIds.Any(id => Flag(state.EffectRuntime.EffectNodes.Find(node => node.EffectId == id).NormalizedResult, "changed"));
                    if (resultInitiator?.Kind != NormalizedValueKind.Integer || resultInitiator.IntegerValue != parent.PlayerId ||
                        resultPlayers?.Kind != NormalizedValueKind.Array || resultPlayers.Items.Any(value => value.Kind != NormalizedValueKind.Integer || value.IntegerValue < 1 || value.IntegerValue > int.MaxValue) ||
                        !resultPlayers.Items.Select(value => (int)value.IntegerValue).SequenceEqual(targets) || changed?.Kind != NormalizedValueKind.Boolean || changed.BooleanValue != anyChanged) return false;
                }
            }
            foreach (var child in state.EffectRuntime.EffectNodes.Where(node => Value(node.NormalizedArguments, "authorizationEffectId") != null))
                if (!IsAuthorizedUpgradeChild(state, child)) return false;
            reason = ""; return true;
        }
        private static EffectStepResult UpgradeAllPlayers(EffectExecutionContext context)
        {
            var invalid = CheckArguments(context); if (invalid != null) return invalid;
            var selection = SelectUpgradeTarget(context, out string enterpriseId); if (selection != null) return selection;
            var node = context.Node;
            var query = new EnterpriseStateQuery(context.Registry.EnterpriseDefinitions);
            var definition = query.RequireDefinition(context.State, enterpriseId);
            if (node.FlowStage.Length == 0)
            {
                var targets = PlayerOrder(context.State, node.PlayerId);
                // 提交任何升级前复验所有合作轨道；授权集合只由Host创建。
                foreach (int player in targets) query.GetLevel(context.State, player, enterpriseId);
                Set(node, "initiatingPlayerId", NormalizedValue.CreateInteger(node.PlayerId));
                Set(node, "authorizedPlayerIds", NormalizedValue.CreateArray(targets.Select(player => NormalizedValue.CreateInteger(player)).ToList()));
                Set(node, "authorizedEnterpriseDefinitionId", NormalizedValue.CreateString(definition.DefinitionId));
                Set(node, "nextIndex", NormalizedValue.CreateInteger(0)); Set(node, "completedCount", NormalizedValue.CreateInteger(0));
            }
            else if (!TryValidateUpgradeAuthorizations(context.State, out var reason)) throw new InvalidOperationException(reason);
            var players = AuthorizedPlayers(node);
            int done = Number(node.ContinuationData, "completedCount"), sent = Number(node.ContinuationData, "nextIndex");
            if (sent > done)
            {
                var child = context.ChildNodes[done];
                if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted) return EffectStepResult.Failed("enterprise_player_upgrade_failed");
                if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待当前玩家升级与到达奖励完成。");
                done++; Set(node, "completedCount", NormalizedValue.CreateInteger(done));
            }
            if (done == players.Count)
            {
                var result = Object("changed", NormalizedValue.CreateBoolean(context.ChildNodes.Any(child => Flag(child.NormalizedResult, "changed"))),
                    "initiatingPlayerId", NormalizedValue.CreateInteger(node.PlayerId), "completedPlayerIds", NormalizedValue.CreateArray(players.Select(player => NormalizedValue.CreateInteger(player)).ToList()));
                if (Value(node.NormalizedArguments, "enterpriseItemIds") != null)
                {
                    result.Properties.Add(new NormalizedValueEntry { Name = "selectedEnterpriseItemId", Value = NormalizedValue.CreateString(enterpriseId) });
                    result.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                }
                return EffectStepResult.Completed(result).WithFlowStage("finished");
            }
            var spec = UpgradeSpec(players[done], enterpriseId, 1);
            spec.SourceId = node.SourceId; spec.StableKey = node.EffectId + ":player:" + done;
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "authorizationEffectId", Value = NormalizedValue.CreateString(node.EffectId) });
            spec.NormalizedArguments.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            Set(node, "nextIndex", NormalizedValue.CreateInteger(done + 1));
            return EffectStepResult.Continue("awaiting_player").AddChild(spec);
        }

        private static EffectStepResult Upgrade(EffectExecutionContext context)
        {
            if (Value(context.Node.NormalizedArguments, "enterpriseItemIds") == null) return ChangeLevel(context);
            var invalid = CheckArguments(context); if (invalid != null) return invalid;
            var selection = SelectUpgradeTarget(context, out _);
            return selection ?? ChangeLevel(context);
        }

        private static EffectStepResult SelectUpgradeTarget(EffectExecutionContext context, out string target)
        {
            var node = context.Node;
            target = UpgradeTarget(node);
            if (Value(node.NormalizedArguments, "enterpriseItemIds") == null || Value(node.ContinuationData, "selectedEnterpriseItemId") != null) return null;
            var query = new EnterpriseStateQuery(context.Registry.EnterpriseDefinitions);
            if (node.FlowStage.Length == 0)
            {
                var scope = Value(node.NormalizedArguments, "enterpriseItemIds").Items.Select(value => value.StringValue).ToList();
                var active = query.GetEnterpriseItemIds(context.State);
                if (scope.Any(id => !active.Contains(id))) return EffectStepResult.Failed("enterprise_not_active");
                var frozen = active.Where(scope.Contains).ToList();
                foreach (string id in frozen) query.GetLevel(context.State, node.PlayerId, id);
                Set(node, "upgradeCandidateItemIds", Strings(frozen));
                Set(node, "upgradeCandidateDefinitionIds", Strings(frozen.Select(id => context.State.ItemZones.Items.Single(item => item.ItemId == id).DefinitionId)));
            }
            else if (node.FlowStage != "choose_enterprise") throw new InvalidOperationException("enterprise_upgrade_selection_stage_invalid");
            var ids = Value(node.ContinuationData, "upgradeCandidateItemIds").Items.Select(value => value.StringValue).ToList();
            var definitions = Value(node.ContinuationData, "upgradeCandidateDefinitionIds").Items.Select(value => value.StringValue).ToList();
            var candidates = query.GetEnterpriseItemIds(context.State).Where(id => ids.Contains(id) &&
                context.State.ItemZones.Items.Single(item => item.ItemId == id).DefinitionId == definitions[ids.IndexOf(id)]).ToList();
            foreach (string id in candidates) query.GetLevel(context.State, node.PlayerId, id);
            string selected = node.FlowStage == "choose_enterprise" ? Answer(context) : string.Empty;
            if (candidates.Count == 0) return NoCandidates().WithFlowStage("finished");
            if (!candidates.Contains(selected))
            {
                if (candidates.Count == 1) selected = candidates[0];
                else return EffectStepResult.Continue("choose_enterprise").AddInteraction(PrepareInteraction(context,
                    new EffectInteractionSpec { InteractionTypeId = "enterprise.upgrade", AnsweringPlayerId = node.PlayerId,
                        Visibility = "owner", PromptKey = "enterprise.upgrade.choose", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 }.WithCandidates(candidates)));
            }
            Set(node, "selectedEnterpriseItemId", NormalizedValue.CreateString(selected));
            target = selected;
            node.FlowStage = string.Empty;
            return null;
        }

        public static bool TryValidateUpgradeSelections(GameState state, EnterpriseDefinitions definitions, out string reason)
        {
            if (!EnterpriseSpecialSelectionExecutor.TryValidateCandidateScopes(state, out reason)) return false;
            reason = "enterprise_upgrade_selection_restore_invalid";
            foreach (var node in state.EffectRuntime.EffectNodes.Where(value => (value.EffectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise || value.EffectTypeId == UpgradeAllPlayersTypeId) && Value(value.NormalizedArguments, "enterpriseItemIds") != null))
            {
                if (ValidateArguments(node.EffectTypeId, node.NormalizedArguments).Length > 0 || MissingFields(node.EffectTypeId, node.NormalizedArguments).Count > 0 ||
                    Text(node.NormalizedArguments, "player") != node.PlayerId.ToString(CultureInfo.InvariantCulture) || state.FindPlayer(node.PlayerId) == null) return false;
                var ids = Value(node.ContinuationData, "upgradeCandidateItemIds");
                var definitionIds = Value(node.ContinuationData, "upgradeCandidateDefinitionIds");
                var selected = Value(node.ContinuationData, "selectedEnterpriseItemId");
                if (node.FlowStage.Length == 0 && ids == null && definitionIds == null && selected == null && node.EnterpriseArrivalReceipts.Count == 0) continue;
                if (ids?.Kind != NormalizedValueKind.Array || ids.Items.Any(value => value.Kind != NormalizedValueKind.String || string.IsNullOrWhiteSpace(value.StringValue)) ||
                    ids.Items.Select(value => value.StringValue).Distinct(StringComparer.Ordinal).Count() != ids.Items.Count || ids.Items.Count != Value(node.NormalizedArguments, "enterpriseItemIds").Items.Count ||
                    ids.Items.Any(value => !Value(node.NormalizedArguments, "enterpriseItemIds").Items.Any(input => input.StringValue == value.StringValue)) ||
                    definitionIds?.Kind != NormalizedValueKind.Array || definitionIds.Items.Count != ids.Items.Count ||
                    definitionIds.Items.Any(value => value.Kind != NormalizedValueKind.String || string.IsNullOrWhiteSpace(value.StringValue) || definitions != null && definitions.FindEnterprise(value.StringValue) == null)) return false;
                var frozen = ids.Items.Select(value => value.StringValue).ToList();
                foreach (var request in state.EffectRuntime.InteractionRequests.Where(request => request.OwnerEffectId == node.EffectId))
                    if (request.InteractionTypeId != "enterprise.upgrade" || request.AnsweringPlayerId != node.PlayerId || request.PromptKey != "enterprise.upgrade.choose" ||
                        request.CandidateIds.Any(id => !frozen.Contains(id)) || request.MinSelections != 1 || request.MaxSelections != 1 || request.AllowDecline) return false;
                if (selected == null)
                {
                    if (node.EnterpriseArrivalReceipts.Count != 0 || node.ChildEffectIds.Count != 0 || Value(node.ContinuationData, "oldLevel") != null || Value(node.ContinuationData, "nextIndex") != null) return false;
                    if (node.FlowStage == "choose_enterprise")
                    {
                        if (frozen.Count < 2 || !state.EffectRuntime.InteractionRequests.Any(request => request.OwnerEffectId == node.EffectId && (request.Status == "open" || request.Status == "answered"))) return false;
                    }
                    else if (node.FlowStage != "finished" || Flag(node.NormalizedResult, "changed") || Text(node.NormalizedResult, "reasonCode") != "no_candidates") return false;
                    continue;
                }
                var stages = node.EffectTypeId == UpgradeAllPlayersTypeId ? new[] { "awaiting_player", "finished" } : new[] { "awaiting_arrival", "finished" };
                if (selected.Kind != NormalizedValueKind.String || !frozen.Contains(selected.StringValue) || !stages.Contains(node.FlowStage)) return false;
                var item = state.ItemZones.Items.Find(item => item.ItemId == selected.StringValue);
                if (item != null && item.DefinitionId != definitionIds.Items[frozen.IndexOf(selected.StringValue)].StringValue) return false;
                if (node.FlowStage != "finished")
                {
                    try
                    {
                        if (item == null || state.ItemZones.Zones.Find(zone => zone.ZoneId == item.ZoneId)?.TemplateId != "core.zone.enterprise_display") return false;
                        if (definitions != null) new EnterpriseStateQuery(definitions).RequireDefinition(state, selected.StringValue);
                        new EnterpriseStateQuery(definitions).GetLevel(state, node.PlayerId, selected.StringValue);
                    }
                    catch (InvalidOperationException) { return false; }
                }
                if (node.EffectTypeId == UpgradeAllPlayersTypeId)
                {
                    if (node.EnterpriseArrivalReceipts.Count != 0 || node.FlowStage == "finished" && Text(node.NormalizedResult, "selectedEnterpriseItemId") != selected.StringValue) return false;
                    continue;
                }
                var oldLevel = Value(node.ContinuationData, "oldLevel"); var next = Value(node.ContinuationData, "nextIndex"); var count = Value(node.ContinuationData, "requestedCount");
                if (oldLevel?.Kind != NormalizedValueKind.Integer || oldLevel.IntegerValue < 0 || oldLevel.IntegerValue > 5 ||
                    next?.Kind != NormalizedValueKind.Integer || next.IntegerValue < 0 || next.IntegerValue > Math.Min(5, Number(node.NormalizedArguments, "levels")) ||
                    count?.Kind != NormalizedValueKind.Integer || count.IntegerValue != Math.Min(5, Number(node.NormalizedArguments, "levels")) || node.EnterpriseArrivalReceipts.Count != next.IntegerValue) return false;
                for (int index = 0; index < node.EnterpriseArrivalReceipts.Count; index++)
                {
                    var receipt = node.EnterpriseArrivalReceipts[index];
                    if (receipt.EnterpriseItemId != selected.StringValue || receipt.PlayerId != node.PlayerId || receipt.NewLevel != receipt.OldLevel + 1 ||
                        receipt.EventId != StableIdFactory.Create("event", node.EffectId, "arrival", index.ToString(CultureInfo.InvariantCulture)) || index == 0 && receipt.OldLevel != oldLevel.IntegerValue) return false;
                    var ruleEvent = state.EffectRuntime.RuleEvents.Find(value => value.EventId == receipt.EventId);
                    if (ruleEvent == null || ruleEvent.SourceEffectId != node.EffectId || ruleEvent.EventType != LevelChangedEvent || ruleEvent.PlayerId != node.PlayerId ||
                        Text(ruleEvent.Payload, "enterpriseItemId") != selected.StringValue || Number(ruleEvent.Payload, "oldLevel", -1) != receipt.OldLevel || Number(ruleEvent.Payload, "newLevel", -1) != receipt.NewLevel) return false;
                }
                if (node.FlowStage == "finished" && (node.PendingOutcome == EffectPendingOutcome.Completed || node.Status == EffectNodeStatus.Completed))
                {
                    var arrivals = Value(node.NormalizedResult, "arrivalEventIds"); var resultLevel = Value(node.NormalizedResult, "newLevel");
                    if (Text(node.NormalizedResult, "selectedEnterpriseItemId") != selected.StringValue || Number(node.NormalizedResult, "oldLevel", -1) != oldLevel.IntegerValue ||
                        resultLevel?.Kind != NormalizedValueKind.Integer || resultLevel.IntegerValue < 0 || resultLevel.IntegerValue > 5 ||
                        Value(node.NormalizedResult, "changed")?.Kind != NormalizedValueKind.Boolean || Flag(node.NormalizedResult, "changed") != (node.EnterpriseArrivalReceipts.Count > 0) ||
                        arrivals?.Kind != NormalizedValueKind.Array || arrivals.Items.Any(value => value.Kind != NormalizedValueKind.String) ||
                        !arrivals.Items.Select(value => value.StringValue).SequenceEqual(node.EnterpriseArrivalReceipts.Select(receipt => receipt.EventId))) return false;
                }
            }
            reason = string.Empty; return true;
        }
        private static EffectStepResult ChangeLevel(EffectExecutionContext context)
        {
            var invalid = CheckArguments(context); if (invalid != null) return invalid;
            var node = context.Node; var query = new EnterpriseStateQuery(context.Registry.EnterpriseDefinitions);
            string enterpriseItemId = Text(node.NormalizedArguments, "enterpriseItemId", Text(node.ContinuationData, "selectedEnterpriseItemId"));
            query.RequireDefinition(context.State, enterpriseItemId);
            int levelDelta = node.EffectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise ? Number(node.NormalizedArguments, "levels") : Number(node.NormalizedArguments, "levelDelta");
            bool resolveArrivalReward = node.EffectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise || Flag(node.NormalizedArguments, "resolveArrivalReward");
            if (node.FlowStage.Length == 0)
            {
                Set(node, "oldLevel", NormalizedValue.CreateInteger(query.GetLevel(context.State, node.PlayerId, enterpriseItemId)));
                Set(node, "requestedCount", NormalizedValue.CreateInteger(Math.Min(5L, Math.Abs((long)levelDelta))));
                Set(node, "nextIndex", NormalizedValue.CreateInteger(0));
            }
            else if (node.FlowStage != "awaiting_arrival" && node.FlowStage != "finished") throw new InvalidOperationException("enterprise_level_stage_invalid");
            int index = Number(node.ContinuationData, "nextIndex", -1), requested = Number(node.ContinuationData, "requestedCount", -1);
            if (index < 0 || index > requested || requested < 1 || requested > 5 || node.EnterpriseArrivalReceipts.Count != index) throw new InvalidOperationException("enterprise_arrival_state_invalid");
            int oldLevel = query.GetLevel(context.State, node.PlayerId, enterpriseItemId);
            int newLevel = Math.Max(0, Math.Min(5, oldLevel + Math.Sign(levelDelta)));
            if (index == requested || oldLevel == newLevel || node.FlowStage == "finished") return FinishLevel(context, enterpriseItemId);
            string reward = query.GetRewardAbilityId(context.State, enterpriseItemId, newLevel, out var departmentItemId);
            if (!resolveArrivalReward) reward = "";
            var marker = query.GetCooperationMarker(context.State, node.PlayerId, enterpriseItemId);
            var transfer = new ItemTransferRequest { ExecutingPlayerId = node.PlayerId, ItemIds = new List<string> { marker.ItemId }, ExpectedSource = new ItemPositionRef { ZoneId = marker.ZoneId, SlotId = marker.SlotId },
                Destination = new ZoneRef { ZoneId = marker.ZoneId }, InsertionKind = "slot", DestinationSlotId = "level_" + newLevel, OperationProfileId = "core.player_marker_storage", RequiredCount = 1 };
            new ItemTransferService().RelocateItems(context, transfer);
            var receipt = new EnterpriseArrivalReceipt { EventId = StableIdFactory.Create("event", node.EffectId, "arrival", index.ToString(CultureInfo.InvariantCulture)), EnterpriseItemId = enterpriseItemId,
                DepartmentItemId = departmentItemId, RewardAbilityId = reward, PlayerId = node.PlayerId, OldLevel = oldLevel, NewLevel = newLevel };
            node.EnterpriseArrivalReceipts.Add(receipt);
            Set(node, "nextIndex", NormalizedValue.CreateInteger(index + 1));
            return EffectStepResult.Continue("awaiting_arrival").AddEvent(new EffectEventRequest { EventId = receipt.EventId, EventType = LevelChangedEvent, SourceEffectId = node.EffectId,
                TargetEntityId = enterpriseItemId, PlayerId = node.PlayerId, Payload = Object("enterpriseItemId", NormalizedValue.CreateString(enterpriseItemId), "oldLevel", NormalizedValue.CreateInteger(oldLevel),
                    "newLevel", NormalizedValue.CreateInteger(newLevel), "departmentItemId", NormalizedValue.CreateString(departmentItemId), "abilityId", NormalizedValue.CreateString(reward)) });
        }
        private static EffectStepResult FinishLevel(EffectExecutionContext context, string enterpriseItemId)
        {
            var node = context.Node; int newLevel = new EnterpriseStateQuery(context.Registry.EnterpriseDefinitions).GetLevel(context.State, node.PlayerId, enterpriseItemId);
            var result = Object("oldLevel", Value(node.ContinuationData, "oldLevel").Clone(), "newLevel", NormalizedValue.CreateInteger(newLevel), "changed", NormalizedValue.CreateBoolean(node.EnterpriseArrivalReceipts.Count > 0),
                "arrivalEventIds", Strings(node.EnterpriseArrivalReceipts.Select(receipt => receipt.EventId)));
            if (Value(node.NormalizedArguments, "enterpriseItemIds") != null) result.Properties.Add(new NormalizedValueEntry { Name = "selectedEnterpriseItemId", Value = NormalizedValue.CreateString(enterpriseItemId) });
            if (node.EnterpriseArrivalReceipts.Count == 0) result.Properties.Add(new NormalizedValueEntry { Name = "reasonCode", Value = NormalizedValue.CreateString("limit_reached") });
            result.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return EffectStepResult.Completed(result).WithFlowStage("finished");
        }
        private static EffectStepResult SwitchDepartment(EffectExecutionContext context)
        {
            var invalid = CheckArguments(context); if (invalid != null) return invalid;
            var node = context.Node;
            if (node.FlowStage == "awaiting_department_reward") return EffectStepResult.Completed(node.NormalizedResult).WithFlowStage("finished");
            if (node.FlowStage != "" && node.FlowStage != "choose_department") throw new InvalidOperationException("enterprise_department_stage_invalid");
            var query = new EnterpriseStateQuery(context.Registry.EnterpriseDefinitions); string enterpriseItemId = Text(node.NormalizedArguments, "enterpriseItemId");
            var candidates = query.GetDepartmentCandidates(context.State, enterpriseItemId);
            string departmentItemId = Text(node.NormalizedArguments, "departmentItemId");
            if (node.FlowStage == "choose_department") departmentItemId = Answer(context);
            if (departmentItemId.Length == 0)
            {
                if (candidates.Count == 0) return NoCandidates();
                return EffectStepResult.Continue("choose_department").AddInteraction(PrepareInteraction(context, new EffectInteractionSpec { InteractionTypeId = "enterprise.department", AnsweringPlayerId = node.PlayerId,
                    Visibility = "owner", PromptKey = "enterprise.department.choose", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { } }.WithCandidates(candidates)));
            }
            if (!candidates.Contains(departmentItemId))
            {
                if (node.FlowStage != "choose_department") return EffectStepResult.Failed("enterprise_department_invalid");
                node.FlowStage = ""; return SwitchDepartment(context);
            }
            string previous = query.GetActiveDepartmentItemId(context.State, enterpriseItemId);
            int level = query.GetLevel(context.State, node.PlayerId, enterpriseItemId);
            var relation = context.State.ItemZones.Relations.Single(value => value.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation && value.SourceItemId == enterpriseItemId);
            relation.TargetItemId = departmentItemId;
            string abilityId = query.GetRewardAbilityId(context.State, enterpriseItemId, level, out _);
            Set(node, "departmentItemId", NormalizedValue.CreateString(departmentItemId)); Set(node, "previousDepartmentItemId", NormalizedValue.CreateString(previous));
            node.NormalizedResult = Object("changed", NormalizedValue.CreateBoolean(true), "enterpriseItemId", NormalizedValue.CreateString(enterpriseItemId), "departmentItemId", NormalizedValue.CreateString(departmentItemId));
            return EffectStepResult.Continue("awaiting_department_reward").AddEvent(new EffectEventRequest { EventId = StableIdFactory.Create("event", node.EffectId, "department_changed"), EventType = DepartmentChangedEvent,
                SourceEffectId = node.EffectId, PlayerId = node.PlayerId, TargetEntityId = enterpriseItemId, Payload = Object("enterpriseItemId", NormalizedValue.CreateString(enterpriseItemId), "previousDepartmentItemId", NormalizedValue.CreateString(previous),
                    "departmentItemId", NormalizedValue.CreateString(departmentItemId), "level", NormalizedValue.CreateInteger(level), "abilityId", NormalizedValue.CreateString(abilityId)) });
        }
        internal static EffectStepResult NoCandidates() => EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(false), "reasonCode", NormalizedValue.CreateString("no_candidates")));
        internal static EffectInteractionSpec WithCandidates(this EffectInteractionSpec interaction, IEnumerable<string> candidates) { interaction.CandidateIds.AddRange(candidates); return interaction; }
    }
}
