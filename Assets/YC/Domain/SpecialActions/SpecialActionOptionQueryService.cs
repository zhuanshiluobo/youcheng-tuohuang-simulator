using System;
using System.Collections.Generic;
using YC.Domain.CityStyles;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.SpecialActions
{
    public sealed class SpecialActionOptionQueryService
    {
        private readonly IMapQueryService mapQuery;
        private readonly InfluenceService influenceService;
        private readonly CityMovementService movementService;
        private readonly SpecialActionLifecycleService lifecycleService;
        private readonly MainActionBudgetService mainActionBudgetService;

        public SpecialActionOptionQueryService(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            CityMovementService movementService,
            SpecialActionLifecycleService lifecycleService)
            : this(
                mapQuery,
                influenceService,
                movementService,
                lifecycleService,
                new MainActionBudgetService())
        {
        }

        public SpecialActionOptionQueryService(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            CityMovementService movementService,
            SpecialActionLifecycleService lifecycleService,
            MainActionBudgetService mainActionBudgetService)
        {
            this.mapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            this.influenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            this.movementService = movementService ?? throw new ArgumentNullException(nameof(movementService));
            this.lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
            this.mainActionBudgetService = mainActionBudgetService ?? throw new ArgumentNullException(nameof(mainActionBudgetService));
        }

        public SpecialActionOptionQueryResult Query(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var result = new SpecialActionOptionQueryResult();
            var player = state.FindPlayer(playerId);
            if (player == null || player.DeclaredCityStyles == null)
            {
                return result;
            }

            for (var i = 0; i < player.DeclaredCityStyles.Count; i++)
            {
                var declaration = player.DeclaredCityStyles[i];
                if (declaration == null || string.IsNullOrEmpty(declaration.UnlockedSpecialActionId))
                {
                    continue;
                }

                var definition = SpecialActionDatabase.Get(declaration.UnlockedSpecialActionId);
                if (definition == null)
                {
                    continue;
                }

                result.Options.Add(BuildOption(state, player, definition, declaration));
            }

            return result;
        }

        public List<string> GetLegalInfluencePlacementSlotIds(GameState state, int playerId)
        {
            var result = new List<string>();
            for (var locationIndex = 0; locationIndex < mapQuery.Map.Locations.Count; locationIndex++)
            {
                var location = mapQuery.Map.Locations[locationIndex];
                for (var slotIndex = 0; slotIndex < location.InfluenceSlotCount; slotIndex++)
                {
                    AddIfCanPlace(state, playerId, InfluenceService.GetLocationSlotId(location.LocationId, slotIndex), result);
                }
            }

            for (var routeIndex = 0; routeIndex < mapQuery.Map.Routes.Count; routeIndex++)
            {
                var route = mapQuery.Map.Routes[routeIndex];
                for (var slotIndex = 0; slotIndex < route.InfluenceSlotCount; slotIndex++)
                {
                    AddIfCanPlace(state, playerId, InfluenceService.GetRouteSlotId(route.RouteId, slotIndex), result);
                }
            }

            return result;
        }

        public List<string> GetReplaceableInfluenceSlotIds(GameState state, int playerId)
        {
            var result = new List<string>();
            if (state == null || state.Map == null || state.Map.Influences == null)
            {
                return result;
            }

            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence != null && influence.PlayerId != playerId && !string.IsNullOrEmpty(influence.SlotId))
                {
                    result.Add(influence.SlotId);
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        public List<string> GetLegalFreeMoveTargetIds(GameState state, int playerId)
        {
            var result = new List<string>();
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var locationId = mapQuery.Map.Locations[i].LocationId;
                if (movementService.CanMoveCityForFacility(state, playerId, locationId).IsValid)
                {
                    result.Add(locationId);
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        public List<string> GetLegalRouteInfluenceSlotIds(GameState state, int playerId, string routeId)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(routeId))
            {
                return result;
            }

            MapRouteDefinition route;
            try
            {
                route = mapQuery.GetRoute(routeId);
            }
            catch (ArgumentException)
            {
                return result;
            }

            for (var i = 0; i < route.InfluenceSlotCount; i++)
            {
                AddIfCanPlace(state, playerId, InfluenceService.GetRouteSlotId(routeId, i), result);
            }

            return result;
        }

        public int GetRequiredMilitaryPlacementCount(GameState state, int playerId)
        {
            var player = state == null ? null : state.FindPlayer(playerId);
            if (player == null)
            {
                return 0;
            }

            var declarationCount = CountDeclarations(player, CityStyleDatabase.MilitaryIndustrialArea);
            var legalCount = GetLegalInfluencePlacementSlotIds(state, playerId).Count;
            return Math.Min(3, Math.Min(declarationCount, Math.Min(Math.Max(0, player.InfluenceSupply), legalCount)));
        }

        public List<SpecialActionPaymentOption> GetCompositePaymentOptions(PlayerState player)
        {
            var result = new List<SpecialActionPaymentOption>();
            if (player == null || player.Resources == null)
            {
                return result;
            }

            for (var originium = 0; originium <= 3; originium++)
            {
                var iron = 3 - originium;
                var option = new SpecialActionPaymentOption
                {
                    OptionId = "originium_" + originium + "_iron_" + iron,
                    Originium = originium,
                    OriginiumShard = 1,
                    Iron = iron
                };
                if (player.Resources.CanPay(option.ToResourceSet()))
                {
                    result.Add(option);
                }
            }

            return result;
        }

        private SpecialActionOption BuildOption(
            GameState state,
            PlayerState player,
            SpecialActionDefinition definition,
            CityStyleDeclarationState declaration)
        {
            var option = new SpecialActionOption
            {
                SpecialActionId = definition.SpecialActionId,
                CityStyleId = definition.CityStyleId,
                Name = definition.Name,
                Description = definition.Description,
                DeclarationMarkerId = declaration.InfluenceMarkerId,
                MarkerArea = declaration.MarkerArea,
                RemainingUses = declaration.RemainingSpecialActionUses,
                UsedThisRound = player.UsedSpecialActionIdsThisRound != null &&
                                player.UsedSpecialActionIdsThisRound.Contains(definition.SpecialActionId),
                FixedCost = definition.FixedCost == null ? new ResourceSet() : definition.FixedCost.Clone()
            };

            PopulateTargetsAndPayments(state, player, definition, option);
            option.DisabledReason = GetDisabledReason(state, player, definition, declaration, option);
            option.CanUse = string.IsNullOrEmpty(option.DisabledReason);
            if (option.CanUse && HasNoExecutableTarget(definition, option))
            {
                option.Warning = "当前没有可执行目标，发动后对应步骤会跳过。";
            }

            return option;
        }

        private void PopulateTargetsAndPayments(
            GameState state,
            PlayerState player,
            SpecialActionDefinition definition,
            SpecialActionOption option)
        {
            switch (definition.EffectKind)
            {
                case SpecialActionEffectKind.DeployInfluence:
                    option.LegalInfluenceSlotIds = GetLegalInfluencePlacementSlotIds(state, player.PlayerId);
                    option.RequiredTargetCount = Math.Min(
                        definition.MaximumTargetCount,
                        Math.Min(
                            CountDeclarations(player, definition.CityStyleId),
                            Math.Min(Math.Max(0, player.InfluenceSupply), option.LegalInfluenceSlotIds.Count)));
                    break;

                case SpecialActionEffectKind.ReplaceInfluence:
                    option.ReplaceableInfluenceSlotIds = GetReplaceableInfluenceSlotIds(state, player.PlayerId);
                    option.RequiredTargetCount = option.ReplaceableInfluenceSlotIds.Count > 0 ? 1 : 0;
                    break;

                case SpecialActionEffectKind.CompositePowerMove:
                    option.PaymentOptions = GetCompositePaymentOptions(player);
                    option.LegalMoveTargetIds = GetLegalFreeMoveTargetIds(state, player.PlayerId);
                    option.RequiredTargetCount = option.LegalMoveTargetIds.Count > 0 ? 1 : 0;
                    break;

                case SpecialActionEffectKind.ConsecutiveFreeMoves:
                    option.LegalMoveTargetIds = GetLegalFreeMoveTargetIds(state, player.PlayerId);
                    option.RequiredTargetCount = option.LegalMoveTargetIds.Count > 0 ? 1 : 0;
                    break;
            }
        }

        private string GetDisabledReason(
            GameState state,
            PlayerState player,
            SpecialActionDefinition definition,
            CityStyleDeclarationState declaration,
            SpecialActionOption option)
        {
            if (state.Phase != GamePhase.ActionRound1 && state.Phase != GamePhase.ActionRound2)
            {
                return "特殊行动只能在行动轮阶段发动。";
            }

            if (state.CurrentPlayerId != player.PlayerId)
            {
                return "当前不是该玩家的行动轮。";
            }

            if (state.HasPendingChoice())
            {
                return "请先完成当前待处理选择。";
            }

            var budgetValidation = mainActionBudgetService.ValidateCanSpend(state, player.PlayerId);
            if (!budgetValidation.IsValid)
            {
                return budgetValidation.Reason;
            }

            if (option.UsedThisRound)
            {
                return "该特殊行动本回合已经使用过。";
            }

            var markerValidation = lifecycleService.ValidateAvailable(player, definition, declaration.InfluenceMarkerId);
            if (!markerValidation.IsValid)
            {
                return markerValidation.Reason;
            }

            if (definition.LocksCharacterCard && player.UsedCharacterThisTurn)
            {
                return "本玩家行动轮已使用角色牌，不能发动该特殊行动。";
            }

            if (definition.EffectKind == SpecialActionEffectKind.CompositePowerMove)
            {
                if (option.PaymentOptions.Count == 0)
                {
                    return "无法支付 1 个源石碎片及合计 3 个源岩/异铁。";
                }
            }
            else if (definition.FixedCost != null && !player.Resources.CanPay(definition.FixedCost))
            {
                return "资源不足，无法支付特殊行动费用。";
            }

            return string.Empty;
        }

        private static bool HasNoExecutableTarget(SpecialActionDefinition definition, SpecialActionOption option)
        {
            return definition.EffectKind == SpecialActionEffectKind.DeployInfluence ||
                   definition.EffectKind == SpecialActionEffectKind.ReplaceInfluence ||
                   definition.EffectKind == SpecialActionEffectKind.CompositePowerMove ||
                   definition.EffectKind == SpecialActionEffectKind.ConsecutiveFreeMoves
                ? option.RequiredTargetCount == 0
                : false;
        }

        private void AddIfCanPlace(GameState state, int playerId, string slotId, List<string> result)
        {
            if (influenceService.CanPlace(state, playerId, slotId).IsValid)
            {
                result.Add(slotId);
            }
        }

        private static int CountDeclarations(PlayerState player, string cityStyleId)
        {
            var count = 0;
            if (player.DeclaredCityStyles != null)
            {
                for (var i = 0; i < player.DeclaredCityStyles.Count; i++)
                {
                    var declaration = player.DeclaredCityStyles[i];
                    if (declaration != null && declaration.CityStyleId == cityStyleId)
                    {
                        count += 1;
                    }
                }
            }

            return count;
        }
    }
}
