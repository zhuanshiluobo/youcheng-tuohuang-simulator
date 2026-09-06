#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;

namespace YC.Tests.PlayMode
{
    public static class RoundTrackerPrefabPlayModeProbe
    {
        public static void Run()
        {
            if (UnityEngine.Object.FindObjectsOfType<EventSystem>().Length != 1)
            {
                throw new InvalidOperationException("SampleScene 必须且只能有一个 EventSystem。");
            }

            var controller = UnityEngine.Object.FindObjectOfType<RoundTrackerController>();
            if (controller == null || !controller.IsConfigured)
            {
                throw new InvalidOperationException("SampleScene 的 RoundTracker Prefab 未正确配置。");
            }

            var state = new GameState
            {
                Round = 3,
                Phase = GamePhase.ActionRound1,
                Players =
                {
                    new PlayerState { PlayerId = 1, Name = "甲", Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Name = "乙", Color = PlayerColor.Blue },
                    new PlayerState { PlayerId = 3, Name = "丙", Color = PlayerColor.Green },
                    new PlayerState { PlayerId = 4, Name = "丁", Color = PlayerColor.Yellow }
                }
            };
            controller.RefreshFromState(state);
            AssertSingleRoundMarker(controller);

            state.Round = 4;
            controller.RefreshFromState(state);
            AssertSingleRoundMarker(controller);

            if (RequireChild(controller.transform, "Game Over Overlay").gameObject.activeSelf)
            {
                throw new InvalidOperationException("非终局状态不应打开 GameOver Overlay。");
            }

            state.Phase = GamePhase.FinalScoring;
            state.FinalScoring = new FinalScoringState
            {
                IsResolved = true,
                TiebreakSummary = "总分最高。",
                WinnerPlayerIds = { 2 },
                PlayerScores =
                {
                    new FinalPlayerScoreState { PlayerId = 1, BaseScore = 2, RegionScore = 2, ResourceScore = 1, TotalScore = 5 },
                    new FinalPlayerScoreState { PlayerId = 2, BaseScore = 5, FacilityScore = 2, RegionScore = 3, ResourceScore = 2, TotalScore = 12 }
                }
            };
            controller.RefreshFromState(state);
            AssertSingleRoundMarker(controller);
            if (!RequireChild(controller.transform, "Game Over Overlay").gameObject.activeSelf)
            {
                throw new InvalidOperationException("终局状态应打开 GameOver Overlay。");
            }

            RequireChild(controller.transform, "Final Score Row P1");
            RequireChild(controller.transform, "Final Score Row P2");
            RequireChild(controller.transform, "Final Score Detail Chart P2");
            controller.ShowFinalScoreDetailsForPlayer(1);
            if (!controller.IsFinalScoreDetailsOpen || controller.SelectedFinalScorePlayerId != 1 ||
                !RequireChild(controller.transform, "Final Score Detail Chart P1").gameObject.activeSelf)
            {
                throw new InvalidOperationException("最终计分详情打开或玩家切换失败。");
            }
        }

        private static Transform RequireChild(Transform root, string name)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name == name)
                {
                    return transforms[i];
                }
            }

            throw new InvalidOperationException("缺少运行时对象：" + name);
        }

        private static void AssertSingleRoundMarker(RoundTrackerController controller)
        {
            RequireChild(controller.transform, "Round Marker");
            if (CountChildren(controller.transform, "Round Marker") != 1)
            {
                throw new InvalidOperationException("四人游戏连续刷新时必须共用一个全局回合标记。");
            }
        }

        private static int CountChildren(Transform root, string name)
        {
            var count = 0;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name == name)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
#endif
