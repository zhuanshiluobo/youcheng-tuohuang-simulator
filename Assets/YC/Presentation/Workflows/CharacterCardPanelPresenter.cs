using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class CharacterCardPanelPresenter
    {
        private readonly CharacterCardOptionQueryService optionQueryService;
        public Func<GameplayTextRef, GameplayTextDocument> ResolveGameplayText { private get; set; }

        private string StatusText(GameplayTextRef reference) => ResolveGameplayText?.Invoke(reference)?.ToPlainText() ?? string.Empty;

        public CharacterCardPanelPresenter()
            : this(new CharacterCardOptionQueryService())
        {
        }

        public CharacterCardPanelPresenter(CharacterCardOptionQueryService optionQueryService)
        {
            this.optionQueryService = optionQueryService ?? throw new ArgumentNullException(nameof(optionQueryService));
        }

        public CharacterCardPanelViewModel BuildView(GameState state, int localPlayerId) => BuildProjectedView(state, localPlayerId, null);

        public static bool UsesOriginalModeViewer(InteractionRequestProjection request) => request != null &&
            (request.InteractionTypeId == CharacterLifecycleService.ModeInteractionTypeId ||
             request.InteractionTypeId == CharacterLifecycleService.OrderInteractionTypeId);

        public static string ModeSelectionCandidate(InteractionRequestProjection request, string mode)
        {
            if (request?.InteractionTypeId == CharacterLifecycleService.OrderInteractionTypeId)
            {
                if (mode == "strategy") return "strategy-first";
                if (mode == "tactic") return "tactic-first";
            }
            return mode;
        }

        public CharacterAvailability GetPendingModeStop(GameState state, int localPlayerId, GameStateView visible = null)
        {
            if (state == null) return null;
            visible = visible ?? GameStateViewProjector.ProjectForPlayer(state, localPlayerId);
            return visible.ViewerRole == GameStateViewerRole.Player && visible.ViewerPlayerId == localPlayerId
                ? visible.Characters.FirstOrDefault(value => value.ControllerPlayerId == localPlayerId && value.CanStopPendingMode) : null;
        }

        public GameCommand CreateStopRemainingModeCommand(CharacterAvailability ability, int playerId, int revision)
        {
            if (ability?.CanStopPendingMode != true) throw new ArgumentException("当前角色技能不能停止。", nameof(ability));
            return new GameCommand { Kind = GameCommandKind.UseCharacterCard, PlayerId = playerId, TargetId = ability.ItemId,
                Parameters = { ["intent"] = UseCharacterCardCommandHandler.StopRemainingModeIntent, ["useId"] = ability.PendingUseId,
                    ["interactionId"] = ability.PendingModeInteractionId, ["expectedRevision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
        }

        public CharacterCardPanelViewModel BuildProjectedView(GameState state, int localPlayerId, GameStateView visible, string selectedCardId = null)
        {
            var legacy = BuildLegacyView(state, localPlayerId);
            if (state?.ItemZones?.Zones.Count == 0 || state?.ItemZones == null)
                return selectedCardId == null || selectedCardId == legacy.CoveredCardId ? legacy : CharacterCardPanelViewModel.Empty(string.Empty);
            visible = visible ?? GameStateViewProjector.ProjectForPlayer(state, localPlayerId);
            if (visible.ViewerRole != GameStateViewerRole.Player || visible.ViewerPlayerId != localPlayerId)
                return CharacterCardPanelViewModel.Empty(string.Empty);
            var player = visible.Players.Find(value => value.PlayerId == localPlayerId);
            if (player == null) return CharacterCardPanelViewModel.Empty(string.Empty);
            var coverRequest = visible.Interactions.FirstOrDefault(request => request.AnsweringPlayerId == localPlayerId &&
                request.Status == "open" && request.Kind == CharacterCoverEffectExecutor.InteractionTypeId);
            var canCover = state.Phase == GamePhase.CharacterCover && coverRequest != null;
            var displayOrder = new CharacterCardDisplayOrder(state.ContentDefinitions);
            List<ItemInstanceState> In(string template) => displayOrder.Sort(visible.ItemZones.Items.Where(item => item.ControllerSubject.PlayerId == localPlayerId &&
                visible.ItemZones.Zones.Any(zone => zone.Zone.ZoneId == item.ZoneId && zone.Zone.TemplateId == template)), item => item.DefinitionId, item => item.ItemId);
            var hand = In("core.zone.character_hand"); var discard = In("core.zone.character_discard");
            var coveredCards = In("core.zone.character_covered");
            var currentUse = visible.CharacterUses.LastOrDefault(use => use.ExecutingPlayerId == localPlayerId && !use.Finished && !use.SelectionCancelled);
            if (selectedCardId != null && (coveredCards.All(item => item.ItemId != selectedCardId) ||
                currentUse != null && currentUse.CardInstanceId != selectedCardId)) return CharacterCardPanelViewModel.Empty(string.Empty);
            var selection = currentUse == null ? null : visible.Interactions.FirstOrDefault(request =>
                request.AnsweringPlayerId == localPlayerId && request.OwnerEffectId == currentUse.OwnerEffectId && request.Status == "open" &&
                (request.Kind == CharacterLifecycleService.ModeInteractionTypeId || request.Kind == CharacterLifecycleService.OrderInteractionTypeId ||
                 request.Kind == CharacterLifecycleService.SecondModeInteractionTypeId));
            var covered = selectedCardId != null ? coveredCards.First(item => item.ItemId == selectedCardId) : currentUse != null
                ? visible.ItemZones.Items.FirstOrDefault(item => item.ItemId == currentUse.CardInstanceId && item.ControllerSubject.PlayerId == localPlayerId)
                : coveredCards.FirstOrDefault(item => visible.Characters.Any(value => value.ItemId == item.ItemId && value.CanUse)) ?? coveredCards.FirstOrDefault();
            CharacterCardHandItemViewModel Card(ItemInstanceState item, bool canCover) => new CharacterCardHandItemViewModel(item.ItemId,
                CharacterCardDatabase.Get(state, item.DefinitionId)?.Name ?? item.DefinitionId, canCover);
            var useSelection = ProjectInteraction(selection);
            bool canUse = covered != null && (currentUse != null || visible.Characters.Any(value => value.ItemId == covered.ItemId && value.CanUse));
            bool CanSelect(string mode) => canUse && (currentUse == null ||
                useSelection?.CandidateIds.Contains(ModeSelectionCandidate(useSelection, mode)) == true);
            var definition = covered == null ? null : CharacterCardDatabase.Get(state, covered.DefinitionId);
            var result = new CharacterCardPanelViewModel(hand.Select(item => Card(item, canCover && coverRequest.CandidateIds.Contains(item.ItemId))).ToList(), discard.Select(item => Card(item, false)).ToList(),
                legacy.CoveredStatus, legacy.InteractionStatus, canCover && hand.Any(item => coverRequest.CandidateIds.Contains(item.ItemId)), canUse, CanSelect("strategy"), CanSelect("tactic"), CanSelect("both") && currentUse != null, player.UsedCharacterThisRound,
                covered?.ItemId, player.Resources?.Originium ?? 0, player.Resources?.OriginiumShard ?? 0, player.Resources?.Iron ?? 0, player.Resources?.PureOriginium ?? 0,
                definition?.StrategyEffect ?? CharacterCardEffectKind.Unsupported, definition?.TacticEffect ?? CharacterCardEffectKind.Unsupported,
                string.Empty, string.Empty, player.Color, string.Empty);
            result.CoveredStatusText = legacy.CoveredStatusText;
            result.InteractionStatusText = legacy.InteractionStatusText;
            result.UsesEntityLifecycle = true;
            result.CurrentCoverSelection = ProjectInteraction(coverRequest);
            result.CurrentUseId = currentUse?.UseId ?? string.Empty;
            result.CurrentUseSelection = useSelection;
            result.CoveredCards = coveredCards.Select(item => {
                bool usable = currentUse != null ? currentUse.CardInstanceId == item.ItemId : visible.Characters.Any(value => value.ItemId == item.ItemId && value.CanUse);
                return new CharacterCardHandItemViewModel(item.ItemId, CharacterCardDatabase.Get(state, item.DefinitionId)?.Name ?? item.DefinitionId, false,
                    usable, usable && (currentUse == null || selection?.CandidateIds.Contains("strategy") == true),
                    usable && (currentUse == null || selection?.CandidateIds.Contains("tactic") == true));
            }).ToList();
            foreach (var item in hand.Concat(discard).Concat(coveredCards))
                result.CardDefinitionIds[item.ItemId] = item.DefinitionId;
            return result;
        }

        private static InteractionRequestProjection ProjectInteraction(InteractionView selection) => selection == null ? null : new InteractionRequestProjection {
                VisibleToViewer = true, InteractionId = selection.InteractionId, SourceNodeId = selection.SourceNodeId,
                OwnerEffectId = selection.OwnerEffectId, InteractionTypeId = selection.Kind, AnsweringPlayerId = selection.AnsweringPlayerId,
                Visibility = selection.Visibility, PromptKey = selection.PromptKey, PromptParameters = selection.PromptParameters?.Clone(),
                Presentation = YC.Domain.Presentation.GameplayPresentationNormalizer.Clone(selection.Presentation),
                CandidateSetId = selection.CandidateSetId, CandidateSetVersion = selection.CandidateSetVersion,
                CandidateIds = new List<string>(selection.CandidateIds), MinSelections = selection.MinSelections, MaxSelections = selection.MaxSelections,
                AllowDecline = selection.AllowDecline, AnswerSchema = selection.AnswerSchema, Status = selection.Status, StateRevision = selection.StateRevision
            };

        private CharacterCardPanelViewModel BuildLegacyView(GameState state, int localPlayerId)
        {
            var player = state == null ? null : state.FindPlayer(localPlayerId);
            if (state == null || player == null)
            {
                var missing = new GameplayTextRef("core:legacy.character.status.player_missing");
                var empty = CharacterCardPanelViewModel.Empty(StatusText(missing));
                empty.InteractionStatusText = missing;
                return empty;
            }

            var hasCoveredCard = !string.IsNullOrEmpty(player.CoveredCharacterCardId);
            var isLocalTurn = state.CurrentPlayerId == localPlayerId;
            var pendingCharacter = state.PendingCharacterEffect != null &&
                                   state.PendingCharacterEffect.IsValid() &&
                                   state.PendingCharacterEffect.PlayerId == localPlayerId
                ? state.PendingCharacterEffect
                : null;
            var isSecondEffectExecution = pendingCharacter != null &&
                                          pendingCharacter.ChoiceType == CharacterPendingChoiceTypes.SecondEffectExecution;
            var isSecondEffectDecision = pendingCharacter != null &&
                                          pendingCharacter.ChoiceType == CharacterPendingChoiceTypes.SecondEffectDecision;
            var canContinueSecondEffect = !isSecondEffectDecision ||
                                          pendingCharacter.OptionIds.Contains(CharacterEffectChoiceIds.ContinueSecondEffect);
            var inputBlocked = HasBlockingPendingChoice(state, localPlayerId) &&
                               !isSecondEffectDecision &&
                               !isSecondEffectExecution;
            var canCover = state.Phase == GamePhase.CharacterCover &&
                           isLocalTurn &&
                           !inputBlocked &&
                           !hasCoveredCard;
            var actionPhase = state.Phase == GamePhase.ActionRound1 ||
                              state.Phase == GamePhase.ActionRound2;
            var canUse = actionPhase &&
                         isLocalTurn &&
                         !inputBlocked &&
                         hasCoveredCard &&
                         !player.UsedCharacterThisRound &&
                         (!player.CharacterCardLockedThisTurn || isSecondEffectDecision || isSecondEffectExecution);
            var coveredDefinition = hasCoveredCard
                ? CharacterCardDatabase.Get(state, player.CoveredCharacterCardId)
                : null;
            var canUseStrategy = canUse &&
                                 canContinueSecondEffect &&
                                 coveredDefinition != null &&
                                 coveredDefinition.StrategyEffect != CharacterCardEffectKind.Unsupported &&
                                 ((!isSecondEffectDecision && !isSecondEffectExecution) ||
                                  pendingCharacter.RemainingEffectMode == CharacterEffectModes.Strategy);
            var canUseTactic = canUse &&
                               canContinueSecondEffect &&
                               coveredDefinition != null &&
                               coveredDefinition.TacticEffect != CharacterCardEffectKind.Unsupported &&
                               ((!isSecondEffectDecision && !isSecondEffectExecution) ||
                                pendingCharacter.RemainingEffectMode == CharacterEffectModes.Tactic);
            var hasImplementedEffect = canUseStrategy || canUseTactic;
            var canOpenUnfinishedCharacterUse = actionPhase &&
                                                isLocalTurn &&
                                                hasCoveredCard &&
                                                !player.UsedCharacterThisRound &&
                                                (isSecondEffectDecision || isSecondEffectExecution);

            // 规则层会在空手盖放时原子地把弃牌堆回收到手牌。展示层先投影同一结果，
            // 让玩家能够选择要盖放的牌，但不提前修改 GameState。
            var projectDiscardAsHand = canCover &&
                                       (player.HandCardIds == null || player.HandCardIds.Count == 0) &&
                                       player.DiscardCardIds != null &&
                                       player.DiscardCardIds.Count > 0;
            var displayedHandIds = (projectDiscardAsHand
                ? player.DiscardCardIds
                : player.HandCardIds)?.ToList();
            var displayOrder = new CharacterCardDisplayOrder(state.ContentDefinitions);
            if (displayedHandIds != null)
                displayedHandIds = displayOrder.Sort(displayedHandIds, id => CharacterCardDisplayOrder.ResolveDefinition(state, id), id => id);
            var hand = new List<CharacterCardHandItemViewModel>();
            if (displayedHandIds != null)
            {
                for (var i = 0; i < displayedHandIds.Count; i++)
                {
                    var cardId = displayedHandIds[i] ?? string.Empty;
                    hand.Add(new CharacterCardHandItemViewModel(
                        cardId,
                        ResolveCardDisplayName(state, cardId),
                        canCover));
                }
            }

            var discard = new List<CharacterCardHandItemViewModel>();
            if (!projectDiscardAsHand && player.DiscardCardIds != null)
            {
                var displayedDiscardIds = displayOrder.Sort(player.DiscardCardIds,
                    id => CharacterCardDisplayOrder.ResolveDefinition(state, id), id => id);
                for (var i = 0; i < displayedDiscardIds.Count; i++)
                {
                    var cardId = displayedDiscardIds[i] ?? string.Empty;
                    discard.Add(new CharacterCardHandItemViewModel(
                        cardId,
                        ResolveCardDisplayName(state, cardId),
                        false));
                }
            }

            GameplayTextRef coveredStatus;
            if (player.UsedCharacterThisRound)
            {
                coveredStatus = new GameplayTextRef("core:legacy.character.status.used");
            }
            else if (player.CharacterCardLockedThisTurn)
            {
                coveredStatus = new GameplayTextRef("core:legacy.character.status.locked");
            }
            else if (hasCoveredCard)
            {
                coveredStatus = new GameplayTextRef("core:legacy.character.status.covered");
            }
            else
            {
                coveredStatus = new GameplayTextRef("core:legacy.character.status.not_covered");
            }

            GameplayTextRef interactionStatus;
            if (inputBlocked)
            {
                interactionStatus = new GameplayTextRef("core:legacy.character.status.pending_choice");
            }
            else if (state.Phase == GamePhase.CharacterCover)
            {
                interactionStatus = canCover ? new GameplayTextRef("core:legacy.character.status.cover_select") : coveredStatus;
            }
            else if (actionPhase)
            {
                interactionStatus = isSecondEffectDecision
                    ? (canContinueSecondEffect
                        ? new GameplayTextRef("core:legacy.character.status.second_available")
                        : new GameplayTextRef("core:legacy.character.status.second_no_targets"))
                    : canUse && !hasImplementedEffect
                    ? new GameplayTextRef("core:legacy.character.status.effect_not_integrated")
                    : (canUse ? new GameplayTextRef("core:legacy.character.status.can_use") : coveredStatus);
            }
            else
            {
                interactionStatus = new GameplayTextRef("core:legacy.character.status.inspect_only");
            }

            return new CharacterCardPanelViewModel(
                hand.AsReadOnly(),
                discard.AsReadOnly(),
                StatusText(coveredStatus),
                StatusText(interactionStatus),
                canCover,
                (canUse && hasImplementedEffect) || canOpenUnfinishedCharacterUse,
                canUseStrategy,
                canUseTactic,
                false,
                player.UsedCharacterThisRound,
                hasCoveredCard ? player.CoveredCharacterCardId : string.Empty,
                player.Resources == null ? 0 : player.Resources.Originium,
                player.Resources == null ? 0 : player.Resources.OriginiumShard,
                player.Resources == null ? 0 : player.Resources.Iron,
                player.Resources == null ? 0 : player.Resources.PureOriginium,
                coveredDefinition == null ? CharacterCardEffectKind.Unsupported : coveredDefinition.StrategyEffect,
                coveredDefinition == null ? CharacterCardEffectKind.Unsupported : coveredDefinition.TacticEffect,
                pendingCharacter == null ? string.Empty : pendingCharacter.ChoiceType,
                pendingCharacter == null || pendingCharacter.RemainingCardIds == null || pendingCharacter.RemainingCardIds.Count == 0
                    ? string.Empty
                    : ResolveCardDisplayName(state, pendingCharacter.RemainingCardIds[0]),
                player.Color,
                pendingCharacter == null ? string.Empty : pendingCharacter.RemainingEffectMode)
                { CoveredStatusText = coveredStatus, InteractionStatusText = interactionStatus };
        }

        public CharacterCardOptionQueryResult QueryOptions(
            GameState state,
            int localPlayerId,
            CharacterCardEffectKind effect,
            IReadOnlyDictionary<string, string> selectedParameters)
        {
            return optionQueryService.Query(state, localPlayerId, effect, selectedParameters);
        }

        public string GetEffectUnavailableReason(GameState state, int playerId, bool strategy)
            => GetEffectUnavailableReason(BuildView(state, playerId), strategy);

        public string GetEffectUnavailableReason(CharacterCardPanelViewModel model, bool strategy)
        {
            var reference = GetEffectUnavailableReasonText(model, strategy);
            return reference == null ? string.Empty : ResolveGameplayText?.Invoke(reference)?.ToPlainText() ?? model.InteractionStatus;
        }

        public GameplayTextRef GetEffectUnavailableReasonText(CharacterCardPanelViewModel model, bool strategy)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (strategy ? model.CanUseStrategy : model.CanUseTactic) return null;
            if (!model.CanUse) return model.InteractionStatusText;
            return new GameplayTextRef((strategy ? model.StrategyEffect : model.TacticEffect) == CharacterCardEffectKind.Unsupported
                ? "core:legacy.character.unavailable.effect_not_integrated" : "core:legacy.character.unavailable.effect_currently_unavailable");
        }

        public CharacterCardOptionQueryResult QueryPendingOptions(
            GameState state,
            int localPlayerId,
            IReadOnlyDictionary<string, string> selectedParameters)
        {
            return optionQueryService.QueryPending(state, localPlayerId, selectedParameters);
        }

        public GameplayInteractionPresentation GetPendingPresentation(GameState state, int localPlayerId,
            CharacterCardOptionQueryResult query, GameStateView visible)
        {
            var pending = state?.PendingCharacterEffect;
            if (query?.Presentation == null || pending == null || !pending.IsValid() || pending.PlayerId != localPlayerId)
                return null;
            visible = visible ?? GameStateViewProjector.ProjectForPlayer(state, localPlayerId);
            if (visible.ViewerRole != GameStateViewerRole.Player || visible.ViewerPlayerId != localPlayerId ||
                visible.GameId != state.GameId || visible.ContentPackHash != state.ContentPackHash ||
                visible.StateRevision != (state.EffectRuntime?.StateRevision ?? 0)) return null;
            var interactionId = StableIdFactory.Create("legacy-interaction", pending.SourceCommandId ?? string.Empty,
                pending.ChoiceType ?? string.Empty, pending.CardId ?? string.Empty);
            var matches = visible.Interactions.Where(value => value.InteractionId == interactionId).Take(2).ToArray();
            if (matches.Length != 1 || matches[0].AnsweringPlayerId != localPlayerId || matches[0].Status != "open" ||
                matches[0].Kind != pending.ChoiceType || !matches[0].CandidateIds.SequenceEqual(pending.OptionIds)) return null;
            return GameplayPresentationNormalizer.Clone(query.Presentation);
        }

        public GameCommand CreateResolvePendingCommand(int playerId, IReadOnlyDictionary<string, string> effectParameters)
        {
            var command = new GameCommand { Kind = GameCommandKind.ResolvePendingChoice, PlayerId = playerId };
            if (effectParameters != null)
            {
                foreach (var pair in effectParameters) command.Parameters[pair.Key] = pair.Value ?? string.Empty;
            }
            return command;
        }

        public GameCommand CreateCoverCommand(int playerId, string cardId)
        {
            if (string.IsNullOrEmpty(cardId))
            {
                throw new ArgumentException("角色牌 ID 不能为空。", nameof(cardId));
            }

            return new GameCommand
            {
                Kind = GameCommandKind.CoverCharacterCard,
                PlayerId = playerId,
                TargetId = cardId,
                Parameters =
                {
                    [CoverCharacterCardCommandHandler.CardIdParameter] = cardId
                }
            };
        }

        public GameCommand CreateBeginUseCommand(int playerId, string cardId) => CreateUseCommand(playerId, cardId, string.Empty, string.Empty,
            new Dictionary<string, string> { [CharacterEffectParameterKeys.OfferSecondEffect] = "true" });

        public GameCommand CreateUseSelectionCommand(CharacterCardPanelViewModel view, int playerId, string candidate)
        {
            var request = view?.CurrentUseSelection;
            if (request == null || !request.CandidateIds.Contains(candidate)) throw new ArgumentException("角色使用选择已失效。", nameof(candidate));
            return EffectInteractionCommands.Answer(request, playerId, new[] { candidate });
        }

        public GameCommand CreateUseCommand(
            int playerId,
            string cardId,
            string effectMode,
            string effectOrder,
            IReadOnlyDictionary<string, string> effectParameters = null)
        {
            if (string.IsNullOrEmpty(cardId))
            {
                throw new ArgumentException("角色牌 ID 不能为空。", nameof(cardId));
            }

            var command = new GameCommand
            {
                Kind = GameCommandKind.UseCharacterCard,
                PlayerId = playerId,
                TargetId = cardId,
                Parameters =
                {
                    [UseCharacterCardCommandHandler.CardIdParameter] = cardId,
                    [UseCharacterCardCommandHandler.EffectModeParameter] = effectMode ?? string.Empty
                }
            };

            if (!string.IsNullOrEmpty(effectOrder))
            {
                command.Parameters[UseCharacterCardCommandHandler.EffectOrderParameter] = effectOrder;
            }

            if (effectParameters != null)
            {
                foreach (var pair in effectParameters)
                {
                    command.Parameters[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            return command;
        }

        public static string ResolveCardDisplayName(GameState state, string cardId)
        {
            var definition = CharacterCardDatabase.Get(state, cardId);
            return definition == null || string.IsNullOrEmpty(definition.Name) ? ResolveCardDisplayName(cardId) : definition.Name;
        }

        public static string ResolveCardDisplayName(string cardId)
        {
            if (string.IsNullOrEmpty(cardId))
            {
                return "未知角色牌";
            }

            var normalized = cardId.ToLowerInvariant();
            if (cardId.Contains("雷蛇") || normalized.Contains("liskarm") || normalized.Contains("leishe")) return "雷蛇";
            if (cardId.Contains("极境") || normalized.Contains("elysium") || normalized.Contains("jijing")) return "极境";
            if (cardId.Contains("德克萨斯") || normalized.Contains("texas") || normalized.Contains("dekesasi")) return "德克萨斯";
            if (cardId.Contains("坎诺特") || normalized.Contains("cannot") || normalized.Contains("kannuote")) return "坎诺特";
            if (cardId.Contains("锡人") || normalized.Contains("tin-man") || normalized.Contains("tinman") || normalized.Contains("xiren")) return "锡人";

            var definition = CharacterCardDatabase.Get(cardId);
            if (definition != null && !string.IsNullOrEmpty(definition.Name))
            {
                return definition.Name;
            }

            if (cardId.Contains("雷蛇") || normalized.Contains("liskarm") || normalized.Contains("leishe")) return "雷蛇";
            if (cardId.Contains("极境") || normalized.Contains("elysium") || normalized.Contains("jijing")) return "极境";
            if (cardId.Contains("德克萨斯") || normalized.Contains("texas") || normalized.Contains("dekesasi")) return "德克萨斯";
            if (cardId.Contains("坎诺特") || normalized.Contains("cannot") || normalized.Contains("kannuote")) return "坎诺特";
            if (cardId.Contains("锡人") || normalized.Contains("tin-man") || normalized.Contains("tinman") || normalized.Contains("xiren")) return "锡人";
            return cardId;
        }

        private static bool HasBlockingPendingChoice(GameState state, int localPlayerId)
        {
            if (state == null)
            {
                return false;
            }

            if (state.PendingChoice != null && state.PendingChoice.IsValid())
            {
                return true;
            }

            if (state.PendingCardSession != null && state.PendingCardSession.IsValid())
            {
                return true;
            }

            if (state.PendingCharacterEffect != null && state.PendingCharacterEffect.IsValid())
            {
                return true;
            }

            if (state.EffectRuntime == null || state.EffectRuntime.InteractionRequests == null)
            {
                return false;
            }

            for (var i = 0; i < state.EffectRuntime.InteractionRequests.Count; i++)
            {
                var request = state.EffectRuntime.InteractionRequests[i];
                if (request == null || request.Status != "open" || request.IsInternalMainlineInteraction())
                {
                    continue;
                }

                if (state.Phase == GamePhase.CharacterCover && request.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

    }

    public sealed class CharacterCardPanelViewModel
    {
        public CharacterCardPanelViewModel(
            IReadOnlyList<CharacterCardHandItemViewModel> handCards,
            IReadOnlyList<CharacterCardHandItemViewModel> discardCards,
            string coveredStatus,
            string interactionStatus,
            bool canCover,
            bool canUse,
            bool canUseStrategy,
            bool canUseTactic,
            bool canUseBoth,
            bool usedCharacterThisRound,
            string coveredCardId,
            int originium,
            int originiumShard,
            int iron,
            int pureOriginium,
            CharacterCardEffectKind strategyEffect,
            CharacterCardEffectKind tacticEffect,
            string pendingChoiceType,
            string pendingCardDisplayName,
            PlayerColor coveredCardBackColor,
            string remainingEffectMode)
        {
            HandCards = handCards ?? new List<CharacterCardHandItemViewModel>().AsReadOnly();
            DiscardCards = discardCards ?? new List<CharacterCardHandItemViewModel>().AsReadOnly();
            CoveredStatus = coveredStatus ?? string.Empty;
            InteractionStatus = interactionStatus ?? string.Empty;
            CanCover = canCover;
            CanUse = canUse;
            CanUseStrategy = canUseStrategy;
            CanUseTactic = canUseTactic;
            CanUseBoth = canUseBoth;
            UsedCharacterThisRound = usedCharacterThisRound;
            CoveredCardId = coveredCardId ?? string.Empty;
            CoveredCards = string.IsNullOrEmpty(CoveredCardId) ? new List<CharacterCardHandItemViewModel>() : new List<CharacterCardHandItemViewModel> { new CharacterCardHandItemViewModel(CoveredCardId, CharacterCardPanelPresenter.ResolveCardDisplayName(CoveredCardId), false, canUse, canUseStrategy, canUseTactic) };
            Originium = System.Math.Max(0, originium);
            OriginiumShard = System.Math.Max(0, originiumShard);
            Iron = System.Math.Max(0, iron);
            PureOriginium = System.Math.Max(0, pureOriginium);
            StrategyEffect = strategyEffect;
            TacticEffect = tacticEffect;
            PendingChoiceType = pendingChoiceType ?? string.Empty;
            PendingCardDisplayName = pendingCardDisplayName ?? string.Empty;
            CoveredCardBackColor = coveredCardBackColor;
            RemainingEffectMode = remainingEffectMode ?? string.Empty;
        }

        public bool UsesEntityLifecycle { get; internal set; }
        public string CurrentUseId { get; internal set; } = string.Empty;
        public InteractionRequestProjection CurrentUseSelection { get; internal set; }
        public InteractionRequestProjection CurrentCoverSelection { get; internal set; }
        public IReadOnlyList<CharacterCardHandItemViewModel> CoveredCards { get; internal set; }
        public IReadOnlyList<CharacterCardHandItemViewModel> HandCards { get; private set; }
        public Dictionary<string, string> CardDefinitionIds { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public string ResolveDefinitionId(string itemId) => itemId != null && CardDefinitionIds.TryGetValue(itemId, out var definitionId) ? definitionId : itemId;
        public IReadOnlyList<CharacterCardHandItemViewModel> DiscardCards { get; private set; }
        public GameplayTextRef CoveredStatusText { get; internal set; }
        public GameplayTextRef InteractionStatusText { get; internal set; }
        public string CoveredStatus { get; private set; }
        public string InteractionStatus { get; private set; }
        public bool CanCover { get; private set; }
        public bool CanUse { get; private set; }
        public bool CanUseStrategy { get; private set; }
        public bool CanUseTactic { get; private set; }
        public bool CanUseBoth { get; private set; }
        public bool UsedCharacterThisRound { get; private set; }
        public string CoveredCardId { get; private set; }
        public int Originium { get; private set; }
        public int OriginiumShard { get; private set; }
        public int Iron { get; private set; }
        public int PureOriginium { get; private set; }
        public CharacterCardEffectKind StrategyEffect { get; private set; }
        public CharacterCardEffectKind TacticEffect { get; private set; }
        public string PendingChoiceType { get; private set; }
        public string PendingCardDisplayName { get; private set; }
        public PlayerColor CoveredCardBackColor { get; private set; }
        public string RemainingEffectMode { get; private set; }
        public bool HasPendingCharacterChoice => !string.IsNullOrEmpty(PendingChoiceType);
        public bool IsSecondEffectDecision => PendingChoiceType == CharacterPendingChoiceTypes.SecondEffectDecision ||
            CurrentUseSelection?.InteractionTypeId == CharacterLifecycleService.SecondModeInteractionTypeId;
        public bool IsSecondEffectExecution => PendingChoiceType == CharacterPendingChoiceTypes.SecondEffectExecution;

        public static CharacterCardPanelViewModel Empty(string status)
        {
            return new CharacterCardPanelViewModel(
                new List<CharacterCardHandItemViewModel>().AsReadOnly(),
                new List<CharacterCardHandItemViewModel>().AsReadOnly(),
                string.Empty,
                status,
                false,
                false,
                false,
                false,
                false,
                false,
                string.Empty,
                0,
                0,
                0,
                0,
                CharacterCardEffectKind.Unsupported,
                CharacterCardEffectKind.Unsupported,
                string.Empty,
                string.Empty,
                default(PlayerColor),
                string.Empty);
        }
    }

    public sealed class CharacterCardHandItemViewModel
    {
        public CharacterCardHandItemViewModel(string cardId, string displayName, bool canCover, bool canUse = false,
            bool canUseStrategy = false, bool canUseTactic = false)
        {
            CardId = cardId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            CanCover = canCover;
            CanUse = canUse; CanUseStrategy = canUseStrategy; CanUseTactic = canUseTactic;
        }

        public string CardId { get; private set; }
        public string DisplayName { get; private set; }
        public bool CanCover { get; private set; }
        public bool CanUse { get; private set; }
        public bool CanUseStrategy { get; private set; }
        public bool CanUseTactic { get; private set; }
    }
}
