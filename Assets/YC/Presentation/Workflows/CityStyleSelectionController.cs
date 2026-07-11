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

        public CityStyleSelectionController()
            : this(new DeclareCityStyleService())
        {
        }

        public CityStyleSelectionController(DeclareCityStyleService declareCityStyleService)
        {
            this.declareCityStyleService = declareCityStyleService ?? throw new ArgumentNullException(nameof(declareCityStyleService));
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
}
