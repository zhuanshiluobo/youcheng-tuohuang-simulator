using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.State;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    public sealed partial class CharacterCardEffectInteractionUiCoordinatorTests
    {
        // 读取正式 Registry；只验证组件行为，不代表当前场景或正常局的视觉验收。
        [TestCase("CharacterCardEffectChoiceDialog")]
        [TestCase("FacilityEffectChoiceDialog")]
        public void RewardCardHostsResolveDeclaredAwardArtworkForRealInstance(string host)
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            try
            {
                var fixture = CreateCoordinator(CreateState());
                var hud = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
                var registryType = RewardUiType("GameplayDialogRegistry");
                var registry = hud.GetComponentInChildren(registryType, true);
                var dialog = host == "CharacterCardEffectChoiceDialog" ? fixture.Dialog :
                    Activator.CreateInstance(RewardUiType(host), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new object[] { registry, (RectTransform)canvasObject.transform }, null);
                var request = RewardRequest();
                var projection = InteractionRequestProjector.ProjectForPlayer(request, 1);
                var spec = Activator.CreateInstance(RewardUiType("EffectDialogSelectionSpec"), true);
                void Set(string name, object value) => spec.GetType().GetField(name).SetValue(spec, value);
                Set("Request", projection);
                Set("SelectedIds", new string[0]);
                Set("IsEffectPage", false);
                Set("Label", new Func<string, string>(id => id));
                Set("IsCurrent", new Func<bool>(() => true));
                dialog.GetType().GetMethod("ShowSelection").Invoke(dialog,
                    new object[] { "奖赏选择验证", "正式奖赏声明与实例映射。", spec, false });
                var overlay = GetOverlay(dialog);
                AssertRewardCardPage(overlay);
                var cards = RewardCards(overlay);
                Assert.That(cards, Has.Length.EqualTo(2));
                foreach (var card in cards)
                {
                    Assert.That(((RawImage)card.GetType().GetProperty("CardImage").GetValue(card)).texture, Is.Not.Null);
                    Assert.That(((Text)card.GetType().GetProperty("FallbackLabel").GetValue(card)).gameObject.activeSelf, Is.False);
                }
                artifacts.WriteLabeledText("真实奖赏实例与卡面", JsonConvert.SerializeObject(new { host, projection, artworkCount = cards.Length }));
                dialog.GetType().GetMethod("Hide").Invoke(dialog, null);
            }
            finally { artifacts.Finish(); }
        }

        [Test]
        public void RewardCardMissingArtworkKeepsCardPageAndSubmitsOnlySelectedRealItemOnce()
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            try
            {
                var state = CreateState();
                var request = RewardRequest();
                state.EffectRuntime.StateRevision = request.StateRevision;
                state.EffectRuntime.InteractionRequests.Add(request);
                var fixture = CreateCoordinator(state);
                var before = JsonConvert.SerializeObject(request);
                var commands = new List<GameCommand>();
                // 无 ExpansionUi 卡面来源，验证正式协调器面对缺图仍保持奖赏卡页。
                var renderer = CreateGenericRenderer(state, fixture.Dialog, commands);
                foreach (var id in request.CandidateIds)
                    LogAssert.Expect(LogType.Warning, "[reward_card_artwork_missing] request=" + request.InteractionId + " item=" + id);
                renderer.Render(InteractionRequestProjector.ProjectForPlayer(request, 1));
                var overlay = GetOverlay(fixture.Dialog);
                AssertRewardCardPage(overlay);
                var cards = RewardCards(overlay);
                Assert.That(cards, Has.Length.EqualTo(2));
                foreach (var card in cards)
                {
                    Assert.That(((RawImage)card.GetType().GetProperty("CardImage").GetValue(card)).texture, Is.Null);
                    Assert.That(((Text)card.GetType().GetProperty("FallbackLabel").GetValue(card)).gameObject.activeSelf, Is.True);
                }
                ClickButton(overlay, "Character Effect Option 1");
                Assert.That(commands, Is.Empty, "选中卡片只更新草稿。");
                var confirm = FindChild(overlay, "Confirm Selection").GetComponent<Button>();
                confirm.onClick.Invoke(); confirm.onClick.Invoke();
                Assert.That(commands, Has.Count.EqualTo(1));
                Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { request.CandidateIds[1] }));
                Assert.That(JsonConvert.SerializeObject(request), Is.EqualTo(before));
                artifacts.WriteLabeledText("缺图卡页原始请求与实际命令", JsonConvert.SerializeObject(new { request, commands }));
                renderer.Clear();
            }
            finally { artifacts.Finish(); }
        }

        [Test]
        public void RewardArtworkCatalogUsesAwardTypeAndRuntimeDefinitionWithoutDirtyingAsset()
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            var catalog = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/YC/Presentation/Content/CardVisualCatalog.asset");
            var resolver = catalog.GetType().GetField("runtimeDefinitionResolver", BindingFlags.Instance | BindingFlags.NonPublic);
            var original = resolver.GetValue(catalog);
            var wasDirty = EditorUtility.IsDirty(catalog);
            try
            {
                var getAward = catalog.GetType().GetMethod("GetAward");
                var direct = getAward.Invoke(catalog, new object[] { "infected.award.score" });
                Assert.That(direct, Is.Not.Null);
                catalog.GetType().GetMethod("BindRuntimeDefinitions").Invoke(catalog,
                    new object[] { new Func<string, string>(id => id == "opaque-award" ? "infected.award.score" : id) });
                Assert.That(getAward.Invoke(catalog, new object[] { "opaque-award" }), Is.SameAs(direct));
                Assert.That(getAward.Invoke(catalog, new object[] { "liskarm" }), Is.Null, "不能借用角色图冒充奖赏图。");
                Assert.That(EditorUtility.IsDirty(catalog), Is.EqualTo(wasDirty));
                artifacts.WriteLabeledText("奖赏卡面只读解析", "award 类型、真实声明、运行时实例映射及资产 dirty 状态已核对。");
            }
            finally { resolver.SetValue(catalog, original); artifacts.Finish(); }
        }

        private static InteractionRequest RewardRequest()
        {
            var request = new InteractionRequest
            {
                InteractionId = "real-award-card-page", InteractionTypeId = RewardAcquisitionFlow.InteractionTypeId,
                Status = "open", Visibility = "owner", AnsweringPlayerId = 1, StateRevision = 12,
                CandidateSetId = "actual-awards", CandidateSetVersion = 3, AnswerSchema = "candidate_id",
                CandidateIds = new List<string> { "opaque-award-score", "opaque-award-roads" }, MinSelections = 1, MaxSelections = 1,
                PromptKey = "core:custom.reward.prompt"
            };
            request.PromptParameters.Properties.Add(new NormalizedValueEntry { Name = "candidateDefinitionIds",
                Value = NormalizedValue.CreateArray(new List<NormalizedValue> {
                    NormalizedValue.CreateString("infected.award.score"), NormalizedValue.CreateString("infected.award.roads") }) });
            return request;
        }

        private static Type RewardUiType(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static Component[] RewardCards(GameObject overlay) => overlay.GetComponentsInChildren(RewardUiType("FacilityEffectCardView"), false);
        private static void AssertRewardCardPage(GameObject overlay)
        {
            var page = overlay.GetComponent(RewardUiType("EffectDialogShellView"));
            Assert.That((bool)page.GetType().GetProperty("IsCardPicker").GetValue(page), Is.True);
        }
    }
}
