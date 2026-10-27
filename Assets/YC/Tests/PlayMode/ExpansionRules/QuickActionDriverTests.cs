using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 只验证原正式行的绑定／原事件与驱动边界，模拟回执不算正常行动或Lua效果验收。
    public sealed class QuickActionDriverTests
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private GameObject hud, eventHost;
        private readonly List<GameCommand> commands = new List<GameCommand>();
        private readonly List<object> reasons = new List<object>();
        private readonly HashSet<string> attempts = new HashSet<string>();
        private GameStateView view;
        private AbilityActionOption role, other, disabled;
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static Type Driver => T("Validation.HotseatUiValidationDriver");
        private static object Read(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value) ?? value.GetType().GetProperty(name, Flags)?.GetValue(value);
        private static object DriverCall(string method, params object[] args) => Driver.GetMethod(method, Flags).Invoke(null, args);
        private object[] Rows() => ((IEnumerable)DriverCall("ReadVisibleQuickRows", view, "game:1:2:1", attempts,
            new Func<string, int>(_ => 0), new Action<object>(reasons.Add))).Cast<object>().ToArray();
        private static AbilityActionOption Option(string id, string item, string action, bool enabled = true) => new AbilityActionOption {
            OptionId = id, ActionId = action, EntryKind = "quick", Enabled = enabled, DisplayName = "隔离动作",
            DisabledReason = enabled ? "" : "isolated-unavailable",
            SourceRef = new AbilitySourceRef { ItemId = item, DefinitionId = "definition-" + item, SourceKey = "source-" + item, AuthorizedPlayerId = 1 },
            VersionRef = new AbilityVersionRef { ContentPackHash = "isolated-pack" },
            RequestRef = new AbilityActionRequestRef { InteractionId = "request-" + id, FactsDigest = "facts-" + id, AnsweringPlayerId = 1,
                AllowedAnswers = new List<string> { id }, WindowId = "window", RequestGeneration = 1 }
        };

        [UnitySetUp] public IEnumerator RenderOriginalDeclaredRows()
        {
            commands.Clear(); reasons.Clear(); attempts.Clear();
            var theme = UnityEditor.AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog"));
            T("UiTheme").GetMethod("Initialize").Invoke(null, new[] { theme });
            hud = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            if (EventSystem.current == null) eventHost = new GameObject("快捷行隔离事件系统", typeof(EventSystem), typeof(StandaloneInputModule));
            yield return null;
            var h = hud.GetComponent(T("GameplayInteractionHudView"));
            var panel = Read(h, "ActionPanelView");
            var registry = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            Action noop = () => { };
            var controller = T("ActionPanelController").GetMethod("Bind", Flags).Invoke(null, new object[] {
                panel, Read(registry, "CardVisualCatalog"), new Action<string>(_ => Assert.Fail("声明行不能退回旧物品入口")),
                noop, noop, noop, noop, noop, noop, noop, noop });
            T("ActionPanelController").GetProperty("CanBeginAction").SetValue(controller, new Func<bool>(() => true));
            T("ActionPanelController").GetProperty("CanSubmitCharacter").SetValue(controller, new Func<bool>(() => true));
            T("ActionPanelController").GetProperty("DeclaredActionRequested").SetValue(controller,
                new Action<AbilityActionOption>(option => commands.Add(AbilityActionCommand.Create(1, option))));
            role = Option("opaque-option-alpha", "real-role-item", "arbitrary.role.use");
            other = Option("opaque-option-beta", "real-other-item", "arbitrary.quick");
            other.SortOrder = 1; disabled = Option("opaque-option-disabled", "real-disabled-item", "arbitrary.disabled", false);
            var options = new[] { role, other, disabled };
            view = new GameStateView { GameId = "isolated-game", ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1,
                ContentPackHash = "isolated-pack", StateRevision = 18, HasAbilityActionCatalog = true, AbilityActions = options.ToList() };
            view.Characters.Add(new CharacterAvailability { ItemId = role.SourceRef.ItemId, ControllerPlayerId = 1, CanUse = true });
            foreach (var option in options) view.ItemZones.Items.Add(new ItemInstanceState { ItemId = option.SourceRef.ItemId, DefinitionId = option.SourceRef.DefinitionId });
            var model = (CharacterCardPanelViewModel)FormatterServices.GetUninitializedObject(typeof(CharacterCardPanelViewModel));
            model.HasAbilityActionCatalog = true; model.DeclaredQuickActions = options;
            T("ActionPanelController").GetMethod("RenderCharacterQuickAction").Invoke(controller, new object[] { model });
            var markerType = Type.GetType("YC.PlayerJourney.PlayerAutomationId, YC.PlayerJourney", true);
            var tab = hud.GetComponentsInChildren(markerType, true).Cast<Component>().Single(value => (string)Read(value, "Id") == "action.tab.1");
            Click(tab.GetComponent<Button>());
            yield return null; Canvas.ForceUpdateCanvases();
        }

        private static void Click(Button button)
        {
            Canvas.ForceUpdateCanvases();
            var point = (Vector2)DriverCall("UiPointerPoint", button.gameObject);
            Assert.That(DriverCall("FirstUiClickTarget", point), Is.EqualTo(button.gameObject));
            var data = new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(button.gameObject, data, ExecuteEvents.pointerClickHandler);
        }

        [UnityTest] public IEnumerator FormalRowsUseOpaqueOptionIdsAndSkipDisabledOrSpentSources()
        {
            var rows = Rows(); Assert.That(rows.Length, Is.EqualTo(2));
            Assert.That((string)Read(rows[0], "ItemId"), Is.EqualTo(role.SourceRef.ItemId));
            Click((Button)Read(rows[0], "Button"));
            Assert.That(commands.Single().Parameters["optionId"], Is.EqualTo(role.OptionId));
            Assert.That(commands.Single().Parameters["optionId"], Is.Not.EqualTo(role.SourceRef.ItemId));
            attempts.Add((string)Read(rows[0], "AttemptKey"));
            rows = Rows(); Assert.That(rows.Length, Is.EqualTo(1));
            Assert.That((string)Read(rows[0], "ItemId"), Is.EqualTo(other.SourceRef.ItemId));
            Click((Button)Read(rows[0], "Button"));
            Assert.That(commands.Last().Parameters["optionId"], Is.EqualTo(other.OptionId));
            attempts.Add((string)Read(rows[0], "AttemptKey"));
            Assert.That(Rows(), Is.Empty, "窗口内同来源动作限一次，无可执行行就交回正常推进");
            Assert.That(commands.Count, Is.EqualTo(2));
            Assert.That(reasons.Any(value => (string)Read(value, "OptionId") == disabled.OptionId &&
                ((string)Read(value, "Reason")).Contains("isolated-unavailable")), Is.True);
            yield return null;
        }

        [UnityTest] public IEnumerator OnlyActualCharacterModeRequestsEnterCharacterPageContinuation()
        {
            int modes = 0;
            IEnumerator Mode() { modes++; yield break; }
            yield return (IEnumerator)DriverCall("ContinueDriverQuickRequest", new InteractionRequestProjection { InteractionTypeId = "lua.choice" }, new Func<IEnumerator>(Mode));
            yield return (IEnumerator)DriverCall("ContinueDriverQuickRequest", null, new Func<IEnumerator>(Mode));
            Assert.That(modes, Is.Zero, "普通声明行动的Choice或无请求不能强等角色页");
            yield return (IEnumerator)DriverCall("ContinueDriverQuickRequest", new InteractionRequestProjection { InteractionTypeId = CharacterLifecycleService.ModeInteractionTypeId }, new Func<IEnumerator>(Mode));
            Assert.That(modes, Is.EqualTo(1));
        }

        [UnityTest] public IEnumerator AccurateReceiptAndCompletedInvocationAreBothRequired()
        {
            var command = AbilityActionCommand.Create(1, other);
            var success = CommandResult.SuccessResult(new List<GameEvent>(), "隔离回执");
            Assert.That(DriverCall("AccurateDriverQuickReceipt", other, 1, command, new WorkflowSubmissionResult(success, false)), Is.False);
            Assert.That(DriverCall("AccurateDriverQuickReceipt", other, 1, command, new WorkflowSubmissionResult(success, true)), Is.True);
            command.Parameters["requestId"] = "stale-request";
            Assert.That(DriverCall("AccurateDriverQuickReceipt", other, 1, command, new WorkflowSubmissionResult(success, true)), Is.False);
            var invocation = new AbilityActionInvocation { OwnerEffectId = "owner", Phase = "body", Outcome = "", BodyEffectIds = new List<string> { "body" } };
            var nodes = new List<EffectNodeRuntimeState> { new EffectNodeRuntimeState { EffectId = "owner", Status = EffectNodeStatus.Created },
                new EffectNodeRuntimeState { EffectId = "body", Status = EffectNodeStatus.Created } };
            Assert.That(DriverCall("DriverQuickInvocationCompleted", invocation, nodes), Is.False);
            invocation.Phase = invocation.Outcome = "completed"; invocation.ReceiptIds.Add("completion");
            Assert.That(DriverCall("DriverQuickInvocationCompleted", invocation, nodes), Is.False);
            foreach (var node in nodes) node.Status = EffectNodeStatus.Completed;
            Assert.That(DriverCall("DriverQuickInvocationCompleted", invocation, nodes), Is.True);
            yield return null;
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (hud != null) UnityEngine.Object.Destroy(hud);
            if (eventHost != null) UnityEngine.Object.Destroy(eventHost);
            yield return null;
        }
    }
}
