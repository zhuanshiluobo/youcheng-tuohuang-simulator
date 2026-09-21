using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class CityStyleSpecialActionEffectTypeIds
    {
        public const string Activate = "effect.city_style.special_action.activate";
        public const string ResolveContent = "effect.city_style.special_action.content";
        public const string ScriptStep = "effect.city_style.special_action.step";
        public const string OperatePlayerMarker = "effect.player.marker.operate";
        public const string GrantMainActions = "effect.player.main_action.grant";
    }

    public static class CityStyleSpecialActionEventTypeIds
    {
        public const string Activated = "CityStyleSpecialActionActivated";
        public const string CleanupStarted = "PlayerCleanupStarted";
    }

    public static class CityStyleCandidateKinds
    {
        public const string None = "city_style.special_action.none";
        public const string InfluenceSlots = "city_style.special_action.influence_slots";
        public const string OpponentInfluences = "city_style.special_action.opponent_influences";
        public const string MoveLocations = "city_style.special_action.move_locations";
    }

    public static class CityStyleSpecialActionEffectSpecFactory
    {
        public static EffectSpec Activate(
            int playerId,
            string specialActionId,
            string declarationMarkerId,
            int originiumAmount = -1,
            int ironAmount = -1,
            string sourceId = "")
        {
            return new EffectSpec(
                CityStyleSpecialActionEffectTypeIds.Activate,
                Object(
                    Entry("specialActionId", NormalizedValue.CreateString(specialActionId ?? string.Empty)),
                    Entry("declarationMarkerId", NormalizedValue.CreateString(declarationMarkerId ?? string.Empty)),
                    Entry("originiumAmount", NormalizedValue.CreateInteger(originiumAmount)),
                    Entry("ironAmount", NormalizedValue.CreateInteger(ironAmount))))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = CityStyleSpecialActionEffectExecutor.DefinitionVersion
            };
        }

        public static EffectSpec Cleanup(int playerId, string sourceId = "")
        {
            return new EffectSpec(
                CityStyleSpecialActionEffectTypeIds.OperatePlayerMarker,
                Object(
                    Entry("mode", NormalizedValue.CreateString("round_cleanup")),
                    Entry("playerId", NormalizedValue.CreateStableReference("player", playerId.ToString(CultureInfo.InvariantCulture)))))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = CityStyleSpecialActionEffectExecutor.DefinitionVersion
            };
        }

        private static NormalizedValue Object(params NormalizedValueEntry[] entries)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>(entries));
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }
    }

    /// <summary>
    /// 特殊行动的宿主流程。它只认识行为族和稳定数据，不按内容 ID 决定效果；具体效果由
    /// CityStyleSpecialActionActivated 的 Lua 处理器返回 ScriptStep/通用 Effect。
    /// </summary>
    public sealed class CityStyleSpecialActionEffectExecutor
    {
        public const string DefinitionVersion = "1.1.0";

        private readonly SpecialActionOptionQueryService optionQuery;
        private readonly CandidateSetResolver candidateResolver;
        private readonly CandidatePolicyRegistry candidatePolicies;
        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly CityMovementService movementService;

        public CityStyleSpecialActionEffectExecutor(
            SpecialActionOptionQueryService optionQuery,
            CandidatePolicyRegistry candidatePolicies = null,
            IMapQueryService mapQuery = null,
            InfluenceService influenceService = null,
            CityMovementService movementService = null)
        {
            this.optionQuery = optionQuery ?? throw new ArgumentNullException(nameof(optionQuery));
            this.candidatePolicies = candidatePolicies ?? new CandidatePolicyRegistry();
            candidateResolver = new CandidateSetResolver();
            this.mapQuery = mapQuery;
            this.influenceService = influenceService;
            this.movementService = movementService;
        }

        public static void Register(
            EffectRegistry registry,
            SpecialActionOptionQueryService optionQuery,
            CandidatePolicyRegistry candidatePolicies = null,
            IMapQueryService mapQuery = null,
            InfluenceService influenceService = null,
            CityMovementService movementService = null)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            var executor = new CityStyleSpecialActionEffectExecutor(
                optionQuery, candidatePolicies, mapQuery, influenceService, movementService);
            RegisterIfMissing(registry, CityStyleSpecialActionEffectTypeIds.Activate,
                executor.ExecuteActivate, EffectExecutorKind.IntrinsicFlow);
            ResourcePaymentChoiceEffectExecutor.Register(registry);
            RegisterIfMissing(registry, CityStyleSpecialActionEffectTypeIds.ResolveContent,
                executor.ExecuteContent, EffectExecutorKind.IntrinsicFlow);
            RegisterIfMissing(registry, CityStyleSpecialActionEffectTypeIds.ScriptStep,
                executor.ExecuteScriptStep, EffectExecutorKind.IntrinsicFlow);
            RegisterIfMissing(registry, CityStyleSpecialActionEffectTypeIds.OperatePlayerMarker,
                ExecuteMarkerOperation, EffectExecutorKind.Atomic);
            RegisterIfMissing(registry, CityStyleSpecialActionEffectTypeIds.GrantMainActions,
                ExecuteGrantMainActions, EffectExecutorKind.Atomic);
        }

        public static void RegisterCleanup(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            RegisterIfMissing(registry, CityStyleSpecialActionEffectTypeIds.OperatePlayerMarker,
                ExecuteMarkerOperation, EffectExecutorKind.Atomic);
        }

        private static void RegisterIfMissing(
            EffectRegistry registry,
            string typeId,
            Func<EffectExecutionContext, EffectStepResult> executor,
            EffectExecutorKind kind)
        {
            EffectRegistration ignored;
            if (registry.TryGet(typeId, out ignored)) return;
            registry.Register(new EffectRegistration(typeId, executor, kind, DefinitionVersion)
            {
                Validator = ValidateObjectSpec
            });
        }

        private EffectStepResult ExecuteActivate(EffectExecutionContext context)
        {
            if (context.Node.PendingOutcome != EffectPendingOutcome.None)
            {
                return EffectStepResult.Completed(context.Node.NormalizedResult == null
                    ? NormalizedValue.CreateNull() : context.Node.NormalizedResult.Clone());
            }

            string stage = context.Node.FlowStage ?? string.Empty;
            string actionId = ReadString(context.Node.NormalizedArguments, "specialActionId", string.Empty);
            string markerId = ReadString(context.Node.NormalizedArguments, "declarationMarkerId", string.Empty);
            if (string.IsNullOrEmpty(actionId) || string.IsNullOrEmpty(markerId))
                return EffectStepResult.Failed("invalid_arguments");

            SpecialActionDefinition definition = SpecialActionDatabase.Get(actionId);
            PlayerState player = context.State.FindPlayer(context.Node.PlayerId);
            if (definition == null || player == null)
                return EffectStepResult.Failed("invalid_source");

            if (string.IsNullOrEmpty(stage))
            {
                ValidationResult validation = ValidateActivation(context.State, player, definition, markerId);
                if (!validation.IsValid)
                    return EffectStepResult.Failed(validation.ErrorCode.ToString(), NormalizedValue.CreateString(validation.Reason));

                var left = new List<EffectSpec>();
                if (definition.FlexibleOriginiumAndIronCost > 0)
                {
                    var options = new List<ResourceSet>();
                    for (int ore = 0; ore <= definition.FlexibleOriginiumAndIronCost; ore++)
                    {
                        var cost = definition.FixedCost == null ? new ResourceSet() : definition.FixedCost.Clone();
                        cost.Originium += ore;
                        cost.Iron += definition.FlexibleOriginiumAndIronCost - ore;
                        options.Add(cost);
                    }
                    left.Add(ResourcePaymentChoiceEffectExecutor.Create(context.Node.PlayerId, options, "city_style.composite.payment"));
                }
                else if (definition.FixedCost != null)
                {
                    foreach (var item in definition.FixedCost.Enumerate())
                        if (item.Value > 0) left.Add(ResourceEffectSpecFactory.Pay(context.Node.PlayerId, item.Key, item.Value, actionId));
                }
                var content = EffectSpec.Create(CityStyleSpecialActionEffectTypeIds.ResolveContent,
                    context.Node.NormalizedArguments.Clone(), context.Node.PlayerId);
                var condition = EffectSpec.Condition(left, new List<EffectSpec> { content });
                condition.PlayerId = context.Node.PlayerId;
                return EffectStepResult.Continue("activated").AddChild(condition);
            }
            if (stage != "activated") return EffectStepResult.Failed("invalid_flow_stage");
            if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("特殊行动子 Effect 尚未结束。");
            if (context.ChildNodes.Count == 0 || ReadString(context.ChildNodes[0].NormalizedResult, "outcome", "") != "condition_met")
                return EffectStepResult.Failed("special_action_condition_not_met");
            new MainActionBudgetService().SpendCompletedMainAction(context.State, context.Node.PlayerId);
            return EffectStepResult.Completed(NormalizedValue.CreateString("completed"));
        }

        private EffectStepResult ExecuteContent(EffectExecutionContext context)
        {
            string actionId = ReadString(context.Node.NormalizedArguments, "specialActionId", "");
            string markerId = ReadString(context.Node.NormalizedArguments, "declarationMarkerId", "");
            var definition = SpecialActionDatabase.Get(actionId);
            var player = context.State.FindPlayer(context.Node.PlayerId);
            if (definition == null || player == null) return EffectStepResult.Failed("invalid_source");
            if (string.IsNullOrEmpty(context.Node.FlowStage))
            {
                CandidateSetDraft draft = BuildCandidateDraft(context, definition);
                CandidateResolutionResult resolution = candidateResolver.Resolve(
                    draft, candidatePolicies, context.State.EffectRuntime.StateRevision);
                if (!resolution.Succeeded)
                    return EffectStepResult.Failed(resolution.FaultCode, NormalizedValue.CreateString(resolution.Diagnostic));

                if (context.State.EffectRuntime.CandidateResolutions == null)
                    context.State.EffectRuntime.CandidateResolutions = new List<CandidateResolutionRecord>();
                context.State.EffectRuntime.CandidateResolutions.Add(resolution.Record.Clone());
                MarkActivated(player, definition, markerId);
                if (player.UsedSpecialActionIdsThisRound == null)
                    player.UsedSpecialActionIdsThisRound = new List<string>();
                if (!player.UsedSpecialActionIdsThisRound.Contains(definition.SpecialActionId))
                    player.UsedSpecialActionIdsThisRound.Add(definition.SpecialActionId);

                if (definition.LocksCharacterCard) new MainActionBudgetService().LockCharacterCard(context.State, context.Node.PlayerId);
                var step = EffectStepResult.Continue("activated");
                step.AddEvent(new EffectEventRequest
                {
                    EventId = StableIdFactory.Create("event", context.Node.EffectId, CityStyleSpecialActionEventTypeIds.Activated, actionId),
                    EventType = CityStyleSpecialActionEventTypeIds.Activated,
                    SourceEffectId = context.Node.EffectId,
                    OwnerNodeId = context.Node.EffectId,
                    RouteKey = actionId,
                    TargetEntityId = actionId,
                    PlayerId = context.Node.PlayerId,
                    ResponseKind = RuleEventResponseKind.Effects,
                    DefinitionVersion = DefinitionVersion,
                    Visibility = "owner",
                    SemanticKey = "activated",
                    Payload = CreateActivationPayload(context.State, context.Node, definition, draft, resolution.Record, markerId)
                });
                return step;
            }

            if (context.Node.FlowStage != "activated") return EffectStepResult.Failed("invalid_flow_stage");
            if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("特殊行动子 Effect 尚未结束。");
            if (HasFailedChild(context)) return EffectStepResult.Failed("special_action_child_failed");
            return EffectStepResult.Completed(NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "outcome", Value = NormalizedValue.CreateString("completed") },
                new NormalizedValueEntry { Name = "specialActionId", Value = NormalizedValue.CreateString(actionId) }
            }));
        }

        private EffectStepResult ExecuteScriptStep(EffectExecutionContext context)
        {
            throw new KernelException(EffectFaultCodes.DefinitionVersionMismatch, "城市样式旧步骤适配已撤下，请使用通用 Effect。");
        }

        private static EffectStepResult ExecuteMarkerOperation(EffectExecutionContext context)
        {
            // 旧的 C# 工厂使用 mode；Lua 内容契约使用 operation。两者都归一到
            // 同一行为族，避免 Event handler 产出的正式 Lua Effect 被误判为无效操作。
            string mode = ReadString(
                context.Node.NormalizedArguments,
                "mode",
                ReadString(context.Node.NormalizedArguments, "operation", string.Empty));
            if (mode != "round_cleanup") return EffectStepResult.Failed("invalid_marker_operation");
            PlayerState player = context.State.FindPlayer(context.Node.PlayerId);
            if (player == null) return EffectStepResult.Failed("invalid_player");
            if (player.UsedSpecialActionIdsThisRound != null) player.UsedSpecialActionIdsThisRound.Clear();
            if (player.DeclaredCityStyles != null)
            {
                for (int i = 0; i < player.DeclaredCityStyles.Count; i++)
                {
                    CityStyleDeclarationState marker = player.DeclaredCityStyles[i];
                    if (marker == null) continue;
                    SpecialActionDefinition definition = string.IsNullOrEmpty(marker.UnlockedSpecialActionId)
                        ? null : SpecialActionDatabase.Get(marker.UnlockedSpecialActionId);
                    if (definition == null)
                    {
                        if (marker.MarkerArea == CityStyleMarkerAreas.Used) marker.MarkerArea = CityStyleMarkerAreas.Declared;
                        continue;
                    }
                    if (definition.Level < 2 && marker.MarkerArea == CityStyleMarkerAreas.Used)
                    {
                        marker.MarkerArea = CityStyleMarkerAreas.Unused;
                        marker.RemainingSpecialActionUses = 1;
                    }
                    else if (definition.Level >= 2 &&
                             (marker.MarkerArea == SpecialActionMarkerAreas.UsedFromTwo ||
                              (marker.MarkerArea == CityStyleMarkerAreas.Used && marker.RemainingSpecialActionUses >= 2)))
                    {
                        marker.MarkerArea = CityStyleMarkerAreas.UsesOne;
                        marker.RemainingSpecialActionUses = 1;
                    }
                    else if (definition.Level >= 2 &&
                             (marker.MarkerArea == SpecialActionMarkerAreas.UsedFromOne ||
                              (marker.MarkerArea == CityStyleMarkerAreas.Used && marker.RemainingSpecialActionUses == 1)))
                    {
                        marker.MarkerArea = CityStyleMarkerAreas.UsesZero;
                        marker.RemainingSpecialActionUses = 0;
                    }
                }
            }
            return EffectStepResult.Completed(NormalizedValue.CreateString("cleanup_completed"));
        }

        private static EffectStepResult ExecuteGrantMainActions(EffectExecutionContext context)
        {
            int amount = ReadInt(context.Node.NormalizedArguments, "amount", 0);
            if (amount <= 0) return EffectStepResult.Failed("invalid_amount");
            new MainActionBudgetService().GrantAdditionalMainActions(context.State, context.Node.PlayerId, amount);
            if (ReadBool(context.Node.NormalizedArguments, "lockCharacterCard", true))
                new MainActionBudgetService().LockCharacterCard(context.State, context.Node.PlayerId);
            return EffectStepResult.Completed(NormalizedValue.CreateInteger(amount));
        }

        private ValidationResult ValidateActivation(GameState state, PlayerState player, SpecialActionDefinition definition, string markerId)
        {
            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
                return ValidationResult.Failure(CommandErrorCode.WrongPhase, "特殊行动只能在行动轮阶段发动。");
            if (state.CurrentPlayerId != player.PlayerId)
                return ValidationResult.Failure(CommandErrorCode.NotCurrentPlayer, "当前不是该玩家的行动轮。");
            if (state.HasPendingChoice() || state.HasOpenActionableInteraction())
                return ValidationResult.Failure(CommandErrorCode.PendingChoiceRequired, "请先完成当前待处理交互。");
            ValidationResult budget = new MainActionBudgetService().ValidateCanSpend(state, player.PlayerId);
            if (!budget.IsValid) return budget;
            if (player.UsedSpecialActionIdsThisRound != null && player.UsedSpecialActionIdsThisRound.Contains(definition.SpecialActionId))
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "该特殊行动本回合已经使用过。");
            CityStyleDeclarationState marker = FindMarker(player, markerId);
            if (marker == null || marker.UnlockedSpecialActionId != definition.SpecialActionId)
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "所选城市样式标记未解锁该特殊行动。");
            if (marker.RemainingSpecialActionUses <= 0)
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "该城市样式标记的特殊行动次数已耗尽。");
            if (definition.LocksCharacterCard && player.UsedCharacterThisTurn)
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "本玩家行动轮已使用角色牌。");
            return ValidationResult.Success;
        }

        private CandidateSetDraft BuildCandidateDraft(EffectExecutionContext context, SpecialActionDefinition definition)
        {
            var draft = new CandidateSetDraft
            {
                CandidateSetId = StableIdFactory.Create("candidate", context.Node.EffectId, definition.SpecialActionId),
                Version = 1,
                CandidateKind = CityStyleCandidateKinds.None,
                SourceEffectId = context.Node.EffectId,
                ExecutingPlayerId = context.Node.PlayerId,
                CandidatePoolIds = new List<string>(),
                DefaultCandidateIds = new List<string>(),
                CurrentIds = new List<string>()
            };
            if (definition.EffectKind == SpecialActionEffectKind.DeployInfluence)
            {
                draft.CandidateKind = CityStyleCandidateKinds.InfluenceSlots;
                draft.DefaultCandidateIds.AddRange(optionQuery.GetLegalInfluencePlacementSlotIds(context.State, context.Node.PlayerId));
            }
            else if (definition.EffectKind == SpecialActionEffectKind.ReplaceInfluence)
            {
                draft.CandidateKind = CityStyleCandidateKinds.OpponentInfluences;
                PlayerState player = context.State.FindPlayer(context.Node.PlayerId);
                if (context.State.Map != null && context.State.Map.Influences != null)
                {
                    for (int i = 0; i < context.State.Map.Influences.Count; i++)
                    {
                        InfluencePlacement influence = context.State.Map.Influences[i];
                        if (influence != null && influence.PlayerId != context.Node.PlayerId && !string.IsNullOrEmpty(influence.InfluenceId))
                            draft.DefaultCandidateIds.Add(influence.InfluenceId);
                    }
                }
                draft.DefaultCandidateIds.Sort(StringComparer.Ordinal);
            }
            else if (definition.EffectKind == SpecialActionEffectKind.CompositePowerMove || definition.EffectKind == SpecialActionEffectKind.ConsecutiveFreeMoves)
            {
                draft.CandidateKind = CityStyleCandidateKinds.MoveLocations;
                draft.DefaultCandidateIds.AddRange(optionQuery.GetLegalFreeMoveTargetIds(context.State, context.Node.PlayerId));
            }
            draft.CandidatePoolIds.AddRange(draft.DefaultCandidateIds);
            return draft;
        }

        private static NormalizedValue CreateActivationPayload(
            GameState state,
            EffectNodeRuntimeState node,
            SpecialActionDefinition definition,
            CandidateSetDraft draft,
            CandidateResolutionRecord resolution,
            string markerId)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                Entry("playerId", NormalizedValue.CreateInteger(node.PlayerId)),
                Entry("specialActionId", NormalizedValue.CreateString(definition.SpecialActionId)),
                Entry("markerInstanceId", NormalizedValue.CreateString(markerId)),
                Entry("candidateSetId", NormalizedValue.CreateString(resolution.CandidateSetId)),
                Entry("candidateSetVersion", NormalizedValue.CreateInteger(resolution.Version)),
                Entry("candidateKind", NormalizedValue.CreateString(draft.CandidateKind)),
                Entry("candidateIds", StringArray(resolution.FinalCandidateIds)),
                Entry("definitionEffectKind", NormalizedValue.CreateString(definition.EffectKind.ToString())),
                Entry("requiredTargetCount", NormalizedValue.CreateInteger(RequiredTargetCount(state, node.PlayerId, definition, resolution.FinalCandidateIds))),
                Entry("maximumTargetCount", NormalizedValue.CreateInteger(definition.MaximumTargetCount)),
                Entry("freeMoveCount", NormalizedValue.CreateInteger(definition.FreeMoveCount)),
                Entry("extraMainActionCount", NormalizedValue.CreateInteger(definition.ExtraMainActionCount)),
                Entry("locksCharacterCard", NormalizedValue.CreateBoolean(definition.LocksCharacterCard))
            });
        }

        private static int RequiredTargetCount(
            GameState state,
            int playerId,
            SpecialActionDefinition definition,
            IList<string> candidates)
        {
            int available = candidates == null ? 0 : candidates.Count;
            if (definition.EffectKind == SpecialActionEffectKind.DeployInfluence)
            {
                PlayerState player = state == null ? null : state.FindPlayer(playerId);
                int declarations = CountDeclarations(player, definition.CityStyleId);
                int supply = player == null ? 0 : Math.Max(0, player.InfluenceSupply);
                return Math.Min(
                    definition.MaximumTargetCount,
                    Math.Min(declarations, Math.Min(supply, available)));
            }
            if (definition.EffectKind == SpecialActionEffectKind.ReplaceInfluence ||
                definition.EffectKind == SpecialActionEffectKind.CompositePowerMove)
                return available > 0 ? 1 : 0;
            return available > 0 ? 1 : 0;
        }

        private static int CountDeclarations(PlayerState player, string cityStyleId)
        {
            if (player == null || player.DeclaredCityStyles == null || string.IsNullOrEmpty(cityStyleId)) return 0;
            int count = 0;
            for (int i = 0; i < player.DeclaredCityStyles.Count; i++)
            {
                CityStyleDeclarationState declaration = player.DeclaredCityStyles[i];
                if (declaration != null && declaration.CityStyleId == cityStyleId) count++;
            }
            return count;
        }

        private List<string> GetRouteSlots(GameState state, string routeId, int playerId)
        {
            var result = new List<string>();
            if (mapQuery == null || influenceService == null || string.IsNullOrEmpty(routeId)) return result;
            MapRouteDefinition route = mapQuery.GetRoute(routeId);
            for (int i = 0; i < route.InfluenceSlotCount; i++)
            {
                string slot = InfluenceService.GetRouteSlotId(routeId, i);
                if (influenceService.CanPlace(state, playerId, slot).IsValid) result.Add(slot);
            }
            return result;
        }

        private static string ReadLatestRouteId(IList<EffectNodeRuntimeState> children)
        {
            for (int i = children.Count - 1; i >= 0; i--)
            {
                string route = ReadString(children[i].NormalizedResult, "routeId", string.Empty);
                if (!string.IsNullOrEmpty(route)) return route;
            }
            return string.Empty;
        }

        private static void MarkActivated(PlayerState player, SpecialActionDefinition definition, string markerId)
        {
            CityStyleDeclarationState marker = FindMarker(player, markerId);
            if (definition.Level >= 2)
            {
                marker.MarkerArea = marker.RemainingSpecialActionUses >= 2 ? SpecialActionMarkerAreas.UsedFromTwo : SpecialActionMarkerAreas.UsedFromOne;
                return;
            }
            marker.MarkerArea = CityStyleMarkerAreas.Used;
            marker.RemainingSpecialActionUses = 0;
            for (int i = 0; i < player.DeclaredCityStyles.Count; i++)
            {
                CityStyleDeclarationState other = player.DeclaredCityStyles[i];
                if (other != null && other.CityStyleId == definition.CityStyleId &&
                    (other.MarkerArea == CityStyleMarkerAreas.Declared ||
                     (other.UnlockedSpecialActionId == definition.SpecialActionId && other.MarkerArea == CityStyleMarkerAreas.Unused && other.RemainingSpecialActionUses > 0)))
                {
                    other.MarkerArea = CityStyleMarkerAreas.Used;
                    other.RemainingSpecialActionUses = 0;
                }
            }
        }

        private static CityStyleDeclarationState FindMarker(PlayerState player, string markerId)
        {
            if (player == null || player.DeclaredCityStyles == null) return null;
            for (int i = 0; i < player.DeclaredCityStyles.Count; i++)
                if (player.DeclaredCityStyles[i] != null && player.DeclaredCityStyles[i].InfluenceMarkerId == markerId) return player.DeclaredCityStyles[i];
            return null;
        }

        private static bool HasNonTerminalChild(EffectExecutionContext context)
        {
            foreach (EffectNodeRuntimeState child in context.ChildNodes)
                if (child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed && child.Status != EffectNodeStatus.Faulted) return true;
            return false;
        }

        private static bool HasFailedChild(EffectExecutionContext context)
        {
            foreach (EffectNodeRuntimeState child in context.ChildNodes)
                if (child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted) return true;
            return false;
        }

        private static string ValidateObjectSpec(EffectSpec spec)
        {
            return spec == null || spec.NormalizedArguments == null || spec.NormalizedArguments.Kind != NormalizedValueKind.Object
                ? "城市样式特殊行动 Effect 参数必须是对象。" : string.Empty;
        }

        private static string ReadString(NormalizedValue value, string name, string fallback)
        {
            NormalizedValue item;
            return TryGet(value, name, out item) && item != null && item.Kind == NormalizedValueKind.String ? item.StringValue : fallback;
        }

        private static int ReadInt(NormalizedValue value, string name, int fallback)
        {
            NormalizedValue item;
            return TryGet(value, name, out item) && item != null && item.Kind == NormalizedValueKind.Integer ? (int)item.IntegerValue : fallback;
        }

        private static bool ReadBool(NormalizedValue value, string name, bool fallback)
        {
            NormalizedValue item;
            return TryGet(value, name, out item) && item != null && item.Kind == NormalizedValueKind.Boolean ? item.BooleanValue : fallback;
        }

        private static List<string> ReadStringArray(NormalizedValue value, string name)
        {
            var result = new List<string>();
            NormalizedValue item;
            if (!TryGet(value, name, out item) || item == null || item.Kind != NormalizedValueKind.Array || item.Items == null) return result;
            foreach (NormalizedValue child in item.Items)
            {
                if (child != null && child.Kind == NormalizedValueKind.String && !string.IsNullOrEmpty(child.StringValue)) result.Add(child.StringValue);
                else if (child != null && child.Kind == NormalizedValueKind.StableReference && !string.IsNullOrEmpty(child.ReferenceId)) result.Add(child.ReferenceId);
            }
            return result;
        }

        private static List<string> ReadAnswerIds(NormalizedValue answer)
        {
            var result = new List<string>();
            if (answer == null) return result;
            if (answer.Kind == NormalizedValueKind.Array)
            {
                foreach (NormalizedValue item in answer.Items ?? new List<NormalizedValue>())
                {
                    string id = item != null && item.Kind == NormalizedValueKind.StableReference ? item.ReferenceId : item == null ? string.Empty : item.StringValue;
                    if (!string.IsNullOrEmpty(id) && !result.Contains(id)) result.Add(id);
                }
            }
            else if (answer.Kind == NormalizedValueKind.String || answer.Kind == NormalizedValueKind.StableReference)
                result.Add(answer.Kind == NormalizedValueKind.String ? answer.StringValue : answer.ReferenceId);
            return result;
        }

        private static bool TryGet(NormalizedValue value, string name, out NormalizedValue result)
        {
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
                foreach (NormalizedValueEntry entry in value.Properties)
                    if (entry != null && entry.Name == name) { result = entry.Value; return true; }
            result = null;
            return false;
        }

        private static NormalizedValue StringArray(IList<string> values)
        {
            var result = new List<NormalizedValue>();
            if (values != null) foreach (string value in values) result.Add(NormalizedValue.CreateString(value ?? string.Empty));
            return NormalizedValue.CreateArray(result);
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }
    }

    internal static class EffectStepResultExtensions
    {
        public static EffectStepResult CopyCandidates(this EffectStepResult result, IList<string> candidates)
        {
            if (result == null || result.Interactions.Count == 0) return result;
            result.Interactions[result.Interactions.Count - 1].CandidateIds.Clear();
            if (candidates != null) result.Interactions[result.Interactions.Count - 1].CandidateIds.AddRange(candidates);
            return result;
        }
    }
}
