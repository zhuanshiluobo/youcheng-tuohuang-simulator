using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Scoring;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    /// <summary>
    /// Lua 首批契约的宿主执行器。尚未迁移的类型仍保留注册和明确失败语义；
    /// 已接入的类型在这里直接修改领域状态或挂接正式子 Effect。
    /// </summary>
    public static class LuaDomainEffectTypeIds
    {
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
            string[] effectTypeIds =
            {
                LuaDomainEffectTypeIds.Choice,
                LuaDomainEffectTypeIds.Repeat,
                LuaDomainEffectTypeIds.RollDice,
                LuaDomainEffectTypeIds.ResourceDiscount,
                LuaDomainEffectTypeIds.ResourceSell,
                LuaDomainEffectTypeIds.LoseScore,
                LuaDomainEffectTypeIds.PlaceRoad,
                LuaDomainEffectTypeIds.OperateToken,
                LuaDomainEffectTypeIds.ShuffleFacilityDeck,
                LuaDomainEffectTypeIds.MoveFacilityCard,
                LuaDomainEffectTypeIds.RotateFacilityCard,
                LuaDomainEffectTypeIds.RevealCharacterCard,
                LuaDomainEffectTypeIds.SetCharacterDoubleUseRule,
                LuaDomainEffectTypeIds.MoveCharacterCard,
                LuaDomainEffectTypeIds.DispatchOperator,
                LuaDomainEffectTypeIds.UpgradeEnterprise,
                LuaDomainEffectTypeIds.ActivateEnterpriseSpecial,
                LuaDomainEffectTypeIds.SwitchDepartment,
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
            List<string> candidates = ReadStringArray(context.Node.NormalizedArguments, "options");
            if (candidates.Count == 0)
            {
                return EffectStepResult.Failed("lua_choice_no_candidates");
            }

            int minSelections = ReadInteger(context.Node.NormalizedArguments, "minSelections", 1);
            int maxSelections = ReadInteger(context.Node.NormalizedArguments, "maxSelections", minSelections);
            if (minSelections < 1 || maxSelections < minSelections || maxSelections > candidates.Count)
            {
                return EffectStepResult.Failed("lua_choice_selection_range_invalid");
            }

            if (context.Node.FlowStage == "awaiting_choice")
            {
                NormalizedValue answer = context.GetLatestInteractionAnswer();
                if (answer == null) return EffectStepResult.NoProgress("Lua Choice 尚未收到回答。");
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
                    context.Node.NormalizedResult = selected[0];
                    return EffectStepResult.Continue("branch_completed").AddChildren(children);
                }
                return EffectStepResult.Completed(maxSelections == 1 ? selected[0] : NormalizedValue.CreateArray(selected));
            }

            var interaction = new EffectInteractionSpec
            {
                InteractionTypeId = ReadString(context.Node.NormalizedArguments, "interactionType", "lua.choice"),
                Visibility = GameStateVisibilityPolicy.Owner,
                PromptKey = ReadString(context.Node.NormalizedArguments, "promptKey", "lua.choice.select"),
                AnsweringPlayerId = ReadPlayerId(context.Node.NormalizedArguments, context.Node.PlayerId),
                MinSelections = minSelections,
                MaxSelections = maxSelections,
                AnswerSchema = maxSelections > 1 ? "candidate_id_array" : "candidate_id"
            };
            interaction.CandidateIds.AddRange(candidates);

            return EffectStepResult.Continue("awaiting_choice").AddInteraction(interaction);
        }

        private static EffectStepResult ExecuteMainAction(EffectExecutionContext context)
        {
            if (context == null || context.Node == null)
            {
                return EffectStepResult.Failed("lua_main_action_context_invalid");
            }

            string executionMode = ReadString(context.Node.NormalizedArguments, "executionMode", string.Empty);
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

            int minimumScore = ReadInteger(
                context.Node.NormalizedArguments,
                "minimumScore",
                ScoreTrackService.MinimumTrackScore);
            int previousScore = player.Score;
            int nextScore = Math.Max(minimumScore, previousScore - amount);
            player.Score = nextScore;

            return EffectStepResult.Completed(NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(playerId) },
                new NormalizedValueEntry { Name = "amount", Value = NormalizedValue.CreateInteger(amount) },
                new NormalizedValueEntry { Name = "previousScore", Value = NormalizedValue.CreateInteger(previousScore) },
                new NormalizedValueEntry { Name = "score", Value = NormalizedValue.CreateInteger(nextScore) },
                new NormalizedValueEntry { Name = "minimumScore", Value = NormalizedValue.CreateInteger(minimumScore) }
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
            return spec == null || spec.NormalizedArguments == null ||
                   spec.NormalizedArguments.Kind != NormalizedValueKind.Object
                ? "Lua 固有 Effect 参数必须是对象。"
                : string.Empty;
        }
    }
}
