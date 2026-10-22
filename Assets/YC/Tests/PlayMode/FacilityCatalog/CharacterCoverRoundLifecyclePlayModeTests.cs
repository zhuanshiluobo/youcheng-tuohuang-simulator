using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Commands;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    public sealed class CharacterCoverRoundLifecyclePlayModeTests
    {
        [UnityTest]
        public IEnumerator NormalGame_EveryRoundShowsCoverAndSurvivesPreviousEffectCleanup()
        {
            Assert.That(Array.IndexOf(Environment.GetCommandLineArgs(),
                "--yc-dev-right-card-smoke-no-dialog"), Is.LessThan(0), "回合流程回归必须使用正常开局。");
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                var context = Find("YC.Presentation.GameLaunchContext");
                if (context != null)
                {
                    UnityEngine.Object.Destroy(context.gameObject);
                    yield return null;
                }
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var controller = Find("YC.Presentation.MobileCityInteractionController");
                var session = Field(controller, "session");
                var state = (GameState)Property(session, "State");
                var coveredRounds = new HashSet<int>();
                var local = (int)Field(controller, "localPlayerId");
                Assert.That(state.Phase, Is.EqualTo(GamePhase.Entrance));
                Assert.That(state.Players.Count, Is.EqualTo(1), "此回归使用正式本地单人开局。");

                for (var step = 0; step < 300 && state.Phase != GamePhase.GameOver &&
                     state.Phase != GamePhase.FinalScoring; step++)
                {
                    var player = state.FindPlayer(state.CurrentPlayerId);
                    if (state.Phase == GamePhase.CharacterCover)
                    {
                        Assert.That(state.CurrentPlayerId, Is.EqualTo(local));
                        var dialog = Field(controller, "characterCoverChoiceDialog");
                        Assert.That(dialog, Is.Not.SameAs(Field(controller, "characterCardEffectChoiceDialog")),
                            "阶段盖放与效果结算必须各自拥有页面生命周期。");
                        var shell = Field(dialog, "shell");
                        var page = Field(shell, "view") as Component;
                        Assert.That(page, Is.Not.Null, scene + " 第 " + state.Round + " 回合缺少盖放页。");
                        Assert.That(page.gameObject.activeInHierarchy, Is.True,
                            scene + " 第 " + state.Round + " 回合盖放页不可见。");

                        // 覆盖真实刷新顺序：新盖放页先打开，上一个地图/角色交互随后清理。
                        Invoke(Field(controller, "influenceEffectInteraction"), "Clear");
                        Invoke(Field(controller, "characterAbilityInteraction"), "Clear");
                        Invoke(Field(controller, "characterCardEffectInteraction"), "HideDialog");
                        Assert.That(Field(shell, "view"), Is.SameAs(page), "旧效果清理不得销毁新盖放页。");
                        Assert.That(page.gameObject.activeInHierarchy, Is.True, "旧效果清理不得隐藏盖放页。");
                        Invoke(controller, "SynchronizeInteractionFromState");
                        Assert.That(Field(shell, "view"), Is.SameAs(page), "同回合刷新不得重建盖放页。");

                        var handCount = player.HandCardIds.Count;
                        var candidate = Child(page.transform, "Character Cover Card 0");
                        var card = candidate.GetComponent(Type.GetType(
                            "YC.Presentation.FacilityEffectCardView, Assembly-CSharp", true));
                        var choose = (Button)Property(card, "Button");
                        Assert.That(choose.isActiveAndEnabled && choose.IsInteractable(), Is.True);
                        choose.onClick.Invoke();
                        var selected = (string)Field(controller, "selectedCoverCardId");
                        Assert.That(player.HandCardIds, Does.Contain(selected));
                        page = (Component)Field(shell, "view");
                        var confirm = Child(page.transform, "Confirm Selection").GetComponent<Button>();
                        Assert.That(confirm.isActiveAndEnabled && confirm.IsInteractable(), Is.True);
                        coveredRounds.Add(state.Round);
                        confirm.onClick.Invoke();
                        yield return null;
                        Assert.That(player.CoveredCharacterCardId, Is.EqualTo(selected));
                        Assert.That(player.HandCardIds.Count, Is.EqualTo(handCount - 1));
                        Assert.That(state.Phase, Is.EqualTo(GamePhase.ActionRound1));
                        continue;
                    }

                    GameCommand command;
                    var request = state.EffectRuntime?.InteractionRequests.Find(item =>
                        item != null && item.Status == "open" && !item.IsInternalMainlineInteraction());
                    if (request != null)
                    {
                        Assert.That(request.CandidateIds, Is.Not.Empty, request.InteractionTypeId);
                        command = new GameCommand { Kind = GameCommandKind.AnswerInteraction,
                            PlayerId = request.AnsweringPlayerId };
                        command.Parameters["interactionId"] = request.InteractionId;
                        command.Parameters["expectedRevision"] = request.StateRevision.ToString();
                        command.OptionIds.Add(request.CandidateIds[0]);
                    }
                    else if (state.Phase == GamePhase.Entrance)
                    {
                        var location = StaticMapDefinitions.Resolve(state.MapId).Locations.Find(item =>
                            item.CanDockCity &&
                            (state.MapId != StaticMapDefinitions.FourPlayerMapId ||
                             StaticMapDefinitions.FourPlayerInitialLocationIds.Contains(item.LocationId)));
                        Assert.That(location, Is.Not.Null);
                        command = new GameCommand { Kind = GameCommandKind.ChooseInitialLocation,
                            PlayerId = player.PlayerId, TargetId = location.LocationId };
                    }
                    else if (state.Phase == GamePhase.ResourceCollection)
                    {
                        // 采集是所有玩家可自行提交的开放窗口，CurrentPlayerId 在此为 -1。
                        // 正式本地单人入口以本地玩家提交；不得据当前行动玩家推导采集者。
                        var collector = state.FindPlayer(local);
                        Assert.That(collector, Is.Not.Null, "采集阶段必须存在本地玩家。");
                        Assert.That(collector.HasCollectedResourcesThisRound, Is.False,
                            "本地单人已经提交采集后应自动完成收尾并推进回合。");
                        command = new GameCommand { Kind = GameCommandKind.CollectResource,
                            PlayerId = collector.PlayerId };
                    }
                    else if (state.Phase == GamePhase.ActionRound1 || state.Phase == GamePhase.ActionRound2)
                    {
                        command = new GameCommand
                        {
                            Kind = player.ActedMainActionThisTurn ? GameCommandKind.EndAction :
                                GameCommandKind.DeployInfluence,
                            PlayerId = player.PlayerId
                        };
                    }
                    else
                    {
                        Assert.Fail(scene + " 回合主链停在未预期阶段：" + state.Round + " / " + state.Phase);
                        yield break;
                    }

                    var result = (CommandResult)Invoke(session, "Submit", command);
                    Assert.That(result.Succeeded, Is.True, scene + " 第 " + state.Round + " 回合 / " +
                        command.Kind + "：" + result.Validation?.Reason);
                    // 每一个真实规则命令之后都刷新实际控制器，保留交互路由 WasActive 的历史。
                    Invoke(controller, "SynchronizeInteractionFromState");
                    yield return null;
                }

                Assert.That(coveredRounds.Count, Is.EqualTo(state.MaxRounds),
                    scene + " 必须经过全部回合盖放，包含第 2 回合。");
                for (var round = 1; round <= state.MaxRounds; round++)
                    Assert.That(coveredRounds, Does.Contain(round), scene + " 缺少第 " + round + " 回合盖放。");
                Assert.That(state.Phase, Is.EqualTo(GamePhase.FinalScoring).Or.EqualTo(GamePhase.GameOver));
            }
        }

        private static Component Find(string name) => UnityEngine.Object.FindObjectOfType(
            Type.GetType(name + ", Assembly-CSharp", true)) as Component;
        private static object Field(object target, string name) => target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static object Property(object target, string name) => target.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public).GetValue(target);
        private static object Invoke(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Invoke(target, args);
        private static Transform Child(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = Child(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
