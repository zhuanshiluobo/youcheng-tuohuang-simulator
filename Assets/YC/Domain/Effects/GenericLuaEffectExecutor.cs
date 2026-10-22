using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Scoring;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Presentation;

namespace YC.Domain.Effects
{
    /// <summary>
    /// Lua 首批契约的宿主执行器。尚未迁移的类型仍保留注册和明确失败语义；
    /// 已接入的类型在这里直接修改领域状态或挂接正式子 Effect。
    /// </summary>
    public static class LuaDomainEffectTypeIds
    {
        public const string ChoiceExecutionInteraction = "effect.choice.execute";
        public const string Choice = "effect.flow.choice";
        public const string Repeat = "effect.flow.repeat";
        public const string RollDice = "effect.random.roll_dice";
        public const string ResourceDiscount = "effect.resource.discount";
        public const string ResourceSell = "effect.resource.sell";
        public const string LoseScore = "effect.score.lose";
        public const string PlaceRoad = "effect.influence.place_road";
        public const string OperateToken = "effect.token.operate";
        public const string ShuffleFacilityDeck = "effect.facility.shuffle_deck";
        public const string MoveFacilityCard = "effect.facility.move_card";
        public const string RotateFacilityCard = "effect.facility.rotate_card";
        public const string RevealCharacterCard = "effect.character.reveal";
        public const string SetCharacterDoubleUseRule = "effect.character.double_use_rule";
        public const string MoveCharacterCard = "effect.character.move";
        public const string DispatchOperator = "effect.operator.dispatch";
        public const string UpgradeEnterprise = "effect.enterprise.upgrade";
        public const string ActivateEnterpriseSpecial = "effect.enterprise.activate_special";
        public const string SwitchDepartment = "effect.enterprise.switch_department";
        public const string OverrideNextStartPlayer = "effect.round.override_start_player";
        public const string ExecuteMainAction = "effect.main_action.execute";
        public const string OpenPlayerTaskGroup = "effect.task_group.open";
        public const string MainActionDeploy = "effect.main_action.deploy";
        public const string MainActionDispatch = "effect.main_action.dispatch";
        public const string MainActionBuild = "effect.main_action.build";
        public const string MainActionMoveCity = "effect.main_action.move_city";
        public const string MainActionSpecial = "effect.main_action.special";
        public const string DeclareCityStyle = "effect.city_style.declare";
    }

    public sealed class GenericLuaEffectExecutor
    {
        private readonly string effectTypeId;

        private GenericLuaEffectExecutor(string effectTypeId)
        {
            this.effectTypeId = effectTypeId;
        }

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            ContentPrimitiveEffectExecutor.Register(registry);
            ResourceCollectionEffectExecutor.Register(registry);
            RouteResourceSelectionEffectExecutor.Register(registry);
            CityStyleDeclarationEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry);
            DeckShuffleEffectExecutor.Register(registry);
            RepeatEffectExecutor.Register(registry);
            YC.Domain.Cards.CharacterLifecycleService.Register(registry);
            YC.Domain.Cards.CharacterContentEffectExecutor.Register(registry);
            DiceRollEffectExecutor.Register(registry);
            YC.Domain.Movement.MapOperationEffectExecutor.Register(registry);
            YC.Domain.Enterprises.EnterpriseEffectExecutor.Register(registry);
            string[] effectTypeIds =
            {
                LuaDomainEffectTypeIds.Choice,
                LuaDomainEffectTypeIds.ResourceDiscount,
                LuaDomainEffectTypeIds.ResourceSell,
                LuaDomainEffectTypeIds.LoseScore,
                LuaDomainEffectTypeIds.RotateFacilityCard,
                LuaDomainEffectTypeIds.RevealCharacterCard,
                LuaDomainEffectTypeIds.OverrideNextStartPlayer,
                LuaDomainEffectTypeIds.ExecuteMainAction,
                LuaDomainEffectTypeIds.OpenPlayerTaskGroup,
                LuaDomainEffectTypeIds.MainActionDeploy,
                LuaDomainEffectTypeIds.MainActionDispatch,
                LuaDomainEffectTypeIds.MainActionBuild,
                LuaDomainEffectTypeIds.MainActionMoveCity,
                LuaDomainEffectTypeIds.MainActionSpecial,
                LuaDomainEffectTypeIds.DeclareCityStyle
            };

            for (int i = 0; i < effectTypeIds.Length; i++)
            {
                EffectRegistration ignored;
                if (registry.TryGet(effectTypeIds[i], out ignored)) continue;
                var executor = new GenericLuaEffectExecutor(effectTypeIds[i]);
                registry.Register(new EffectRegistration(
                    effectTypeIds[i],
                    executor.Execute,
                    effectTypeIds[i] == LuaDomainEffectTypeIds.Choice || effectTypeIds[i] == LuaDomainEffectTypeIds.ExecuteMainAction
                        ? EffectExecutorKind.IntrinsicFlow : EffectExecutorKind.Atomic,
                    "1.0.0")
                {
                    Validator = Validate
                });
            }
        }

        private EffectStepResult Execute(EffectExecutionContext context)
        {
            if (effectTypeId == LuaDomainEffectTypeIds.Choice)
            {
                return ExecuteChoice(context);
            }

            if (effectTypeId == LuaDomainEffectTypeIds.ExecuteMainAction)
            {
                return ExecuteMainAction(context);
            }

            if (effectTypeId == LuaDomainEffectTypeIds.LoseScore)
            {
                return ExecuteLoseScore(context);
            }

            // 注册 API 名称不等于规则已实现。静默 Completed 会吞掉整条卡面效果，
            // 让跑局和恢复测试产生假阳性；未迁移的合同必须由内核 fail-stop。
            throw new InvalidOperationException("Lua Effect 尚未实现宿主结算：" + effectTypeId);
        }

        private static EffectStepResult ExecuteChoice(EffectExecutionContext context)
        {
            if (context == null || context.Node == null)
            {
                return EffectStepResult.Failed("lua_choice_context_invalid");
            }

            if (context.Node.FlowStage == "branch_completed") return EffectStepResult.Completed(context.Node.NormalizedResult);
            string argumentError = ValidateChoiceArguments(context.Node.NormalizedArguments);
            if (argumentError.Length > 0) return EffectStepResult.Failed(argumentError);
            if (ItemTransferEffectExecutor.Value(context.Node.NormalizedArguments, "amountRange") != null)
                return ExecuteAmountChoice(context);
            List<string> candidates = ReadStringArray(context.Node.NormalizedArguments, "options");
            if (candidates.Count == 0 && ReadInteger(context.Node.NormalizedArguments, "minSelections", 1) == 0)
                return EffectStepResult.Completed(ChoiceResult(context.Node.NormalizedArguments, NormalizedValue.CreateNull()));
            if (candidates.Count == 0) return EffectStepResult.Failed("lua_choice_no_candidates");

            int minSelections = ReadInteger(context.Node.NormalizedArguments, "minSelections", 1);
            int maxSelections = ReadInteger(context.Node.NormalizedArguments, "maxSelections", minSelections);
            if (minSelections < 0 || maxSelections < 1 || maxSelections < minSelections || maxSelections > candidates.Count)
            {
                return EffectStepResult.Failed("lua_choice_selection_range_invalid");
            }

            if (context.Node.FlowStage == "awaiting_choice")
            {
                NormalizedValue answer = context.GetLatestInteractionAnswer();
                if (answer == null) return EffectStepResult.NoProgress("Lua Choice 尚未收到回答。");
                if (minSelections == 0 && answer.Kind == NormalizedValueKind.Boolean && !answer.BooleanValue)
                    return EffectStepResult.Completed(ChoiceResult(context.Node.NormalizedArguments, NormalizedValue.CreateNull()));
                // UI 使用候选引用数组，诊断/旧命令可能使用字符串或单个引用。
                // continuation 只消费规范结果，不能依赖提交入口的编码形状。
                var values = answer.Kind == NormalizedValueKind.Array
                    ? answer.Items : new List<NormalizedValue> { answer };
                var selected = new List<NormalizedValue>();
                foreach (var value in values)
                {
                    if (value == null) return EffectStepResult.Failed("lua_choice_answer_invalid");
                    string id = value.Kind == NormalizedValueKind.String ? value.StringValue :
                        value.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : string.Empty;
                    if (string.IsNullOrEmpty(id) || !candidates.Contains(id))
                        return EffectStepResult.Failed("lua_choice_answer_invalid");
                    selected.Add(NormalizedValue.CreateString(id));
                }
                if (selected.Count < minSelections || selected.Count > maxSelections)
                    return EffectStepResult.Failed("lua_choice_answer_invalid");
                if (context.Node.NestedGroupSizes.Count > 0)
                {
                    if (selected.Count != 1) return EffectStepResult.Failed("choice_branch_requires_single_selection");
                    int branch = candidates.IndexOf(selected[0].StringValue);
                    if (branch >= context.Node.NestedGroupSizes.Count) return EffectStepResult.Failed("choice_branch_missing");
                    int offset = 0;
                    for (int i = 0; i < branch; i++) offset += context.Node.NestedGroupSizes[i];
                    var children = new List<EffectSpec>();
                    for (int i = 0; i < context.Node.NestedGroupSizes[branch]; i++) children.Add(EffectRegistry.FromRuntimeSpec(context.Node.NestedEffects[offset + i]));
                    context.Node.NormalizedResult = ChoiceResult(context.Node.NormalizedArguments, selected[0]);
                    return EffectStepResult.Continue("branch_completed").AddChildren(children);
                }
                return EffectStepResult.Completed(ChoiceResult(context.Node.NormalizedArguments,
                    selected.Count == 0 ? NormalizedValue.CreateNull() : maxSelections == 1 ? selected[0] : NormalizedValue.CreateArray(selected)));
            }

            var interaction = new EffectInteractionSpec
            {
                InteractionTypeId = ReadString(context.Node.NormalizedArguments, "interactionType", "lua.choice"),
                Visibility = GameStateVisibilityPolicy.Owner,
                PromptKey = ReadString(context.Node.NormalizedArguments, "promptKey", "lua.choice.select"),
                AnsweringPlayerId = ReadPlayerId(context.Node.NormalizedArguments, context.Node.PlayerId),
                MinSelections = minSelections,
                MaxSelections = maxSelections,
                AnswerSchema = maxSelections > 1 ? "candidate_id_array" : "candidate_id",
                AllowDecline = minSelections == 0
            };
            interaction.CandidateIds.AddRange(candidates);
            if (interaction.InteractionTypeId == LuaDomainEffectTypeIds.ChoiceExecutionInteraction || interaction.InteractionTypeId == "character.ability.choice.reward")
                interaction.PromptParameters = ItemTransferEffectExecutor.Object("choiceIntent", NormalizedValue.CreateString("choice_execute"));

            return EffectStepResult.Continue("awaiting_choice").AddInteraction(interaction);
        }

        public static string ValidateChoiceArguments(NormalizedValue arguments)
        {
            if (arguments?.Kind != NormalizedValueKind.Object || !arguments.IsValid()) return "lua_choice_arguments_invalid";
            var resultContext = ItemTransferEffectExecutor.Value(arguments, "resultContext");
            if (resultContext != null && (resultContext.Kind != NormalizedValueKind.Object || !IsPlainChoiceContext(resultContext)))
                return "lua_choice_result_context_invalid";
            var range = ItemTransferEffectExecutor.Value(arguments, "amountRange");
            if (range == null) return string.Empty;
            if (range.Kind != NormalizedValueKind.Object ||
                arguments.Properties.Any(entry => !new[] { "player", "amountRange", "resultContext", "kind", "effectTypeId" }.Contains(entry.Name)) ||
                new[] { "options", "branches", "minSelections", "maxSelections" }.Any(key => ItemTransferEffectExecutor.Value(arguments, key) != null) ||
                range.Properties.Any(entry => !new[] { "minAmount", "maxAmount", "sourcePlayer", "resourceType" }.Contains(entry.Name)))
                return "lua_choice_amount_range_invalid";
            var min = ItemTransferEffectExecutor.Value(range, "minAmount");
            var max = ItemTransferEffectExecutor.Value(range, "maxAmount");
            if (min?.Kind != NormalizedValueKind.Integer || max?.Kind != NormalizedValueKind.Integer ||
                min.IntegerValue < 0 || max.IntegerValue < min.IntegerValue || max.IntegerValue > int.MaxValue)
                return "lua_choice_amount_range_invalid";
            var source = ItemTransferEffectExecutor.Value(range, "sourcePlayer");
            var resource = ItemTransferEffectExecutor.Value(range, "resourceType");
            if ((source == null) != (resource == null) || source != null &&
                (!ResourceLossEffectExecutor.TryReadPlayer(source, out _) || !ResourceLossEffectExecutor.TryReadResourceType(resource, out _)))
                return "lua_choice_amount_source_invalid";
            return string.Empty;
        }

        private static bool IsPlainChoiceContext(NormalizedValue value)
        {
            if (value.Kind == NormalizedValueKind.Object)
                return !value.Properties.Any(entry => entry.Name == "effectTypeId" || entry.Name == "kind" &&
                    entry.Value.Kind == NormalizedValueKind.String && entry.Value.StringValue.Contains("effect")) &&
                    value.Properties.All(entry => IsPlainChoiceContext(entry.Value));
            return value.Kind != NormalizedValueKind.Array || value.Items.All(IsPlainChoiceContext);
        }

        private static NormalizedValue ChoiceResult(NormalizedValue arguments, NormalizedValue value)
        {
            var resultContext = ItemTransferEffectExecutor.Value(arguments, "resultContext");
            return resultContext == null ? value : ItemTransferEffectExecutor.Object("value", value,
                "context", resultContext.Clone());
        }

        private static EffectStepResult ExecuteAmountChoice(EffectExecutionContext context)
        {
            var args = context.Node.NormalizedArguments;
            if (ReadPlayerId(args, context.Node.PlayerId) != context.Node.PlayerId)
                return EffectStepResult.Failed("lua_choice_amount_player_invalid");
            var range = ItemTransferEffectExecutor.Value(args, "amountRange");
            int minimum = (int)ItemTransferEffectExecutor.Value(range, "minAmount").IntegerValue;
            int maximum = (int)ItemTransferEffectExecutor.Value(range, "maxAmount").IntegerValue;
            string amountKind = "amount";
            var source = ItemTransferEffectExecutor.Value(range, "sourcePlayer");
            if (source != null)
            {
                ResourceLossEffectExecutor.TryReadPlayer(source, out int sourcePlayer);
                if (context.State.FindPlayer(sourcePlayer)?.Resources == null) return EffectStepResult.Failed("lua_choice_amount_source_invalid");
                var resource = ItemTransferEffectExecutor.Value(range, "resourceType");
                ResourceLossEffectExecutor.TryReadResourceType(resource, out var resourceType);
                int available = ResourceLossEffectExecutor.AvailableAmount(context.State, sourcePlayer, resourceType);
                if (available < 0) return EffectStepResult.Failed("lua_choice_amount_balance_invalid");
                maximum = Math.Min(maximum, available);
                amountKind = resource.StringValue;
            }
            if (maximum < minimum) return EffectStepResult.Failed("lua_choice_amount_unavailable");
            var parameters = ItemTransferEffectExecutor.Object("minAmount", NormalizedValue.CreateInteger(minimum),
                "maxAmount", NormalizedValue.CreateInteger(maximum), "amountKind", NormalizedValue.CreateString(amountKind),
                "rowIds", NormalizedValue.CreateArray(new[] { NormalizedValue.CreateString(amountKind) }),
                "rowMaximums", NormalizedValue.CreateArray(new[] { NormalizedValue.CreateInteger(maximum) }));
            if (context.Node.FlowStage == "awaiting_amount" &&
                ContentPrimitiveEffectExecutor.TryReadBoundedAmount(parameters, context.GetLatestInteractionAnswer(), out int amount))
                return EffectStepResult.Completed(ChoiceResult(args, NormalizedValue.CreateInteger(amount)));
            // 旧答案超出实际库存时重新发布准确范围，不把答案静默截断。
            return EffectStepResult.Continue("awaiting_amount").AddInteraction(new EffectInteractionSpec
            {
                InteractionTypeId = ContentPrimitiveEffectExecutor.QuantityInteractionTypeId,
                PromptKey = ContentPrimitiveEffectExecutor.QuantityInteractionTypeId,
                AnsweringPlayerId = context.Node.PlayerId, Visibility = GameStateVisibilityPolicy.Owner,
                AnswerSchema = ContentPrimitiveEffectExecutor.QuantityAnswerSchema, PromptParameters = parameters,
                MinSelections = 0, MaxSelections = 0, AllowDecline = false
            });
        }

        private static EffectStepResult ExecuteMainAction(EffectExecutionContext context)
        {
            if (context == null || context.Node == null)
            {
                return EffectStepResult.Failed("lua_main_action_context_invalid");
            }

            string executionMode = ReadString(context.Node.NormalizedArguments, "executionMode", string.Empty);
            if (executionMode == "immediate")
            {
                int playerId = ReadPlayerId(context.Node.NormalizedArguments, context.Node.PlayerId);
                var allowed = ReadStringArray(context.Node.NormalizedArguments, "allowedActionTypes");
                if (playerId != context.Node.PlayerId || context.State.FindPlayer(playerId) == null ||
                    allowed.Count == 0 || allowed.Distinct().Count() != allowed.Count ||
                    allowed.Any(action => !new[] { "build", "explore", "move_city", "deploy", "dispatch", "special" }.Contains(action)))
                    return EffectStepResult.Failed("main_action_restriction_invalid");
                if (context.Node.FlowStage == "executing" && context.ChildNodes.Any(child => child.Status != EffectNodeStatus.Completed && child.Status != EffectNodeStatus.Failed && child.Status != EffectNodeStatus.Faulted))
                    return EffectStepResult.NoProgress("等待即时主要行动结算完成。");
                if (context.Node.FlowStage == "executing")
                    return context.ChildNodes.Any(child => child.Status == EffectNodeStatus.Failed || child.Status == EffectNodeStatus.Faulted)
                        ? EffectStepResult.Failed("immediate_main_action_failed") : EffectStepResult.Completed();
                if (context.Node.FlowStage == "awaiting_action")
                {
                    var answer = context.GetLatestInteractionAnswer();
                    if (answer?.Kind == NormalizedValueKind.Array && answer.Items.Count == 1) answer = answer.Items[0];
                    string selected = answer?.Kind == NormalizedValueKind.StableReference ? answer.ReferenceId : answer?.StringValue;
                    if (!allowed.Contains(selected)) return EffectStepResult.Failed("immediate_main_action_invalid");
                    EffectSpec child = selected == "move_city" ? CityMoveEffectSpecFactory.Move(playerId, "", false, false, context.Node.SourceId) :
                        EffectSpec.Create(selected == "build" ? FacilitySelectionEffectExecutor.TypeId : ExplorationSelectionEffectExecutor.TypeId,
                            ItemTransferEffectExecutor.Object("player", NormalizedValue.CreateString(playerId.ToString(CultureInfo.InvariantCulture))), playerId);
                    if (selected == "deploy") child = InfluenceEffectSpecFactory.PlaceInfluence(playerId, "", context.Node.SourceId);
                    if (selected == "dispatch") child = EffectSpec.Create(ContentPrimitiveEffectExecutor.SelectInfluence, ItemTransferEffectExecutor.Object("player", NormalizedValue.CreateString(playerId.ToString()), "operation", NormalizedValue.CreateString("move"), "subjectFilter", NormalizedValue.CreateString("self"), "count", NormalizedValue.CreateInteger(2), "interactionType", NormalizedValue.CreateString("character.ability.choice.move"), "promptKey", NormalizedValue.CreateString("effect.influence.move.choose_target")), playerId);
                    if (selected == "special") child = EffectSpec.Create(YC.Domain.Cards.CharacterContentEffectExecutor.Prefix + "immediate_special", playerId: playerId);
                    return EffectStepResult.Continue("executing").AddChild(child);
                }
                if (!string.IsNullOrEmpty(context.Node.FlowStage)) return EffectStepResult.Failed("immediate_main_action_stage_invalid");
                var interaction = new EffectInteractionSpec { InteractionTypeId = LuaDomainEffectTypeIds.ChoiceExecutionInteraction, Visibility = "owner",
                    PromptKey = "main_action.choose_non_special", AnsweringPlayerId = playerId, MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id",
                    PromptParameters = ItemTransferEffectExecutor.Object("choiceIntent", NormalizedValue.CreateString("choice_execute")),
                    Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:interaction.main_action.choose") } };
                interaction.CandidateIds.AddRange(allowed);
                foreach (string action in allowed) interaction.Presentation.Options.Add(new GameplayOptionPresentation {
                    CandidateId = action, Label = new GameplayTextRef("core:interaction.main_action." + action) });
                if (YC.Domain.Items.ItemZoneLegacyProjection.IsActive(context.State)) interaction = ItemTransferEffectExecutor.PrepareInteraction(context, interaction);
                return EffectStepResult.Continue("awaiting_action").AddInteraction(interaction);
            }
            if (executionMode == "grant_budget")
            {
                int amount = ReadInteger(context.Node.NormalizedArguments, "amount", 0);
                if (amount <= 0)
                {
                    return EffectStepResult.Failed("lua_main_action_grant_amount_invalid");
                }

                int playerId = ReadPlayerId(context.Node.NormalizedArguments, context.Node.PlayerId);
                new MainActionBudgetService().GrantAdditionalMainActions(context.State, playerId, amount);
                return EffectStepResult.Completed(NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(playerId) },
                    new NormalizedValueEntry { Name = "amount", Value = NormalizedValue.CreateInteger(amount) },
                    new NormalizedValueEntry { Name = "executionMode", Value = NormalizedValue.CreateString(executionMode) }
                }));
            }

            if (executionMode == "facility_entry")
                throw new KernelException(EffectFaultCodes.DefinitionVersionMismatch, "旧设施入口适配已撤下。");

            return EffectStepResult.Failed("lua_main_action_mode_not_implemented");
        }

        private static EffectStepResult ExecuteLoseScore(EffectExecutionContext context)
        {
            if (context == null || context.Node == null || context.State == null)
            {
                return EffectStepResult.Failed("lua_lose_score_context_invalid");
            }

            int amount = ReadInteger(context.Node.NormalizedArguments, "amount", 0);
            if (amount <= 0)
            {
                return EffectStepResult.Failed("lua_lose_score_amount_invalid");
            }

            int playerId = ReadPlayerId(context.Node.NormalizedArguments, context.Node.PlayerId);
            PlayerState player = context.State.FindPlayer(playerId);
            if (player == null)
            {
                return EffectStepResult.Failed("lua_lose_score_player_invalid");
            }

            int previousScore = player.Score;
            int nextScore = checked(previousScore - amount);
            NormalizedValue minimumScore;
            if (TryGet(context.Node.NormalizedArguments, "minimumScore", out minimumScore))
            {
                if (minimumScore == null || minimumScore.Kind != NormalizedValueKind.Integer ||
                    minimumScore.IntegerValue < int.MinValue || minimumScore.IntegerValue > int.MaxValue)
                    return EffectStepResult.Failed("lua_lose_score_minimum_invalid");
                nextScore = Math.Max((int)minimumScore.IntegerValue, nextScore);
            }
            player.Score = nextScore;

            return EffectStepResult.Completed(NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(playerId) },
                new NormalizedValueEntry { Name = "amount", Value = NormalizedValue.CreateInteger(amount) },
                new NormalizedValueEntry { Name = "previousScore", Value = NormalizedValue.CreateInteger(previousScore) },
                new NormalizedValueEntry { Name = "score", Value = NormalizedValue.CreateInteger(nextScore) }
            }));
        }

        private static List<string> ReadStringArray(NormalizedValue arguments, string name)
        {
            var result = new List<string>();
            NormalizedValue value;
            if (!TryGet(arguments, name, out value) || value == null || value.Kind != NormalizedValueKind.Array || value.Items == null)
            {
                return result;
            }

            for (int i = 0; i < value.Items.Count; i++)
            {
                NormalizedValue item = value.Items[i];
                if (item == null) continue;
                if (item.Kind == NormalizedValueKind.String && !string.IsNullOrEmpty(item.StringValue))
                {
                    result.Add(item.StringValue);
                }
                else if (item.Kind == NormalizedValueKind.StableReference && !string.IsNullOrEmpty(item.ReferenceId))
                {
                    result.Add(item.ReferenceId);
                }
            }

            return result;
        }

        private static int ReadPlayerId(NormalizedValue arguments, int fallback)
        {
            NormalizedValue value;
            if (!TryGet(arguments, "player", out value))
            {
                TryGet(arguments, "executingPlayer", out value);
            }

            if (value != null)
            {
                if (value.Kind == NormalizedValueKind.Integer)
                {
                    return (int)value.IntegerValue;
                }

                string playerKey = value.Kind == NormalizedValueKind.String
                    ? value.StringValue
                    : value.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : string.Empty;
                if (!string.IsNullOrEmpty(playerKey))
                {
                    if (playerKey.StartsWith("p", StringComparison.OrdinalIgnoreCase))
                    {
                        playerKey = playerKey.Substring(1);
                    }

                    int parsed;
                    if (int.TryParse(playerKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        return parsed;
                    }
                }
            }

            return fallback;
        }

        private static int ReadInteger(NormalizedValue arguments, string name, int fallback)
        {
            NormalizedValue value;
            return TryGet(arguments, name, out value) && value != null && value.Kind == NormalizedValueKind.Integer
                ? (int)value.IntegerValue
                : fallback;
        }

        private static string ReadString(NormalizedValue arguments, string name, string fallback)
        {
            NormalizedValue value;
            return TryGet(arguments, name, out value) && value != null && value.Kind == NormalizedValueKind.String
                ? value.StringValue
                : fallback;
        }

        private static string ReadReference(NormalizedValue arguments, string name)
        {
            NormalizedValue value;
            if (!TryGet(arguments, name, out value) || value == null)
            {
                return string.Empty;
            }

            if (value.Kind == NormalizedValueKind.String)
            {
                return value.StringValue ?? string.Empty;
            }

            return value.Kind == NormalizedValueKind.StableReference
                ? value.ReferenceId ?? string.Empty
                : string.Empty;
        }

        private static bool HasNonTerminalChild(EffectExecutionContext context)
        {
            if (context == null || context.Node == null || context.Node.ChildEffectIds == null)
            {
                return false;
            }

            for (int i = 0; i < context.Node.ChildEffectIds.Count; i++)
            {
                EffectNodeRuntimeState child = context.State.EffectRuntime.EffectNodes.Find(
                    candidate => candidate != null && candidate.EffectId == context.Node.ChildEffectIds[i]);
                if (child != null && child.Status != EffectNodeStatus.Completed &&
                    child.Status != EffectNodeStatus.Failed)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasFailedChild(EffectExecutionContext context)
        {
            if (context == null || context.Node == null || context.Node.ChildEffectIds == null)
            {
                return false;
            }

            for (int i = 0; i < context.Node.ChildEffectIds.Count; i++)
            {
                EffectNodeRuntimeState child = context.State.EffectRuntime.EffectNodes.Find(
                    candidate => candidate != null && candidate.EffectId == context.Node.ChildEffectIds[i]);
                if (child != null && child.Status == EffectNodeStatus.Failed)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGet(NormalizedValue value, string name, out NormalizedValue result)
        {
            result = null;
            if (value == null || value.Kind != NormalizedValueKind.Object || value.Properties == null)
            {
                return false;
            }

            for (int i = 0; i < value.Properties.Count; i++)
            {
                NormalizedValueEntry entry = value.Properties[i];
                if (entry != null && entry.Name == name)
                {
                    result = entry.Value;
                    return true;
                }
            }

            return false;
        }

        private static string Validate(EffectSpec spec)
        {
            if (spec?.EffectTypeId == LuaDomainEffectTypeIds.Choice) return ValidateChoiceArguments(spec.NormalizedArguments);
            return spec == null || spec.NormalizedArguments == null ||
                   spec.NormalizedArguments.Kind != NormalizedValueKind.Object
                ? "Lua 固有 Effect 参数必须是对象。"
                : string.Empty;
        }
    }
}
