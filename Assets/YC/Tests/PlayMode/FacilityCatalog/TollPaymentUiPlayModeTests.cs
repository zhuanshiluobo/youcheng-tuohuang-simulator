using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Influence;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    public sealed class TollPaymentUiPlayModeTests
    {
        [UnityTest]
        public IEnumerator ActualScenes_PaymentIconsReopenChangeRecipientAndCancelDraft()
        {
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                var launch = Find("GameLaunchContext");
                if (launch != null) { UnityEngine.Object.Destroy(launch.gameObject); yield return null; }
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null; yield return null;
                SetSize(1920, 1080);
                for (var i = 0; i < 6; i++) yield return null;
                var controller = Find("MobileCityInteractionController");
                var state = (GameState)Property(Field(controller, "session"), "State");
                var playerId = (int)Field(controller, "localPlayerId");
                var player = state.FindPlayer(playerId);
                state.PendingChoice = null; state.PendingCardSession = null;
                state.PendingCharacterEffect = null; state.PendingSpecialAction = null;
                state.EffectRuntime = new EffectRuntimeState();
                state.Phase = GamePhase.ResourceCollection; state.Round = 2; state.CurrentPlayerId = playerId;
                player.CityLocationId = "A-01"; player.Resources.GoldVoucher = 4;
                player.ResourceCollectionStartGoldVoucher = 4; player.HasCollectedResourcesThisRound = false;
                while (state.Players.Count(p => p.PlayerId != playerId) < 2)
                {
                    var id = state.Players.Max(p => p.PlayerId) + 1;
                    state.Players.Add(new PlayerState { PlayerId = id, Name = "支付测试玩家" + id,
                        Resources = new ResourceSet { Originium = 1, OriginiumShard = 2, Iron = 3,
                            PureOriginium = 4, GoldVoucher = 5 } });
                }
                state.Map.ResourceTokens.Clear(); state.Map.Influences.Clear();
                foreach (var id in new[] { "A-01", "A-02" })
                    state.Map.ResourceTokens.Add(new ResourceTokenState { LocationId = id, ResourceType = ResourceType.Iron, Amount = 1 });
                state.Map.Influences.Add(new InfluencePlacement { InfluenceId = "ui-payment-location", PlayerId = playerId,
                    SlotId = InfluenceService.GetLocationSlotId("A-02", 0), LocationId = "A-02" });
                Call(controller, "SynchronizeInteractionFromState");
                var collection = Field(controller, "resourceCollectionPresenter");
                var dialog = Field(controller, "eventChoiceDialog");
                object view = null;
                Button cancel = null, bank = null;
                void OpenPayment()
                {
                    Call(collection, "SelectRoutePayment", "A1");
                    view = Field(dialog, "view");
                    cancel = (Button)Property(view, "ResourcePaymentCancelButton");
                    bank = (Button)Property(view, "ResourcePaymentBankButton");
                }
                var modules = Find("GameplayMainModules");
                Assert.That(((Text[])Field(modules, "opponentResourceValues")).All(t => t.color == Color.black), Is.True);
                var registry = Property(Find("GameplayInteractionHudView"), "DialogRegistry");
                Call(modules, "Render", state, null, playerId, Property(registry, "CardVisualCatalog"));
                ((Button[])Field(modules, "opponentDetailToggles"))[0].onClick.Invoke();
                OpenPayment();
                Assert.That(((Text)Property(view, "CloseButtonLabel")).rectTransform.anchoredPosition.y, Is.EqualTo(3f));
                Assert.That(cancel.gameObject.activeSelf, Is.False, "尚未选择支付时不显示撤销入口");
                CheckVoucher(bank);
                bank.onClick.Invoke(); yield return null;
                OpenPayment();
                Assert.That(cancel.gameObject.activeSelf, Is.True);
                Assert.That(((Text)Property(view, "ResourcePaymentReceiverText")).text,
                    Is.EqualTo(string.Format((string)Property(view, "ResourcePaymentSelectedFormat"),
                        (string)Property(view, "ResourcePaymentBankName"), 2)));
                yield return Capture(scene + "-已选银行");
                ((Button)Property(view, "CloseButton")).onClick.Invoke(); yield return null;
                Assert.That(Call(collection, "GetSelectedPaymentRecipient", "A1"), Is.EqualTo(-1));
                OpenPayment(); cancel.onClick.Invoke(); yield return null;
                Assert.That(Call(collection, "GetSelectedPaymentRecipient", "A1"), Is.Null);
                Assert.That(((IEnumerable)Property(collection, "SelectedLocationIds")).Cast<string>(), Does.Not.Contain("A-02"));
                Assert.That(player.Resources.GoldVoucher, Is.EqualTo(4));

                // Change ownership between refreshes to exercise both recipient options through the actual adapter.
                var recipients = state.Players.Where(p => p.PlayerId != playerId).Take(2).ToArray();
                Assert.That(recipients.Length, Is.EqualTo(2));
                foreach (var recipient in recipients)
                {
                    state.Map.Influences.RemoveAll(i => i.RouteId == "A1");
                    state.Map.Influences.Add(new InfluencePlacement { InfluenceId = "ui-payment-owner", PlayerId = recipient.PlayerId,
                        SlotId = InfluenceService.GetRouteSlotId("A1", 0), RouteId = "A1" });
                    Call(controller, "SynchronizeInteractionFromState");
                    OpenPayment();
                    var host = (RectTransform)Property(view, "ResourcePaymentRecipientHost");
                    var pay = host.GetComponentsInChildren<Button>().Single();
                    CheckVoucher(pay);
                    Assert.That(cancel.gameObject.activeSelf, Is.EqualTo(recipient != recipients[0]));
                    pay.onClick.Invoke(); yield return null;
                    Assert.That(Call(collection, "GetSelectedPaymentRecipient", "A1"), Is.EqualTo(recipient.PlayerId));
                    OpenPayment();
                    var adapter = Field(controller, "workflowView");
                    var displayName = (string)Call(adapter, "GetPlayerDisplayName", recipient.PlayerId);
                    Assert.That(((Text)Property(view, "ResourcePaymentReceiverText")).text,
                        Is.EqualTo(string.Format((string)Property(view, "ResourcePaymentSelectedFormat"), displayName, 2)));
                    host = (RectTransform)Property(view, "ResourcePaymentRecipientHost");
                    Assert.That(host.GetComponentsInChildren<Button>(), Has.Length.EqualTo(1), "回显已选对象时保留支付按钮");
                    yield return Capture(scene + "-已选玩家" + recipient.PlayerId);
                    ((Button)Property(view, "CloseButton")).onClick.Invoke(); yield return null;
                }
                SetSize(900, 600); for (var i = 0; i < 8; i++) yield return null;
                OpenPayment();
                yield return Capture(scene + "-支付窗口-900x600");
                cancel.onClick.Invoke(); yield return null;
                Assert.That(Call(collection, "GetSelectedPaymentRecipient", "A1"), Is.Null);
                Assert.That(((IEnumerable)Property(collection, "PaidRouteIds")).Cast<string>(), Is.Empty);
                Assert.That(player.Resources.GoldVoucher, Is.EqualTo(4));
            }
        }

        private static void CheckVoucher(Button button)
        {
            var labels = button.GetComponentsInChildren<Text>();
            Assert.That(labels, Is.Not.Empty);
            Assert.That(labels.All(t => !t.text.Contains("金券")), Is.True, "按钮费用使用数字和图标");
            Assert.That(button.GetComponentsInChildren<Image>().Any(i => i.sprite != null && i.sprite.name == "resource-gold-voucher"), Is.True);
        }
        private static IEnumerator Capture(string name)
        {
            Canvas.ForceUpdateCanvases(); for (var i = 0; i < 8; i++) yield return null;
            var path = Path.GetFullPath("Logs/UIMisc-20261001/visual"); Directory.CreateDirectory(path);
            var resources = ((Text[])Field(Find("GameplayMainModules"), "opponentResourceValues"))
                .Where(t => t.gameObject.activeInHierarchy).ToArray();
            File.WriteAllLines(Path.Combine(path, name + "-资源显示.txt"), resources.Select(t =>
                t.text + " rect=" + t.rectTransform.rect + " vertices=" + t.cachedTextGenerator.vertexCount +
                " alpha=" + t.canvasRenderer.GetAlpha() + " font=" + t.font.name));
            Assert.That(resources.All(t => t.cachedTextGenerator.vertexCount > 0), Is.True,
                "实际左侧资源数字必须生成可见字形，不能被字体行高裁掉");
            var file = Path.Combine(path, name + ".png"); ScreenCapture.CaptureScreenshot(file);
            for (var i = 0; i < 12; i++) yield return null;
            Assert.That(File.Exists(file), Is.True);
        }
        private static void SetSize(int width, int height)
        {
#if UNITY_EDITOR
            var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(width, height) });
#endif
        }
        private static Component Find(string name) => UnityEngine.Object.FindObjectOfType(Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true)) as Component;
        private static object Property(object o, string name) => o.GetType().GetProperty(name).GetValue(o);
        private static object Field(object o, string name) => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);
        private static object Call(object o, string name, params object[] args) => o.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);
    }
}
