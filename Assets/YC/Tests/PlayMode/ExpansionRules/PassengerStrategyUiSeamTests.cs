using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using static YC.Tests.PlayMode.ExpansionRules.GameplayPageAcceptanceFixture;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 隔离接缝：正常菜单进入 SampleScene 后，仅借实际场景的 Registry/PageHost。
    // 独立状态和受控 Lua 入口不代表正常获得异客、完整局或人工画面验收。
    public sealed class PassengerStrategyUiSeamTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameplayPageAcceptanceFixture f;
        private InteractionRequestRouter router;
        private object coordinator, dialog, submission;
        private readonly List<GameCommand> sent = new List<GameCommand>();
        private int receipts;
        private string previousCommandId;

        [UnityTest]
        public IEnumerator IsolatedPositiveAmountAdvancesExistingPagesAfterMatchedReceipts() => Verify(3, 2);

        [UnityTest]
        public IEnumerator IsolatedZeroBalanceAllowsZeroAndPreservesMarkerRowBinding() => Verify(0, 0);

        private IEnumerator Verify(int targetBalance, int chosenAmount)
        {
            sent.Clear(); receipts = 0; previousCommandId = null;
            f = new GameplayPageAcceptanceFixture();
            yield return f.Start(4, _ => Settle());
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo("SampleScene"));
            ((Behaviour)f.Gameplay).enabled = false;
            var registrySource = f.Registry;
            var pageHost = f.Host;
            var sourceState = f.State;
            var configuration = new GameRuleConfiguration { MapId = sourceState.MapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise" } };
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"))
                .CreateActiveSnapshot(configuration);
            configuration.ContentPackHash = pack.ContentHash;
            var content = pack.ActiveDefinitions.Single(value => value.DefinitionId == "operator.passenger");
            var ability = content.Abilities.Single(value => value.Mode == "strategy");
            Assert.That(ability.CompletionHandlerId, Is.EqualTo("content.operator.passenger.strategy.step_completed"),
                "仅在本批规则接口停写后运行，不能让旧策略冒充新三步");
            var source = pack.GetScript(ability.Script);
            var registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry);
            GenericLuaEffectExecutor.Register(registry);
            ContentPrimitiveEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack);
            registry.GameplayPresenter = (string path, GameplayPresentationContext context,
                out GameplayPresentationResult result, out string diagnostic) =>
            {
                bool success = pack.TryPresent(path, context, out result, out var code, out diagnostic);
                if (!success) diagnostic = code + ": " + diagnostic;
                return success;
            };
            var state = new GameState { GameId = "isolated.passenger.ui." + targetBalance,
                MapId = sourceState.MapId, Map = GameStateCloneService.DeepClone(sourceState.Map),
                ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(),
                    Facilities = pack.CreateFacilities().ToList() }, RuleConfiguration = configuration,
                ContentPackHash = pack.ContentHash, Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1,
                CurrentPlayerId = 1, StartPlayerId = 1,
                Players = { new PlayerState { PlayerId = 1, Resources = new ResourceSet { Iron = 1 } },
                    new PlayerState { PlayerId = 2, Resources = new ResourceSet { Iron = targetBalance } } } };
            f.Session = new GameSession(state);
            f.Session.RegisterHandler(new AnswerInteractionCommandHandler(registry));
            const string entry = "isolated.passenger.ui.invoke";
            registry.Register(new EffectRegistration(entry, context => EffectStepResult.Completed().AddEvent(
                new EffectEventRequest { EventType = CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType,
                    RouteKey = ability.AbilityId, PlayerId = context.Node.PlayerId,
                    Payload = NormalizedValue.CreateObject(Array.Empty<NormalizedValueEntry>()) })));
            Assert.That(registry.CharacterActivationSubscriptions[ability.AbilityId], Is.EqualTo(ability.SubscriptionId));
            Assert.That(registry.ContentPackHash, Is.EqualTo(pack.ContentHash));
            Assert.That(f.State.ContentPackHash, Is.EqualTo(pack.ContentHash));
            Assert.That(f.State.RuleConfiguration.ContentPackHash, Is.EqualTo(pack.ContentHash));
            Assert.That(f.State.RuleConfiguration.EnabledExpansionIds, Is.EquivalentTo(new[] { "core", "enterprise" }));
            var engine = new EffectTreeExecutor(f.State, registry);
            engine.CreateRoot(EffectSpec.Create(entry, playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, f.State.EffectRuntime.LastFaultMessage);

            dialog = Activator.CreateInstance(T("CharacterCardEffectChoiceDialog"), Members, null,
                new object[] { registrySource, pageHost }, null);
            coordinator = Activator.CreateInstance(T("CharacterAbilityInteractionUiCoordinator"), Members, null,
                new object[] { (Func<GameState>)(() => f.State), (Func<int>)(() => 1), dialog,
                    (Action<IReadOnlyList<WorkflowHighlight>>)(_ => { }), (Action)(() => { }),
                    (Action<GameCommand>)(command => sent.Add(command)), (Action<string>)(_ => { }) }, null);
            coordinator.GetType().GetProperty("GetVisibleRequests").SetValue(coordinator,
                (Func<IReadOnlyList<InteractionRequest>>)(() => f.State.EffectRuntime.InteractionRequests));
            coordinator.GetType().GetProperty("CanSubmit").SetValue(coordinator, (Func<bool>)(() => true));
            router = new InteractionRequestRouter();
            router.Register((IInteractionRequestRenderer)coordinator);
            Action refresh = () => router.RouteOpen(f.State.EffectRuntime.InteractionRequests, 1);
            Action<string, string, bool, CommandErrorCode> resolved = (game, command, applied, error) =>
            {
                receipts++;
                Assert.That(game, Is.EqualTo(f.State.GameId));
                Assert.That(command, Is.EqualTo(sent.Last().CommandId));
                Assert.That(applied, Is.True, error.ToString());
                Call(coordinator, "NotifyInteractionCommandResolved", game, command, applied, error);
            };
            submission = Activator.CreateInstance(T("CommandSubmissionController"), Members, null,
                new object[] { f.Session, null, 1, null, refresh, (Action<string>)(_ => { }),
                    (Action<string>)(_ => { }), null, resolved }, null);
            Call(submission, "Initialize");
            refresh(); yield return Settle();
            f.Record("异客组件接缝边界", new { isolated = true, targetBalance, chosenAmount,
                ability.Version, ability.CompletionHandlerId, luaHash = LuaContentHasher.ComputeSha256(source),
                pageSource = "实际 SampleScene 的 GameplayInteractionHud.DialogRegistry 与 Frame.ContentRect",
                scope = "独立两人状态；正式策略 Lua；三页射线输入；真实提交账本准确回执；不计正常完整局" });

            var targetRequest = f.Request;
            Assert.That(targetRequest.PromptKey, Is.EqualTo("effect.player.choose_target"));
            Assert.That(targetRequest.CandidateIds, Is.EqualTo(new[] { "player:2" }));
            var players = Active("PlayerSelectionPageView").Single();
            Assert.That(((IEnumerable<int>)Read(Read(players, "Draft"), "SelectedIds")), Is.Empty,
                "两人局也必须由本人选择唯一对手，不能默认提交");
            var opponent = ((IEnumerable)Read(players, "Options")).Cast<Component>()
                .Single(value => value.gameObject.name == "Player Option 2");
            yield return f.Click(ButtonObject(opponent, "Button"), "隔离异客_选对手");
            Assert.That(sent, Is.Empty);
            yield return f.Click(ButtonObject(players, "ConfirmButton"), "隔离异客_确认对手");
            AssertQueued(targetRequest, 1, "player:2", null);
            AssertUnchanged(targetBalance);
            AcceptQueued(); yield return Settle();

            var resourceRequest = f.Request;
            Assert.That(resourceRequest.GetStableInteractionId(), Is.Not.EqualTo(targetRequest.GetStableInteractionId()));
            Assert.That(resourceRequest.PromptKey, Is.EqualTo("effect.resource.choose_type"));
            Assert.That(resourceRequest.CandidateIds, Is.EquivalentTo(new[] { "originium", "originium-shard", "iron" }));
            var resourceRows = Rows();
            Assert.That(resourceRows, Has.Length.EqualTo(3));
            var resourcePage = Active("EffectDialogShellView").Single();
            Assert.That(Confirm(resourcePage).interactable, Is.False, "不得带入对手选择或默认选中第一种资源");
            int iron = resourceRequest.CandidateIds.IndexOf("iron");
            yield return f.Click(ButtonObject(resourceRows[iron], "IncreaseButton"), "隔离异客_选择异铁");
            Assert.That(sent, Has.Count.EqualTo(1));
            yield return f.Click(Confirm(resourcePage).gameObject, "隔离异客_确认资源");
            AssertQueued(resourceRequest, 2, "iron", null);
            AssertUnchanged(targetBalance);
            AcceptQueued(); yield return Settle();

            var quantityRequest = f.Request;
            Assert.That(quantityRequest.GetStableInteractionId(), Is.Not.EqualTo(resourceRequest.GetStableInteractionId()));
            Assert.That(quantityRequest.InteractionTypeId, Is.EqualTo("effect.quantity.choose"));
            Assert.That(quantityRequest.AnswerSchema, Is.EqualTo("bounded_amount"));
            Assert.That(quantityRequest.CandidateIds, Is.Empty);
            Assert.That(Parameter(quantityRequest.PromptParameters, "minAmount").IntegerValue, Is.Zero);
            Assert.That(Parameter(quantityRequest.PromptParameters, "maxAmount").IntegerValue, Is.EqualTo(targetBalance));
            var rowIds = Parameter(quantityRequest.PromptParameters, "rowIds");
            Assert.That(rowIds.Kind, Is.EqualTo(NormalizedValueKind.Array));
            Assert.That(rowIds.Items, Has.Count.EqualTo(1));
            Assert.That(rowIds.Items[0].StringValue, Is.Not.EqualTo("player_marker"));
            Assert.That(Parameter(quantityRequest.PromptParameters, "rowMaximums").Items.Single().IntegerValue,
                Is.EqualTo(targetBalance));
            ProbeRowBinding(router.CurrentRequest, rowIds.Items[0].StringValue);
            if (targetBalance == 0) ProbeLegacyMarkerBinding();
            Call(dialog, "BindGameplayText", null, null);
            Call(dialog, "Hide"); refresh(); yield return Settle();
            var quantityRow = Rows().Single();
            var quantityPage = Active("EffectDialogShellView").Single();
            Assert.That(((InputField)Read(quantityRow, "ValueInput")).text, Is.EqualTo("0"));
            if (targetBalance == 0)
            {
                Assert.That(((Button)Read(quantityRow, "IncreaseButton")).interactable, Is.False);
                Assert.That(Confirm(quantityPage).interactable, Is.True, "0…0 是合法数量，零不是取消");
            }
            for (int i = 0; i < chosenAmount; i++)
                yield return f.Click(ButtonObject(quantityRow, "IncreaseButton"), "隔离异客_增加数量_" + i);
            Assert.That(sent, Has.Count.EqualTo(2));
            yield return f.Click(Confirm(quantityPage).gameObject, "隔离异客_确认整数数量");
            AssertQueued(quantityRequest, 3, null, chosenAmount.ToString(CultureInfo.InvariantCulture));
            AssertUnchanged(targetBalance);
            AcceptQueued(); yield return Settle();
            Assert.That(f.Request, Is.Null);
            Assert.That(Read(dialog, "IsShowing"), Is.EqualTo(false), "不增加第四个确认页面");
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(1 + chosenAmount));
            Assert.That(f.State.FindPlayer(2).Resources.Iron, Is.EqualTo(targetBalance - chosenAmount));
            Assert.That(f.State.Players.Select(value => value.Score), Is.EqualTo(new[] { 1, 1 }));
            Assert.That(sent, Has.Count.EqualTo(3)); Assert.That(receipts, Is.EqualTo(3));
            f.Record("异客组件接缝结果", new { isolated = true, targetBalance, chosenAmount,
                commands = sent.Select(command => new { command.CommandId, command.PlayerId, command.OptionIds, command.Parameters }),
                receipts, resources = f.State.Players.Select(player => new { player.PlayerId, player.Resources.Iron, player.Score }) });
        }

        private static NormalizedValue Parameter(NormalizedValue value, string name) => value.Properties.Single(entry => entry.Name == name).Value;
        private Component[] Rows() => f.Host.GetComponentsInChildren(T("EffectDialogResourceRowView"), false)
            .Where(value => value.gameObject.activeInHierarchy).OrderBy(value => value.transform.GetSiblingIndex()).ToArray();
        private static Button Confirm(Component page) => page.GetComponentsInChildren<Button>(false).Single(value => value.gameObject.name == "Confirm");
        private void AssertUnchanged(int targetBalance)
        {
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(f.State.FindPlayer(2).Resources.Iron, Is.EqualTo(targetBalance));
            Assert.That(f.State.Players.Select(player => player.Score), Is.EqualTo(new[] { 0, 0 }));
        }
        private void AssertQueued(InteractionRequest request, int count, string candidate, string amount)
        {
            Assert.That(sent, Has.Count.EqualTo(count));
            var command = sent.Last();
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(command.PlayerId, Is.EqualTo(1));
            Assert.That(command.Parameters["interactionId"], Is.EqualTo(request.GetStableInteractionId()));
            Assert.That(command.Parameters["expectedRevision"], Is.EqualTo(request.StateRevision.ToString(CultureInfo.InvariantCulture)));
            Assert.That(command.OptionIds, Is.EqualTo(candidate == null ? Array.Empty<string>() : new[] { candidate }));
            if (amount != null) Assert.That(command.Parameters["answerValue"], Is.EqualTo(amount));
            Assert.That(Read(coordinator, "inFlightCommandId"), Is.EqualTo(command.CommandId));
            Call(coordinator, "NotifyInteractionCommandResolved", "wrong.game", command.CommandId, true, CommandErrorCode.None);
            Call(coordinator, "NotifyInteractionCommandResolved", f.State.GameId, previousCommandId ?? "wrong.command", true, CommandErrorCode.None);
            Assert.That(Read(coordinator, "inFlightCommandId"), Is.EqualTo(command.CommandId), "错局/旧命令回执不能释放当前 busy");
            router.RouteOpen(f.State.EffectRuntime.InteractionRequests, 1);
            Assert.That(Read(dialog, "IsShowing"), Is.EqualTo(false), "准确回执到达前不能重新呈现或继续提交");
        }
        private void AcceptQueued()
        {
            var command = sent.Last(); var args = new object[] { command, false };
            var result = (CommandResult)submission.GetType().GetMethod("Submit").Invoke(submission, args);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            Assert.That(args[1], Is.EqualTo(true)); Assert.That(receipts, Is.EqualTo(sent.Count));
            Assert.That(Read(coordinator, "inFlightCommandId"), Is.EqualTo(string.Empty));
            previousCommandId = command.CommandId;
            router.RouteOpen(f.State.EffectRuntime.InteractionRequests, 1);
        }
        private void ProbeRowBinding(InteractionRequestProjection request, string rowId)
        {
            // 只探针正式 rowId 到原行组件的文档接线；不把探针文案当实际内容或视觉通过。
            var probe = GameStateCloneService.DeepClone(request);
            var reference = new GameplayTextRef("isolated.row.binding");
            probe.Presentation = new GameplayInteractionPresentation { QuantityRows = {
                new GameplayQuantityRowPresentation { RowId = rowId, Label = reference } } };
            var document = new GameplayTextDocument(Array.Empty<GameplayTextRun>(), "isolated.row.binding");
            Component rendered = null; int resolves = 0;
            Call(dialog, "BindGameplayText", (Func<GameplayTextRef, GameplayTextDocument>)(text =>
                { if (ReferenceEquals(text, reference)) resolves++; return ReferenceEquals(text, reference) ? document : null; }),
                (Action<Component, GameplayTextDocument, bool>)((target, value, _) =>
                { Assert.That(value, Is.SameAs(document)); rendered = target; }));
            Call(dialog, "ShowQuantity", probe, "", 0, (int)Parameter(probe.PromptParameters, "maxAmount").IntegerValue,
                new[] { 0 }, null, (Func<bool>)(() => true), (Action<int>)(_ => { }));
            Assert.That(resolves, Is.EqualTo(1), "正式 rowIds 必须命中自己的 QuantityRows 文档");
            Assert.That(rendered, Is.Not.Null);
            Assert.That(rendered.gameObject, Is.EqualTo(((Text)Read(Rows().Single(), "Label")).gameObject));
        }
        private void ProbeLegacyMarkerBinding()
        {
            var old = GameStateCloneService.DeepClone(router.CurrentRequest);
            old.PromptParameters = NormalizedValue.CreateObject(new[] {
                new NormalizedValueEntry { Name = "amountKind", Value = NormalizedValue.CreateString("player_marker") },
                new NormalizedValueEntry { Name = "minAmount", Value = NormalizedValue.CreateInteger(0) },
                new NormalizedValueEntry { Name = "maxAmount", Value = NormalizedValue.CreateInteger(0) } });
            ProbeRowBinding(old, "player_marker");
            old.PromptParameters.Properties.Add(new NormalizedValueEntry { Name = "rowIds",
                Value = NormalizedValue.CreateArray(new[] { NormalizedValue.CreateString("player_marker") }) });
            ProbeRowBinding(old, "player_marker");
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            router?.Clear(); (submission as IDisposable)?.Dispose(); (coordinator as IDisposable)?.Dispose();
            router = null; submission = coordinator = dialog = null;
            yield return SceneManager.LoadSceneAsync("StartScene");
        }
    }
}
