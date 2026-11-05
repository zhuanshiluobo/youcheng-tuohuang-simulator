using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    /// <summary>
    /// 统一盖放节点中的本人子任务；父阶段选择新独立组或旧顺序组，并等待必要子树。
    /// </summary>
    public static partial class CharacterCoverEffectExecutor
    {
        public const string EffectTypeId = "character.cover.task";
        public const string InteractionTypeId = "character.cover.card";
        public const string RetiredV2Reason = "character_cover_v2_contract_retired";

        public static string ValidateArguments(NormalizedValue args)
        {
            var version = ItemTransferEffectExecutor.Value(args, "apiVersion");
            if (version?.Kind == NormalizedValueKind.Integer && version.IntegerValue == 2) return RetiredV2Reason;
            if (version?.Kind != NormalizedValueKind.Integer || version.IntegerValue != 3 ||
                CharacterLifecycleService.ParsePlayer(ItemTransferEffectExecutor.Text(args, "player")) < 1 || ItemTransferEffectExecutor.Text(args, "cardInstanceId").Length == 0 ||
                ItemTransferEffectExecutor.Text(args, "coverMode") != "additional" || ItemTransferEffectExecutor.Value(args, "blockedActionRoundSequence")?.Kind != NormalizedValueKind.Integer ||
                (ItemTransferEffectExecutor.Value(args,"blockedActionRoundSequence").IntegerValue < 0 || ItemTransferEffectExecutor.Value(args,"blockedActionRoundSequence").IntegerValue > int.MaxValue)) return "character_cover_arguments_invalid";
            var allowed = new[] { "kind", "effectTypeId", "apiVersion", "player", "cardInstanceId", "coverMode", "blockedActionRoundSequence", "acceptedTypeId", "reusable" };
            if (!new[] { "core.character_card", "core.generated_character_card" }.Contains(ItemTransferEffectExecutor.Text(args, "acceptedTypeId")) ||
                ItemTransferEffectExecutor.Value(args, "reusable")?.Kind != NormalizedValueKind.Boolean) return "character_cover_arguments_invalid";
            if (args.Properties.Any(entry => !allowed.Contains(entry.Name))) return "character_cover_arguments_invalid";
            return "";
        }

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (!registry.TryGet(EffectTypeId, out _))
            {
                registry.Register(new EffectRegistration(
                    EffectTypeId,
                    Execute,
                    EffectExecutorKind.IntrinsicFlow, SharedCoverDefinitionVersion) { ExecutionDoesNotRetainState = true, Validator = spec => ItemTransferEffectExecutor.Value(spec.NormalizedArguments,"apiVersion")==null ? "" : ValidateArguments(spec.NormalizedArguments) });
            }
        }

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var apiVersion = ItemTransferEffectExecutor.Value(context.Node.NormalizedArguments, "apiVersion");
            if (apiVersion != null)
            {
                string error = ValidateArguments(context.Node.NormalizedArguments);
                if (error.Length > 0) return EffectStepResult.Failed(error);
            }
            int playerId = ReadInteger(context.Node.NormalizedArguments, "playerId", context.Node.PlayerId);
            PlayerState player = context.State.FindPlayer(playerId);
            if (player == null)
            {
                return EffectStepResult.Failed("cover_player_missing");
            }
            var parentExecution = context.State.EffectRuntime.EffectNodes.Find(node => node.EffectId == context.Node.ParentEffectId);
            if (RoundExecutionService.IsParallelCoverExecution(parentExecution))
            {
                var main = context.State.EffectRuntime.MainNodes.Find(node => node.ExecutionEffectId == parentExecution.EffectId &&
                    node.NodeId == ReadString(context.Node.NormalizedArguments, "mainNodeId"));
                var group = parentExecution.ChildSchedulingGroups.Find(candidate => candidate.StableGroupKey == "character_cover_players");
                if (context.State.Phase != GamePhase.CharacterCover || main == null ||
                    main.NodeId != context.State.EffectRuntime.ActiveMainNodeId || playerId != context.Node.PlayerId ||
                    ReadString(parentExecution.NormalizedArguments, "mainNodeId") != main.NodeId ||
                    !parentExecution.ChildEffectIds.Contains(context.Node.EffectId) ||
                    group == null || !group.ChildEffectIds.Contains(context.Node.EffectId) ||
                    group.OwnerEffectId != parentExecution.EffectId || group.ProtocolVersion != 1 || group.Mode != EffectChildSchedulingMode.Independent ||
                    ItemTransferEffectExecutor.Number(context.Node.NormalizedArguments, "apiVersion") == 2)
                    return EffectStepResult.Failed("normal_cover_outside_mainline");
            }

            if (!ItemZoneLegacyProjection.IsActive(context.State)) return EffectStepResult.Failed("character_cover_item_state_required");
            context.Node.Visibility = GameStateVisibilityPolicy.Owner;
            if (apiVersion != null)
            {
                if (CharacterLifecycleService.ParsePlayer(ItemTransferEffectExecutor.Text(context.Node.NormalizedArguments, "player")) != context.Node.PlayerId)
                    return EffectStepResult.Failed("character_cover_arguments_invalid");
                return ExecuteSharedAdditional(context);
            }
            // 正常Host任务负责选择和回收流程；已确认盖放交给唯一共同移动实现。
            if (!MainCoverAuthority.TryGetTask(context.State, context.Node, true, out _)) return EffectStepResult.Failed("normal_cover_outside_mainline");
            return ExecuteSharedMain(context);
        }

        internal static void MarkCompletedPlayer(EffectExecutionContext context, int playerId)
        {
            if (string.IsNullOrEmpty(context.Node.ParentEffectId) ||
                context.State.EffectRuntime == null)
            {
                return;
            }

            EffectNodeRuntimeState parent = context.State.EffectRuntime.EffectNodes.Find(
                candidate => candidate != null && candidate.EffectId == context.Node.ParentEffectId);
            string mainNodeId = ReadString(parent == null ? null : parent.NormalizedArguments, "mainNodeId");
            MainlineNodeRuntimeState mainNode = context.State.EffectRuntime.MainNodes.Find(
                candidate => candidate != null && candidate.NodeId == mainNodeId);
            if (mainNode == null)
            {
                return;
            }

            if (mainNode.CompletedPlayerIds == null)
            {
                mainNode.CompletedPlayerIds = new List<int>();
            }

            if (!mainNode.CompletedPlayerIds.Contains(playerId))
            {
                mainNode.CompletedPlayerIds.Add(playerId);
            }
            if (RoundExecutionService.IsParallelCoverExecution(parent))
            {
                mainNode.ActiveTaskPlayerId = -1;
                return;
            }

            IList<int> order = context.State.EffectRuntime.PlayerOrderSnapshot;
            mainNode.ActiveTaskPlayerId = -1;
            if (order != null)
            {
                for (int i = 0; i < order.Count; i++)
                {
                    int candidateId = order[i];
                    if (!mainNode.CompletedPlayerIds.Contains(candidateId))
                    {
                        mainNode.ActiveTaskPlayerId = candidateId;
                        break;
                    }
                }
            }
        }

        private static bool AreAllPlayersCovered(GameState state)
        {
            if (state == null || state.EffectRuntime == null ||
                state.EffectRuntime.PlayerOrderSnapshot == null ||
                state.EffectRuntime.PlayerOrderSnapshot.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < state.EffectRuntime.PlayerOrderSnapshot.Count; i++)
            {
                PlayerState player = state.FindPlayer(state.EffectRuntime.PlayerOrderSnapshot[i]);
                if (player == null || string.IsNullOrEmpty(player.CoveredCharacterCardId))
                {
                    return false;
                }
            }

            return true;
        }

        private static NormalizedValue CreatePublicPayload(
            GameState state,
            int playerId,
            string cardId,
            bool allPlayersCovered)
        {
            int coverIndex = state.EffectRuntime.PlayerOrderSnapshot.IndexOf(playerId);
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "allPlayersCovered", Value = NormalizedValue.CreateBoolean(allPlayersCovered) },
                new NormalizedValueEntry { Name = "cardInstanceRef", Value = NormalizedValue.CreateStableReference("character_card", "hidden") },
                new NormalizedValueEntry { Name = "coverIndex", Value = NormalizedValue.CreateInteger(coverIndex < 0 ? 0 : coverIndex) },
                new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(playerId) }
            });
        }

        private static NormalizedValue CreatePrivatePayload(GameState state, int playerId, string cardId)
        {
            int coverIndex = state.EffectRuntime.PlayerOrderSnapshot.IndexOf(playerId);
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "cardInstanceRef", Value = NormalizedValue.CreateStableReference("character_card", cardId ?? string.Empty) },
                new NormalizedValueEntry { Name = "coverIndex", Value = NormalizedValue.CreateInteger(coverIndex < 0 ? 0 : coverIndex) }
            });
        }

        private static NormalizedValue CreateResult(int playerId, string cardId, bool allPlayersCovered)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "allPlayersCovered", Value = NormalizedValue.CreateBoolean(allPlayersCovered) },
                new NormalizedValueEntry { Name = "cardCovered", Value = NormalizedValue.CreateBoolean(!string.IsNullOrEmpty(cardId)) },
                new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(playerId) }
            });
        }

        private static int ReadInteger(NormalizedValue value, string name, int fallback)
        {
            NormalizedValue item = Find(value, name);
            return item != null && item.Kind == NormalizedValueKind.Integer
                ? (int)item.IntegerValue
                : fallback;
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
            if (value == null || value.Kind != NormalizedValueKind.Object || value.Properties == null)
            {
                return null;
            }

            for (int i = 0; i < value.Properties.Count; i++)
            {
                NormalizedValueEntry entry = value.Properties[i];
                if (entry != null && entry.Name == name) return entry.Value;
            }

            return null;
        }
    }
}
