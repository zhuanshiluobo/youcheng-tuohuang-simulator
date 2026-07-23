using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class SpecialActionPreviewUiTests
    {
        private GameObject host;

        [TearDown]
        public void TearDown()
        {
            DestroyNamedObject("City Style Declaration Preview Canvas");
            DestroyNamedObject("Special Action Choice Overlay");
            DestroyNamedObject("EventSystem");
            if (host != null)
            {
                UnityEngine.Object.DestroyImmediate(host);
                host = null;
            }
        }

        [Test]
        public void MarkerDrag_RejectsInvalidDropAndSubmitsOnlyOnItsLegalHighlightedArea()
        {
            var submissionCount = 0;
            var submittedActionId = string.Empty;
            var submittedMarkerId = string.Empty;
            var submittedOriginium = -1;
            var submittedIron = -1;
            var dialog = ShowDialog(
                string.Empty,
                (actionId, markerId, originium, iron) =>
                {
                    submissionCount += 1;
                    submittedActionId = actionId;
                    submittedMarkerId = markerId;
                    submittedOriginium = originium;
                    submittedIron = iron;
                    return true;
                });
            var canvas = GameObject.Find("City Style Declaration Preview Canvas");
            var marker = FindTransform(canvas, "样式预览影响力 玩家1 标记1");
            Assert.That(marker, Is.Not.Null);
            Assert.That(marker.GetComponent<Button>().interactable, Is.True);

            var pointerInteraction = marker.GetComponent(
                Type.GetType("YC.Presentation.CardPointerInteraction, Assembly-CSharp", true));
            var invalid = CreatePointerEvent(canvas);
            InvokePointer(pointerInteraction, "OnBeginDrag", invalid);
            Assert.That(GameObject.Find("特殊行动影响力拖动虚影"), Is.Not.Null);
            Assert.That(GameObject.Find("特殊行动合法落区 used"), Is.Not.Null);

            invalid.pointerCurrentRaycast = new RaycastResult
            {
                gameObject = FindTransform(canvas, "City Style Declaration Preview Panel").gameObject
            };
            invalid.position = new Vector2(-1000f, -1000f);
            InvokePointer(pointerInteraction, "OnEndDrag", invalid);
            Assert.That(submissionCount, Is.Zero);
            Assert.That(GetProperty<bool>(dialog, "IsShowing"), Is.True);
            Assert.That(GameObject.Find("特殊行动影响力拖动虚影"), Is.Null);

            var valid = CreatePointerEvent(canvas);
            InvokePointer(pointerInteraction, "OnBeginDrag", valid);
            var legalTarget = GameObject.Find("特殊行动合法落区 used");
            Assert.That(legalTarget, Is.Not.Null);
            valid.pointerCurrentRaycast = new RaycastResult { gameObject = legalTarget };
            InvokePointer(pointerInteraction, "OnEndDrag", valid);

            Assert.That(submissionCount, Is.EqualTo(1));
            Assert.That(submittedActionId, Is.EqualTo(SpecialActionDatabase.MilitaryIndustrialArea));
            Assert.That(submittedMarkerId, Is.EqualTo("marker-1"));
            Assert.That(submittedOriginium, Is.Zero);
            Assert.That(submittedIron, Is.Zero);
            Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Null);
        }

        [Test]
        public void MarkerDrag_MilitaryHighlightCoversThePrintedUsedArea()
        {
            ShowDialog(string.Empty, (actionId, markerId, originium, iron) => true);
            var canvas = GameObject.Find("City Style Declaration Preview Canvas");
            var marker = FindTransform(canvas, "样式预览影响力 玩家1 标记1");
            var pointerInteraction = marker.GetComponent(
                Type.GetType("YC.Presentation.CardPointerInteraction, Assembly-CSharp", true));

            InvokePointer(pointerInteraction, "OnBeginDrag", CreatePointerEvent(canvas));

            var target = GameObject.Find("特殊行动合法落区 used");
            Assert.That(target, Is.Not.Null);
            var rect = target.GetComponent<RectTransform>();
            Assert.That(rect.anchorMin.x, Is.EqualTo(0.54f).Within(0.0001f));
            Assert.That(rect.anchorMin.y, Is.EqualTo(0.08f).Within(0.0001f));
            Assert.That(rect.anchorMax.x, Is.EqualTo(0.945f).Within(0.0001f));
            Assert.That(rect.anchorMax.y, Is.EqualTo(0.485f).Within(0.0001f));
            Assert.That(rect.offsetMin, Is.EqualTo(Vector2.zero));
            Assert.That(rect.offsetMax, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void WarningDrop_RequiresExplicitConfirmationAndCancelDoesNotSubmit()
        {
            var submissionCount = 0;
            var dialog = ShowDialog(
                "当前没有可执行目标，发动后对应步骤会跳过。",
                (actionId, markerId, originium, iron) =>
                {
                    submissionCount += 1;
                    return true;
                });
            var canvas = GameObject.Find("City Style Declaration Preview Canvas");
            DropMarkerOnLegalTarget(canvas);

            Assert.That(submissionCount, Is.Zero);
            var confirmation = GameObject.Find("Special Action Warning Confirmation");
            Assert.That(confirmation, Is.Not.Null);
            dialog.GetType().GetMethod("ChangeCityStyle", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(dialog, new object[] { 1 });
            Assert.That(
                GetProperty<string>(dialog, "CurrentCityStyleId"),
                Is.EqualTo(CityStyleDatabase.MilitaryIndustrialArea),
                "警告模态打开时不得切换背后的样式卡。");
            FindButton(confirmation, "Cancel Special Action Warning").onClick.Invoke();
            Assert.That(submissionCount, Is.Zero);
            Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Not.Null);
            Assert.That(GameObject.Find("Special Action Warning Confirmation"), Is.Null);

            canvas = GameObject.Find("City Style Declaration Preview Canvas");
            DropMarkerOnLegalTarget(canvas);
            confirmation = GameObject.Find("Special Action Warning Confirmation");
            FindButton(confirmation, "Confirm Special Action Warning").onClick.Invoke();

            Assert.That(submissionCount, Is.EqualTo(1));
            Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Null);
        }

        [Test]
        public void CompositeDrop_WarningPrecedesCancelablePaymentAndConfirmCarriesAllocation()
        {
            var submissionCount = 0;
            var submittedOriginium = -1;
            var submittedIron = -1;
            ShowDialog(
                "当前没有可执行目标，发动后对应步骤会跳过。",
                (actionId, markerId, originium, iron) =>
                {
                    submissionCount += 1;
                    submittedOriginium = originium;
                    submittedIron = iron;
                    return true;
                },
                CityStyleDatabase.CompositePowerSystem,
                SpecialActionDatabase.CompositePowerSystem,
                2,
                3);
            var canvas = GameObject.Find("City Style Declaration Preview Canvas");
            DropMarkerOnLegalTarget(canvas);

            Assert.That(GameObject.Find("Special Action Warning Confirmation"), Is.Not.Null);
            Assert.That(GameObject.Find("Special Action Choice Overlay"), Is.Null);
            Assert.That(submissionCount, Is.Zero);
            FindButton(
                GameObject.Find("Special Action Warning Confirmation"),
                "Confirm Special Action Warning").onClick.Invoke();

            var payment = GameObject.Find("Special Action Choice Overlay");
            Assert.That(payment, Is.Not.Null);
            Assert.That(submissionCount, Is.Zero, "支付确认前不得提交首条特殊行动命令。");
            Assert.That(FindTransform(payment, "Value 0").GetComponent<Text>().text, Is.EqualTo("2"));
            Assert.That(FindTransform(payment, "Value 1").GetComponent<Text>().text, Is.EqualTo("0"));
            FindButton(payment, "Cancel Special Action Payment").onClick.Invoke();

            Assert.That(GameObject.Find("Special Action Choice Overlay"), Is.Null);
            Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Not.Null);
            Assert.That(submissionCount, Is.Zero, "取消支付必须返回预览且不提交。");

            canvas = GameObject.Find("City Style Declaration Preview Canvas");
            DropMarkerOnLegalTarget(canvas);
            FindButton(
                GameObject.Find("Special Action Warning Confirmation"),
                "Confirm Special Action Warning").onClick.Invoke();
            payment = GameObject.Find("Special Action Choice Overlay");
            FindButton(payment, "Increase 1").onClick.Invoke();
            FindButton(payment, "Confirm Special Action Payment").onClick.Invoke();

            Assert.That(submissionCount, Is.EqualTo(1));
            Assert.That(submittedOriginium, Is.EqualTo(2));
            Assert.That(submittedIron, Is.EqualTo(1));
            Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Null);
        }

        [Test]
        public void CompositePaymentDraft_BackNavigationAndPreviewCloseDoNotSubmit()
        {
            var submissionCount = 0;
            var dialog = ShowDialog(
                string.Empty,
                (actionId, markerId, originium, iron) =>
                {
                    submissionCount += 1;
                    return true;
                },
                CityStyleDatabase.CompositePowerSystem,
                SpecialActionDatabase.CompositePowerSystem,
                2,
                3);
            var dialogType = dialog.GetType();

            DropMarkerOnLegalTarget(GameObject.Find("City Style Declaration Preview Canvas"));
            Assert.That(GameObject.Find("Special Action Choice Overlay"), Is.Not.Null);
            dialogType.GetMethod(
                    "HandleBackNavigation",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(dialog, null);

            Assert.That(submissionCount, Is.Zero, "Esc/返回仅取消本地支付草稿，不得提交命令。");
            Assert.That(GameObject.Find("Special Action Choice Overlay"), Is.Null);
            Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Not.Null);

            DropMarkerOnLegalTarget(GameObject.Find("City Style Declaration Preview Canvas"));
            Assert.That(GameObject.Find("Special Action Choice Overlay"), Is.Not.Null);
            dialogType.GetMethod("Hide", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(dialog, null);

            Assert.That(submissionCount, Is.Zero, "关闭样式预览也不得提交尚未确认的支付草稿。");
            Assert.That(GameObject.Find("Special Action Choice Overlay"), Is.Null);
            Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Null);
        }

        [Test]
        public void CompositePaymentModal_BlocksStylePageChangesUntilClosed()
        {
            var dialog = ShowDialog(
                string.Empty,
                (actionId, markerId, originium, iron) => true,
                CityStyleDatabase.CompositePowerSystem,
                SpecialActionDatabase.CompositePowerSystem,
                2,
                3);
            var dialogType = dialog.GetType();
            var changeCityStyle = dialogType.GetMethod(
                "ChangeCityStyle",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var dragGhostField = dialogType.GetField(
                "specialActionDragGhost",
                BindingFlags.Instance | BindingFlags.NonPublic);

            DropMarkerOnLegalTarget(GameObject.Find("City Style Declaration Preview Canvas"));
            var paymentOverlay = GameObject.Find("Special Action Choice Overlay");
            Assert.That(paymentOverlay, Is.Not.Null);
            Assert.That(paymentOverlay.GetComponent<Image>().raycastTarget, Is.True);

            var canvas = GameObject.Find("City Style Declaration Preview Canvas");
            var pointerType = Type.GetType(
                "YC.Presentation.CardPointerInteraction, Assembly-CSharp",
                true);
            var pointerInteraction = canvas.GetComponentInChildren(pointerType, true);
            Assert.That(pointerInteraction, Is.Not.Null);
            InvokePointer(pointerInteraction, "OnBeginDrag", CreatePointerEvent(canvas));
            Assert.That(dragGhostField.GetValue(dialog), Is.Null);

            changeCityStyle.Invoke(dialog, new object[] { 1 });
            Assert.That(
                GetProperty<string>(dialog, "CurrentCityStyleId"),
                Is.EqualTo(CityStyleDatabase.CompositePowerSystem));

            FindButton(
                GameObject.Find("Special Action Choice Overlay"),
                "Cancel Special Action Payment").onClick.Invoke();
            InvokePointer(pointerInteraction, "OnBeginDrag", CreatePointerEvent(canvas));
            Assert.That(dragGhostField.GetValue(dialog), Is.Not.Null);
            changeCityStyle.Invoke(dialog, new object[] { 1 });
            Assert.That(
                GetProperty<string>(dialog, "CurrentCityStyleId"),
                Is.EqualTo(CityStyleDatabase.MaterialRelayStation));
        }

        private object ShowDialog(
            string warning,
            Func<string, string, int, int, bool> tryUseSpecialAction)
        {
            return ShowDialog(
                warning,
                tryUseSpecialAction,
                CityStyleDatabase.MilitaryIndustrialArea,
                SpecialActionDatabase.MilitaryIndustrialArea,
                0,
                0);
        }

        private object ShowDialog(
            string warning,
            Func<string, string, int, int, bool> tryUseSpecialAction,
            string cityStyleId,
            string specialActionId,
            int maximumOriginium,
            int maximumIron)
        {
            host = new GameObject("Special Action Preview Test Host", typeof(RectTransform));
            var marker = new CityStyleMarkerViewModel(
                cityStyleId,
                1,
                PlayerColor.Red,
                CityStyleMarkerAreas.Unused,
                "marker-1",
                specialActionId,
                true,
                CityStyleMarkerAreas.Used,
                string.Empty,
                warning,
                maximumOriginium,
                maximumIron);
            var model = new CityStyleOptionsViewModel(
                new List<CityStyleOptionViewModel>
                {
                    new CityStyleOptionViewModel
                    {
                        CityStyleId = cityStyleId,
                        Name = "测试城市样式",
                        CanDeclare = false,
                        Reason = "仅测试特殊行动拖拽"
                    },
                    new CityStyleOptionViewModel
                    {
                        CityStyleId = CityStyleDatabase.MaterialRelayStation,
                        Name = "物资中继站",
                        CanDeclare = false,
                        Reason = "仅测试模态期间切卡锁定"
                    }
                }.AsReadOnly(),
                new List<CityBoardSlotViewModel>().AsReadOnly(),
                new List<CityStyleMarkerViewModel> { marker }.AsReadOnly(),
                cityStyleId,
                null,
                null,
                null,
                null,
                tryUseSpecialAction);
            var dialogType = Type.GetType(
                "YC.Presentation.CityStyleDeclarationPreviewDialog, Assembly-CSharp",
                true);
            var dialog = Activator.CreateInstance(dialogType, true);
            dialogType.GetMethod("Show", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(dialog, new object[] { host.GetComponent<RectTransform>(), model });
            Canvas.ForceUpdateCanvases();
            return dialog;
        }

        private static void DropMarkerOnLegalTarget(GameObject canvas)
        {
            var marker = FindTransform(canvas, "样式预览影响力 玩家1 标记1");
            var pointerInteraction = marker.GetComponent(
                Type.GetType("YC.Presentation.CardPointerInteraction, Assembly-CSharp", true));
            var pointer = CreatePointerEvent(canvas);
            InvokePointer(pointerInteraction, "OnBeginDrag", pointer);
            var target = GameObject.Find("特殊行动合法落区 used");
            Assert.That(target, Is.Not.Null);
            pointer.pointerCurrentRaycast = new RaycastResult { gameObject = target };
            InvokePointer(pointerInteraction, "OnEndDrag", pointer);
        }

        private static PointerEventData CreatePointerEvent(GameObject canvas)
        {
            var canvasRect = canvas.GetComponent<RectTransform>();
            return new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                position = RectTransformUtility.WorldToScreenPoint(null, canvasRect.position)
            };
        }

        private static void InvokePointer(Component interaction, string methodName, PointerEventData pointer)
        {
            interaction.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)
                .Invoke(interaction, new object[] { pointer });
        }

        private static RectTransform FindTransform(GameObject root, string name)
        {
            if (root == null)
            {
                return null;
            }

            var transforms = root.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name == name)
                {
                    return transforms[i];
                }
            }

            return null;
        }

        private static Button FindButton(GameObject root, string name)
        {
            var buttons = root.GetComponentsInChildren<Button>(true);
            for (var i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].name == name)
                {
                    return buttons[i];
                }
            }

            return null;
        }

        private static T GetProperty<T>(object owner, string propertyName)
        {
            return (T)owner.GetType().GetProperty(propertyName).GetValue(owner, null);
        }

        private static void DestroyNamedObject(string name)
        {
            var gameObject = GameObject.Find(name);
            if (gameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
    }
}
