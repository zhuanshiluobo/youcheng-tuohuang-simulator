using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class EventCardInteractionUiCoordinatorTests
    {
        private const string Platform = "enterprise.event.collection_platform";
        private const string Cave = "enterprise.event.cave_ruins";
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private ExternalContentPack installed;
        private GameObject hud;
        private Component frame, registry;
        private object dialog, visuals, previousResolver;
        private IInteractionRequestRenderer coordinator;
        private GameState state;
        private EffectRegistry effects;
        private InteractionRequest request;
        private ItemInstanceState revealedItem;
        private readonly List<GameCommand> commands = new List<GameCommand>();
        private int viewer;
        private string prompt;

        private static Type Runtime(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Instance).Invoke(target, args);
        private static T Property<T>(object target, string name) =>
            (T)target.GetType().GetProperty(name, Instance).GetValue(target);

        [OneTimeSetUp]
        public void LoadInstalledContent()
        {
            installed = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            if (!EventCardDatabase.IsInitialized) EventCardDatabase.InitializeExternal(installed.CreateEvents());
        }

        [SetUp]
        public void CreateDialogFromExistingHudPrefab()
        {
            commands.Clear(); viewer = 1; prompt = string.Empty;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            Assert.That(prefab, Is.Not.Null);
            hud = UnityEngine.Object.Instantiate(prefab);
            frame = hud.GetComponentInChildren(Runtime("GameplayHudFrame"), true);
            registry = hud.GetComponentInChildren(Runtime("GameplayDialogRegistry"), true);
            Assert.That(frame, Is.Not.Null); Assert.That(registry, Is.Not.Null);
            if ((Component)Runtime("GameplayHudFrame").GetProperty("Active").GetValue(null) != frame) Call(frame, "Awake");
            var parent = Property<RectTransform>(frame, "ContentRect");
            dialog = Activator.CreateInstance(Runtime("EventChoiceDialog"), Instance, null,
                new object[] { registry, (Func<RectTransform>)(() => parent) }, null);
            visuals = Property<object>(registry, "CardVisualCatalog");
            previousResolver = visuals.GetType().GetField("runtimeDefinitionResolver", Instance).GetValue(visuals);
            Call(visuals, "BindRuntimeDefinitions", (Func<string, string>)(id => GameContentDefinitions.ResolveDefinitionId(state, id)));
        }

        [TearDown]
        public void DisposeTransientUi()
        {
            coordinator?.Clear(); coordinator = null;
            if (dialog != null) Call(dialog, "Hide"); dialog = null;
            if (visuals != null) visuals.GetType().GetField("runtimeDefinitionResolver", Instance).SetValue(visuals, previousResolver);
            visuals = null;
            if (hud != null) UnityEngine.Object.DestroyImmediate(hud);
        }

        private void RevealFormalFourPlayerEvent(string definitionId, bool enterprise)
        {
            var configuration = new GameRuleConfiguration { PlayerCount = 4, MapId = StaticMapDefinitions.FourPlayerMapId,
                EnabledExpansionIds = enterprise ? new List<string> { "core", "enterprise" } : new List<string> { "core" } };
            var catalog = ActiveContentCatalogBuilder.Build(installed, configuration);
            configuration.ContentPackHash = catalog.ContentPackHash;
            state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1,
                Enumerable.Range(1, 4).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "测试玩家" + id }).ToList(),
                configuration, 321, catalog.SetupRecipe, catalog.ContentDefinitions);
            state.Phase = GamePhase.ActionRound1; state.Round = state.ActionRound = 1;
            state.CurrentPlayerId = state.StartPlayerId = 1;
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2, 3, 4 };
            revealedItem = state.ItemZones.Items.Single(item => item.DefinitionId == definitionId);
            var order = state.ItemZones.Orders.Single(value => value.ZoneId == revealedItem.ZoneId);
            order.ItemIds.Remove(revealedItem.ItemId); order.ItemIds.Insert(0, revealedItem.ItemId); order.OrderRevision++;
            var card = EventCardDatabase.Get(state, definitionId);
            Assert.That(card, Is.Not.Null);
            var map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
            var influence = new InfluenceService(map);
            effects = new EffectRegistry(); catalog.Register(effects);
            ResourceEffectExecutor.Register(effects); InfluenceEffectExecutor.Register(effects, influence);
            EventCardEffectExecutor.Register(effects, map, influence, new EventDeckService(321), new ResourceTokenService());
            var target = map.Map.Locations.First(location => StaticMapDefinitions.GetEventColor(map.Map, location.LocationId) == card.Color);
            var engine = new EffectTreeExecutor(state, effects);
            engine.CreateRoot(EventCardEffectSpecFactory.Resolve(1, card.Color, target.LocationId));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            request = state.EffectRuntime.InteractionRequests.Single(value => value.Status == "open");
            Assert.That(request.InteractionTypeId, Is.EqualTo(EventCardEffectExecutor.OptionInteractionTypeId));
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
            Assert.That(state.Players.Count, Is.EqualTo(4));
            coordinator = (IInteractionRequestRenderer)Activator.CreateInstance(Runtime("EventCardInteractionUiCoordinator"), Instance, null,
                new object[] { (Func<GameState>)(() => state), (Func<int>)(() => viewer), (Func<int, string>)(id => "玩家" + id), dialog,
                    (Action<IReadOnlyList<WorkflowHighlight>>)(_ => { }), (Action)(() => { }),
                    (Action<GameCommand>)(command => commands.Add(command)), (Action<string>)(value => prompt = value) }, null);
            // 展示和提交都消费玩家投影；不回读未投影的 Host 请求。
            Call(coordinator, "set_GetVisibleRequests", (Func<IReadOnlyList<InteractionRequest>>)(() =>
                VisibleInteractionRequestSource.Read(state, GameStateViewProjector.ProjectForPlayer(state, viewer), viewer)));
        }

        private Component Render()
        {
            Call(frame, "SetRequest", request.GetStableInteractionId(), request.StateRevision);
            coordinator.Render(InteractionRequestProjector.ProjectForPlayer(request, viewer));
            return (Component)dialog.GetType().GetField("view", Instance).GetValue(dialog);
        }

        private static Button OptionButton(Component view, int visualIndex)
        {
            var markerType = Type.GetType("YC.PlayerJourney.PlayerAutomationId, YC.PlayerJourney", true);
            var marker = view.GetComponentsInChildren(markerType, true).Single(component =>
                (string)markerType.GetField("Id").GetValue(component) == "event.option." + visualIndex);
            return marker.GetComponent<Button>();
        }

        private void AssertArtwork(Component view, string definitionId, string spriteName)
        {
            Assert.That(view, Is.Not.Null, "真实事件请求应建立弹窗。");
            Assert.That(view.gameObject.activeInHierarchy, Is.True);
            var sprite = (Sprite)Call(visuals, "GetEvent", definitionId);
            Assert.That(sprite, Is.Not.Null); Assert.That(sprite.name, Is.EqualTo(spriteName));
            var image = Property<RawImage>(view, "EventCardArtworkImage");
            Assert.That(image.texture, Is.SameAs(sprite.texture));
            var rect = sprite.textureRect;
            Assert.That(image.uvRect, Is.EqualTo(new Rect((rect.x + .5f) / sprite.texture.width,
                (rect.y + .5f) / sprite.texture.height, (rect.width - 1f) / sprite.texture.width, (rect.height - 1f) / sprite.texture.height)));
        }

        private GameCommand AssertAnswer(string definitionId, int optionIndex)
        {
            Assert.That(commands.Count, Is.EqualTo(1));
            var command = commands.Single();
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(command.PlayerId, Is.EqualTo(1));
            Assert.That(command.OptionIds, Is.EqualTo(new[] { EventCardOptionIdFactory.Create(definitionId, optionIndex) }));
            Assert.That(command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter], Is.EqualTo(request.GetStableInteractionId()));
            Assert.That(command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter], Is.EqualTo(request.StateRevision.ToString()));
            return command;
        }

        [TestCase(Platform, "event_red_007", 0)] [TestCase(Platform, "event_red_007", 1)] [TestCase(Platform, "event_red_007", 2)]
        [TestCase(Cave, "event_yellow_012", 0)] [TestCase(Cave, "event_yellow_012", 1)] [TestCase(Cave, "event_yellow_012", 2)]
        public void FourPlayerEnterpriseReplacementDisplaysAndAnswersItsOwnDefinition(string id, string spriteName, int option)
        {
            RevealFormalFourPlayerEvent(id, true);
            string original = id == Platform ? "event_red_03" : "event_yellow_02";
            Assert.That(state.ItemZones.Items.Any(item => item.DefinitionId == original), Is.False);
            var view = Render(); AssertArtwork(view, id, spriteName);
            OptionButton(view, option).onClick.Invoke(); AssertAnswer(id, option);
            Assert.That(Property<bool>(dialog, "IsShowing"), Is.False);
        }

        [TestCase("event_red_03", 0, 1)] [TestCase("event_red_03", 1, 0)]
        [TestCase("event_yellow_02", 0, 0)] [TestCase("event_yellow_02", 1, 1)]
        public void CoreCardsRetainTheirExistingArtworkOptionMapping(string id, int visualIndex, int ruleIndex)
        {
            RevealFormalFourPlayerEvent(id, false);
            var view = Render(); Assert.That(view, Is.Not.Null);
            OptionButton(view, visualIndex).onClick.Invoke(); AssertAnswer(id, ruleIndex);
        }

        [TestCase(Platform)] [TestCase(Cave)]
        public void RealInstanceIdResolvesToItsFrozenDefinitionAndKeepsDefinitionCandidates(string id)
        {
            RevealFormalFourPlayerEvent(id, true);
            request.PromptParameters.Properties.Single(value => value.Name == "cardId").Value = NormalizedValue.CreateString(revealedItem.ItemId);
            Assert.That(revealedItem.ItemId, Is.Not.EqualTo(id));
            var view = Render(); AssertArtwork(view, id, id == Platform ? "event_red_007" : "event_yellow_012");
            OptionButton(view, 1).onClick.Invoke(); AssertAnswer(id, 1);
        }

        [TestCase("event_green_01")] [TestCase("event.unknown")]
        public void MissingDefinitionInCurrentGameDoesNotFallBackToGlobalCatalog(string id)
        {
            RevealFormalFourPlayerEvent("event_green_01", false);
            Assert.That(EventCardDatabase.Get("event_green_01"), Is.Not.Null);
            state.ContentDefinitions.Events.RemoveAll(value => value.CardId == id);
            request.PromptParameters.Properties.Single(value => value.Name == "cardId").Value = NormalizedValue.CreateString(id);
            Assert.That(Render(), Is.Null); Assert.That(commands, Is.Empty);
            Assert.That(prompt, Does.Contain(id));
        }

        [Test]
        public void NonAnsweringPlayerCannotRenderOrSubmitEnterpriseOptions()
        {
            RevealFormalFourPlayerEvent(Platform, true); viewer = 2;
            Assert.That(coordinator.CanRender(InteractionRequestProjector.ProjectForPlayer(request, viewer)), Is.False);
            Assert.That(Render(), Is.Null); Assert.That(commands, Is.Empty);
        }

        [TestCase(true)] [TestCase(false)]
        public void ChangedRevisionOrCandidateVersionRejectsPreviouslyDisplayedButton(bool changeRevision)
        {
            RevealFormalFourPlayerEvent(Platform, true);
            var button = OptionButton(Render(), 1);
            if (changeRevision) request.StateRevision++; else request.CandidateSetVersion++;
            button.onClick.Invoke(); Assert.That(commands, Is.Empty);
        }

        [TestCase(Platform, 1, 4, 3, 0, 0)] [TestCase(Platform, 2, 0, 0, 4, 5)]
        [TestCase(Cave, 0, 5, 0, 0, 0)] [TestCase(Cave, 1, 0, 0, 0, 10)]
        public void DisplayedRewardChoiceResolvesThroughFormalLuaExactlyOnce(string id, int option, int originium, int shards, int iron, int vouchers)
        {
            RevealFormalFourPlayerEvent(id, true);
            var before = GameStateCloneService.DeepClone(state.FindPlayer(1).Resources);
            OptionButton(Render(), option).onClick.Invoke();
            var command = AssertAnswer(id, option);
            var handler = new AnswerInteractionCommandHandler(effects);
            var result = handler.Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            var after = GameStateCloneService.DeepClone(state.FindPlayer(1).Resources);
            Assert.That(after.Originium - before.Originium, Is.EqualTo(originium));
            Assert.That(after.OriginiumShard - before.OriginiumShard, Is.EqualTo(shards));
            Assert.That(after.Iron - before.Iron, Is.EqualTo(iron));
            Assert.That(after.GoldVoucher - before.GoldVoucher, Is.EqualTo(vouchers));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(value => value.Status == "open"), Is.False);
            Assert.That(handler.Handle(state, command).Succeeded, Is.False, "同一已完成请求不能重复领取奖励。");
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(after.Originium));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(after.OriginiumShard));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(after.Iron));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(after.GoldVoucher));
        }
    }
}
