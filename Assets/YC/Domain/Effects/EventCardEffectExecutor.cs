using System;
using System.Collections.Generic;
using System.Globalization;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static class EventCardEffectTypeIds
    {
        public const string Resolve = "effect.event_card.resolve";
    }

    public static class EventCardEffectEventTypeIds
    {
        public const string Revealed = "EventCardRevealed";
        public const string Resolved = "EventCardResolved";
    }

    public static class EventCardOptionIdFactory
    {
        public static string Create(string cardId, int optionIndex)
        {
            return (cardId ?? string.Empty) + ":option:" + optionIndex.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryRead(string candidateId, string cardId, int optionCount, out int optionIndex)
        {
            optionIndex = -1;
            string prefix = (cardId ?? string.Empty) + ":option:";
            if (string.IsNullOrEmpty(candidateId) || !candidateId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            return int.TryParse(
                       candidateId.Substring(prefix.Length),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out optionIndex) &&
                   optionIndex >= 0 && optionIndex < optionCount;
        }
    }

    public static class EventCardEffectSpecFactory
    {
        public static EffectSpec Resolve(
            int playerId,
            EventColor color,
            string targetLocationId,
            string primaryInfluenceSlotId = "",
            string sourceId = "")
        {
            var entries = new List<NormalizedValueEntry>
            {
                Entry("eventColor", NormalizedValue.CreateString(color.ToString())),
                Entry("targetLocation", NormalizedValue.CreateStableReference("location", targetLocationId ?? string.Empty)),
                Entry("primaryInfluenceSlot", string.IsNullOrEmpty(primaryInfluenceSlotId)
                    ? NormalizedValue.CreateNull()
                    : NormalizedValue.CreateStableReference("slot", primaryInfluenceSlotId))
            };
            return new EffectSpec(EventCardEffectTypeIds.Resolve, NormalizedValue.CreateObject(entries))
            {
                PlayerId = playerId,
                SourceId = sourceId ?? string.Empty,
                DefinitionVersion = EventCardEffectExecutor.DefinitionVersion
            };
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }
    }

    /// <summary>
    /// 事件牌的唯一运行时解析入口：抽牌、展示、选项交互和完成 Event 都是同一个可恢复节点。
    /// 牌 ID 仅用于目录查找和稳定候选/事件路由，不参与规则分支。
    /// </summary>
    public sealed class EventCardEffectExecutor
    {
        public const string DefinitionVersion = "1.0.0";
        public const string OptionInteractionTypeId = "event_card.option";
        public const string InfluenceInteractionTypeId = "event_card.influence_targets";

        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly EventDeckService eventDeckService;
        private readonly ResourceTokenService resourceTokenService;

        public EventCardEffectExecutor(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            this.eventDeckService = eventDeckService ?? throw new ArgumentNullException(nameof(eventDeckService));
            this.resourceTokenService = resourceTokenService ?? throw new ArgumentNullException(nameof(resourceTokenService));
        }

        public static void Register(
            EffectRegistry registry,
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            var executor = new EventCardEffectExecutor(mapQuery, influenceService, eventDeckService, resourceTokenService);
            EffectRegistration ignored;
            if (registry.TryGet(EventCardEffectTypeIds.Resolve, out ignored)) return;
            registry.Register(new EffectRegistration(
                EventCardEffectTypeIds.Resolve,
                executor.Execute,
                EffectExecutorKind.IntrinsicFlow,
                DefinitionVersion)
            {
                Validator = ValidateSpec
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

            string targetLocationId;
            EventColor color;
            string primaryInfluenceSlotId;
            string diagnostic;
            if (!TryReadArguments(
                    context.Node.NormalizedArguments,
                    out targetLocationId,
                    out color,
                    out primaryInfluenceSlotId,
                    out diagnostic))
            {
                return EffectStepResult.Failed("invalid_arguments", NormalizedValue.CreateString(diagnostic));
            }

            string stage = context.Node.FlowStage ?? string.Empty;
            if (string.IsNullOrEmpty(stage))
            {
                return Reveal(context, targetLocationId, color, primaryInfluenceSlotId);
            }

            string cardId = ReadString(context.Node.NormalizedResult, "cardId", string.Empty);
            EventCardDefinition card = EventCardDatabase.Get(cardId);
            if (card == null)
            {
                return EffectStepResult.Failed("card_definition_missing");
            }

            if (stage == "awaiting_option")
            {
                string optionId;
                int optionIndex;
                if (!TryReadSingleCandidate(context.GetLatestInteractionAnswer(), out optionId) ||
                    !EventCardOptionIdFactory.TryRead(optionId, card.CardId, card.ChoiceDescriptions.Count, out optionIndex))
                {
                    return EffectStepResult.Failed("invalid_option");
                }

                SetResult(context.Node, card, targetLocationId, optionIndex, optionId, primaryInfluenceSlotId, null);
                int requiredInfluenceCount = CountInfluenceTargets(card.ChoicePendingEffects[optionIndex]);
                if (requiredInfluenceCount <= 0)
                {
                    return Resolve(context, card, targetLocationId, optionIndex, optionId, primaryInfluenceSlotId, new List<string>());
                }

                var candidates = BuildInfluenceCandidates(
                    context.State,
                    context.Node.PlayerId,
                    targetLocationId,
                    card.ChoicePendingEffects[optionIndex],
                    primaryInfluenceSlotId);
                if (candidates.Count < requiredInfluenceCount)
                {
                    return EffectStepResult.Failed("insufficient_influence_targets");
                }

                var influenceInteraction = new EffectInteractionSpec
                {
                    InteractionTypeId = InfluenceInteractionTypeId,
                    Visibility = "public",
                    PromptKey = "event_card.choose_influence_targets",
                    AnsweringPlayerId = context.Node.PlayerId,
                    CandidateSetId = "event-card-influence:" + context.Node.EffectId,
                    CandidateSetVersion = 1,
                    MinSelections = requiredInfluenceCount,
                    MaxSelections = requiredInfluenceCount,
                    AnswerSchema = "candidate_ids"
                };
                influenceInteraction.CandidateIds.AddRange(candidates);
                return EffectStepResult.Continue("awaiting_influence")
                    .AddInteraction(influenceInteraction);
            }

            if (stage == "resolving_immediate")
            {
                return Resolve(
                    context,
                    card,
                    targetLocationId,
                    -1,
                    string.Empty,
                    primaryInfluenceSlotId,
                    new List<string>());
            }

            if (stage == "awaiting_influence")
            {
                var selectedSlots = ReadCandidates(context.GetLatestInteractionAnswer());
                int optionIndex = ReadInteger(context.Node.NormalizedResult, "selectedOptionIndex", -1);
                string optionId = ReadString(context.Node.NormalizedResult, "selectedOptionId", string.Empty);
                if (optionIndex < 0 || optionIndex >= card.ChoicePendingEffects.Count)
                {
                    return EffectStepResult.Failed("invalid_option");
                }

                int requiredInfluenceCount = CountInfluenceTargets(card.ChoicePendingEffects[optionIndex]);
                if (selectedSlots.Count != requiredInfluenceCount)
                {
                    return EffectStepResult.Failed("invalid_influence_targets");
                }

                SetResult(context.Node, card, targetLocationId, optionIndex, optionId, primaryInfluenceSlotId, selectedSlots);
                return Resolve(context, card, targetLocationId, optionIndex, optionId, primaryInfluenceSlotId, selectedSlots);
            }

            return EffectStepResult.Failed("invalid_flow_stage");
        }

        private EffectStepResult Reveal(
            EffectExecutionContext context,
            string targetLocationId,
            EventColor color,
            string primaryInfluenceSlotId)
        {
            PlayerState player = context.State.FindPlayer(context.Node.PlayerId);
            if (player == null)
            {
                return EffectStepResult.Failed("invalid_player");
            }

            try
            {
                mapQuery.GetLocation(targetLocationId);
            }
            catch (ArgumentException)
            {
                return EffectStepResult.Failed("unknown_location");
            }

            if (resourceTokenService.HasResourceToken(context.State.Map, targetLocationId))
            {
                var skipped = ResultObject(
                    "cardId", string.Empty,
                    "outcome", "skipped_existing_resource",
                    "targetLocationId", targetLocationId);
                return EffectStepResult.Completed(skipped);
            }

            if (eventDeckService.RemainingCount(context.State.Decks, color) <= 0)
            {
                return EffectStepResult.Failed("event_deck_empty");
            }

            string cardId = eventDeckService.Draw(context.State.Decks, color);
            EventCardDefinition card = EventCardDatabase.Get(cardId);
            if (card == null)
            {
                return EffectStepResult.Failed("card_definition_missing");
            }

            resourceTokenService.PlaceToken(
                context.State.Map,
                targetLocationId,
                card.RepresentativeResourceType,
                card.RepresentativeResourceAmount);
            if (context.State.Map.OpenLocationIds == null)
            {
                context.State.Map.OpenLocationIds = new List<string>();
            }
            if (!context.State.Map.OpenLocationIds.Contains(targetLocationId))
            {
                context.State.Map.OpenLocationIds.Add(targetLocationId);
            }

            SetResult(context.Node, card, targetLocationId, -1, string.Empty, primaryInfluenceSlotId, null);
            bool hasChoiceInteraction = card.ChoiceDescriptions.Count > 0;
            var step = EffectStepResult.Continue(hasChoiceInteraction ? "awaiting_option" : "resolving_immediate");
            step.AddEvent(new EffectEventRequest
            {
                EventId = StableIdFactory.Create("event", context.Node.EffectId, EventCardEffectEventTypeIds.Revealed, card.CardId),
                EventType = EventCardEffectEventTypeIds.Revealed,
                SourceEffectId = context.Node.EffectId,
                OwnerNodeId = context.Node.EffectId,
                RouteKey = card.CardId,
                TargetEntityId = targetLocationId,
                PlayerId = context.Node.PlayerId,
                Payload = CreateRevealPayload(card, targetLocationId),
                ResponseKind = RuleEventResponseKind.Effects,
                DefinitionVersion = DefinitionVersion,
                Visibility = "public",
                SemanticKey = "reveal"
            });
            if (hasChoiceInteraction)
            {
                step.AddInteraction(CreateOptionInteraction(context.Node, card));
            }
            return step;
        }

        private static EffectStepResult Resolve(
            EffectExecutionContext context,
            EventCardDefinition card,
            string targetLocationId,
            int optionIndex,
            string optionId,
            string primaryInfluenceSlotId,
            IReadOnlyList<string> selectedSlots)
        {
            var step = EffectStepResult.Completed(ResultObject(
                "cardId", card.CardId,
                "outcome", "resolved",
                "selectedOptionId", optionId,
                "selectedOptionIndex", optionIndex.ToString(CultureInfo.InvariantCulture),
                "targetLocationId", targetLocationId));
            step.AddEvent(new EffectEventRequest
            {
                EventId = StableIdFactory.Create("event", context.Node.EffectId, EventCardEffectEventTypeIds.Resolved, card.CardId, optionId),
                EventType = EventCardEffectEventTypeIds.Resolved,
                SourceEffectId = context.Node.EffectId,
                OwnerNodeId = context.Node.EffectId,
                RouteKey = card.CardId,
                TargetEntityId = targetLocationId,
                PlayerId = context.Node.PlayerId,
                Payload = CreateResolvedPayload(card, targetLocationId, optionIndex, optionId, primaryInfluenceSlotId, selectedSlots),
                ResponseKind = RuleEventResponseKind.Effects,
                DefinitionVersion = DefinitionVersion,
                Visibility = "public",
                SemanticKey = "resolve"
            });
            return step;
        }

        private static EffectInteractionSpec CreateOptionInteraction(EffectNodeRuntimeState node, EventCardDefinition card)
        {
            var candidates = new List<string>();
            for (int i = 0; i < card.ChoiceDescriptions.Count; i++)
            {
                candidates.Add(EventCardOptionIdFactory.Create(card.CardId, i));
            }

            var interaction = new EffectInteractionSpec
            {
                InteractionTypeId = OptionInteractionTypeId,
                Visibility = "public",
                PromptKey = "event_card.choose_option",
                PromptParameters = ResultObject("cardId", card.CardId, "cardName", card.Name, "cardDescription", card.Description),
                AnsweringPlayerId = node.PlayerId,
                CandidateSetId = "event-card-options:" + node.EffectId,
                CandidateSetVersion = 1,
                MinSelections = 1,
                MaxSelections = 1,
                AnswerSchema = "candidate_id"
            };
            interaction.CandidateIds.AddRange(candidates);
            return interaction;
        }

        private List<string> BuildInfluenceCandidates(
            GameState state,
            int playerId,
            string originLocationId,
            IReadOnlyList<EventEffect> effects,
            string reservedPrimarySlotId)
        {
            int required = CountInfluenceTargets(effects);
            var result = new List<string>();
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(reservedPrimarySlotId)) reserved.Add(reservedPrimarySlotId);

            for (int locationIndex = 0; locationIndex < mapQuery.Map.Locations.Count; locationIndex++)
            {
                MapLocationDefinition location = mapQuery.Map.Locations[locationIndex];
                for (int slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    AddCandidate(state, playerId, originLocationId, effects, reserved,
                        InfluenceService.GetLocationSlotId(location.LocationId, slotIndex), result);
                }
            }
            for (int routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
            {
                MapRouteDefinition route = mapQuery.Map.Routes[routeIndex];
                for (int slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    AddCandidate(state, playerId, originLocationId, effects, reserved,
                        InfluenceService.GetRouteSlotId(route.RouteId, slotIndex), result);
                }
            }

            result.Sort(StringComparer.Ordinal);
            if (result.Count > 64) result.RemoveRange(64, result.Count - 64);
            return result;
        }

        private void AddCandidate(
            GameState state,
            int playerId,
            string originLocationId,
            IReadOnlyList<EventEffect> effects,
            HashSet<string> reserved,
            string slotId,
            List<string> result)
        {
            if (reserved.Contains(slotId) || FindInfluenceAtSlot(state, slotId) != null) return;
            InfluenceSlotReference slot;
            string reason;
            if (!InfluenceSlotReference.TryParse(mapQuery, slotId, out slot, out reason)) return;
            if (!IsInAnyScope(effects, originLocationId, slot)) return;
            if (slot.Kind == InfluenceSlotKind.Location)
            {
                if (!resourceTokenService.HasResourceToken(state.Map, slot.LocationId) && slot.LocationId != originLocationId) return;
                if (HasOpponentCityAtLocation(state, playerId, slot.LocationId)) return;
            }
            else
            {
                for (int i = 0; i < state.Map.RoadRouteIds.Count; i++)
                {
                    if (state.Map.RoadRouteIds[i] == slot.RouteId) return;
                }
            }

            if (influenceService.CanPlace(state, playerId, slotId).IsValid)
            {
                result.Add(slotId);
            }
        }

        private bool IsInAnyScope(IReadOnlyList<EventEffect> effects, string originLocationId, InfluenceSlotReference slot)
        {
            bool hasInfluenceEffect = false;
            for (int i = 0; i < effects.Count; i++)
            {
                EventEffect effect = effects[i];
                if (effect == null || effect.Kind != EventEffectKind.PlaceInfluence || effect.Amount <= 0) continue;
                hasInfluenceEffect = true;
                if (effect.TargetScope == EventEffectTargetScope.None ||
                    effect.TargetScope == EventEffectTargetScope.CurrentLocationOrAdjacentRoute &&
                    ((slot.Kind == InfluenceSlotKind.Location && slot.LocationId == originLocationId) || RouteCoversLocation(slot.RouteId, originLocationId)) ||
                    effect.TargetScope == EventEffectTargetScope.AdjacentRoute &&
                    slot.Kind == InfluenceSlotKind.Route && RouteCoversLocation(slot.RouteId, originLocationId))
                {
                    return true;
                }
            }

            return !hasInfluenceEffect;
        }

        private bool RouteCoversLocation(string routeId, string locationId)
        {
            MapRouteDefinition route;
            try { route = mapQuery.GetRoute(routeId); }
            catch (ArgumentException) { return false; }
            return (route.CoveredLocationIds != null && route.CoveredLocationIds.Contains(locationId)) ||
                   route.FromLocationId == locationId || route.ToLocationId == locationId;
        }

        private static int CountInfluenceTargets(IReadOnlyList<EventEffect> effects)
        {
            int result = 0;
            if (effects == null) return result;
            for (int i = 0; i < effects.Count; i++)
            {
                if (effects[i] != null && effects[i].Kind == EventEffectKind.PlaceInfluence)
                {
                    result += Math.Max(0, effects[i].Amount);
                }
            }
            return result;
        }

        private static NormalizedValue CreateRevealPayload(EventCardDefinition card, string targetLocationId)
        {
            return ResultObjectWithValues(
                "cardDescription", card.Description,
                "cardId", card.CardId,
                "cardName", card.Name,
                "eventColor", card.Color.ToString(),
                "targetLocationId", targetLocationId,
                "optionIds", CreateOptionIds(card));
        }

        private static NormalizedValue CreateResolvedPayload(
            EventCardDefinition card,
            string targetLocationId,
            int optionIndex,
            string optionId,
            string primaryInfluenceSlotId,
            IReadOnlyList<string> selectedSlots)
        {
            return ResultObjectWithValues(
                "cardId", card.CardId,
                "eventColor", card.Color.ToString(),
                "optionId", optionId,
                "optionIndex", optionIndex.ToString(CultureInfo.InvariantCulture),
                "primaryInfluenceSlotId", string.IsNullOrEmpty(primaryInfluenceSlotId) ? string.Empty : primaryInfluenceSlotId,
                "selectedInfluenceSlotIds", CreateStringArray(selectedSlots),
                "targetLocationId", targetLocationId);
        }

        private static NormalizedValue CreateOptionIds(EventCardDefinition card)
        {
            var ids = new List<NormalizedValue>();
            for (int i = 0; i < card.ChoiceDescriptions.Count; i++)
            {
                ids.Add(NormalizedValue.CreateStableReference("option", EventCardOptionIdFactory.Create(card.CardId, i)));
            }
            return NormalizedValue.CreateArray(ids);
        }

        private static NormalizedValue CreateStringArray(IReadOnlyList<string> values)
        {
            var items = new List<NormalizedValue>();
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++) items.Add(NormalizedValue.CreateStableReference("slot", values[i]));
            }
            return NormalizedValue.CreateArray(items);
        }

        private static NormalizedValue ResultObject(params string[] values)
        {
            var entries = new List<NormalizedValueEntry>();
            for (int i = 0; i + 1 < values.Length; i += 2)
            {
                entries.Add(new NormalizedValueEntry
                {
                    Name = values[i],
                    Value = NormalizedValue.CreateString(values[i + 1] ?? string.Empty)
                });
            }
            return NormalizedValue.CreateObject(entries);
        }

        private static NormalizedValue ResultObjectWithValues(params object[] values)
        {
            var entries = new List<NormalizedValueEntry>();
            for (int i = 0; i + 1 < values.Length; i += 2)
            {
                object raw = values[i + 1];
                entries.Add(new NormalizedValueEntry
                {
                    Name = values[i] as string ?? string.Empty,
                    Value = raw as NormalizedValue ?? NormalizedValue.CreateString(raw == null ? string.Empty : raw.ToString())
                });
            }
            return NormalizedValue.CreateObject(entries);
        }

        private static void SetResult(
            EffectNodeRuntimeState node,
            EventCardDefinition card,
            string targetLocationId,
            int optionIndex,
            string optionId,
            string primaryInfluenceSlotId,
            IReadOnlyList<string> selectedSlots)
        {
            var entries = new List<NormalizedValueEntry>
            {
                Entry("cardId", NormalizedValue.CreateString(card.CardId)),
                Entry("eventColor", NormalizedValue.CreateString(card.Color.ToString())),
                Entry("selectedOptionId", NormalizedValue.CreateString(optionId ?? string.Empty)),
                Entry("selectedOptionIndex", NormalizedValue.CreateInteger(optionIndex)),
                Entry("targetLocationId", NormalizedValue.CreateString(targetLocationId)),
                Entry("primaryInfluenceSlotId", NormalizedValue.CreateString(primaryInfluenceSlotId ?? string.Empty)),
                Entry("selectedInfluenceSlotIds", CreateStringArray(selectedSlots))
            };
            node.NormalizedResult = NormalizedValue.CreateObject(entries);
        }

        private static NormalizedValueEntry Entry(string name, NormalizedValue value)
        {
            return new NormalizedValueEntry { Name = name, Value = value };
        }

        private static bool TryReadArguments(
            NormalizedValue args,
            out string targetLocationId,
            out EventColor color,
            out string primaryInfluenceSlotId,
            out string diagnostic)
        {
            targetLocationId = string.Empty;
            color = EventColor.Green;
            primaryInfluenceSlotId = string.Empty;
            diagnostic = string.Empty;
            NormalizedValue target;
            NormalizedValue eventColor;
            NormalizedValue primary;
            if (!TryGet(args, "targetLocation", out target) ||
                target == null || target.Kind != NormalizedValueKind.StableReference || target.ReferenceType != "location" ||
                string.IsNullOrEmpty(target.ReferenceId) ||
                !TryGet(args, "eventColor", out eventColor) ||
                eventColor == null || eventColor.Kind != NormalizedValueKind.String ||
                !Enum.TryParse(eventColor.StringValue, true, out color))
            {
                diagnostic = "事件牌 Effect 缺少有效的 targetLocation 或 eventColor。";
                return false;
            }
            targetLocationId = target.ReferenceId;
            if (TryGet(args, "primaryInfluenceSlot", out primary) && primary != null && primary.Kind == NormalizedValueKind.StableReference && primary.ReferenceType == "slot")
            {
                primaryInfluenceSlotId = primary.ReferenceId;
            }
            return true;
        }

        private static string ValidateSpec(EffectSpec spec)
        {
            if (spec == null || spec.NormalizedArguments == null || spec.NormalizedArguments.Kind != NormalizedValueKind.Object)
            {
                return "事件牌 Effect 参数必须是对象。";
            }
            string target;
            EventColor color;
            string primary;
            string diagnostic;
            return TryReadArguments(spec.NormalizedArguments, out target, out color, out primary, out diagnostic) ? string.Empty : diagnostic;
        }

        private static bool TryReadSingleCandidate(NormalizedValue answer, out string candidateId)
        {
            candidateId = string.Empty;
            if (answer == null) return false;
            if (answer.Kind == NormalizedValueKind.Array)
            {
                return answer.Items != null && answer.Items.Count == 1 && TryReadCandidate(answer.Items[0], out candidateId);
            }
            return TryReadCandidate(answer, out candidateId);
        }

        private static List<string> ReadCandidates(NormalizedValue answer)
        {
            var result = new List<string>();
            if (answer == null) return result;
            if (answer.Kind == NormalizedValueKind.Array)
            {
                if (answer.Items == null) return result;
                for (int i = 0; i < answer.Items.Count; i++)
                {
                    string id;
                    if (TryReadCandidate(answer.Items[i], out id) && !result.Contains(id)) result.Add(id);
                }
            }
            else
            {
                string id;
                if (TryReadCandidate(answer, out id)) result.Add(id);
            }
            return result;
        }

        private static bool TryReadCandidate(NormalizedValue value, out string candidateId)
        {
            candidateId = string.Empty;
            if (value == null) return false;
            if (value.Kind == NormalizedValueKind.String)
            {
                candidateId = value.StringValue;
                return !string.IsNullOrEmpty(candidateId);
            }
            if (value.Kind == NormalizedValueKind.StableReference &&
                (value.ReferenceType == "candidate" || value.ReferenceType == "option" || value.ReferenceType == "slot"))
            {
                candidateId = value.ReferenceId;
                return !string.IsNullOrEmpty(candidateId);
            }
            return false;
        }

        private static string ReadString(NormalizedValue value, string name, string fallback)
        {
            NormalizedValue candidate;
            return TryGet(value, name, out candidate) && candidate != null && candidate.Kind == NormalizedValueKind.String
                ? candidate.StringValue
                : fallback;
        }

        private static int ReadInteger(NormalizedValue value, string name, int fallback)
        {
            NormalizedValue candidate;
            return TryGet(value, name, out candidate) && candidate != null && candidate.Kind == NormalizedValueKind.Integer
                ? (int)candidate.IntegerValue
                : fallback;
        }

        private static bool TryGet(NormalizedValue value, string name, out NormalizedValue result)
        {
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
            {
                for (int i = 0; i < value.Properties.Count; i++)
                {
                    if (value.Properties[i] != null && value.Properties[i].Name == name)
                    {
                        result = value.Properties[i].Value;
                        return true;
                    }
                }
            }
            result = null;
            return false;
        }

        private static InfluencePlacement FindInfluenceAtSlot(GameState state, string slotId)
        {
            for (int i = 0; i < state.Map.Influences.Count; i++)
            {
                InfluencePlacement placement = state.Map.Influences[i];
                if (placement != null && placement.SlotId == slotId) return placement;
            }
            return null;
        }

        private static bool HasOpponentCityAtLocation(GameState state, int playerId, string locationId)
        {
            for (int i = 0; i < state.Players.Count; i++)
            {
                PlayerState player = state.Players[i];
                if (player != null && player.PlayerId != playerId && player.CityLocationId == locationId) return true;
            }
            return false;
        }
    }
}
