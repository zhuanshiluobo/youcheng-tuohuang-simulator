using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    /// <summary>
    /// 资源和分数的最小通用 Effect。事件牌、移动和探索只生成这些稳定类型，
    /// 不再把“某张牌给什么奖励”写进命令处理器。
    /// </summary>
    public static class ResourceEffectTypeIds
    {
        public const string Gain = "effect.resource.gain";
        public const string GainOpponents = "effect.resource.gain_opponents";
        public const string Pay = "effect.resource.pay";
        public const string GainScore = "effect.score.gain";
    }

    public static class ResourceEffectSpecFactory
    {
        public static EffectSpec Gain(int playerId, ResourceType type, int amount, string sourceId = "")
        {
            return Create(ResourceEffectTypeIds.Gain, playerId, type, amount, sourceId);
        }

        public static EffectSpec Pay(int playerId, ResourceType type, int amount, string sourceId = "")
        {
            return Create(ResourceEffectTypeIds.Pay, playerId, type, amount, sourceId);
        }

        public static EffectSpec GainOpponents(int playerId, ResourceType type, int amount, string sourceId = "")
        {
            return Create(ResourceEffectTypeIds.GainOpponents, playerId, type, amount, sourceId);
        }

        public static EffectSpec GainScore(int playerId, int amount, string sourceId = "")
        {
            return new EffectSpec(
                ResourceEffectTypeIds.GainScore,
                CreateObject(
                    Entry("executingPlayer", NormalizedValue.CreateStableReference("player", playerId.ToString(CultureInfo.InvariantCulture))),
                    Entry("amount", NormalizedValue.CreateInteger(amount)),
                    Entry("sourceId", NormalizedValue.CreateString(sourceId ?? string.Empty))))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = ResourceEffectExecutor.DefinitionVersion
            };
        }

        private static EffectSpec Create(string typeId, int playerId, ResourceType type, int amount, string sourceId)
        {
            return new EffectSpec(
                typeId,
                CreateObject(
                    Entry("executingPlayer", NormalizedValue.CreateStableReference("player", playerId.ToString(CultureInfo.InvariantCulture))),
                    Entry("resourceType", NormalizedValue.CreateString(type.ToString())),
                    Entry("amount", NormalizedValue.CreateInteger(amount)),
                    Entry("sourceId", NormalizedValue.CreateString(sourceId ?? string.Empty))))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = ResourceEffectExecutor.DefinitionVersion
            };
        }

        private static NormalizedValue CreateObject(params NormalizedValueEntry[] entries)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>(entries));
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }
    }

    public sealed class ResourceEffectExecutor
    {
        public const string DefinitionVersion = "1.0.0";

        private readonly bool isScoreExecutor;
        private readonly bool isPaymentExecutor;
        private readonly bool targetsOpponents;

        private ResourceEffectExecutor(bool isPaymentExecutor, bool isScoreExecutor, bool targetsOpponents = false)
        {
            this.isPaymentExecutor = isPaymentExecutor;
            this.isScoreExecutor = isScoreExecutor;
            this.targetsOpponents = targetsOpponents;
        }

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            RegisterIfMissing(registry, ResourceEffectTypeIds.Gain, new ResourceEffectExecutor(false, false));
            RegisterIfMissing(registry, ResourceEffectTypeIds.GainOpponents, new ResourceEffectExecutor(false, false, true));
            RegisterIfMissing(registry, ResourceEffectTypeIds.Pay, new ResourceEffectExecutor(true, false));
            RegisterIfMissing(registry, ResourceEffectTypeIds.GainScore, new ResourceEffectExecutor(false, true));
        }

        private static void RegisterIfMissing(EffectRegistry registry, string typeId, ResourceEffectExecutor executor)
        {
            EffectRegistration ignored;
            if (registry.TryGet(typeId, out ignored)) return;
            registry.Register(new EffectRegistration(typeId, executor.Execute, EffectExecutorKind.Atomic, DefinitionVersion)
            {
                Validator = executor.ValidateSpec
            });
        }

        private EffectStepResult Execute(EffectExecutionContext context)
        {
            if (context.Node.PendingOutcome != EffectPendingOutcome.None)
            {
                return EffectStepResult.Completed(context.Node.NormalizedResult == null
                    ? NormalizedValue.CreateNull()
                    : context.Node.NormalizedResult.Clone());
            }

            int playerId;
            ResourceType resourceType = ResourceType.Originium;
            int amount;
            string diagnostic;
            bool parsed = isScoreExecutor
                ? TryReadScore(context.Node.NormalizedArguments, out playerId, out amount, out diagnostic)
                : TryRead(context.Node.NormalizedArguments, out playerId, out resourceType, out amount, out diagnostic);
            if (!parsed)
            {
                return EffectStepResult.Failed("invalid_arguments", NormalizedValue.CreateString(diagnostic));
            }

            PlayerState player = context.State.FindPlayer(playerId);
            if (player == null)
            {
                return EffectStepResult.Failed("invalid_player");
            }

            if (isScoreExecutor)
            {
                player.Score += amount;
                return EffectStepResult.Completed(Result(playerId, ResourceEffectTypeIds.GainScore, amount, resourceType));
            }

            if (targetsOpponents)
            {
                for (int i = 0; i < context.State.Players.Count; i++)
                {
                    PlayerState opponent = context.State.Players[i];
                    if (opponent == null || opponent.PlayerId == playerId) continue;
                    int currentOpponentAmount = opponent.Resources.Get(resourceType);
                    opponent.Resources.Set(resourceType, currentOpponentAmount + amount);
                }

                return EffectStepResult.Completed(Result(playerId, ResourceEffectTypeIds.GainOpponents, amount, resourceType));
            }

            int current = player.Resources.Get(resourceType);
            if (isPaymentExecutor)
            {
                if (current < amount)
                {
                    return EffectStepResult.Failed(
                        "insufficient_resource",
                        Result(playerId, ResourceEffectTypeIds.Pay, amount, resourceType));
                }

                player.Resources.Set(resourceType, current - amount);
                return EffectStepResult.Completed(Result(playerId, ResourceEffectTypeIds.Pay, amount, resourceType));
            }

            player.Resources.Set(resourceType, current + amount);
            return EffectStepResult.Completed(Result(playerId, ResourceEffectTypeIds.Gain, amount, resourceType));
        }

        private static bool TryRead(
            NormalizedValue args,
            out int playerId,
            out ResourceType resourceType,
            out int amount,
            out string diagnostic)
        {
            playerId = -1;
            resourceType = ResourceType.Originium;
            amount = 0;
            diagnostic = string.Empty;
            NormalizedValue player;
            NormalizedValue type;
            NormalizedValue value;
            if (!TryGet(args, "executingPlayer", out player) ||
                !TryGet(args, "resourceType", out type) ||
                !TryGet(args, "amount", out value) ||
                !TryReadPlayer(player, out playerId) ||
                type == null || type.Kind != NormalizedValueKind.String ||
                !Enum.TryParse(type.StringValue, true, out resourceType) ||
                value == null || value.Kind != NormalizedValueKind.Integer ||
                value.IntegerValue <= 0 || value.IntegerValue > int.MaxValue)
            {
                diagnostic = "资源 Effect 必须包含有效的 executingPlayer、resourceType 和正 amount。";
                return false;
            }

            amount = (int)value.IntegerValue;
            return true;
        }

        private string ValidateSpec(EffectSpec spec)
        {
            if (spec == null || spec.NormalizedArguments == null || spec.NormalizedArguments.Kind != NormalizedValueKind.Object)
            {
                return "资源 Effect 参数必须是对象。";
            }

            NormalizedValue player;
            NormalizedValue type;
            NormalizedValue amount;
            if (!TryGet(spec.NormalizedArguments, "executingPlayer", out player) ||
                !TryGet(spec.NormalizedArguments, "amount", out amount) ||
                (!isScoreExecutor && !TryGet(spec.NormalizedArguments, "resourceType", out type)))
            {
                return "资源 Effect 缺少必需字段。";
            }

            int ignoredPlayer;
            ResourceType ignoredType;
            int ignoredAmount;
            string diagnostic;
            bool valid = isScoreExecutor
                ? TryReadScore(spec.NormalizedArguments, out ignoredPlayer, out ignoredAmount, out diagnostic)
                : TryRead(spec.NormalizedArguments, out ignoredPlayer, out ignoredType, out ignoredAmount, out diagnostic);
            return valid
                ? string.Empty
                : diagnostic;
        }

        private static bool TryReadScore(
            NormalizedValue args,
            out int playerId,
            out int amount,
            out string diagnostic)
        {
            playerId = -1;
            amount = 0;
            diagnostic = string.Empty;
            NormalizedValue player;
            NormalizedValue value;
            if (!TryGet(args, "executingPlayer", out player) ||
                !TryGet(args, "amount", out value) ||
                !TryReadPlayer(player, out playerId) ||
                value == null || value.Kind != NormalizedValueKind.Integer ||
                value.IntegerValue <= 0 || value.IntegerValue > int.MaxValue)
            {
                diagnostic = "分数 Effect 必须包含有效的 executingPlayer 和正 amount。";
                return false;
            }

            amount = (int)value.IntegerValue;
            return true;
        }

        private static NormalizedValue Result(int playerId, string operation, int amount, ResourceType resourceType)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "amount", Value = NormalizedValue.CreateInteger(amount) },
                new NormalizedValueEntry { Name = "operation", Value = NormalizedValue.CreateString(operation) },
                new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(playerId) },
                new NormalizedValueEntry { Name = "resourceType", Value = NormalizedValue.CreateString(resourceType.ToString()) }
            });
        }

        private static bool TryReadPlayer(NormalizedValue value, out int playerId)
        {
            playerId = -1;
            if (value == null) return false;
            if (value.Kind == NormalizedValueKind.Integer)
            {
                playerId = (int)value.IntegerValue;
                return value.IntegerValue >= 0;
            }

            return value.Kind == NormalizedValueKind.StableReference &&
                   value.ReferenceType == "player" &&
                   TryParsePlayerReference(value.ReferenceId, out playerId) &&
                   playerId >= 0;
        }

        private static bool TryParsePlayerReference(string referenceId, out int playerId)
        {
            playerId = -1;
            string value = referenceId ?? string.Empty;
            if (value.StartsWith("p", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(1);
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out playerId);
        }

        private static bool TryGet(NormalizedValue value, string name, out NormalizedValue result)
        {
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
            {
                for (int i = 0; i < value.Properties.Count; i++)
                {
                    NormalizedValueEntry entry = value.Properties[i];
                    if (entry != null && entry.Name == name)
                    {
                        result = entry.Value;
                        return true;
                    }
                }
            }

            result = null;
            return false;
        }
    }
}
