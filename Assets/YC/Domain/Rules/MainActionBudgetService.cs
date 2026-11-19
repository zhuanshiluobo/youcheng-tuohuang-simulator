using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Commands;
using YC.Domain.State;

namespace YC.Domain.Rules
{
    public sealed class MainActionBudgetService
    {
        public const int DefaultMainActionsPerTurn = 1;
        public const string Deploy = "deploy", Dispatch = "dispatch", Explore = "explore", MoveCity = "move_city", Build = "build", Special = "special";
        private static readonly string[] SupportedTypes = { Deploy, Dispatch, Explore, MoveCity, Build, Special };
        private static readonly string[] LegacyRestrictedTypes = { Build, Explore, MoveCity };

        public static bool IsSupportedActionType(string actionType) => SupportedTypes.Contains(actionType, StringComparer.Ordinal);

        public static string ActionTypeFor(GameCommandKind kind)
        {
            switch (kind)
            {
                case GameCommandKind.DeployInfluence: return Deploy;
                case GameCommandKind.DispatchInfluence: return Dispatch;
                case GameCommandKind.ExploreLocation: return Explore;
                case GameCommandKind.MoveCity: return MoveCity;
                case GameCommandKind.BuildFacility: return Build;
                case GameCommandKind.UseSpecialAction: return Special;
                default: return null;
            }
        }

        private static bool Allows(MainActionBudgetBatch batch, string actionType) =>
            actionType == null || batch.AllowedActionTypes.Count == 0 || batch.AllowedActionTypes.Contains(actionType);

        private static List<MainActionBudgetBatch> LegacyBatches(PlayerState player)
        {
            var batches = new List<MainActionBudgetBatch>();
            if (player.RestrictedMainActionsThisTurn > 0)
                batches.Add(new MainActionBudgetBatch { Remaining = player.RestrictedMainActionsThisTurn, AllowedActionTypes = LegacyRestrictedTypes.ToList() });
            int unrestricted = player.RemainingMainActionsThisTurn - player.RestrictedMainActionsThisTurn;
            if (unrestricted > 0) batches.Add(new MainActionBudgetBatch { Remaining = unrestricted });
            return batches;
        }

        private static List<MainActionBudgetBatch> CurrentBatches(PlayerState player) => player.MainActionBudgetBatches ?? LegacyBatches(player);

        private static List<MainActionBudgetBatch> EnsureBatches(PlayerState player) =>
            player.MainActionBudgetBatches ?? (player.MainActionBudgetBatches = LegacyBatches(player));

        private static void SyncTotals(PlayerState player)
        {
            player.RemainingMainActionsThisTurn = checked(player.MainActionBudgetBatches.Sum(batch => batch.Remaining));
            player.RestrictedMainActionsThisTurn = checked(player.MainActionBudgetBatches.Where(batch => !Allows(batch, Special)).Sum(batch => batch.Remaining));
            player.ActedMainActionThisTurn = player.RemainingMainActionsThisTurn <= 0;
        }

        public static bool HasValidBatches(PlayerState player)
        {
            if (player == null || player.RemainingMainActionsThisTurn < 0 || player.RestrictedMainActionsThisTurn < 0 ||
                player.RestrictedMainActionsThisTurn > player.RemainingMainActionsThisTurn) return false;
            if (player.MainActionBudgetBatches == null) return true;
            if (player.MainActionBudgetBatches.Any(batch => batch == null || batch.Remaining <= 0 || batch.AllowedActionTypes == null ||
                batch.AllowedActionTypes.Any(type => !IsSupportedActionType(type)) ||
                batch.AllowedActionTypes.Distinct(StringComparer.Ordinal).Count() != batch.AllowedActionTypes.Count)) return false;
            long total = player.MainActionBudgetBatches.Sum(batch => (long)batch.Remaining);
            long restricted = player.MainActionBudgetBatches.Where(batch => !Allows(batch, Special)).Sum(batch => (long)batch.Remaining);
            return total == player.RemainingMainActionsThisTurn && restricted == player.RestrictedMainActionsThisTurn;
        }

        public ValidationResult ValidateCanSpend(GameState state, int playerId, string actionType = null)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidPlayer, "执行主要行动的玩家不存在。");
            }

            if (actionType != null && !IsSupportedActionType(actionType))
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "主要行动类型无效。");
            if (!HasValidBatches(player))
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "主要行动额度状态无效。");

            if (player.ActedMainActionThisTurn || player.RemainingMainActionsThisTurn <= 0)
            {
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "本玩家行动轮的主要行动次数已用尽。");
            }

            if (!CurrentBatches(player).Any(batch => batch.Remaining > 0 && Allows(batch, actionType)))
                return ValidationResult.Failure(CommandErrorCode.InvalidTarget, "当前没有可用于此行动的主要行动额度。");

            return ValidationResult.Success;
        }

        public bool HasAvailableMainAction(GameState state, int playerId, string actionType = null)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            return player != null && ValidateCanSpend(state, playerId, actionType).IsValid;
        }

        public ValidationResult ValidateCanSpendSpecial(GameState state, int playerId)
        {
            return ValidateCanSpend(state, playerId, Special);
        }

        public void SpendCompletedMainAction(GameState state, int playerId, bool specialAction = false)
        {
            SpendCompletedMainAction(state, playerId, specialAction ? Special : null);
        }

        public void SpendCompletedMainAction(GameState state, int playerId, string actionType)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null || !ValidateCanSpend(state, playerId, actionType).IsValid) return;
            var batches = EnsureBatches(player);
            // 优先消费允许范围最窄的匹配批次，范围相同时按授予顺序消费。
            var selected = batches.Where(batch => batch.Remaining > 0 && Allows(batch, actionType))
                .OrderBy(batch => batch.AllowedActionTypes.Count == 0 ? int.MaxValue : batch.AllowedActionTypes.Count).First();
            selected.Remaining--;
            if (selected.Remaining == 0) batches.Remove(selected);
            player.CompletedMainActionsThisTurn += 1;
            SyncTotals(player);
        }

        public void GrantAdditionalMainActions(GameState state, int playerId, int amount, bool nonSpecialOnly = false)
        {
            GrantAdditionalMainActions(state, playerId, amount, nonSpecialOnly ? LegacyRestrictedTypes : Array.Empty<string>());
        }

        public void GrantAdditionalMainActions(GameState state, int playerId, int amount, IEnumerable<string> allowedActionTypes)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (amount <= 0)
            {
                return;
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return;
            }

            if (!HasValidBatches(player)) throw new InvalidOperationException("main_action_budget_invalid");
            var allowed = (allowedActionTypes ?? Enumerable.Empty<string>()).ToList();
            if (allowed.Any(type => !IsSupportedActionType(type)) || allowed.Distinct(StringComparer.Ordinal).Count() != allowed.Count)
                throw new ArgumentException("main_action_restriction_invalid", nameof(allowedActionTypes));
            checked { _ = player.RemainingMainActionsThisTurn + amount; }
            EnsureBatches(player).Add(new MainActionBudgetBatch { Remaining = amount, AllowedActionTypes = allowed.OrderBy(type => type, StringComparer.Ordinal).ToList() });
            SyncTotals(player);
        }

        public void LockCharacterCard(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player != null)
            {
                player.CharacterCardLockedThisTurn = true;
            }
        }

        public void ResetForNewActionTurn(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return;
            }

            player.RemainingMainActionsThisTurn = DefaultMainActionsPerTurn;
            player.RestrictedMainActionsThisTurn = 0;
            player.MainActionBudgetBatches = new List<MainActionBudgetBatch> { new MainActionBudgetBatch { Remaining = DefaultMainActionsPerTurn } };
            player.CompletedMainActionsThisTurn = 0;
            player.ActedMainActionThisTurn = false;
            player.UsedCharacterThisTurn = false;
            player.CharacterCardLockedThisTurn = false;
        }

        public void EndActionTurn(GameState state, int playerId)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var player = state.FindPlayer(playerId);
            if (player == null)
            {
                return;
            }

            player.RemainingMainActionsThisTurn = 0;
            player.RestrictedMainActionsThisTurn = 0;
            player.MainActionBudgetBatches = new List<MainActionBudgetBatch>();
            player.ActedMainActionThisTurn = true;
            player.UsedCharacterThisTurn = false;
            player.CharacterCardLockedThisTurn = false;
        }
    }
}
