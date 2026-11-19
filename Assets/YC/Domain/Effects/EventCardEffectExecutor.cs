using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Items;
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

            if (!int.TryParse(
                       candidateId.Substring(prefix.Length),
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out int parsed) || parsed < 0 || parsed >= optionCount ||
                !string.Equals(candidateId, Create(cardId, parsed), StringComparison.Ordinal)) return false;
            optionIndex = parsed;
            return true;
        }

        // 供新协议装配时使用；旧候选仍由上面的接口处理，不能按旧索引猜测新身份。
        public static string CreateChoice(string cardId, string choiceId)
        {
            if (!IsValidChoiceIdentity(cardId) || !IsValidChoiceIdentity(choiceId))
                throw new ArgumentException("事件定义 ID 与选择 ID 必须是有效的稳定身份。");
            return StableIdFactory.Create("event-choice", cardId, choiceId);
        }

        public static bool TryReadChoice(string candidateId, string cardId, IReadOnlyList<string> choiceIds, out string choiceId)
        {
            choiceId = string.Empty;
            if (string.IsNullOrEmpty(candidateId) || !IsValidChoiceIdentity(cardId) || choiceIds == null) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string match = null;
            foreach (var id in choiceIds)
            {
                if (!IsValidChoiceIdentity(id) || !seen.Add(id)) return false;
                if (string.Equals(candidateId, CreateChoice(cardId, id), StringComparison.Ordinal)) match = id;
            }
            if (match == null) return false;
            choiceId = match;
            return true;
        }

        private static bool IsValidChoiceIdentity(string value) => !string.IsNullOrWhiteSpace(value) &&
            value.Length <= 128 && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
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
        public const string DefinitionVersion = "2.0.0";
        public const string OptionInteractionTypeId = "event_card.option";
        public const string InfluenceInteractionTypeId = "event_card.influence_targets";

        /// <summary>恢复时只核验持久身份，不按旧数字选项重建未完成节点。</summary>
        public static bool TryValidateSaved(GameState state, EffectNodeRuntimeState node, out string diagnostic)
        {
            diagnostic = string.Empty;
            if (node == null || node.EffectTypeId != EventCardEffectTypeIds.Resolve ||
                node.Status == EffectNodeStatus.Completed || node.Status == EffectNodeStatus.Failed) return true;
            if (node.DefinitionVersion != DefinitionVersion)
            {
                diagnostic = "event_card_restore_version_unsupported：未完成的事件牌使用旧执行版本，请使用原内容和原版本恢复。";
                return false;
            }
            if (string.IsNullOrEmpty(node.FlowStage)) return true;
            string outcome = ReadString(node.NormalizedResult, "outcome", "");
            if (outcome == "skipped_existing_resource") return true;
            string cardId = ReadString(node.NormalizedResult, "cardId", "");
            var card = EventCardDatabase.Get(state, cardId);
            if (card == null) { diagnostic = "event_card_restore_definition_missing：事件定义不在原局内容快照中。"; return false; }
            if (card.EventChoiceSchemaVersion != 1)
            {
                diagnostic = "event_card_restore_schema_unsupported：旧事件选择格式不能自动恢复，请保留原存档并使用原版本。";
                return false;
            }
            if (card.Choices == null) { diagnostic = "event_card_restore_choices_missing"; return false; }
            if (node.PendingOutcome == EffectPendingOutcome.None && node.FlowStage == "awaiting_option")
            {
                var requests = state.EffectRuntime.InteractionRequests.Where(request => request != null &&
                    request.OwnerEffectId == node.EffectId && request.InteractionTypeId == OptionInteractionTypeId &&
                    (request.Status == "open" || request.Status == "answered")).ToList();
                if (requests.Count != 1 || requests[0].CandidateSetVersion != 2 || requests[0].CandidateIds == null ||
                    !requests[0].CandidateIds.SequenceEqual(card.Choices.Select(choice => EventCardOptionIdFactory.CreateChoice(card.CardId, choice.ChoiceId))))
                { diagnostic = "event_card_restore_candidates_mismatch：事件选择与原局内容快照不一致。"; return false; }
                if (requests[0].Status == "answered" && (!TryReadSingleCandidate(requests[0].NormalizedAnswer, out string answer) ||
                    !requests[0].CandidateIds.Contains(answer)))
                { diagnostic = "event_card_restore_answer_mismatch：已保存的事件选择无效。"; return false; }
                return true;
            }
            if (node.PendingOutcome == EffectPendingOutcome.None && node.FlowStage == "resolving_immediate")
            {
                if (card.Choices.Count == 0 && card.ImmediateResolve != null) return true;
                diagnostic = "event_card_restore_immediate_mismatch"; return false;
            }
            if (node.PendingOutcome == EffectPendingOutcome.None && node.FlowStage == "resolving_choice")
            {
                string choiceId = ReadString(node.NormalizedResult, "selectedChoiceId", "");
                string optionId = ReadString(node.NormalizedResult, "selectedOptionId", "");
                if (card.Choices.Count == 0 ? card.ImmediateResolve != null && choiceId == "" && optionId == "" :
                    EventCardOptionIdFactory.TryReadChoice(optionId, card.CardId, card.Choices.Select(choice => choice.ChoiceId).ToList(), out var selected) && selected == choiceId)
                    return true;
                diagnostic = "event_card_restore_choice_mismatch：进行中的选择不属于原事件定义。"; return false;
            }
            if (outcome == "resolved")
            {
                string choiceId = ReadString(node.NormalizedResult, "selectedChoiceId", "");
                string optionId = ReadString(node.NormalizedResult, "selectedOptionId", "");
                bool valid = card.Choices.Count == 0 ? card.ImmediateResolve != null && choiceId == "" && optionId == "" :
                    EventCardOptionIdFactory.TryReadChoice(optionId, card.CardId, card.Choices.Select(choice => choice.ChoiceId).ToList(), out var savedChoice) && savedChoice == choiceId;
                if (valid) return true;
                diagnostic = "event_card_restore_choice_mismatch：已保存的选择不属于原事件定义。"; return false;
            }
            diagnostic = "event_card_restore_stage_unsupported：未完成事件的阶段不受支持。";
            return false;
        }

        private readonly IMapQueryService mapQuery;
        private readonly EventDeckService eventDeckService;
        private readonly ResourceTokenService resourceTokenService;

        public EventCardEffectExecutor(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            EventDeckService eventDeckService,
            ResourceTokenService resourceTokenService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            if (influenceService == null) throw new ArgumentNullException(nameof(influenceService));
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
            EventCardDefinition card = EventCardDatabase.Get(context.State, cardId);
            if (card == null)
            {
                return EffectStepResult.Failed("card_definition_missing");
            }
            if (card.EventChoiceSchemaVersion != 1 || card.Choices == null)
                return EffectStepResult.Failed("event_choice_schema_unsupported");

            if (stage == "awaiting_option")
            {
                if (!TryReadSingleCandidate(context.GetLatestInteractionAnswer(), out string optionId) ||
                    !EventCardOptionIdFactory.TryReadChoice(optionId, card.CardId,
                        card.Choices.Select(choice => choice.ChoiceId).ToList(), out string choiceId))
                    return EffectStepResult.Failed("invalid_option");
                SetChoiceResult(context.Node, card, targetLocationId, choiceId, optionId, primaryInfluenceSlotId);
                return ResolveChoice(context, card, targetLocationId, choiceId, optionId, primaryInfluenceSlotId);
            }

            if (stage == "resolving_immediate")
                return ResolveChoice(context, card, targetLocationId, string.Empty, string.Empty, primaryInfluenceSlotId);

            if (stage == "resolving_choice")
            {
                var failedTarget = context.ChildNodes.FirstOrDefault(child => child.Status == EffectNodeStatus.Failed &&
                    child.EffectTypeId == LuaDomainEffectTypeIds.Choice &&
                    ItemTransferEffectExecutor.Text(child.NormalizedArguments, "interactionType") == "effect.influence.place.target");
                if (failedTarget != null) return EffectStepResult.Failed("insufficient_influence_targets", context.Node.NormalizedResult);
                return EffectStepResult.Completed(ResultObject("cardId", card.CardId, "outcome", "resolved",
                    "selectedChoiceId", ReadString(context.Node.NormalizedResult, "selectedChoiceId", ""),
                    "selectedOptionId", ReadString(context.Node.NormalizedResult, "selectedOptionId", ""), "targetLocationId", targetLocationId));
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

            string cardId;
            if (ItemZoneLegacyProjection.IsActive(context.State))
            {
                var deck = ItemTransferEffectExecutor.FindZone(context.State, "event_" + color.ToString().ToLowerInvariant());
                var order = context.State.ItemZones.Orders.Single(o => o.ZoneId == deck.ZoneId);
                var item = context.State.ItemZones.Items.Single(i => i.ItemId == order.ItemIds[0]);
                cardId = item.DefinitionId;
                // 事件牌无公开通用转移许可；抽取由唯一事件业务节点提交。
                var retired = ItemTransferEffectExecutor.FindZone(context.State, "retired");
                context.Node.PositionChanges.Add(new ItemPositionChange { ItemId = item.ItemId, From = new ItemPositionRef { ZoneId = deck.ZoneId }, To = new ItemPositionRef { ZoneId = retired.ZoneId } });
                ItemZoneStateMigrator.SetPosition(context.State, item, retired.ZoneId); order.OrderRevision++;
            }
            else cardId = eventDeckService.Draw(context.State.Decks, color);
            EventCardDefinition card = EventCardDatabase.Get(context.State, cardId);
            if (card == null)
            {
                return EffectStepResult.Failed("card_definition_missing");
            }
            if (card.EventChoiceSchemaVersion != 1 || card.Choices == null)
                return EffectStepResult.Failed("event_choice_schema_unsupported");

            if (ItemZoneLegacyProjection.IsActive(context.State))
            {
                var parent = context.State.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == targetLocationId);
                var target = context.State.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.resource_point_markers");
                if (!new ItemZoneStateFactory().TryCreateGrantedItem(context.State, context.Node.EffectId, "revealed_resource_point", "core.resource_point", "core.resource_point_token", target.ZoneId, new ItemSubjectRef(), out string tokenId, out string reason)) throw new InvalidOperationException(reason);
                context.State.ItemZones.Items.Single(i => i.ItemId == tokenId).LocalState = ItemTransferEffectExecutor.Object("resourceType", NormalizedValue.CreateInteger((int)card.RepresentativeResourceType), "amount", NormalizedValue.CreateInteger(card.RepresentativeResourceAmount));
            }
            else resourceTokenService.PlaceToken(context.State.Map, targetLocationId, card.RepresentativeResourceType, card.RepresentativeResourceAmount);
            if (context.State.Map.OpenLocationIds == null)
            {
                context.State.Map.OpenLocationIds = new List<string>();
            }
            if (!context.State.Map.OpenLocationIds.Contains(targetLocationId))
            {
                context.State.Map.OpenLocationIds.Add(targetLocationId);
            }

            SetChoiceResult(context.Node, card, targetLocationId, string.Empty, string.Empty, primaryInfluenceSlotId);
            bool hasChoiceInteraction = card.Choices.Count > 0;
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

        private static EffectStepResult ResolveChoice(EffectExecutionContext context, EventCardDefinition card,
            string targetLocationId, string choiceId, string optionId, string primaryInfluenceSlotId)
        {
            int index = string.IsNullOrEmpty(choiceId) ? -1 : card.Choices.FindIndex(choice => choice.ChoiceId == choiceId);
            if (card.Choices.Count == 0 ? card.ImmediateResolve == null || choiceId.Length != 0 : index < 0)
                return EffectStepResult.Failed("invalid_option");
            string routeKey = index < 0 ? StableIdFactory.Create("event-choice-immediate", card.CardId) : optionId;
            if (!context.Registry.HasPrimaryEventHandler(EventCardEffectEventTypeIds.Resolved, routeKey))
                return EffectStepResult.Failed("event_choice_handler_missing");
            var step = EffectStepResult.Continue("resolving_choice");
            step.AddEvent(new EffectEventRequest
            {
                EventId = StableIdFactory.Create("event", context.Node.EffectId, EventCardEffectEventTypeIds.Resolved, card.CardId, optionId),
                EventType = EventCardEffectEventTypeIds.Resolved,
                SourceEffectId = context.Node.EffectId,
                OwnerNodeId = context.Node.EffectId,
                RouteKey = routeKey,
                TargetEntityId = targetLocationId,
                PlayerId = context.Node.PlayerId,
                Payload = CreateResolvedChoicePayload(card, targetLocationId, choiceId, index, optionId, primaryInfluenceSlotId),
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
            foreach (var choice in card.Choices)
                candidates.Add(EventCardOptionIdFactory.CreateChoice(card.CardId, choice.ChoiceId));

            var interaction = new EffectInteractionSpec
            {
                InteractionTypeId = OptionInteractionTypeId,
                Visibility = "public",
                PromptKey = "event_card.choose_option",
                PromptParameters = ResultObject("cardId", card.CardId, "cardName", card.Name, "cardDescription", card.Description),
                AnsweringPlayerId = node.PlayerId,
                CandidateSetId = "event-card-options:" + node.EffectId,
                CandidateSetVersion = 2,
                MinSelections = 1,
                MaxSelections = 1,
                AnswerSchema = "candidate_id"
            };
            interaction.CandidateIds.AddRange(candidates);
            return interaction;
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

        private static NormalizedValue CreateResolvedChoicePayload(EventCardDefinition card, string targetLocationId,
            string choiceId, int optionIndex, string optionId, string primaryInfluenceSlotId)
        {
            return ResultObjectWithValues(
                "cardId", card.CardId,
                "choiceId", choiceId,
                "eventColor", card.Color.ToString(),
                "optionId", optionId,
                "optionIndex", optionIndex.ToString(CultureInfo.InvariantCulture),
                "primaryInfluenceSlotId", primaryInfluenceSlotId ?? string.Empty,
                "selectedInfluenceSlotIds", CreateStringArray(Array.Empty<string>()),
                "targetLocationId", targetLocationId);
        }

        private static NormalizedValue CreateOptionIds(EventCardDefinition card)
        {
            var ids = new List<NormalizedValue>();
            foreach (var choice in card.Choices)
                ids.Add(NormalizedValue.CreateStableReference("option", EventCardOptionIdFactory.CreateChoice(card.CardId, choice.ChoiceId)));
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

        private static void SetChoiceResult(EffectNodeRuntimeState node, EventCardDefinition card, string targetLocationId,
            string choiceId, string optionId, string primaryInfluenceSlotId)
        {
            node.NormalizedResult = ResultObjectWithValues("cardId", card.CardId, "eventColor", card.Color.ToString(),
                "selectedChoiceId", choiceId ?? string.Empty, "selectedOptionId", optionId ?? string.Empty,
                "targetLocationId", targetLocationId, "primaryInfluenceSlotId", primaryInfluenceSlotId ?? string.Empty);
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

    }
}
