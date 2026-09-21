using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    // NMC-014A: 角色牌完成状态只由 Effect 树提交。
    public sealed class CharacterAbilityDefinition
    {
        public CharacterAbilityDefinition(
            string abilityId,
            string definitionVersion,
            string activationSubscriptionId,
            Func<EffectExecutionContext, EffectStepResult> executor)
        {
            if (string.IsNullOrEmpty(abilityId)) throw new ArgumentException("角色能力 ID 不能为空。", nameof(abilityId));
            if (string.IsNullOrEmpty(definitionVersion)) throw new ArgumentException("角色能力版本不能为空。", nameof(definitionVersion));
            if (string.IsNullOrEmpty(activationSubscriptionId)) throw new ArgumentException("角色能力订阅 ID 不能为空。", nameof(activationSubscriptionId));
            AbilityId = abilityId;
            DefinitionVersion = definitionVersion;
            ActivationSubscriptionId = activationSubscriptionId;
            Executor = executor;
        }

        public string AbilityId { get; }
        public string DefinitionVersion { get; }
        public string ActivationSubscriptionId { get; }
        public Func<EffectExecutionContext, EffectStepResult> Executor { get; }
    }

    /// <summary>
    /// 角色能力目录。Lua 只选择已登记的稳定能力 ID；领域层通过注册表执行，
    /// 不按角色牌实例 ID 写分支。
    /// </summary>
    public static class CharacterAbilityCatalog
    {
        public const string LiskarmStrategyAbilityId = "character.liskarm.strategy";
        public const string LiskarmTacticAbilityId = "character.liskarm.tactic";
        public const string ElysiumStrategyAbilityId = "character.elysium.strategy";
        public const string ElysiumTacticAbilityId = "character.elysium.tactic";
        public const string TexasStrategyAbilityId = "character.texas.strategy";
        public const string TexasTacticAbilityId = "character.texas.tactic";
        public const string CannotStrategyAbilityId = "character.cannot.strategy";
        public const string CannotTacticAbilityId = "character.cannot.tactic";
        public const string TinManStrategyAbilityId = "character.tin-man.strategy";
        public const string TinManTacticAbilityId = "character.tin-man.tactic";
        public const string CurrentDefinitionVersion = "1.4.0";

        private static readonly Dictionary<string, CharacterAbilityDefinition> Definitions =
            new Dictionary<string, CharacterAbilityDefinition>(StringComparer.Ordinal)
            {
                { LiskarmStrategyAbilityId, Create(LiskarmStrategyAbilityId, null) },
                { LiskarmTacticAbilityId, Create(LiskarmTacticAbilityId, null) },
                { ElysiumStrategyAbilityId, Create(ElysiumStrategyAbilityId, null) },
                { ElysiumTacticAbilityId, Create(ElysiumTacticAbilityId, null) },
                { TexasStrategyAbilityId, Create(TexasStrategyAbilityId, null) },
                { TexasTacticAbilityId, Create(TexasTacticAbilityId, null) },
                { CannotStrategyAbilityId, Create(CannotStrategyAbilityId, null) },
                { CannotTacticAbilityId, Create(CannotTacticAbilityId, null) },
                { TinManStrategyAbilityId, Create(TinManStrategyAbilityId, null) },
                { TinManTacticAbilityId, Create(TinManTacticAbilityId, null) }
            };

        private static CharacterAbilityDefinition Create(
            string abilityId,
            Func<EffectExecutionContext, EffectStepResult> executor)
        {
            return new CharacterAbilityDefinition(
                abilityId,
                CurrentDefinitionVersion,
                "characters." + abilityId.Substring("character.".Length),
                executor);
        }

        public static IEnumerable<CharacterAbilityDefinition> All
        {
            get { return Definitions.Values; }
        }

        public static bool TryGet(string abilityId, out CharacterAbilityDefinition definition)
        {
            return Definitions.TryGetValue(abilityId ?? string.Empty, out definition);
        }

        public static bool IsRegistered(string abilityId)
        {
            CharacterAbilityDefinition ignored;
            return TryGet(abilityId, out ignored);
        }
    }

    /// <summary>
    /// 角色效果通用 Effect。激活节点由 Lua Event handler 生成具体能力子节点，
    /// 具体能力只使用通用 Interaction，不创建 PendingCharacterEffect。
    /// </summary>
    public static class CharacterAbilityEffectExecutor
    {
        public const string ActivationEffectTypeId = "character.effect.activate";
        public const string AbilityEffectTypeId = "effect.character.ability";
        public const string SequenceEffectTypeId = "effect.character.ability.sequence";
        public const string CharacterEffectActivatedEventType = "CharacterEffectActivated";
        public const string CharacterEffectCompletedEventType = "CharacterEffectCompleted";
        public const string ResourceInteractionTypeId = "character.ability.resource";
        public const string ChoiceInteractionTypeId = "character.ability.choice";
        public const string TargetInteractionTypeId = "character.ability.target";

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (!registry.TryGet(ActivationEffectTypeId, out _))
            {
                registry.Register(new EffectRegistration(
                    ActivationEffectTypeId,
                    ExecuteActivation,
                    EffectExecutorKind.IntrinsicFlow,
                    CharacterAbilityCatalog.CurrentDefinitionVersion));
            }

            if (!registry.TryGet(AbilityEffectTypeId, out _))
            {
                registry.Register(new EffectRegistration(
                    AbilityEffectTypeId,
                    ExecuteAbility,
                    EffectExecutorKind.IntrinsicFlow,
                    CharacterAbilityCatalog.CurrentDefinitionVersion));
            }

            if (!registry.TryGet(SequenceEffectTypeId, out _))
            {
                registry.Register(new EffectRegistration(
                    SequenceEffectTypeId,
                    ExecuteSequence,
                    EffectExecutorKind.IntrinsicFlow,
                    CharacterAbilityCatalog.CurrentDefinitionVersion));
            }
        }

        public static EffectSpec CreateActivationSpec(
            string cardId,
            string abilityId,
            string mode,
            int playerId,
            string stableKey)
        {
            return CreateActivationSpec(cardId, abilityId, mode, string.Empty, playerId, stableKey);
        }

        public static EffectSpec CreateActivationSpec(
            string cardId,
            string abilityId,
            string mode,
            string effectOrder,
            int playerId,
            string stableKey)
        {
            var spec = EffectSpec.Create(
                ActivationEffectTypeId,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry
                    {
                        Name = "abilityId",
                        Value = NormalizedValue.CreateString(abilityId ?? string.Empty)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "cardInstanceId",
                        Value = NormalizedValue.CreateStableReference("character_card", cardId ?? string.Empty)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "mode",
                        Value = NormalizedValue.CreateString(mode ?? string.Empty)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "effectOrder",
                        Value = NormalizedValue.CreateString(effectOrder ?? string.Empty)
                    }
                }),
                playerId);
            spec.SourceId = "character-card:" + (cardId ?? string.Empty);
            spec.StableKey = stableKey ?? string.Empty;
            spec.Visibility = GameStateVisibilityPolicy.Owner;
            return spec;
        }

        public static EffectSpec CreateSequenceSpec(
            string cardId,
            string firstAbilityId,
            string secondAbilityId,
            string effectOrder,
            int playerId,
            string stableKey)
        {
            var spec = EffectSpec.Create(
                SequenceEffectTypeId,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry
                    {
                        Name = "cardInstanceId",
                        Value = NormalizedValue.CreateStableReference("character_card", cardId ?? string.Empty)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "firstAbilityId",
                        Value = NormalizedValue.CreateString(firstAbilityId ?? string.Empty)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "secondAbilityId",
                        Value = NormalizedValue.CreateString(secondAbilityId ?? string.Empty)
                    },
                    new NormalizedValueEntry
                    {
                        Name = "effectOrder",
                        Value = NormalizedValue.CreateString(effectOrder ?? string.Empty)
                    }
                }),
                playerId);
            spec.SourceId = "character-card-sequence:" + (cardId ?? string.Empty);
            spec.StableKey = stableKey ?? string.Empty;
            spec.Visibility = GameStateVisibilityPolicy.Owner;
            return spec;
        }

        private static EffectStepResult ExecuteActivation(EffectExecutionContext context)
        {
            string abilityId = ReadString(context.Node.NormalizedArguments, "abilityId");
            string cardId = ReadReference(context.Node.NormalizedArguments, "cardInstanceId");
            string mode = ReadString(context.Node.NormalizedArguments, "mode");
            string effectOrder = ReadString(context.Node.NormalizedArguments, "effectOrder");
            if (!context.Registry.CharacterActivationSubscriptions.TryGetValue(abilityId, out var subscriptionId) ||
                !context.Registry.HasEventHandler(subscriptionId))
                throw new KernelException(EffectFaultCodes.UnknownEventHandler, "角色能力缺少外部 Lua handler：" + abilityId);

            PlayerState player = context.State.FindPlayer(context.Node.PlayerId);
            if (player == null || string.IsNullOrEmpty(cardId))
            {
                return EffectStepResult.Failed("character_ability_activation_invalid");
            }

            // Lua handler 可能直接返回通用 Effect 子树（例如雷蛇的两个独立
            // PlaceInfluence）。激活节点必须等全部子节点进入普通终态后，再统一
            // 完成角色牌生命周期；这样不会依赖某个具体角色能力的 C# 收尾分支。
            if (context.Node.FlowStage == "awaiting_lua_effects")
            {
                if (HasNonTerminalChild(context))
                {
                    return EffectStepResult.NoProgress("角色牌 Lua 子 Effect 尚未结束。");
                }

                // 卡面是普通 Effect 式；某个可选条件被放弃或普通子 Effect 失败，不回滚整个角色牌。
                CompleteCharacterUse(context, player, cardId);
                var completed = EffectStepResult.Completed(NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "abilityId", Value = NormalizedValue.CreateString(abilityId) },
                    new NormalizedValueEntry { Name = "cardInstanceId", Value = NormalizedValue.CreateStableReference("character_card", cardId) },
                    new NormalizedValueEntry { Name = "mode", Value = NormalizedValue.CreateString(mode) },
                    new NormalizedValueEntry { Name = "effectOrder", Value = NormalizedValue.CreateString(effectOrder) }
                }));
                completed.AddEvent(CreateAbilityCompletedEvent(context, abilityId, cardId));
                return completed;
            }

            if (player.CoveredCharacterCardId != cardId || player.UsedCharacterThisRound)
            {
                return EffectStepResult.Failed("character_ability_activation_invalid");
            }

            var result = new EffectStepResult();
            result.WithFlowStage("awaiting_lua_effects");
            result.AddEvent(new EffectEventRequest
            {
                EventId = StableIdFactory.Create("event", context.Node.EffectId, CharacterEffectActivatedEventType),
                EventType = CharacterEffectActivatedEventType,
                SourceEffectId = context.Node.EffectId,
                OwnerNodeId = context.Node.EffectId,
                RouteKey = abilityId,
                PlayerId = context.Node.PlayerId,
                Visibility = GameStateVisibilityPolicy.Owner,
                ResponseKind = RuleEventResponseKind.Effects,
                DefinitionVersion = CharacterAbilityCatalog.CurrentDefinitionVersion,
                SemanticKey = abilityId,
                Payload = CreateActivationPayload(abilityId, cardId, mode, effectOrder, true),
                HostOnlyPayload = CreateActivationPayload(abilityId, cardId, mode, effectOrder, false)
            });
            return result;
        }

        private static EffectStepResult ExecuteSequence(EffectExecutionContext context)
        {
            PlayerState player = context.State.FindPlayer(context.Node.PlayerId);
            string cardId = ReadReference(context.Node.NormalizedArguments, "cardInstanceId");
            string firstAbilityId = ReadString(context.Node.NormalizedArguments, "firstAbilityId");
            string secondAbilityId = ReadString(context.Node.NormalizedArguments, "secondAbilityId");
            string effectOrder = ReadString(context.Node.NormalizedArguments, "effectOrder");
            if (player == null || string.IsNullOrEmpty(cardId) ||
                string.IsNullOrEmpty(firstAbilityId) || string.IsNullOrEmpty(secondAbilityId) ||
                !context.Registry.CharacterActivationSubscriptions.ContainsKey(firstAbilityId) ||
                !context.Registry.CharacterActivationSubscriptions.ContainsKey(secondAbilityId) ||
                player.CoveredCharacterCardId != cardId || player.UsedCharacterThisRound)
            {
                return EffectStepResult.Failed("character_ability_sequence_invalid");
            }

            if (context.Node.FlowStage == string.Empty)
            {
                return EffectStepResult.Continue("awaiting_first")
                    .AddChild(CreateActivationSpec(
                        cardId,
                        firstAbilityId,
                        CharacterEffectModes.Both,
                        effectOrder,
                        context.Node.PlayerId,
                        "sequence:first:" + firstAbilityId));
            }

            if (context.Node.FlowStage == "awaiting_first")
            {
                if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("第一项角色能力尚未结束。");
                if (HasFailedChild(context)) return EffectStepResult.Failed("character_ability_sequence_first_failed");
                return EffectStepResult.Continue("awaiting_second")
                    .AddChild(CreateActivationSpec(
                        cardId,
                        secondAbilityId,
                        CharacterEffectModes.Both,
                        effectOrder,
                        context.Node.PlayerId,
                        "sequence:second:" + secondAbilityId));
            }

            if (context.Node.FlowStage == "awaiting_second")
            {
                if (HasNonTerminalChild(context)) return EffectStepResult.NoProgress("第二项角色能力尚未结束。");
                if (HasFailedChild(context)) return EffectStepResult.Failed("character_ability_sequence_second_failed");
                CompleteCharacterUse(player, cardId);
                return EffectStepResult.Completed(ResultObject(
                    new NormalizedValueEntry { Name = "effectOrder", Value = NormalizedValue.CreateString(effectOrder) },
                    new NormalizedValueEntry { Name = "firstAbilityId", Value = NormalizedValue.CreateString(firstAbilityId) },
                    new NormalizedValueEntry { Name = "secondAbilityId", Value = NormalizedValue.CreateString(secondAbilityId) }));
            }

            return EffectStepResult.Failed("character_ability_sequence_state_invalid");
        }

        private static EffectStepResult ExecuteAbility(EffectExecutionContext context)
        {
            string abilityId = ReadString(context.Node.NormalizedArguments, "abilityId");
            CharacterAbilityDefinition definition;
            if (!CharacterAbilityCatalog.TryGet(abilityId, out definition))
            {
                throw new KernelException(
                    EffectFaultCodes.UnknownEffectType,
                    "未登记的角色能力：" + abilityId);
            }

            if (definition.Executor == null)
                throw new KernelException(EffectFaultCodes.InvalidEffectSpec,
                    "此能力已迁移为 Lua 组合，必须经角色激活事件调用：" + abilityId);
            return definition.Executor(context);
        }

        private static NormalizedValue ResultObject(params NormalizedValueEntry[] entries)
        {
            return NormalizedValue.CreateObject(
                entries == null ? new List<NormalizedValueEntry>() : new List<NormalizedValueEntry>(entries));
        }

        private static EffectEventRequest CreateAbilityCompletedEvent(
            EffectExecutionContext context,
            string abilityId,
            string cardId,
            params NormalizedValueEntry[] details)
        {
            var entries = new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "abilityId", Value = NormalizedValue.CreateString(abilityId ?? string.Empty) },
                new NormalizedValueEntry { Name = "cardInstanceRef", Value = NormalizedValue.CreateStableReference("character_card", cardId ?? string.Empty) }
            };
            if (details != null) entries.AddRange(details);
            return new EffectEventRequest
            {
                EventId = StableIdFactory.Create("event", context.Node.EffectId, CharacterEffectCompletedEventType),
                EventType = CharacterEffectCompletedEventType,
                SourceEffectId = context.Node.EffectId,
                OwnerNodeId = context.Node.EffectId,
                RouteKey = abilityId ?? string.Empty,
                PlayerId = context.Node.PlayerId,
                Visibility = GameStateVisibilityPolicy.Owner,
                ResponseKind = RuleEventResponseKind.None,
                DefinitionVersion = CharacterAbilityCatalog.CurrentDefinitionVersion,
                Payload = NormalizedValue.CreateObject(entries)
            };
        }

        private static bool HasNonTerminalChild(EffectExecutionContext context)
        {
            if (context == null || context.ChildNodes == null) return false;
            for (int i = 0; i < context.ChildNodes.Count; i++)
            {
                EffectNodeStatus status = context.ChildNodes[i].Status;
                if (status != EffectNodeStatus.Completed && status != EffectNodeStatus.Failed &&
                    status != EffectNodeStatus.Faulted)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasFailedChild(EffectExecutionContext context)
        {
            if (context == null || context.ChildNodes == null) return false;
            for (int i = 0; i < context.ChildNodes.Count; i++)
            {
                EffectNodeStatus status = context.ChildNodes[i].Status;
                if (status == EffectNodeStatus.Failed || status == EffectNodeStatus.Faulted) return true;
            }

            return false;
        }

        private static void CompleteCharacterUse(
            EffectExecutionContext context,
            PlayerState player,
            string cardId)
        {
            if (string.Equals(
                ReadString(context == null ? null : context.Node.NormalizedArguments, "mode"),
                CharacterEffectModes.Both,
                StringComparison.Ordinal))
            {
                return;
            }

            CompleteCharacterUse(player, cardId);
        }

        private static void CompleteCharacterUse(PlayerState player, string cardId)
        {
            while (player.HandCardIds.Remove(cardId))
            {
            }

            player.CoveredCharacterCardId = string.Empty;
            if (!player.DiscardCardIds.Contains(cardId)) player.DiscardCardIds.Add(cardId);
            player.UsedCharacterThisRound = true;
            player.UsedCharacterThisTurn = true;
        }

        private static NormalizedValue CreateActivationPayload(
            string abilityId,
            string cardId,
            string mode,
            string effectOrder,
            bool masked)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "abilityId", Value = NormalizedValue.CreateString(abilityId) },
                new NormalizedValueEntry
                {
                    Name = "cardInstanceRef",
                    Value = NormalizedValue.CreateStableReference("character_card", masked ? "hidden" : cardId)
                },
                new NormalizedValueEntry { Name = "mode", Value = NormalizedValue.CreateString(mode) },
                new NormalizedValueEntry { Name = "effectOrder", Value = NormalizedValue.CreateString(effectOrder) }
            });
        }

        private static string ReadReference(NormalizedValue value, string name)
        {
            NormalizedValue item = Find(value, name);
            if (item == null) return string.Empty;
            if (item.Kind == NormalizedValueKind.String) return item.StringValue ?? string.Empty;
            return item.Kind == NormalizedValueKind.StableReference ? item.ReferenceId ?? string.Empty : string.Empty;
        }

        private static string ReadString(NormalizedValue value, string name)
        {
            NormalizedValue item = Find(value, name);
            return item != null && item.Kind == NormalizedValueKind.String
                ? item.StringValue ?? string.Empty
                : string.Empty;
        }

        private static NormalizedValue Find(NormalizedValue value, string name)
        {
            if (value == null || value.Kind != NormalizedValueKind.Object || value.Properties == null) return null;
            for (int i = 0; i < value.Properties.Count; i++)
            {
                NormalizedValueEntry entry = value.Properties[i];
                if (entry != null && entry.Name == name) return entry.Value;
            }

            return null;
        }
    }
}
