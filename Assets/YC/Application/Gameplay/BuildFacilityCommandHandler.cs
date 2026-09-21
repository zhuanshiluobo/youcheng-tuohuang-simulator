using System;
using System.Collections.Generic;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Application.Gameplay
{
    public sealed class BuildFacilityCommandHandler : IGameCommandHandler
    {
        public const string FacilityIdParameter = "facilityId";
        public const string CityBoardSlotIndexParameter = "cityBoardSlotIndex";
        public const string PaymentModeParameter = "paymentMode";

        private readonly BuildFacilityService buildFacilityService;
        private readonly RoundAdvanceService roundAdvanceService;
        private readonly EffectRegistry effectRegistry;

        public BuildFacilityCommandHandler()
            : this(new BuildFacilityService(), new RoundAdvanceService(), null)
        {
        }

        public BuildFacilityCommandHandler(BuildFacilityService buildFacilityService, RoundAdvanceService roundAdvanceService)
            : this(buildFacilityService, roundAdvanceService, null)
        {
        }

        public BuildFacilityCommandHandler(
            BuildFacilityService buildFacilityService,
            RoundAdvanceService roundAdvanceService,
            EffectRegistry effectRegistry)
        {
            this.buildFacilityService = buildFacilityService ?? throw new ArgumentNullException(nameof(buildFacilityService));
            this.roundAdvanceService = roundAdvanceService ?? throw new ArgumentNullException(nameof(roundAdvanceService));
            this.effectRegistry = effectRegistry;
        }

        public bool CanHandle(GameCommand command)
        {
            return command != null && command.Kind == GameCommandKind.BuildFacility;
        }

        public CommandResult Handle(GameState state, GameCommand command)
        {
            var guard = MainActionCommandGuard.Validate(state, command);
            if (!guard.IsValid)
            {
                return CommandResult.Invalid(guard);
            }

            var facilityId = GetFacilityId(command);
            int cityBoardSlotIndex;
            var slotValidation = ResolveCityBoardSlotIndex(command, out cityBoardSlotIndex);
            if (!slotValidation.IsValid)
            {
                return CommandResult.Invalid(slotValidation);
            }

            string paymentMode;
            var paymentModeValidation = ResolvePaymentMode(command, out paymentMode);
            if (!paymentModeValidation.IsValid)
            {
                return CommandResult.Invalid(paymentModeValidation);
            }

            BuildFacilityResult result;
            if (effectRegistry == null)
            {
                result = buildFacilityService.Build(
                    state,
                    command.PlayerId,
                    facilityId,
                    cityBoardSlotIndex,
                    paymentMode);
                if (!result.Succeeded)
                {
                    return CommandResult.Invalid(result.Validation);
                }
                roundAdvanceService.MarkMainActionComplete(state, command.PlayerId);
            }
            else
            {
                var executor = new EffectTreeExecutor(state, effectRegistry);
                var buildSpec = FacilityBuildEffectSpecFactory.Build(
                    command.PlayerId,
                    facilityId,
                    cityBoardSlotIndex,
                    paymentMode,
                    false,
                    command.CommandId ?? string.Empty);
                string buildEffectId;
                if (!executor.TryCreatePlayerActionEffect(
                        command.PlayerId,
                        buildSpec,
                        state.EffectRuntime.CurrentRoundExecutionId ?? string.Empty,
                        command.CommandId ?? string.Empty,
                        out buildEffectId))
                {
                    return CommandResult.Invalid(ValidationResult.Failure(
                        CommandErrorCode.UnknownCommand,
                        "建设 Effect 创建失败：" + executor.LastDiagnostic));
                }

                EffectRunReport report = executor.RunUntilQuiescent();
                if (report.Faulted)
                {
                    return CommandResult.Invalid(ValidationResult.Failure(
                        CommandErrorCode.UnknownCommand,
                        "建设 Effect 执行失败：" + report.FaultCode));
                }

                var builtNode = executor.GetNode(buildEffectId);
                if (builtNode == null)
                {
                    return CommandResult.Invalid(ValidationResult.Failure(
                        CommandErrorCode.UnknownCommand,
                        "建设 Effect 尚未完成。"));
                }

                if (builtNode.Status != EffectNodeStatus.Completed &&
                    !(builtNode.Status == EffectNodeStatus.Blocked && state.HasOpenActionableInteraction()))
                {
                    return CommandResult.Invalid(ValidationResult.Failure(
                        CommandErrorCode.UnknownCommand,
                        "建设 Effect 尚未完成。"));
                }

                result = BuildFacilityResult.Success(
                    FacilityCardDatabase.Get(facilityId),
                    cityBoardSlotIndex,
                    paymentMode);
            }

            var message = "Player " + command.PlayerId + " built " + result.Facility.Name + ".";
            return CommandResult.SuccessResult(new List<GameEvent>
            {
                new GameEvent
                {
                    Kind = GameEventKind.FacilityBuilt,
                    PlayerId = command.PlayerId,
                    SubjectId = result.Facility.FacilityId,
                    Message = message,
                    Data =
                    {
                        { "facilityName", result.Facility.Name },
                        { "cityBoardSlotIndex", result.CityBoardSlotIndex.ToString() },
                        { "paymentMode", result.PaymentMode },
                        { "score", result.Facility.Score.ToString() },
                        { "effectType", result.Facility.EffectType }
                    }
                },
                new GameEvent
                {
                    Kind = GameEventKind.ResourceChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.Facility.FacilityId,
                    Message = "Build facility paid cost and applied on-build reward."
                },
                new GameEvent
                {
                    Kind = GameEventKind.ScoreChanged,
                    PlayerId = command.PlayerId,
                    SubjectId = result.Facility.FacilityId,
                    Message = "Build facility changed score."
                }
            }, message);
        }

        private static ValidationResult ResolveCityBoardSlotIndex(GameCommand command, out int cityBoardSlotIndex)
        {
            cityBoardSlotIndex = -1;
            var encoded = GetParameter(command, CityBoardSlotIndexParameter);
            if (string.IsNullOrEmpty(encoded))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "正式建设命令必须指定城市面板槽位。");
            }

            if (!int.TryParse(encoded, out cityBoardSlotIndex))
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "城市面板槽位必须是数字。");
            }

            return ValidationResult.Success;
        }

        private static ValidationResult ResolvePaymentMode(GameCommand command, out string paymentMode)
        {
            paymentMode = GetParameter(command, PaymentModeParameter).Trim().ToLowerInvariant();
            if (paymentMode != BuildFacilityService.PaymentModeResources &&
                paymentMode != BuildFacilityService.PaymentModeGold)
            {
                return ValidationResult.Failure(
                    CommandErrorCode.InvalidTarget,
                    "正式建设命令必须指定资源或金券支付方式。");
            }

            return ValidationResult.Success;
        }

        private static string GetFacilityId(GameCommand command)
        {
            var facilityId = GetParameter(command, FacilityIdParameter);
            return string.IsNullOrEmpty(facilityId) ? command.TargetId : facilityId;
        }

        private static string GetParameter(GameCommand command, string key)
        {
            if (command.Parameters == null)
            {
                return string.Empty;
            }

            string value;
            return command.Parameters.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static FacilityPlacement FindPlacement(GameState state, int playerId, string facilityId, int slotIndex)
        {
            if (state == null || state.Map == null || state.Map.Facilities == null) return null;
            for (int i = state.Map.Facilities.Count - 1; i >= 0; i--)
            {
                FacilityPlacement placement = state.Map.Facilities[i];
                if (placement != null && placement.PlayerId == playerId &&
                    placement.FacilityCardId == facilityId && placement.CityBoardSlotIndex == slotIndex)
                    return placement;
            }
            return null;
        }
    }
}
