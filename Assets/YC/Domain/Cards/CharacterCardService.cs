using System;
using System.Collections.Generic;
using YC.Domain.Commands;
using YC.Domain.Economy;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public sealed class CharacterCardService
    {
        public const int TinManFirstPureOriginiumCost = 12;
        public const int TinManSecondPureOriginiumCost = 15;

        private readonly TurnOrderService turnOrderService;
        private readonly ResourceSaleService resourceSaleService;
        private readonly IMapQueryService sharedMapQuery;
        private readonly InfluenceService sharedInfluenceService;
        private readonly CityMovementService sharedMovementService;

        public CharacterCardService()
            : this(new TurnOrderService(), new ResourceSaleService())
        {
        }

        public CharacterCardService(TurnOrderService turnOrderService)
            : this(turnOrderService, new ResourceSaleService())
        {
        }

        public CharacterCardService(
            TurnOrderService turnOrderService,
            ResourceSaleService resourceSaleService)
            : this(turnOrderService, resourceSaleService, null, null, null)
        {
        }

        public CharacterCardService(
            TurnOrderService turnOrderService,
            ResourceSaleService resourceSaleService,
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            CityMovementService movementService)
        {
            this.turnOrderService = turnOrderService ?? throw new ArgumentNullException(nameof(turnOrderService));
            this.resourceSaleService = resourceSaleService ?? throw new ArgumentNullException(nameof(resourceSaleService));
            if ((mapQuery == null) != (influenceService == null) ||
                (mapQuery == null) != (movementService == null))
            {
                throw new ArgumentException("角色牌行动服务必须同时提供，或全部使用默认解析。");
            }

            sharedMapQuery = mapQuery;
            sharedInfluenceService = influenceService;
            sharedMovementService = movementService;
        }

        public ValidationResult Cover(GameState state, int playerId, string cardId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.Phase != GamePhase.CharacterCover)
            {
                return Failure(CommandErrorCode.WrongPhase, "只能在角色牌盖放阶段盖放角色牌。");
            }

            if (state.HasPendingChoice())
            {
                return Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择效果。");
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return Failure(CommandErrorCode.InvalidPlayer, "盖放角色牌的玩家不存在。");
            }

            if (state.CurrentPlayerId != playerId)
            {
                return Failure(CommandErrorCode.NotCurrentPlayer, "请按角色牌盖放顺序操作。");
            }

            if (!string.IsNullOrEmpty(player.CoveredCharacterCardId))
            {
                return Failure(CommandErrorCode.InvalidTarget, "本回合已经盖放过角色牌。");
            }

            var recycleDiscard = player.HandCardIds.Count == 0;
            var source = recycleDiscard ? player.DiscardCardIds : player.HandCardIds;
            if (string.IsNullOrEmpty(cardId) || !source.Contains(cardId))
            {
                return Failure(CommandErrorCode.InvalidTarget, recycleDiscard
                    ? "手牌为空时只能从回收的角色牌弃牌中选择盖放牌。"
                    : "只能盖放自己手中的角色牌。");
            }

            if (CharacterCardDatabase.Get(cardId) == null)
            {
                return Failure(CommandErrorCode.InvalidTarget, "角色牌定义不存在。");
            }

            if (recycleDiscard)
            {
                player.HandCardIds.AddRange(player.DiscardCardIds);
                player.DiscardCardIds.Clear();
            }

            player.HandCardIds.Remove(cardId);
            player.CoveredCharacterCardId = cardId;
            player.CoveredCharacterCardIds.Clear();
            player.CoveredCharacterCardIds.Add(cardId);

            if (AllPlayersCovered(state))
            {
                state.Phase = GamePhase.ActionRound1;
                state.ActionRound = 1;
                state.CurrentPlayerId = GetFirstTurnPlayerId(state);
            }
            else
            {
                state.CurrentPlayerId = FindNextUncoveredPlayerId(state, playerId);
            }

            return ValidationResult.Success;
        }

        public ValidationResult Use(
            GameState state,
            int playerId,
            string cardId,
            string effectMode,
            string effectOrder,
            IDictionary<string, string> parameters)
        {
            var resolvingSecondEffect = IsResolvingSecondEffect(state, playerId, cardId, effectMode);
            var offerSecondEffect = string.Equals(
                GetParameter(parameters, CharacterEffectParameterKeys.OfferSecondEffect),
                "true",
                StringComparison.OrdinalIgnoreCase);
            var validation = ValidateUse(state, playerId, cardId, effectMode, effectOrder);
            if (!validation.IsValid)
            {
                return validation;
            }

            var definition = CharacterCardDatabase.Get(cardId);
            if (definition.TemplateId == CharacterCardDatabase.TinMan)
            {
                return UseTinMan(state, playerId, cardId, effectMode, effectOrder, parameters, resolvingSecondEffect, offerSecondEffect);
            }

            if (HasBoardEffect(definition))
            {
                return UseBoardCharacterCard(state, playerId, cardId, effectMode, effectOrder, parameters, definition, resolvingSecondEffect, offerSecondEffect);
            }

            var simulatedResources = ClonePlayerResources(state);
            var simulatedScore = state.FindPlayer(playerId).Score;
            var simulatedFacilitySupply = new List<string>(state.Decks.FacilitySupply);
            var simulatedFacilityDeck = new List<string>(state.Decks.FacilityDeck);

            if (effectMode == CharacterEffectModes.Strategy)
            {
                validation = ApplyEffectPlan(definition.StrategyEffect, state, playerId, parameters, simulatedResources, simulatedFacilitySupply, simulatedFacilityDeck, ref simulatedScore);
            }
            else if (effectMode == CharacterEffectModes.Tactic)
            {
                validation = ApplyEffectPlan(definition.TacticEffect, state, playerId, parameters, simulatedResources, simulatedFacilitySupply, simulatedFacilityDeck, ref simulatedScore);
            }
            else if (effectOrder == CharacterEffectOrders.StrategyFirst)
            {
                validation = ApplyEffectPlan(definition.StrategyEffect, state, playerId, parameters, simulatedResources, simulatedFacilitySupply, simulatedFacilityDeck, ref simulatedScore);
                if (validation.IsValid)
                {
                    validation = ApplyEffectPlan(definition.TacticEffect, state, playerId, parameters, simulatedResources, simulatedFacilitySupply, simulatedFacilityDeck, ref simulatedScore);
                }
            }
            else
            {
                validation = ApplyEffectPlan(definition.TacticEffect, state, playerId, parameters, simulatedResources, simulatedFacilitySupply, simulatedFacilityDeck, ref simulatedScore);
                if (validation.IsValid)
                {
                    validation = ApplyEffectPlan(definition.StrategyEffect, state, playerId, parameters, simulatedResources, simulatedFacilitySupply, simulatedFacilityDeck, ref simulatedScore);
                }
            }

            if (!validation.IsValid)
            {
                return validation;
            }

            CommitResources(state, simulatedResources);
            state.Decks.FacilitySupply.Clear();
            state.Decks.FacilitySupply.AddRange(simulatedFacilitySupply);
            state.Decks.FacilityDeck.Clear();
            state.Decks.FacilityDeck.AddRange(simulatedFacilityDeck);
            var player = state.FindPlayer(playerId);
            player.Score = simulatedScore;
            CompleteOrOfferSecondEffect(state, player, cardId, effectMode, definition, resolvingSecondEffect, offerSecondEffect);
            return ValidationResult.Success;
        }

        public ValidationResult ResolvePendingChoice(
            GameState state,
            int playerId,
            string choice,
            string sourceInfluenceSlotId,
            string targetInfluenceSlotId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var pending = state.PendingCharacterEffect;
            if (pending == null || !pending.IsValid())
            {
                return Failure(CommandErrorCode.PendingChoiceRequired, "当前没有待处理的角色牌效果。");
            }

            if (pending.PlayerId != playerId)
            {
                return Failure(CommandErrorCode.InvalidPlayer, "只能由发动锡人的玩家处理待选效果。");
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return Failure(CommandErrorCode.InvalidPlayer, "玩家不存在。");
            }

            if (pending.ChoiceType == CharacterPendingChoiceTypes.SecondEffectDecision)
            {
                return ResolveSecondEffectDecision(state, player, pending, choice);
            }

            if (pending.ChoiceType == CharacterPendingChoiceTypes.TinManFirstPurchase ||
                pending.ChoiceType == CharacterPendingChoiceTypes.TinManSecondPurchase)
            {
                return ResolveTinManStrategyPurchaseChoice(state, playerId, choice);
            }

            if (pending.ChoiceType == CharacterPendingChoiceTypes.LiskarmCleanupRemoval)
            {
                if (string.IsNullOrEmpty(targetInfluenceSlotId) || !pending.OptionIds.Contains(targetInfluenceSlotId))
                {
                    return Failure(CommandErrorCode.InvalidTarget, "必须选择待选列表中的己方影响力。");
                }

                var influenceService = ResolveInfluenceService(state);
                var influence = influenceService.FindInfluence(state, targetInfluenceSlotId);
                if (influence == null || influence.PlayerId != playerId)
                {
                    return Failure(CommandErrorCode.InvalidTarget, "收尾阶段只能移除自己的影响力。");
                }

                var removal = influenceService.Remove(state, targetInfluenceSlotId);
                if (!removal.Succeeded)
                {
                    return removal.Validation;
                }

                for (var i = 0; i < state.DelayedCharacterEffects.Count; i++)
                {
                    var delayed = state.DelayedCharacterEffects[i];
                    if (delayed.EffectType == CharacterPendingChoiceTypes.LiskarmCleanupRemoval &&
                        delayed.PlayerId == playerId &&
                        delayed.CardId == pending.CardId)
                    {
                        state.DelayedCharacterEffects.RemoveAt(i);
                        break;
                    }
                }

                state.PendingCharacterEffect = null;
                return ValidationResult.Success;
            }

            if (pending.ChoiceType != CharacterPendingChoiceTypes.TinManDiscard)
            {
                return Failure(CommandErrorCode.PendingChoiceRequired, "当前待选角色牌效果类型无法处理。");
            }
            return ResolveTinManPendingChoice(
                state,
                playerId,
                choice,
                sourceInfluenceSlotId,
                targetInfluenceSlotId);
        }

        public void CleanupRound(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var player = state.Players[i];
                if (!player.UsedCharacterThisRound && !string.IsNullOrEmpty(player.CoveredCharacterCardId))
                {
                    player.HandCardIds.Add(player.CoveredCharacterCardId);
                    player.HandCardIds.AddRange(player.DiscardCardIds);
                    player.DiscardCardIds.Clear();
                }

                player.CoveredCharacterCardId = string.Empty;
                player.CoveredCharacterCardIds.Clear();
                player.UsedCharacterThisRound = false;
            }
        }

        public ValidationResult BeginCleanupEffects(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            for (var i = 0; i < state.DelayedCharacterEffects.Count; i++)
            {
                var delayed = state.DelayedCharacterEffects[i];
                if (delayed.EffectType != CharacterPendingChoiceTypes.LiskarmCleanupRemoval)
                {
                    continue;
                }

                var options = new List<string>();
                for (var influenceIndex = 0; influenceIndex < state.Map.Influences.Count; influenceIndex++)
                {
                    var influence = state.Map.Influences[influenceIndex];
                    if (influence.PlayerId == delayed.PlayerId)
                    {
                        options.Add(influence.SlotId);
                    }
                }

                if (options.Count == 0)
                {
                    state.DelayedCharacterEffects.RemoveAt(i);
                    i -= 1;
                    continue;
                }

                state.PendingCharacterEffect = new PendingCharacterEffectState
                {
                    ChoiceType = CharacterPendingChoiceTypes.LiskarmCleanupRemoval,
                    PlayerId = delayed.PlayerId,
                    CardId = delayed.CardId,
                    OptionIds = options
                };
                return ValidationResult.Success;
            }

            return ValidationResult.Success;
        }

        private static bool HasBoardEffect(CharacterCardDefinition definition)
        {
            return definition.StrategyEffect == CharacterCardEffectKind.LiskarmSecurityProtocol ||
                   definition.TacticEffect == CharacterCardEffectKind.LiskarmControlPosition ||
                   definition.TacticEffect == CharacterCardEffectKind.ElysiumNavigation ||
                   definition.TacticEffect == CharacterCardEffectKind.TexasRemoveAndDoubleMove;
        }

        private ValidationResult UseBoardCharacterCard(
            GameState state,
            int playerId,
            string cardId,
            string effectMode,
            string effectOrder,
            IDictionary<string, string> parameters,
            CharacterCardDefinition definition,
            bool resolvingSecondEffect,
            bool offerSecondEffect)
        {
            var projected = CloneForCharacterEffect(state);
            ValidationResult result;
            if (effectMode == CharacterEffectModes.Strategy)
            {
                result = ApplyBoardEffect(projected, playerId, cardId, definition.StrategyEffect, parameters);
            }
            else if (effectMode == CharacterEffectModes.Tactic)
            {
                result = ApplyBoardEffect(projected, playerId, cardId, definition.TacticEffect, parameters);
            }
            else if (effectOrder == CharacterEffectOrders.StrategyFirst)
            {
                result = ApplyBoardEffect(projected, playerId, cardId, definition.StrategyEffect, parameters);
                if (result.IsValid)
                {
                    result = ApplyBoardEffect(projected, playerId, cardId, definition.TacticEffect, parameters);
                }
            }
            else
            {
                result = ApplyBoardEffect(projected, playerId, cardId, definition.TacticEffect, parameters);
                if (result.IsValid)
                {
                    result = ApplyBoardEffect(projected, playerId, cardId, definition.StrategyEffect, parameters);
                }
            }

            if (!result.IsValid)
            {
                return result;
            }

            CompleteOrOfferSecondEffect(
                projected,
                projected.FindPlayer(playerId),
                cardId,
                effectMode,
                definition,
                resolvingSecondEffect,
                offerSecondEffect);
            CommitCharacterEffectProjection(state, projected);
            return ValidationResult.Success;
        }

        private ValidationResult ApplyBoardEffect(
            GameState state,
            int playerId,
            string cardId,
            CharacterCardEffectKind effect,
            IDictionary<string, string> parameters)
        {
            var influenceService = ResolveInfluenceService(state);
            var player = state.FindPlayer(playerId);
            switch (effect)
            {
                case CharacterCardEffectKind.LiskarmSecurityProtocol:
                {
                    var placement = influenceService.PlaceAtomically(state, playerId, new[]
                    {
                        GetParameter(parameters, CharacterEffectParameterKeys.PlacementSlotId1),
                        GetParameter(parameters, CharacterEffectParameterKeys.PlacementSlotId2)
                    });
                    if (!placement.Succeeded)
                    {
                        return placement.Validation;
                    }

                    state.DelayedCharacterEffects.Add(new DelayedCharacterEffectState
                    {
                        EffectType = CharacterPendingChoiceTypes.LiskarmCleanupRemoval,
                        PlayerId = playerId,
                        CardId = cardId
                    });
                    return ValidationResult.Success;
                }
                case CharacterCardEffectKind.LiskarmControlPosition:
                {
                    if (player.Resources.GoldVoucher < 3)
                    {
                        return Failure(CommandErrorCode.InsufficientResource, "雷蛇计谋需要支付3金券。");
                    }

                    var targetId = GetParameter(parameters, CharacterEffectParameterKeys.TargetInfluenceSlotId);
                    var target = influenceService.FindInfluence(state, targetId);
                    if (target == null || target.PlayerId == playerId)
                    {
                        return Failure(CommandErrorCode.InvalidTarget, "雷蛇计谋必须选择一个对手影响力。");
                    }

                    var opponentCityBlocksPlacement = !string.IsNullOrEmpty(target.LocationId) &&
                                                      HasOpponentCityAt(state, playerId, target.LocationId);
                    var replacement = opponentCityBlocksPlacement
                        ? influenceService.Remove(state, targetId)
                        : influenceService.Replace(state, targetId, playerId);
                    if (!replacement.Succeeded)
                    {
                        return replacement.Validation;
                    }

                    player.Resources.GoldVoucher -= 3;
                    return ValidationResult.Success;
                }
                case CharacterCardEffectKind.ElysiumLogistics:
                {
                    var resources = ReferencePlayerResources(state);
                    var score = player.Score;
                    var result = PlanElysiumLogistics(playerId, parameters, resources);
                    player.Score = score;
                    return result;
                }
                case CharacterCardEffectKind.ElysiumNavigation:
                {
                    if (player.Resources.OriginiumShard < 3)
                    {
                        return Failure(CommandErrorCode.InsufficientResource, "极境计谋需要支付3源石碎片。");
                    }

                    var targetLocationId = GetParameter(parameters, CharacterEffectParameterKeys.TargetLocationId);
                    var movementService = ResolveMovementService(state);
                    var validation = movementService.CanRaidCityForCharacter(state, playerId, targetLocationId);
                    if (!validation.IsValid)
                    {
                        return validation;
                    }

                    var raid = movementService.RaidCityForCharacter(state, playerId, targetLocationId);
                    if (!raid.Succeeded)
                    {
                        return raid.Validation;
                    }

                    player.Resources.OriginiumShard -= 3;
                    return ValidationResult.Success;
                }
                case CharacterCardEffectKind.TexasSpecialDelivery:
                {
                    var resources = ReferencePlayerResources(state);
                    var score = player.Score;
                    return PlanTexasSpecialDelivery(
                        playerId,
                        parameters,
                        resources,
                        state.Decks.FacilitySupply,
                        state.Decks.FacilityDeck);
                }
                case CharacterCardEffectKind.TexasRemoveAndDoubleMove:
                {
                    if (player.Resources.GoldVoucher < 3)
                    {
                        return Failure(CommandErrorCode.InsufficientResource, "德克萨斯计谋需要支付3金券。");
                    }

                    var moves = new[]
                    {
                        new InfluenceMoveRequest(GetParameter(parameters, CharacterEffectParameterKeys.MoveSourceSlotId1), GetParameter(parameters, CharacterEffectParameterKeys.MoveTargetSlotId1)),
                        new InfluenceMoveRequest(GetParameter(parameters, CharacterEffectParameterKeys.MoveSourceSlotId2), GetParameter(parameters, CharacterEffectParameterKeys.MoveTargetSlotId2))
                    };
                    var move = influenceService.RemoveThenMoveAtomically(
                        state,
                        playerId,
                        GetParameter(parameters, CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId),
                        moves);
                    if (!move.Succeeded)
                    {
                        return move.Validation;
                    }

                    player.Resources.GoldVoucher -= 3;
                    return ValidationResult.Success;
                }
                default:
                    return Failure(CommandErrorCode.InvalidTarget, "该角色牌效果无法按棋盘效果结算。");
            }
        }

        private IMapQueryService ResolveMapQuery(GameState state)
        {
            return sharedMapQuery ?? CreateMapQuery(state);
        }

        private InfluenceService ResolveInfluenceService(GameState state)
        {
            return sharedInfluenceService ?? new InfluenceService(ResolveMapQuery(state));
        }

        private CityMovementService ResolveMovementService(GameState state)
        {
            if (sharedMovementService != null)
            {
                return sharedMovementService;
            }

            var mapQuery = ResolveMapQuery(state);
            return new CityMovementService(
                mapQuery,
                ResolveInfluenceService(state),
                new TravelCostService(mapQuery),
                new EventDeckService(),
                new ResourceTokenService());
        }

        private static IMapQueryService CreateMapQuery(GameState state)
        {
            return new MapQueryService(state.MapId == StaticMapDefinitions.ThreePlayerMapId
                ? StaticMapDefinitions.CreateThreePlayerPlaceholder()
                : StaticMapDefinitions.CreateFourPlayerMap());
        }

        private static Dictionary<int, ResourceSet> ReferencePlayerResources(GameState state)
        {
            var resources = new Dictionary<int, ResourceSet>();
            for (var i = 0; i < state.Players.Count; i++)
            {
                resources[state.Players[i].PlayerId] = state.Players[i].Resources;
            }

            return resources;
        }

        private static bool HasOpponentCityAt(GameState state, int playerId, string locationId)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                if (state.Players[i].PlayerId != playerId && state.Players[i].CityLocationId == locationId)
                {
                    return true;
                }
            }

            return false;
        }

        private static ValidationResult ValidateUse(
            GameState state,
            int playerId,
            string cardId,
            string effectMode,
            string effectOrder)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
            {
                return Failure(CommandErrorCode.WrongPhase, "只能在行动轮使用角色牌。");
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return Failure(CommandErrorCode.InvalidPlayer, "使用角色牌的玩家不存在。");
            }

            if (state.CurrentPlayerId != playerId)
            {
                return Failure(CommandErrorCode.NotCurrentPlayer, "只能在自己的行动窗口使用角色牌。");
            }

            var resolvingSecondEffect = IsResolvingSecondEffect(state, playerId, cardId, effectMode);
            if (state.HasPendingChoice() && !resolvingSecondEffect)
            {
                return Failure(CommandErrorCode.PendingChoiceRequired, "请先处理待选择效果。");
            }

            if (player.UsedCharacterThisRound)
            {
                return Failure(CommandErrorCode.InvalidTarget, "本回合已经使用过角色牌。");
            }

            if (string.IsNullOrEmpty(cardId) || player.CoveredCharacterCardId != cardId)
            {
                return Failure(CommandErrorCode.InvalidTarget, "只能使用本回合盖放的角色牌。");
            }

            var definition = CharacterCardDatabase.Get(cardId);
            if (definition == null)
            {
                return Failure(CommandErrorCode.InvalidTarget, "角色牌定义不存在。");
            }

            if ((effectMode == CharacterEffectModes.Strategy && definition.StrategyEffect == CharacterCardEffectKind.Unsupported) ||
                (effectMode == CharacterEffectModes.Tactic && definition.TacticEffect == CharacterCardEffectKind.Unsupported) ||
                (effectMode == CharacterEffectModes.Both &&
                 (definition.StrategyEffect == CharacterCardEffectKind.Unsupported || definition.TacticEffect == CharacterCardEffectKind.Unsupported)))
            {
                return Failure(CommandErrorCode.InvalidTarget, "所选角色牌效果尚未实现，不能选择该侧或同时结算两侧。");
            }

            if (effectMode != CharacterEffectModes.Strategy &&
                effectMode != CharacterEffectModes.Tactic &&
                effectMode != CharacterEffectModes.Both)
            {
                return Failure(CommandErrorCode.InvalidTarget, "角色牌效果模式无效。");
            }

            if (effectMode == CharacterEffectModes.Both)
            {
                if (playerId != state.StartPlayerId)
                {
                    return Failure(CommandErrorCode.InvalidTarget, "非起始玩家只能选择策略或计谋其中一个。");
                }

                if (effectOrder != CharacterEffectOrders.StrategyFirst && effectOrder != CharacterEffectOrders.TacticFirst)
                {
                    return Failure(CommandErrorCode.InvalidTarget, "同时使用两个效果时必须指定结算顺序。");
                }
            }

            if (state.PendingCharacterEffect != null &&
                state.PendingCharacterEffect.ChoiceType == CharacterPendingChoiceTypes.SecondEffectExecution &&
                !resolvingSecondEffect)
            {
                return Failure(CommandErrorCode.InvalidTarget, "只能发动已确认的第二个角色牌效果。");
            }

            return ValidationResult.Success;
        }

        private ValidationResult ApplyEffectPlan(
            CharacterCardEffectKind effect,
            GameState state,
            int playerId,
            IDictionary<string, string> parameters,
            Dictionary<int, ResourceSet> resources,
            List<string> facilitySupply,
            List<string> facilityDeck,
            ref int score)
        {
            switch (effect)
            {
                case CharacterCardEffectKind.CannotTradeChannel:
                    return PlanCannotTradeChannel(playerId, parameters, resources);
                case CharacterCardEffectKind.CannotRequisition:
                    return PlanCannotRequisition(state, playerId, parameters, resources, ref score);
                case CharacterCardEffectKind.ElysiumLogistics:
                    return PlanElysiumLogistics(playerId, parameters, resources);
                case CharacterCardEffectKind.TexasSpecialDelivery:
                    return PlanTexasSpecialDelivery(playerId, parameters, resources, facilitySupply, facilityDeck);
                default:
                    return Failure(CommandErrorCode.InvalidTarget, "该角色牌效果尚未完成结构化，当前不可使用。");
            }
        }

        private static ValidationResult PlanElysiumLogistics(
            int playerId,
            IDictionary<string, string> parameters,
            Dictionary<int, ResourceSet> resources)
        {
            ResourceType selected;
            if (!TryParseRequisitionResource(GetParameter(parameters, CharacterEffectParameterKeys.ResourceType), out selected))
            {
                return Failure(CommandErrorCode.InvalidTarget, "极境只能选择源岩、源石碎片或异铁。");
            }

            var held = resources[playerId];
            if (!IsMinimumBasicResource(held, selected))
            {
                return Failure(CommandErrorCode.InvalidTarget, "极境只能选择当前持有数量并列最少的资源。");
            }

            held.Set(selected, held.Get(selected) + 4);
            return ValidationResult.Success;
        }

        public static bool IsMinimumBasicResource(ResourceSet resources, ResourceType resourceType)
        {
            if (resources == null ||
                (resourceType != ResourceType.Originium &&
                 resourceType != ResourceType.OriginiumShard &&
                 resourceType != ResourceType.Iron))
            {
                return false;
            }

            var minimum = Math.Min(
                resources.Originium,
                Math.Min(resources.OriginiumShard, resources.Iron));
            return resources.Get(resourceType) == minimum;
        }

        private static ValidationResult PlanTexasSpecialDelivery(
            int playerId,
            IDictionary<string, string> parameters,
            Dictionary<int, ResourceSet> resources,
            List<string> facilitySupply,
            List<string> facilityDeck)
        {
            var facilityCardId = GetParameter(parameters, CharacterEffectParameterKeys.FacilityCardId);
            if (string.IsNullOrEmpty(facilityCardId) || !facilitySupply.Contains(facilityCardId))
            {
                return Failure(CommandErrorCode.InvalidTarget, "德克萨斯必须选择设施供应区中的一张设施牌。");
            }

            resources[playerId].GoldVoucher += 12;
            FacilitySupplyService.ReturnSupplyCardToDeck(
                facilitySupply,
                facilityDeck,
                facilityCardId);
            return ValidationResult.Success;
        }

        private static ValidationResult UseTinMan(
            GameState state,
            int playerId,
            string cardId,
            string effectMode,
            string effectOrder,
            IDictionary<string, string> parameters,
            bool resolvingSecondEffect,
            bool offerSecondEffect)
        {
            var projected = CloneForCharacterEffect(state);
            ValidationResult result;
            if (effectMode == CharacterEffectModes.Strategy)
            {
                result = BeginTinManStrategy(
                    projected,
                    playerId,
                    cardId,
                    ShouldOfferSecondEffect(
                        projected,
                        playerId,
                        effectMode,
                        CharacterCardDatabase.Get(cardId),
                        resolvingSecondEffect,
                        offerSecondEffect)
                        ? CharacterEffectModes.Tactic
                        : string.Empty,
                    resolvingSecondEffect,
                    false);
            }
            else if (effectMode == CharacterEffectModes.Tactic)
            {
                result = BeginTinManDeepPlanning(
                    projected,
                    playerId,
                    cardId,
                    false,
                    ShouldOfferSecondEffect(projected, playerId, effectMode, CharacterCardDatabase.Get(cardId), resolvingSecondEffect, offerSecondEffect)
                        ? CharacterEffectModes.Strategy
                        : string.Empty,
                    resolvingSecondEffect);
            }
            else if (effectOrder == CharacterEffectOrders.StrategyFirst)
            {
                result = BeginTinManStrategy(
                    projected,
                    playerId,
                    cardId,
                    string.Empty,
                    false,
                    true);
            }
            else
            {
                result = BeginTinManDeepPlanning(
                    projected,
                    playerId,
                    cardId,
                    true,
                    string.Empty,
                    false);
            }

            if (!result.IsValid)
            {
                return result;
            }

            CommitCharacterEffectProjection(state, projected);
            return ValidationResult.Success;
        }

        private static ValidationResult BeginTinManStrategy(
            GameState state,
            int playerId,
            string cardId,
            string remainingEffectMode,
            bool isSecondEffect,
            bool resolveTacticAfterStrategy)
        {
            var player = state.FindPlayer(playerId);
            player.Score += 1;
            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = CharacterPendingChoiceTypes.TinManFirstPurchase,
                PlayerId = playerId,
                CardId = cardId,
                OptionIds = CreateTinManPurchaseOptions(
                    CharacterPendingChoiceTypes.TinManFirstPurchase,
                    player.Resources.GoldVoucher),
                ResolveTinManTacticAfterStrategy = resolveTacticAfterStrategy,
                RemainingEffectMode = remainingEffectMode ?? string.Empty,
                IsSecondEffect = isSecondEffect
            };
            return ValidationResult.Success;
        }

        private static List<string> CreateTinManPurchaseOptions(string choiceType, int goldVoucher)
        {
            var options = new List<string>
            {
                CharacterEffectChoiceIds.TinManFinishPurchasing
            };
            if (choiceType == CharacterPendingChoiceTypes.TinManFirstPurchase &&
                goldVoucher >= TinManFirstPureOriginiumCost)
            {
                options.Add(CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium);
            }
            else if (choiceType == CharacterPendingChoiceTypes.TinManSecondPurchase &&
                     goldVoucher >= TinManSecondPureOriginiumCost)
            {
                options.Add(CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium);
            }

            return options;
        }

        private static ValidationResult ResolveTinManStrategyPurchaseChoice(
            GameState state,
            int playerId,
            string choice)
        {
            var projected = CloneForCharacterEffect(state);
            var pending = projected.PendingCharacterEffect;
            var player = projected.FindPlayer(playerId);
            if (pending.OptionIds == null || !pending.OptionIds.Contains(choice))
            {
                return Failure(CommandErrorCode.InvalidTarget, "该选项不属于锡人当前购买步骤。");
            }

            if (choice == CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium)
            {
                if (pending.ChoiceType != CharacterPendingChoiceTypes.TinManFirstPurchase)
                {
                    return Failure(CommandErrorCode.InvalidTarget, "锡人第一笔购买已经处理，不能重复支付。");
                }

                if (player.Resources.GoldVoucher < TinManFirstPureOriginiumCost)
                {
                    return Failure(CommandErrorCode.InsufficientResource, "金券不足，无法完成锡人第一笔购买。");
                }

                player.Resources.GoldVoucher -= TinManFirstPureOriginiumCost;
                player.Resources.PureOriginium += 1;
                pending.TinManPurchasePureOriginium12 = true;
                pending.ChoiceType = CharacterPendingChoiceTypes.TinManSecondPurchase;
                pending.OptionIds = CreateTinManPurchaseOptions(
                    CharacterPendingChoiceTypes.TinManSecondPurchase,
                    player.Resources.GoldVoucher);
                CommitCharacterEffectProjection(state, projected);
                return ValidationResult.Success;
            }

            if (choice == CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium)
            {
                if (pending.ChoiceType != CharacterPendingChoiceTypes.TinManSecondPurchase ||
                    !pending.TinManPurchasePureOriginium12)
                {
                    return Failure(CommandErrorCode.InvalidTarget, "必须先完成锡人第一笔购买，才能进行第二笔购买。");
                }

                if (player.Resources.GoldVoucher < TinManSecondPureOriginiumCost)
                {
                    return Failure(CommandErrorCode.InsufficientResource, "金券不足，无法完成锡人第二笔购买。");
                }

                player.Resources.GoldVoucher -= TinManSecondPureOriginiumCost;
                player.Resources.PureOriginium += 1;
                pending.TinManPurchasePureOriginium15 = true;
            }
            else if (choice != CharacterEffectChoiceIds.TinManFinishPurchasing)
            {
                return Failure(CommandErrorCode.InvalidTarget, "请选择购买当前至纯源石或取消并结算。");
            }

            var cardId = pending.CardId;
            var sourceCommandId = pending.SourceCommandId;
            if (pending.ResolveTinManTacticAfterStrategy)
            {
                projected.PendingCharacterEffect = null;
                var tactic = BeginTinManDeepPlanning(
                    projected,
                    playerId,
                    cardId,
                    false,
                    string.Empty,
                    true);
                if (!tactic.IsValid)
                {
                    return tactic;
                }

                if (projected.PendingCharacterEffect != null &&
                    projected.PendingCharacterEffect.IsValid())
                {
                    projected.PendingCharacterEffect.SourceCommandId = sourceCommandId;
                }

                CommitCharacterEffectProjection(state, projected);
                return ValidationResult.Success;
            }

            var completedSecondEffect = pending.IsSecondEffect;
            var offerRemainingEffect = !string.IsNullOrEmpty(pending.RemainingEffectMode);
            projected.PendingCharacterEffect = null;
            CompleteOrOfferSecondEffect(
                projected,
                player,
                cardId,
                CharacterEffectModes.Strategy,
                CharacterCardDatabase.Get(cardId),
                completedSecondEffect,
                offerRemainingEffect);
            CommitCharacterEffectProjection(state, projected);
            return ValidationResult.Success;
        }

        private static ValidationResult BeginTinManDeepPlanning(
            GameState state,
            int playerId,
            string cardId,
            bool resolveStrategyAfterRecall,
            string remainingEffectMode,
            bool isSecondEffect)
        {
            var player = state.FindPlayer(playerId);
            if (player.DiscardCardIds.Count == 0)
            {
                if (resolveStrategyAfterRecall)
                {
                    return BeginTinManStrategy(
                        state,
                        playerId,
                        cardId,
                        string.Empty,
                        false,
                        false);
                }

                CompleteOrOfferSecondEffect(
                    state,
                    player,
                    cardId,
                    CharacterEffectModes.Tactic,
                    CharacterCardDatabase.Get(cardId),
                    isSecondEffect,
                    !string.IsNullOrEmpty(remainingEffectMode));
                return ValidationResult.Success;
            }

            state.PendingCharacterEffect = new PendingCharacterEffectState
            {
                ChoiceType = CharacterPendingChoiceTypes.TinManDiscard,
                PlayerId = playerId,
                CardId = cardId,
                RemainingCardIds = new List<string>(player.DiscardCardIds),
                OptionIds = new List<string>
                {
                    CharacterEffectChoiceIds.GainGold,
                    CharacterEffectChoiceIds.MoveInfluence
                },
                ResolveTinManStrategyAfterRecall = resolveStrategyAfterRecall,
                RemainingEffectMode = remainingEffectMode ?? string.Empty,
                IsSecondEffect = isSecondEffect
            };
            return ValidationResult.Success;
        }

        private ValidationResult ResolveTinManPendingChoice(
            GameState state,
            int playerId,
            string choice,
            string sourceInfluenceSlotId,
            string targetInfluenceSlotId)
        {
            var projected = CloneForCharacterEffect(state);
            var pending = projected.PendingCharacterEffect;
            var player = projected.FindPlayer(playerId);
            if (choice == CharacterEffectChoiceIds.GainGold)
            {
                player.Resources.GoldVoucher += 5;
            }
            else if (choice == CharacterEffectChoiceIds.MoveInfluence)
            {
                var moveResult = ResolveInfluenceService(projected).Move(
                    projected,
                    playerId,
                    sourceInfluenceSlotId,
                    targetInfluenceSlotId);
                if (!moveResult.Succeeded)
                {
                    return moveResult.Validation;
                }
            }
            else
            {
                return Failure(CommandErrorCode.InvalidTarget, "锡人的弃牌选择只能是获得5金券或移动1影响力。");
            }

            var resolvedCardId = pending.RemainingCardIds[0];
            pending.RemainingCardIds.RemoveAt(0);
            pending.ResolvedCardIds.Add(resolvedCardId);

            if (pending.RemainingCardIds.Count > 0)
            {
                CommitCharacterEffectProjection(state, projected);
                return ValidationResult.Success;
            }

            for (var i = 0; i < pending.ResolvedCardIds.Count; i++)
            {
                var recalledCardId = pending.ResolvedCardIds[i];
                player.DiscardCardIds.Remove(recalledCardId);
                if (!player.HandCardIds.Contains(recalledCardId))
                {
                    player.HandCardIds.Add(recalledCardId);
                }
            }

            if (pending.ResolveTinManStrategyAfterRecall)
            {
                var sourceCommandId = pending.SourceCommandId;
                projected.PendingCharacterEffect = null;
                var strategy = BeginTinManStrategy(
                    projected,
                    playerId,
                    pending.CardId,
                    string.Empty,
                    false,
                    false);
                if (!strategy.IsValid)
                {
                    return strategy;
                }

                projected.PendingCharacterEffect.SourceCommandId = sourceCommandId;
                CommitCharacterEffectProjection(state, projected);
                return ValidationResult.Success;
            }

            var tinManCardId = pending.CardId;
            var completedSecondEffect = pending.IsSecondEffect;
            projected.PendingCharacterEffect = null;
            CompleteOrOfferSecondEffect(
                projected,
                player,
                tinManCardId,
                CharacterEffectModes.Tactic,
                CharacterCardDatabase.Get(tinManCardId),
                completedSecondEffect,
                !string.IsNullOrEmpty(pending.RemainingEffectMode));
            CommitCharacterEffectProjection(state, projected);
            return ValidationResult.Success;
        }

        private static void CompleteCharacterUse(PlayerState player, string cardId)
        {
            player.CoveredCharacterCardId = string.Empty;
            player.CoveredCharacterCardIds.Clear();
            if (!player.DiscardCardIds.Contains(cardId))
            {
                player.DiscardCardIds.Add(cardId);
            }

            player.UsedCharacterThisRound = true;
        }

        private static bool IsResolvingSecondEffect(GameState state, int playerId, string cardId, string effectMode)
        {
            var pending = state == null ? null : state.PendingCharacterEffect;
            return pending != null &&
                   pending.IsValid() &&
                   pending.ChoiceType == CharacterPendingChoiceTypes.SecondEffectExecution &&
                   pending.PlayerId == playerId &&
                   pending.CardId == cardId &&
                   pending.RemainingEffectMode == effectMode;
        }

        private static bool ShouldOfferSecondEffect(
            GameState state,
            int playerId,
            string completedEffectMode,
            CharacterCardDefinition definition,
            bool resolvingSecondEffect,
            bool offerSecondEffect)
        {
            if (!offerSecondEffect || state == null || definition == null || resolvingSecondEffect || playerId != state.StartPlayerId)
            {
                return false;
            }

            if (completedEffectMode == CharacterEffectModes.Strategy)
            {
                return definition.TacticEffect != CharacterCardEffectKind.Unsupported;
            }

            return completedEffectMode == CharacterEffectModes.Tactic &&
                   definition.StrategyEffect != CharacterCardEffectKind.Unsupported;
        }

        private static void CompleteOrOfferSecondEffect(
            GameState state,
            PlayerState player,
            string cardId,
            string completedEffectMode,
            CharacterCardDefinition definition,
            bool resolvingSecondEffect,
            bool offerSecondEffect)
        {
            if (ShouldOfferSecondEffect(state, player.PlayerId, completedEffectMode, definition, resolvingSecondEffect, offerSecondEffect))
            {
                state.PendingCharacterEffect = new PendingCharacterEffectState
                {
                    ChoiceType = CharacterPendingChoiceTypes.SecondEffectDecision,
                    PlayerId = player.PlayerId,
                    CardId = cardId,
                    RemainingEffectMode = completedEffectMode == CharacterEffectModes.Strategy
                        ? CharacterEffectModes.Tactic
                        : CharacterEffectModes.Strategy,
                    OptionIds = new List<string>
                    {
                        CharacterEffectChoiceIds.ContinueSecondEffect,
                        CharacterEffectChoiceIds.FinishCharacterUse
                    }
                };
                return;
            }

            state.PendingCharacterEffect = null;
            CompleteCharacterUse(player, cardId);
        }

        private static ValidationResult ResolveSecondEffectDecision(
            GameState state,
            PlayerState player,
            PendingCharacterEffectState pending,
            string choice)
        {
            if (choice == CharacterEffectChoiceIds.FinishCharacterUse)
            {
                var cardId = pending.CardId;
                state.PendingCharacterEffect = null;
                CompleteCharacterUse(player, cardId);
                return ValidationResult.Success;
            }

            if (choice != CharacterEffectChoiceIds.ContinueSecondEffect)
            {
                return Failure(CommandErrorCode.InvalidTarget, "请选择是否发动角色牌的第二个效果。");
            }

            pending.ChoiceType = CharacterPendingChoiceTypes.SecondEffectExecution;
            pending.OptionIds.Clear();
            pending.OptionIds.Add(pending.RemainingEffectMode);
            return ValidationResult.Success;
        }

        private ValidationResult PlanCannotTradeChannel(
            int playerId,
            IDictionary<string, string> parameters,
            Dictionary<int, ResourceSet> resources)
        {
            int originium;
            int shard;
            int iron;
            int pure;
            if (!TryParseSaleAmount(parameters, CharacterEffectParameterKeys.SaleOriginium, out originium) ||
                !TryParseSaleAmount(parameters, CharacterEffectParameterKeys.SaleOriginiumShard, out shard) ||
                !TryParseSaleAmount(parameters, CharacterEffectParameterKeys.SaleIron, out iron) ||
                !TryParseSaleAmount(parameters, CharacterEffectParameterKeys.SalePureOriginium, out pure))
            {
                return Failure(CommandErrorCode.InvalidTarget, "贸易渠道的出售数量必须是非负整数。");
            }

            var sale = resourceSaleService.Sell(
                resources[playerId],
                new ResourceSaleRequest(originium, shard, iron, pure));
            if (!sale.Succeeded)
            {
                if (sale.FailureKind == ResourceSaleFailureKind.InvalidAmount)
                {
                    return Failure(CommandErrorCode.InvalidTarget, "贸易渠道的出售数量必须是非负整数。");
                }

                return Failure(CommandErrorCode.InsufficientResource, "贸易渠道出售的资源数量超过持有量。");
            }

            return ValidationResult.Success;
        }

        private static ValidationResult PlanCannotRequisition(
            GameState state,
            int playerId,
            IDictionary<string, string> parameters,
            Dictionary<int, ResourceSet> resources,
            ref int score)
        {
            ResourceType resourceType;
            if (!TryParseRequisitionResource(GetParameter(parameters, CharacterEffectParameterKeys.ResourceType), out resourceType))
            {
                return Failure(CommandErrorCode.InvalidTarget, "征收物资只能选择源岩、源石碎片或异铁。");
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                var playerResources = resources[state.Players[i].PlayerId];
                var amount = playerResources.Get(resourceType);
                playerResources.Set(resourceType, 0);
                playerResources.GoldVoucher += amount * 2;
            }

            score += 1;
            return ValidationResult.Success;
        }

        private static bool TryParseRequisitionResource(string value, out ResourceType type)
        {
            switch (value)
            {
                case "originium":
                    type = ResourceType.Originium;
                    return true;
                case "originium-shard":
                    type = ResourceType.OriginiumShard;
                    return true;
                case "iron":
                    type = ResourceType.Iron;
                    return true;
                default:
                    type = ResourceType.GoldVoucher;
                    return false;
            }
        }

        private static bool TryParseSaleAmount(
            IDictionary<string, string> parameters,
            string key,
            out int amount)
        {
            var raw = GetParameter(parameters, key);
            if (string.IsNullOrEmpty(raw))
            {
                amount = 0;
                return true;
            }

            return int.TryParse(raw, out amount);
        }

        private static string GetParameter(IDictionary<string, string> parameters, string key)
        {
            string value;
            return parameters != null && parameters.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static Dictionary<int, ResourceSet> ClonePlayerResources(GameState state)
        {
            var result = new Dictionary<int, ResourceSet>();
            for (var i = 0; i < state.Players.Count; i++)
            {
                result[state.Players[i].PlayerId] = state.Players[i].Resources.Clone();
            }

            return result;
        }

        private static void CommitResources(GameState state, Dictionary<int, ResourceSet> resources)
        {
            for (var i = 0; i < state.Players.Count; i++)
            {
                state.Players[i].Resources = resources[state.Players[i].PlayerId];
            }
        }

        private static GameState CloneForCharacterEffect(GameState source)
        {
            var clone = new GameState
            {
                GameId = source.GameId,
                Phase = source.Phase,
                Round = source.Round,
                MaxRounds = source.MaxRounds,
                StartPlayerId = source.StartPlayerId,
                CurrentPlayerId = source.CurrentPlayerId,
                ActionRound = source.ActionRound,
                UseSeatTurnOrder = source.UseSeatTurnOrder,
                MapId = source.MapId,
                EventDeckSeed = source.EventDeckSeed
            };

            for (var i = 0; i < source.Players.Count; i++)
            {
                var player = source.Players[i];
                clone.Players.Add(new PlayerState
                {
                    PlayerId = player.PlayerId,
                    Name = player.Name,
                    Color = player.Color,
                    Score = player.Score,
                    InfluenceSupply = player.InfluenceSupply,
                    HasScoreTrackMarker = player.HasScoreTrackMarker,
                    CityLocationId = player.CityLocationId,
                    HasMovedCityThisRound = player.HasMovedCityThisRound,
                    HasCollectedResourcesThisRound = player.HasCollectedResourcesThisRound,
                    ResourceCollectionStartGoldVoucher = player.ResourceCollectionStartGoldVoucher,
                    ActedMainActionThisTurn = player.ActedMainActionThisTurn,
                    UsedCharacterThisRound = player.UsedCharacterThisRound,
                    Resources = player.Resources.Clone(),
                    HandCardIds = new List<string>(player.HandCardIds),
                    CoveredCharacterCardIds = new List<string>(player.CoveredCharacterCardIds),
                    DiscardCardIds = new List<string>(player.DiscardCardIds),
                    BuiltFacilityIds = new List<string>(player.BuiltFacilityIds),
                    DeclaredCityStyleIds = new List<string>(player.DeclaredCityStyleIds),
                    DeclaredCityStyles = CloneCityStyleDeclarations(player.DeclaredCityStyles),
                    UsedSpecialActionIdsThisRound = new List<string>(player.UsedSpecialActionIdsThisRound),
                    CoveredCharacterCardId = player.CoveredCharacterCardId
                });
            }

            clone.Map.OpenLocationIds.AddRange(source.Map.OpenLocationIds);
            clone.Map.RoadRouteIds.AddRange(source.Map.RoadRouteIds);
            clone.Map.RemovedFromGameCardIds.AddRange(source.Map.RemovedFromGameCardIds);
            for (var i = 0; i < source.Map.Influences.Count; i++)
            {
                var influence = source.Map.Influences[i];
                clone.Map.Influences.Add(new InfluencePlacement
                {
                    PlayerId = influence.PlayerId,
                    SlotId = influence.SlotId,
                    LocationId = influence.LocationId,
                    RouteId = influence.RouteId
                });
            }

            for (var i = 0; i < source.Map.ResourceTokens.Count; i++)
            {
                var token = source.Map.ResourceTokens[i];
                clone.Map.ResourceTokens.Add(new ResourceTokenState
                {
                    LocationId = token.LocationId,
                    ResourceType = token.ResourceType,
                    Amount = token.Amount
                });
            }

            clone.Decks.FacilitySupply.AddRange(source.Decks.FacilitySupply);
            clone.Decks.FacilityDeck.AddRange(source.Decks.FacilityDeck);
            for (var i = 0; i < source.DelayedCharacterEffects.Count; i++)
            {
                var delayed = source.DelayedCharacterEffects[i];
                clone.DelayedCharacterEffects.Add(new DelayedCharacterEffectState
                {
                    EffectType = delayed.EffectType,
                    PlayerId = delayed.PlayerId,
                    CardId = delayed.CardId
                });
            }

            clone.PendingCharacterEffect = ClonePendingCharacterEffect(source.PendingCharacterEffect);

            return clone;
        }

        private static List<CityStyleDeclarationState> CloneCityStyleDeclarations(
            IList<CityStyleDeclarationState> declarations)
        {
            var result = new List<CityStyleDeclarationState>();
            if (declarations == null)
            {
                return result;
            }

            for (var i = 0; i < declarations.Count; i++)
            {
                var declaration = declarations[i];
                if (declaration == null)
                {
                    continue;
                }

                result.Add(new CityStyleDeclarationState
                {
                    InfluenceMarkerId = declaration.InfluenceMarkerId,
                    CityStyleId = declaration.CityStyleId,
                    MarkerArea = declaration.MarkerArea,
                    UnlockedSpecialActionId = declaration.UnlockedSpecialActionId,
                    RemainingSpecialActionUses = declaration.RemainingSpecialActionUses,
                    UsedFacilityIds = declaration.UsedFacilityIds == null
                        ? new List<string>()
                        : new List<string>(declaration.UsedFacilityIds),
                    UsedCityBoardSlotIndexes = declaration.UsedCityBoardSlotIndexes == null
                        ? new List<int>()
                        : new List<int>(declaration.UsedCityBoardSlotIndexes)
                });
            }

            return result;
        }

        private static void CommitCharacterEffectProjection(GameState target, GameState projection)
        {
            for (var i = 0; i < projection.Players.Count; i++)
            {
                var sourcePlayer = projection.Players[i];
                var targetPlayer = target.FindPlayer(sourcePlayer.PlayerId);
                targetPlayer.Score = sourcePlayer.Score;
                targetPlayer.InfluenceSupply = sourcePlayer.InfluenceSupply;
                targetPlayer.CityLocationId = sourcePlayer.CityLocationId;
                targetPlayer.HasMovedCityThisRound = sourcePlayer.HasMovedCityThisRound;
                targetPlayer.Resources = sourcePlayer.Resources.Clone();
                targetPlayer.HandCardIds.Clear();
                targetPlayer.HandCardIds.AddRange(sourcePlayer.HandCardIds);
                targetPlayer.CoveredCharacterCardIds.Clear();
                targetPlayer.CoveredCharacterCardIds.AddRange(sourcePlayer.CoveredCharacterCardIds);
                targetPlayer.DiscardCardIds.Clear();
                targetPlayer.DiscardCardIds.AddRange(sourcePlayer.DiscardCardIds);
                targetPlayer.CoveredCharacterCardId = sourcePlayer.CoveredCharacterCardId;
                targetPlayer.UsedCharacterThisRound = sourcePlayer.UsedCharacterThisRound;
            }

            target.Map.Influences.Clear();
            for (var i = 0; i < projection.Map.Influences.Count; i++)
            {
                var influence = projection.Map.Influences[i];
                target.Map.Influences.Add(new InfluencePlacement
                {
                    PlayerId = influence.PlayerId,
                    SlotId = influence.SlotId,
                    LocationId = influence.LocationId,
                    RouteId = influence.RouteId
                });
            }

            target.Decks.FacilitySupply.Clear();
            target.Decks.FacilitySupply.AddRange(projection.Decks.FacilitySupply);
            target.Decks.FacilityDeck.Clear();
            target.Decks.FacilityDeck.AddRange(projection.Decks.FacilityDeck);
            target.DelayedCharacterEffects.Clear();
            target.DelayedCharacterEffects.AddRange(projection.DelayedCharacterEffects);
            target.PendingCharacterEffect = ClonePendingCharacterEffect(projection.PendingCharacterEffect);
        }

        private static PendingCharacterEffectState ClonePendingCharacterEffect(PendingCharacterEffectState source)
        {
            if (source == null)
            {
                return null;
            }

            return new PendingCharacterEffectState
            {
                ChoiceType = source.ChoiceType,
                PlayerId = source.PlayerId,
                CardId = source.CardId,
                SourceCommandId = source.SourceCommandId,
                RemainingCardIds = new List<string>(source.RemainingCardIds),
                ResolvedCardIds = new List<string>(source.ResolvedCardIds),
                OptionIds = new List<string>(source.OptionIds),
                ResolveTinManStrategyAfterRecall = source.ResolveTinManStrategyAfterRecall,
                ResolveTinManTacticAfterStrategy = source.ResolveTinManTacticAfterStrategy,
                TinManPurchasePureOriginium12 = source.TinManPurchasePureOriginium12,
                TinManPurchasePureOriginium15 = source.TinManPurchasePureOriginium15,
                RemainingEffectMode = source.RemainingEffectMode,
                IsSecondEffect = source.IsSecondEffect
            };
        }

        private bool AllPlayersCovered(GameState state)
        {
            if (state.Players.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < state.Players.Count; i++)
            {
                if (string.IsNullOrEmpty(state.Players[i].CoveredCharacterCardId))
                {
                    return false;
                }
            }

            return true;
        }

        private int GetFirstTurnPlayerId(GameState state)
        {
            var order = turnOrderService.GetTurnOrder(state);
            return order.Count > 0 ? order[0] : state.StartPlayerId;
        }

        private int FindNextUncoveredPlayerId(GameState state, int playerId)
        {
            var order = turnOrderService.GetTurnOrder(state);
            var currentIndex = 0;
            for (var i = 0; i < order.Count; i++)
            {
                if (order[i] == playerId)
                {
                    currentIndex = i;
                    break;
                }
            }
            for (var offset = 1; offset <= order.Count; offset++)
            {
                var nextPlayerId = order[(currentIndex + offset + order.Count) % order.Count];
                var player = state.FindPlayer(nextPlayerId);
                if (player != null && string.IsNullOrEmpty(player.CoveredCharacterCardId))
                {
                    return nextPlayerId;
                }
            }

            return state.CurrentPlayerId;
        }

        private static ValidationResult Failure(CommandErrorCode code, string reason)
        {
            return ValidationResult.Failure(code, reason);
        }

    }
}
