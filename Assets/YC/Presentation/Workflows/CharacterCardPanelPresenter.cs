using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation.Workflows
{
    public sealed class CharacterCardPanelPresenter
    {
        private readonly CharacterCardOptionQueryService optionQueryService;

        public CharacterCardPanelPresenter()
            : this(new CharacterCardOptionQueryService())
        {
        }

        public CharacterCardPanelPresenter(CharacterCardOptionQueryService optionQueryService)
        {
            this.optionQueryService = optionQueryService ?? throw new ArgumentNullException(nameof(optionQueryService));
        }

        public CharacterCardPanelViewModel BuildView(GameState state, int localPlayerId)
        {
            var player = state == null ? null : state.FindPlayer(localPlayerId);
            if (state == null || player == null)
            {
                return CharacterCardPanelViewModel.Empty("当前玩家不存在。");
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
            var inputBlocked = state.HasPendingChoice() && !isSecondEffectDecision && !isSecondEffectExecution;
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
                ? CharacterCardDatabase.Get(player.CoveredCharacterCardId)
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

            var hand = new List<CharacterCardHandItemViewModel>();
            if (player.HandCardIds != null)
            {
                for (var i = 0; i < player.HandCardIds.Count; i++)
                {
                    var cardId = player.HandCardIds[i] ?? string.Empty;
                    string imageRelativePath;
                    CharacterCardImagePathCatalog.TryGetFrontImageRelativePath(cardId, out imageRelativePath);
                    hand.Add(new CharacterCardHandItemViewModel(
                        cardId,
                        ResolveCardDisplayName(cardId),
                        canCover,
                        imageRelativePath));
                }
            }

            var discard = new List<CharacterCardHandItemViewModel>();
            if (player.DiscardCardIds != null)
            {
                for (var i = 0; i < player.DiscardCardIds.Count; i++)
                {
                    var cardId = player.DiscardCardIds[i] ?? string.Empty;
                    string imageRelativePath;
                    CharacterCardImagePathCatalog.TryGetFrontImageRelativePath(cardId, out imageRelativePath);
                    discard.Add(new CharacterCardHandItemViewModel(
                        cardId,
                        ResolveCardDisplayName(cardId),
                        false,
                        imageRelativePath));
                }
            }

            string coveredBackImageRelativePath;
            if (!hasCoveredCard || !CharacterCardImagePathCatalog.TryGetBackImageRelativePath(player.Color, out coveredBackImageRelativePath))
            {
                coveredBackImageRelativePath = string.Empty;
            }

            string coveredFrontImageRelativePath;
            if (!hasCoveredCard || !CharacterCardImagePathCatalog.TryGetFrontImageRelativePath(player.CoveredCharacterCardId, out coveredFrontImageRelativePath))
            {
                coveredFrontImageRelativePath = string.Empty;
            }

            string coveredStatus;
            if (player.UsedCharacterThisRound)
            {
                coveredStatus = "本回合角色牌已使用";
            }
            else if (player.CharacterCardLockedThisTurn)
            {
                coveredStatus = "本玩家行动轮内角色牌已被特殊行动锁定";
            }
            else if (hasCoveredCard)
            {
                coveredStatus = "已盖放（背面）";
            }
            else
            {
                coveredStatus = "尚未盖放";
            }

            string interactionStatus;
            if (inputBlocked)
            {
                interactionStatus = "请先处理待选择项";
            }
            else if (state.Phase == GamePhase.CharacterCover)
            {
                interactionStatus = canCover ? "拖动到主要行动卡上即可盖放" : coveredStatus;
            }
            else if (actionPhase)
            {
                interactionStatus = isSecondEffectDecision
                    ? (canContinueSecondEffect
                        ? "可继续使用第二个效果；点击翻转则结束角色卡使用"
                        : "当前角色牌效果没有合法的地图目标，请点击翻转完成结算。")
                    : canUse && !hasImplementedEffect
                    ? "效果尚未接入"
                    : (canUse ? "可使用本回合盖放的角色牌" : coveredStatus);
            }
            else
            {
                interactionStatus = "当前阶段仅可查看";
            }

            return new CharacterCardPanelViewModel(
                hand.AsReadOnly(),
                discard.AsReadOnly(),
                coveredStatus,
                interactionStatus,
                canCover,
                canUse && hasImplementedEffect,
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
                    : ResolveCardDisplayName(pendingCharacter.RemainingCardIds[0]),
                coveredBackImageRelativePath,
                coveredFrontImageRelativePath,
                pendingCharacter == null ? string.Empty : pendingCharacter.RemainingEffectMode);
        }

        public CharacterCardOptionQueryResult QueryOptions(
            GameState state,
            int localPlayerId,
            CharacterCardEffectKind effect,
            IReadOnlyDictionary<string, string> selectedParameters)
        {
            return optionQueryService.Query(state, localPlayerId, effect, selectedParameters);
        }

        public CharacterCardOptionQueryResult QueryPendingOptions(
            GameState state,
            int localPlayerId,
            IReadOnlyDictionary<string, string> selectedParameters)
        {
            return optionQueryService.QueryPending(state, localPlayerId, selectedParameters);
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
            string coveredBackImageRelativePath,
            string coveredFrontImageRelativePath,
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
            Originium = System.Math.Max(0, originium);
            OriginiumShard = System.Math.Max(0, originiumShard);
            Iron = System.Math.Max(0, iron);
            PureOriginium = System.Math.Max(0, pureOriginium);
            StrategyEffect = strategyEffect;
            TacticEffect = tacticEffect;
            PendingChoiceType = pendingChoiceType ?? string.Empty;
            PendingCardDisplayName = pendingCardDisplayName ?? string.Empty;
            CoveredBackImageRelativePath = coveredBackImageRelativePath ?? string.Empty;
            CoveredFrontImageRelativePath = coveredFrontImageRelativePath ?? string.Empty;
            RemainingEffectMode = remainingEffectMode ?? string.Empty;
        }

        public IReadOnlyList<CharacterCardHandItemViewModel> HandCards { get; private set; }
        public IReadOnlyList<CharacterCardHandItemViewModel> DiscardCards { get; private set; }
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
        public string CoveredBackImageRelativePath { get; private set; }
        public string CoveredFrontImageRelativePath { get; private set; }
        public string RemainingEffectMode { get; private set; }
        public bool HasPendingCharacterChoice => !string.IsNullOrEmpty(PendingChoiceType);
        public bool IsSecondEffectDecision => PendingChoiceType == CharacterPendingChoiceTypes.SecondEffectDecision;
        public bool IsSecondEffectExecution => PendingChoiceType == CharacterPendingChoiceTypes.SecondEffectExecution;

        public static CharacterCardPanelViewModel Empty(string status)
        {
            return new CharacterCardPanelViewModel(
                new List<CharacterCardHandItemViewModel>().AsReadOnly(),
                new List<CharacterCardHandItemViewModel>().AsReadOnly(),
                "尚未盖放",
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
                string.Empty,
                string.Empty,
                string.Empty);
        }
    }

    public sealed class CharacterCardHandItemViewModel
    {
        public CharacterCardHandItemViewModel(string cardId, string displayName, bool canCover, string frontImageRelativePath)
        {
            CardId = cardId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            CanCover = canCover;
            FrontImageRelativePath = frontImageRelativePath ?? string.Empty;
        }

        public string CardId { get; private set; }
        public string DisplayName { get; private set; }
        public bool CanCover { get; private set; }
        public string FrontImageRelativePath { get; private set; }
    }
}
