using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    /// <summary>
    /// 统一盖放节点中的单人顺序子任务。它只负责一个玩家的牌区迁移、交互和生命周期事件；
    /// 父主节点负责按固定顺序挂载这些任务并等待全部任务进入终态。
    /// </summary>
    public static class CharacterCoverEffectExecutor
    {
        public const string EffectTypeId = "character.cover.task";
        public const string InteractionTypeId = "character.cover.card";

        public static void Register(EffectRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (!registry.TryGet(EffectTypeId, out _))
            {
                registry.Register(new EffectRegistration(
                    EffectTypeId,
                    Execute,
                    EffectExecutorKind.IntrinsicFlow));
            }
        }

        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            int playerId = ReadInteger(context.Node.NormalizedArguments, "playerId", context.Node.PlayerId);
            PlayerState player = context.State.FindPlayer(playerId);
            if (player == null)
            {
                return EffectStepResult.Failed("cover_player_missing");
            }

            if (!string.IsNullOrEmpty(player.CoveredCharacterCardId))
            {
                MarkCompletedPlayer(context, playerId);
                return EffectStepResult.Completed(CreateResult(playerId, string.Empty, false));
            }

            if (context.Node.FlowStage == "awaiting_card")
            {
                string cardId = ReadAnswer(context.GetLatestInteractionAnswer());
                if (string.IsNullOrEmpty(cardId))
                {
                    return EffectStepResult.Failed("cover_answer_missing");
                }

                if (!player.HandCardIds.Contains(cardId))
                {
                    return EffectStepResult.Failed("cover_card_not_in_hand");
                }

                if (CharacterCardDatabase.Get(cardId) == null)
                {
                    return EffectStepResult.Failed("cover_card_definition_missing");
                }

                player.HandCardIds.Remove(cardId);
                player.CoveredCharacterCardId = cardId;
                MarkCompletedPlayer(context, playerId);

                bool allPlayersCovered = AreAllPlayersCovered(context.State);
                var result = EffectStepResult.Completed(CreateResult(playerId, cardId, allPlayersCovered));
                result.AddEvent(new EffectEventRequest
                {
                    EventId = StableIdFactory.Create(
                        "event",
                        context.Node.EffectId,
                        "CharacterCardCovered",
                        playerId.ToString(CultureInfo.InvariantCulture)),
                    EventType = "CharacterCardCovered",
                    SourceEffectId = context.Node.EffectId,
                    OwnerNodeId = context.Node.EffectId,
                    PlayerId = playerId,
                    Visibility = GameStateVisibilityPolicy.Public,
                    ResponseKind = RuleEventResponseKind.Effects,
                    SemanticKey = playerId.ToString(CultureInfo.InvariantCulture),
                    Payload = CreatePublicPayload(context.State, playerId, cardId, allPlayersCovered),
                    HostOnlyPayload = CreatePrivatePayload(context.State, playerId, cardId)
                });
                return result;
            }

            if (player.HandCardIds == null)
            {
                player.HandCardIds = new List<string>();
            }

            // 牌区迁移与交互请求在同一工作副本中提交。恢复后已回答的请求会通过
            // FlowStage 直接进入答案分支，不会再次要求盖放。
            if (player.HandCardIds.Count == 0 && player.DiscardCardIds != null && player.DiscardCardIds.Count > 0)
            {
                player.HandCardIds.AddRange(player.DiscardCardIds);
                player.DiscardCardIds.Clear();
            }

            if (player.HandCardIds.Count == 0)
            {
                return EffectStepResult.Failed("cover_no_character_card");
            }

            var waiting = new EffectStepResult();
            waiting.AddInteraction(new EffectInteractionSpec
            {
                InteractionTypeId = InteractionTypeId,
                Visibility = GameStateVisibilityPolicy.Owner,
                PromptKey = "character.cover.choose",
                AnsweringPlayerId = playerId,
                MinSelections = 1,
                MaxSelections = 1,
                AnswerSchema = "candidate_id"
            });
            for (int i = 0; i < player.HandCardIds.Count; i++)
            {
                string cardId = player.HandCardIds[i];
                if (!string.IsNullOrEmpty(cardId))
                {
                    waiting.Interactions[0].CandidateIds.Add(cardId);
                }
            }

            waiting.WithFlowStage("awaiting_card");
            return waiting;
        }

        private static void MarkCompletedPlayer(EffectExecutionContext context, int playerId)
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

        private static string ReadAnswer(NormalizedValue value)
        {
            if (value == null) return string.Empty;
            if (value.Kind == NormalizedValueKind.String) return value.StringValue ?? string.Empty;
            if (value.Kind == NormalizedValueKind.StableReference &&
                (value.ReferenceType == "candidate" || value.ReferenceType == "character_card"))
            {
                return value.ReferenceId ?? string.Empty;
            }

            if (value.Kind == NormalizedValueKind.Array && value.Items != null && value.Items.Count == 1)
            {
                return ReadAnswer(value.Items[0]);
            }

            return string.Empty;
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
