using System;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.SpecialActions
{
    public static class SpecialActionMarkerAreas
    {
        public const string UsedFromTwo = "used_from_2";
        public const string UsedFromOne = "used_from_1";
    }

    public sealed class SpecialActionLifecycleService
    {
        public ValidationResult ValidateAvailable(
            PlayerState player,
            SpecialActionDefinition definition,
            string declarationMarkerId)
        {
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "玩家不存在。");
            }

            if (definition == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "特殊行动不存在。");
            }

            var declaration = FindDeclaration(player, declarationMarkerId);
            if (declaration == null ||
                declaration.CityStyleId != definition.CityStyleId ||
                declaration.UnlockedSpecialActionId != definition.SpecialActionId)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "所选城市样式标记未解锁该特殊行动。");
            }

            if (declaration.RemainingSpecialActionUses <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "该城市样式标记的特殊行动次数已耗尽。");
            }

            var expectedArea = definition.Level >= 2
                ? (declaration.RemainingSpecialActionUses >= 2
                    ? CityStyleMarkerAreas.UsesTwo
                    : CityStyleMarkerAreas.UsesOne)
                : CityStyleMarkerAreas.Unused;
            if (declaration.MarkerArea != expectedArea)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidSource, "该城市样式标记当前不在可用次数区。");
            }

            return ValidationResult.Success;
        }

        public void MarkActivated(
            PlayerState player,
            SpecialActionDefinition definition,
            string declarationMarkerId)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            var declaration = FindDeclaration(player, declarationMarkerId);
            if (declaration == null)
            {
                throw new InvalidOperationException("找不到特殊行动对应的城市样式标记。");
            }

            if (definition.Level >= 2)
            {
                declaration.MarkerArea = declaration.RemainingSpecialActionUses >= 2
                    ? SpecialActionMarkerAreas.UsedFromTwo
                    : SpecialActionMarkerAreas.UsedFromOne;
            }
            else
            {
                declaration.MarkerArea = CityStyleMarkerAreas.Used;
                declaration.RemainingSpecialActionUses = 0;
                // 军工化按整张卡的宣告数量结算，全部己方可用标记共同发动一次。
                if (definition.SpecialActionId == SpecialActionDatabase.MilitaryIndustrialArea)
                {
                    foreach (var marker in player.DeclaredCityStyles)
                    {
                        if (marker != null && marker.CityStyleId == definition.CityStyleId &&
                            (marker.MarkerArea == CityStyleMarkerAreas.Declared ||
                             (marker.UnlockedSpecialActionId == definition.SpecialActionId &&
                              marker.MarkerArea == CityStyleMarkerAreas.Unused &&
                              marker.RemainingSpecialActionUses > 0)))
                        {
                            marker.MarkerArea = CityStyleMarkerAreas.Used;
                            marker.RemainingSpecialActionUses = 0;
                        }
                    }
                }
            }
        }

        public void CleanupRound(GameState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            for (var playerIndex = 0; playerIndex < state.Players.Count; playerIndex++)
            {
                var player = state.Players[playerIndex];
                if (player == null)
                {
                    continue;
                }

                if (player.UsedSpecialActionIdsThisRound != null)
                {
                    player.UsedSpecialActionIdsThisRound.Clear();
                }

                if (player.DeclaredCityStyles == null)
                {
                    continue;
                }

                for (var declarationIndex = 0; declarationIndex < player.DeclaredCityStyles.Count; declarationIndex++)
                {
                    var declaration = player.DeclaredCityStyles[declarationIndex];
                    // 后续宣告只是军工数量标记，不额外解锁一次行动。
                    if (declaration != null && declaration.CityStyleId == CityStyleDatabase.MilitaryIndustrialArea &&
                        string.IsNullOrEmpty(declaration.UnlockedSpecialActionId) &&
                        declaration.MarkerArea == CityStyleMarkerAreas.Used)
                        declaration.MarkerArea = CityStyleMarkerAreas.Declared;
                    if (declaration == null || string.IsNullOrEmpty(declaration.UnlockedSpecialActionId))
                    {
                        continue;
                    }

                    var definition = SpecialActionDatabase.Get(declaration.UnlockedSpecialActionId);
                    if (definition == null)
                    {
                        continue;
                    }

                    if (definition.Level < 2 && declaration.MarkerArea == CityStyleMarkerAreas.Used)
                    {
                        declaration.MarkerArea = CityStyleMarkerAreas.Unused;
                        declaration.RemainingSpecialActionUses = 1;
                        continue;
                    }

                    if (definition.Level >= 2 &&
                        (declaration.MarkerArea == SpecialActionMarkerAreas.UsedFromTwo ||
                         (declaration.MarkerArea == CityStyleMarkerAreas.Used && declaration.RemainingSpecialActionUses >= 2)))
                    {
                        declaration.MarkerArea = CityStyleMarkerAreas.UsesOne;
                        declaration.RemainingSpecialActionUses = 1;
                        continue;
                    }

                    if (definition.Level >= 2 &&
                        (declaration.MarkerArea == SpecialActionMarkerAreas.UsedFromOne ||
                         (declaration.MarkerArea == CityStyleMarkerAreas.Used && declaration.RemainingSpecialActionUses == 1)))
                    {
                        declaration.MarkerArea = CityStyleMarkerAreas.UsesZero;
                        declaration.RemainingSpecialActionUses = 0;
                    }
                }
            }
        }

        public CityStyleDeclarationState FindDeclaration(PlayerState player, string declarationMarkerId)
        {
            if (player == null || player.DeclaredCityStyles == null || string.IsNullOrEmpty(declarationMarkerId))
            {
                return null;
            }

            for (var i = 0; i < player.DeclaredCityStyles.Count; i++)
            {
                var declaration = player.DeclaredCityStyles[i];
                if (declaration != null && declaration.InfluenceMarkerId == declarationMarkerId)
                {
                    return declaration;
                }
            }

            return null;
        }
    }
}
