using System;
using System.Collections.Generic;
using YC.Application.Gameplay;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed class CityStyleSelectionController
    {
        private readonly DeclareCityStyleService declareCityStyleService;
        private readonly CityStylePatternMatcher patternMatcher;

        public CityStyleSelectionController()
            : this(new DeclareCityStyleService())
        {
        }

        public CityStyleSelectionController(DeclareCityStyleService declareCityStyleService)
        {
            this.declareCityStyleService = declareCityStyleService ?? throw new ArgumentNullException(nameof(declareCityStyleService));
            patternMatcher = new CityStylePatternMatcher();
        }

        public List<CityStyleOptionViewModel> BuildOptions(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var result = new List<CityStyleOptionViewModel>();
            var styleIds = state.Decks.CityStyleSupply.Count > 0
                ? state.Decks.CityStyleSupply
                : CityStyleDatabase.DefaultSupplyIds;

            for (var i = 0; i < styleIds.Count; i++)
            {
                var styleId = styleIds[i];
                CityStyleDefinition cityStyle;
                if (!CityStyleDatabase.TryGet(styleId, out cityStyle))
                {
                    continue;
                }

                var validation = declareCityStyleService.Validate(state, playerId, cityStyle.CityStyleId);
                result.Add(new CityStyleOptionViewModel
                {
                    CityStyleId = cityStyle.CityStyleId,
                    Name = cityStyle.Name,
                    Description = cityStyle.Description,
                    Score = cityStyle.Score,
                    CanDeclare = validation.IsValid,
                    Reason = validation.IsValid ? "可宣告" : validation.Reason
                });
            }

            return result;
        }

        public CityStyleSelectionValidationViewModel ValidateSelection(
            GameState state,
            int playerId,
            string cityStyleId,
            IEnumerable<int> usedCityBoardSlotIndexes)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var selectedSlotIndexes = BuildSortedSlotIndexes(usedCityBoardSlotIndexes);
            CityStyleDefinition cityStyle;
            CityStyleDatabase.TryGet(cityStyleId, out cityStyle);
            var requiredFacilityCount = GetRequiredFacilityCount(cityStyle);
            var validation = declareCityStyleService.Validate(
                state,
                playerId,
                cityStyleId,
                selectedSlotIndexes);
            if (!validation.IsValid)
            {
                return new CityStyleSelectionValidationViewModel(
                    false,
                    validation.Reason,
                    0,
                    requiredFacilityCount,
                    selectedSlotIndexes.Count);
            }

            var match = patternMatcher.MatchSelected(
                state,
                playerId,
                cityStyle,
                selectedSlotIndexes);
            if (!match.Succeeded)
            {
                return new CityStyleSelectionValidationViewModel(
                    false,
                    match.Validation.Reason,
                    0,
                    requiredFacilityCount,
                    selectedSlotIndexes.Count);
            }

            return new CityStyleSelectionValidationViewModel(
                true,
                "可确认",
                match.RotationDegrees,
                requiredFacilityCount,
                selectedSlotIndexes.Count);
        }

        public GameCommand CreateCommand(int playerId, string cityStyleId)
        {
            return new GameCommand
            {
                Kind = GameCommandKind.DeclareCityStyle,
                PlayerId = playerId,
                TargetId = cityStyleId ?? string.Empty,
                Parameters =
                {
                    { DeclareCityStyleCommandHandler.CityStyleIdParameter, cityStyleId ?? string.Empty }
                }
            };
        }

        public GameCommand CreateCommand(
            int playerId,
            string cityStyleId,
            IEnumerable<int> usedCityBoardSlotIndexes)
        {
            var command = CreateCommand(playerId, cityStyleId);
            var selectedSlotIndexes = BuildSortedSlotIndexes(usedCityBoardSlotIndexes);
            var encodedSlotIndexes = new List<string>(selectedSlotIndexes.Count);
            for (var i = 0; i < selectedSlotIndexes.Count; i++)
            {
                encodedSlotIndexes.Add(selectedSlotIndexes[i].ToString());
            }

            command.Parameters[DeclareCityStyleCommandHandler.UsedCityBoardSlotIndexesParameter] =
                string.Join(",", encodedSlotIndexes.ToArray());
            return command;
        }

        private static List<int> BuildSortedSlotIndexes(IEnumerable<int> slotIndexes)
        {
            var result = slotIndexes == null
                ? new List<int>()
                : new List<int>(slotIndexes);
            result.Sort();
            return result;
        }

        private static int GetRequiredFacilityCount(CityStyleDefinition cityStyle)
        {
            if (cityStyle == null)
            {
                return 0;
            }

            var requirement = cityStyle.DeclarationRequirement ?? new CityStyleRequirement();
            return requirement.RequiredPatternCells != null && requirement.RequiredPatternCells.Count > 0
                ? requirement.RequiredPatternCells.Count
                : Math.Max(1, requirement.RequiredFacilityCount);
        }
    }

    public sealed class CityStyleOptionViewModel
    {
        public string CityStyleId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Score { get; set; }
        public bool CanDeclare { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class CityStyleSelectionValidationViewModel
    {
        public CityStyleSelectionValidationViewModel()
            : this(false, string.Empty, 0, 0, 0)
        {
        }

        public CityStyleSelectionValidationViewModel(
            bool canConfirm,
            string reason,
            int rotationDegrees,
            int requiredFacilityCount,
            int selectedFacilityCount)
        {
            CanConfirm = canConfirm;
            Reason = reason ?? string.Empty;
            RotationDegrees = rotationDegrees;
            RequiredFacilityCount = requiredFacilityCount;
            SelectedFacilityCount = selectedFacilityCount;
        }

        public bool CanConfirm { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int RotationDegrees { get; set; }
        public int RequiredFacilityCount { get; set; }
        public int SelectedFacilityCount { get; set; }
    }
}
