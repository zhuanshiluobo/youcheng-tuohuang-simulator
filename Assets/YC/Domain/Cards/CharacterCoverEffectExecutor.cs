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
    public static class CharacterCoverEffectExecutor
    {
        public const string EffectTypeId = "character.cover.task";
        public const string InteractionTypeId = "character.cover.card";

        public static string ValidateArguments(NormalizedValue args)
        {
            int version = ItemTransferEffectExecutor.Number(args, "apiVersion");
            if ((version != 2 && version != 3) ||
                CharacterLifecycleService.ParsePlayer(ItemTransferEffectExecutor.Text(args, "player")) < 1 || ItemTransferEffectExecutor.Text(args, "cardInstanceId").Length == 0 ||
                ItemTransferEffectExecutor.Text(args, "coverMode") != "additional" || ItemTransferEffectExecutor.Value(args, "blockedActionRoundSequence")?.Kind != NormalizedValueKind.Integer ||
                ItemTransferEffectExecutor.Number(args, "blockedActionRoundSequence") < (version == 2 ? 1 : 0)) return "character_cover_arguments_invalid";
            var allowed = new[] { "kind", "effectTypeId", "apiVersion", "player", "cardInstanceId", "coverMode", "blockedActionRoundSequence" };
            if (version == 3)
            {
                allowed = allowed.Concat(new[] { "acceptedTypeId", "reusable" }).ToArray();
                if (!new[] { "core.character_card", "core.generated_character_card" }.Contains(ItemTransferEffectExecutor.Text(args, "acceptedTypeId")) ||
                    ItemTransferEffectExecutor.Value(args, "reusable")?.Kind != NormalizedValueKind.Boolean) return "character_cover_arguments_invalid";
            }
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

            if (YC.Domain.Items.ItemZoneLegacyProjection.IsActive(context.State))
            {
                context.Node.Visibility = GameStateVisibilityPolicy.Owner;
                var args = context.Node.NormalizedArguments;
                bool additional = ItemTransferEffectExecutor.Number(args, "apiVersion") >= 2;
                if (additional)
                {
                    if (ValidateArguments(args).Length > 0 || CharacterLifecycleService.ParsePlayer(ItemTransferEffectExecutor.Text(args, "player")) != context.Node.PlayerId)
                        return EffectStepResult.Failed("character_cover_arguments_invalid");
                    int blocked = ItemTransferEffectExecutor.Number(args, "blockedActionRoundSequence");
                    if (ItemTransferEffectExecutor.Number(args, "apiVersion") == 3)
                    {
                        string source = ContentTransactionEffectExecutor.BoundSource(context);
                        bool reusable = ItemTransferEffectExecutor.Flag(args, "reusable");
                        string accepted = ItemTransferEffectExecutor.Text(args, "acceptedTypeId");
                        if (blocked != (reusable ? 0 : CharacterLifecycleService.GetActionRoundSequence(context.State))) return EffectStepResult.Failed("character_cover_block_sequence_invalid");
                        string card = ItemTransferEffectExecutor.Text(args, "cardInstanceId");
                        if (reusable && !context.State.ItemZones.Items.Any(i => i.ItemId == card && context.State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId == "hand" && z.OwnerPlayerId == context.Node.PlayerId)))
                            return EffectStepResult.Failed("character_cover_source_invalid");
                        context.Node.SourceId = "character-card:" + source;
                        var committed = CharacterLifecycleService.Cover(context, card, true, blocked, accepted, reusable);
                        if (committed.PendingOutcome == EffectPendingOutcome.Failed || committed.PendingOutcome == EffectPendingOutcome.None) return committed;
                        return EffectStepResult.Completed(ItemTransferEffectExecutor.Object("coveredItemId", NormalizedValue.CreateString(card),
                            "coverSlotId", NormalizedValue.CreateString(context.State.CharacterCoverSlots.Single(slot => slot.CardInstanceId == card).SlotId)));
                    }

                    if (blocked != CharacterLifecycleService.GetActionRoundSequence(context.State)) return EffectStepResult.Failed("character_cover_block_sequence_invalid");
                    return CharacterLifecycleService.Cover(context, ItemTransferEffectExecutor.Text(args, "cardInstanceId"), true, blocked);
                }
                var state = context.State;
                var types = ItemTypeRegistry.CreateBuiltIn();
                var hand = ItemTransferEffectExecutor.FindZone(state, "hand", playerId);
                var covered = ItemTransferEffectExecutor.FindZone(state, "covered", playerId);
                if (state.ItemZones.Items.Exists(item => item.ZoneId == covered.ZoneId)) { MarkCompletedPlayer(context, playerId); return EffectStepResult.Completed(CreateResult(playerId, "", false)); }
                if (context.Node.FlowStage == "awaiting_card")
                {
                    string id = ItemTransferEffectExecutor.Answer(context);
                    var result = CharacterLifecycleService.Cover(context, id, false);
                    if (result.PendingOutcome == EffectPendingOutcome.Failed) return result;
                    MarkCompletedPlayer(context, playerId);
                    bool allCovered = AreAllPlayersCovered(state);
                    result.AddEvent(new EffectEventRequest { EventId = StableIdFactory.Create("event", context.Node.EffectId, "CharacterCardCovered", playerId.ToString(CultureInfo.InvariantCulture)),
                        EventType = "CharacterCardCovered", SourceEffectId = context.Node.EffectId, OwnerNodeId = context.Node.EffectId, PlayerId = playerId,
                        Visibility = GameStateVisibilityPolicy.Public, ResponseKind = RuleEventResponseKind.Effects, SemanticKey = playerId.ToString(CultureInfo.InvariantCulture),
                        Payload = CreatePublicPayload(state, playerId, id, allCovered), HostOnlyPayload = CreatePrivatePayload(state, playerId, id) });
                    return result;
                }
                if (context.Node.FlowStage.Length != 0) throw new InvalidOperationException("character_cover_stage_invalid");
                if (!state.ItemZones.Items.Exists(item => item.ZoneId == hand.ZoneId && types.IsAssignableTo(item.TypeId, "core.character_card")))
                {
                    var recycled = CharacterLifecycleService.Recycle(context, playerId, true, Array.Empty<string>());
                    if (recycled.PendingOutcome == EffectPendingOutcome.Failed) return recycled;
                }
                var request = new EffectInteractionSpec { InteractionTypeId = InteractionTypeId, AnsweringPlayerId = playerId, Visibility = "owner", PromptKey = "character.cover.choose", MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id" };
                request.CandidateIds.AddRange(state.ItemZones.Items.FindAll(item => item.ZoneId == hand.ZoneId && types.IsAssignableTo(item.TypeId, "core.character_card")).ConvertAll(item => item.ItemId));
                if (request.CandidateIds.Count == 0) return EffectStepResult.Failed("cover_no_character_card");
                return EffectStepResult.Continue("awaiting_card").AddInteraction(ItemTransferEffectExecutor.PrepareInteraction(context, request));
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
