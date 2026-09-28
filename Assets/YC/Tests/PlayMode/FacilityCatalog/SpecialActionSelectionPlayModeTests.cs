using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.State;
using YC.Domain.Rules;

namespace YC.Tests.PlayMode
{
    // 定向场景测试：只在开局夹具中准备宣告图案与资源；宣告、发动、回答和结算均提交正式命令。
    // 不能将这组结果记作从开始页面完成的整局黑盒覆盖。
    public sealed class SpecialActionSelectionPlayModeTests
    {
        [UnityTest] public IEnumerator Military() => Run(CityStyleDatabase.MilitaryIndustrialArea);
        [UnityTest] public IEnumerator Mobilization() => Run(CityStyleDatabase.MobilizationSupportSystem);
        [UnityTest] public IEnumerator CompositePower() => Run(CityStyleDatabase.CompositePowerSystem);
        [UnityTest] public IEnumerator IndustrialHub() => Run(CityStyleDatabase.SourceStoneIndustrialHub);
        [UnityTest] public IEnumerator EfficientMove() => Run(CityStyleDatabase.EfficientMobileManagementSystem);

        private static IEnumerator Run(string styleId)
        {
            Assert.That(Environment.GetCommandLineArgs(), Does.Contain("--yc-dev-right-card-smoke-no-dialog"));
            yield return (IEnumerator)typeof(GameplayMainHudPlayModeTests).GetMethod("ClearLaunchContext", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            yield return SceneManager.LoadSceneAsync("SampleScene");
            yield return null;
            var controller = UnityEngine.Object.FindObjectOfType(T("MobileCityInteractionController"));
            var session = Field(controller, "session");
            var state = (GameState)Property(session, "State");
            var local = (int)Field(controller, "localPlayerId");
            var player = state.FindPlayer(local);
            player.BuiltFacilityIds.Clear(); player.DeclaredCityStyles.Clear();
            state.Map.Facilities.RemoveAll(item => item.PlayerId == local);
            player.Resources.Originium = player.Resources.OriginiumShard = player.Resources.Iron = 30;
            player.Resources.GoldVoucher = 100;
            void Add(string card, int slot)
            {
                player.BuiltFacilityIds.Add(card);
                state.Map.Facilities.Add(new FacilityPlacement { PlayerId = local, FacilityCardId = card,
                    CityBoardSlotIndex = slot, LocationId = player.CityLocationId });
            }
            switch (styleId)
            {
                case CityStyleDatabase.MilitaryIndustrialArea:
                    Add(FacilityCardDatabase.SourceStoneRefinery, 0); Add(FacilityCardDatabase.EquipmentWarehouse, 1); break;
                case CityStyleDatabase.MobilizationSupportSystem:
                    Add("building_028", 0); Add(FacilityCardDatabase.OriginiumPurificationPlant, 1); Add(FacilityCardDatabase.EquipmentWarehouse, 2); break;
                case CityStyleDatabase.CompositePowerSystem:
                    Add("building_028", 0); Add("building_032", 3); Add(FacilityCardDatabase.OriginiumPurificationPlant, 4); break;
                default:
                    Add(styleId == CityStyleDatabase.SourceStoneIndustrialHub ? FacilityCardDatabase.TradeDistrict : "building_028", 0);
                    Add(FacilityCardDatabase.EquipmentWarehouse, 3);
                    Add(styleId == CityStyleDatabase.SourceStoneIndustrialHub ? FacilityCardDatabase.UrbanizedArea : FacilityCardDatabase.OriginiumPurificationPlant, 4);
                    Add(styleId == CityStyleDatabase.SourceStoneIndustrialHub ? "building_028" : FacilityCardDatabase.UrbanizedArea, 6);
                    Add(styleId == CityStyleDatabase.SourceStoneIndustrialHub ? FacilityCardDatabase.OriginiumPurificationPlant : FacilityCardDatabase.TradeDistrict, 7);
                    Add("building_032", 8); break;
            }
            Call(controller, "SynchronizeInteractionFromState");
            var workflow = Property(Field(controller, "turnActionPresenter"), "CityStyleInteraction");
            Call(workflow, "BeginDeclare", styleId);
            yield return null;
            var dialog = Field(Field(controller, "workflowView"), "cityStyleDeclarationDialog");
            var match = new CityStylePatternMatcher().Match(state, local, CityStyleDatabase.Get(styleId));
            Assert.That(match.Succeeded, Is.True, match.Validation.Reason);
            foreach (var index in match.UsedCityBoardSlotIndexes) Call(dialog, "OnSlotLeftClick", index);
            var declare = (Button)Field(dialog, "confirmDeclarationButton");
            Assert.That(declare.interactable, Is.True, styleId);
            declare.onClick.Invoke();
            yield return null;
            Assert.That(player.DeclaredCityStyles, Has.Count.EqualTo(1), "必须通过正式宣告获得标记。");
            Call(dialog, "Hide");
            var declaration = player.DeclaredCityStyles[0];
            var actionId = declaration.UnlockedSpecialActionId;
            Assert.That(actionId, Is.Not.Empty);
            Call(controller, "RefreshActionPanel");
            var hud = UnityEngine.Object.FindObjectOfType(T("GameplayInteractionHudView"));
            var button = (Button)Property(Property(hud, "ActionPanelView"), "SpecialButton");
            Assert.That(button.interactable, Is.True, styleId);
            button.onClick.Invoke();
            yield return null;
            var page = (Component)Field(Field(Field(controller, "specialActionUsePage"), "shell"), "view");
            Assert.That(page.gameObject.activeInHierarchy, Is.True);
            var row = page.GetComponentsInChildren<Button>().First(item => item.name.StartsWith("Special Action Option ") && item.interactable);
            var revision = state.EffectRuntime.StateRevision;
            row.onClick.Invoke();
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision), "单击只预选，不执行。");
            page = (Component)Field(Field(Field(controller, "specialActionUsePage"), "shell"), "view");
            var confirm = page.GetComponentsInChildren<Button>().First(item => item.name == "Confirm Selection");
            Assert.That(confirm.interactable, Is.True);
            var click = confirm.onClick;
            click.Invoke(); click.Invoke();
            yield return null;
            for (var step = 0; state.HasPendingChoice() && step < 60; step++)
            {
                var request = state.EffectRuntime.InteractionRequests.FirstOrDefault(item => item.Status == "open" && !item.IsInternalMainlineInteraction() && item.AnsweringPlayerId == local);
                Assert.That(request, Is.Not.Null, "未出现可回答的正式请求：" + styleId);
                var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = local };
                command.Parameters["interactionId"] = request.InteractionId;
                command.Parameters["expectedRevision"] = request.StateRevision.ToString();
                command.OptionIds.AddRange(request.CandidateIds.Take(Math.Max(1, request.MinSelections)));
                var outcome = (CommandResult)Call(session, "Submit", command);
                Assert.That(outcome.Succeeded, Is.True, request.InteractionTypeId + " / " + request.PromptKey + " / " + string.Join(",", request.CandidateIds) + " / " + outcome.Validation.Reason);
                Call(controller, "SynchronizeInteractionFromState");
                yield return null;
            }
            Assert.That(state.HasPendingChoice(), Is.False, styleId);
            Assert.That(player.UsedSpecialActionIdsThisRound, Does.Contain(actionId), "必须结算后记录已使用。");
            Assert.That(player.UsedSpecialActionIdsThisRound.Count(id => id == actionId), Is.EqualTo(1));
        }

        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static object Property(object target, string name) => target.GetType().GetProperty(name).GetValue(target);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(target, args);
    }
}
