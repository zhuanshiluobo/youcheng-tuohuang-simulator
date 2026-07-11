using System;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Presentation
{
    public sealed class BuildFacilitySelectionController
    {
        public GameCommand CreateCommand(
            GameState state,
            int playerId,
            string facilityId,
            string paymentMode)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return CreateCommand(
                playerId,
                facilityId,
                BuildFacilityService.FindFirstEmptyCityBoardSlot(state, playerId),
                paymentMode);
        }

        public GameCommand CreateCommand(
            int playerId,
            string facilityId,
            int cityBoardSlotIndex,
            string paymentMode)
        {
            return new GameCommand
            {
                Kind = GameCommandKind.BuildFacility,
                PlayerId = playerId,
                TargetId = facilityId ?? string.Empty,
                Parameters =
                {
                    { BuildFacilityCommandHandler.CityBoardSlotIndexParameter, cityBoardSlotIndex.ToString() },
                    { BuildFacilityCommandHandler.PaymentModeParameter, string.IsNullOrEmpty(paymentMode) ? BuildFacilityService.PaymentModeAuto : paymentMode }
                }
            };
        }
    }
}
