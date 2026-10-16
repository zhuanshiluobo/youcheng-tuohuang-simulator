using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Presentation.Workflows;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.PlayMode.ExpansionRules
{
    public sealed class EnterpriseGameplayUiTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private Component controller, hud, adapter;
        private GameState state;
        private GameStateView view;
        private EffectRegistry registry;
        private EnterpriseDefinitions definitions;
        private object enterpriseRenderer, characterRenderer, router;
        private int viewer = 1;
        private readonly List<GameCommand> commands = new List<GameCommand>();
        private readonly string[] enterpriseIds = { "enterprise.rhodes_island", "enterprise.hyde_brothers", "enterprise.rhine_lab", "enterprise.fussen_bank",
            "enterprise.raythean", "enterprise.mama_johns", "enterprise.blacksteel", "enterprise.rim_billiton" };
        private readonly string[] departmentIds = { "enterprise.department.engineering", "enterprise.department.defense", "enterprise.department.structural" };
        private static Type Presentation(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("YC.Presentation." + name)).First(type => type != null);
        private static object Field(object obj, string name) => obj.GetType().GetField(name, Private | BindingFlags.Public).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Private | BindingFlags.Public).SetValue(obj, value);
        private static object Call(object obj, string name, params object[] args)
        {
            var method = obj.GetType().GetMethods(Private | BindingFlags.Public).Single(value => value.Name == name && value.GetParameters().Length == args.Length &&
                value.GetParameters().Select((parameter, index) => args[index] == null || parameter.ParameterType.IsInstanceOfType(args[index]) || parameter.ParameterType.IsByRef).All(value => value));
            return method.Invoke(obj, args);
        }
        private static object Property(object obj, string name) => obj.GetType().GetProperty(name, Private | BindingFlags.Public).GetValue(obj);
        private static Component Find(string name) => UnityEngine.Object.FindObjectOfType(Presentation(name)) as Component;
        private static void Ignore<T>(T value) { }

        [UnitySetUp] public IEnumerator LoadActualThreePlayerScene() => LoadSceneWithPlayers(3);
        public IEnumerator LoadSceneWithPlayers(int count)
        {
            var launch = Presentation("GameLaunchContext").GetMethod("Ensure").Invoke(null, null);
            var config = new GameRuleConfiguration { PlayerCount = count, MapId = count == 3 ? StaticMapDefinitions.ThreePlayerMapId : StaticMapDefinitions.FourPlayerMapId };
            var seats = Enumerable.Range(1, count).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "合同玩家" + id }).ToList();
            Presentation("GameLaunchContext").GetMethod("Configure", new[] { typeof(LaunchMode), typeof(int), typeof(string), typeof(IList<PlayerSeat>), typeof(GameRuleConfiguration) })
                .Invoke(launch, new object[] { LaunchMode.Local, 1, "", seats, config });
            yield return SceneManager.LoadSceneAsync(count == 3 ? "ThreePlayerScene" : "SampleScene");
            yield return null;
            controller = Find("MobileCityInteractionController"); hud = Find("GameplayInteractionHudView"); adapter = Find("GameplayExpansionUi");
            Assert.That(controller, Is.Not.Null); Assert.That(hud, Is.Not.Null); Assert.That(adapter, Is.Not.Null);
            ((Behaviour)controller).enabled = false;
            viewer = 1; commands.Clear();
            var actual = (GameState)Property(controller, "CurrentState");
            state = GameStateCloneService.DeepClone(actual);
            state.EffectRuntime = new EffectRuntimeState(); state.CharacterUses.Clear(); state.CharacterCoverSlots.Clear();
            state.EffectRuntime.PlayerOrderSnapshot = state.Players.Select(player => player.PlayerId).ToList();
            state.Phase = GamePhase.ActionRound1; state.Round = 1; state.ActionRound = 1; state.CurrentPlayerId = state.StartPlayerId = 1;
            foreach (var player in state.Players) { player.CharacterCardLockedThisTurn = false; player.UsedCharacterThisRound = false; player.Resources.GoldVoucher = 50; }
            definitions = Definitions();
            registry = new EffectRegistry { ContentPackHash = state.ContentPackHash, ContentDefinitions = state.ContentDefinitions, SetupRecipe = state.SetupRecipe };
            EnterpriseEffectExecutor.Register(registry, definitions); ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            CharacterLifecycleService.Register(registry); OperatorDispatchFlow.Register(registry); ExpansionSetupFlow.Register(registry, max => 0);
            AddBoardItems(); RefreshView();
            Call(adapter, "Configure", (Func<GameStateView>)(() => view), (Func<int>)(() => viewer), definitions,
                registry.CharacterAbilityContracts, (Action<GameCommand>)(command => commands.Add(command)));
            var dialogs = Property(hud, "DialogRegistry");
            enterpriseRenderer = Activator.CreateInstance(Presentation("EnterpriseInteractionUiCoordinator"), Private | BindingFlags.Public, null,
                new object[] { adapter, hud, (Func<IReadOnlyList<InteractionRequest>>)(() => VisibleRequests()), (Func<int>)(() => viewer), (Action<GameCommand>)(command => commands.Add(command)) }, null);
            var choice = Activator.CreateInstance(Presentation("CharacterCardEffectChoiceDialog"), Private | BindingFlags.Public, null,
                new object[] { dialogs, Property(Property(hud, "Frame"), "ContentRect") }, null);
            var highlightDelegate = Presentation("CharacterAbilityInteractionUiCoordinator").GetConstructors().Single().GetParameters()[3].ParameterType;
            var ignoreHighlights = typeof(EnterpriseGameplayUiTests).GetMethod(nameof(Ignore), BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(highlightDelegate.GetGenericArguments()[0]).CreateDelegate(highlightDelegate);
            characterRenderer = Activator.CreateInstance(Presentation("CharacterAbilityInteractionUiCoordinator"), Private | BindingFlags.Public, null,
                new object[] { (Func<GameState>)(() => state), (Func<int>)(() => viewer), choice,
                    ignoreHighlights, (Action)(() => { }), (Action<GameCommand>)(command => commands.Add(command)), (Action<string>)(_ => { }) }, null);
        }

        // 表现程序集没有对测试程序集的反向引用；内部协调器通过反射接入真实资产，领域仍使用生产类型。
        private void BindCharacterRenderer()
        {
            characterRenderer?.GetType().GetProperty("ExpansionUi").SetValue(characterRenderer, adapter);
            characterRenderer?.GetType().GetProperty("GetVisibleState").SetValue(characterRenderer, (Func<GameStateView>)(() => view));
            characterRenderer?.GetType().GetProperty("GetVisibleRequests").SetValue(characterRenderer, (Func<IReadOnlyList<InteractionRequest>>)(() => VisibleRequests()));
        }
        private IReadOnlyList<InteractionRequest> VisibleRequests()
        {
            var source = Presentation("Workflows.VisibleInteractionRequestSource");
            return (IReadOnlyList<InteractionRequest>)source.GetMethod("Read").Invoke(null, new object[] { state, view, viewer });
        }
        private void Route()
        {
            if (router == null)
            {
                router = Activator.CreateInstance(Presentation("Workflows.InteractionRequestRouter"));
                Call(router, "Register", enterpriseRenderer); Call(router, "Register", characterRenderer);
            }
            BindCharacterRenderer();
            Call(router, "RouteOpen", VisibleRequests(), viewer);
        }
        [UnityTearDown] public IEnumerator ReleaseUi()
        {
            if (router != null) Call(router, "Clear");
            if (enterpriseRenderer != null) Call(enterpriseRenderer, "Dispose");
            if (characterRenderer != null) Call(characterRenderer, "Dispose");
            router = enterpriseRenderer = characterRenderer = null;
            if (adapter != null) Call(adapter, "Clear");
            yield return null;
        }
        private EnterpriseDefinitions Definitions()
        {
            var result = new EnterpriseDefinitions();
            foreach (string id in enterpriseIds)
            {
                bool department = id == "enterprise.rhine_lab";
                var definition = new EnterpriseDefinition { DefinitionId = id, RuntimeId = id, DisplayNameKey = id,
                    DepartmentRewardPolicy = department ? "active_department" : "", Levels = Enumerable.Range(0, 6).Select(level => new EnterpriseLevelDefinition
                    { Level = level, Kind = level == 0 ? "empty" : department && level > 1 ? "department_reward" : level % 2 == 1 ? "reward" : "special",
                        AbilityId = level == 0 || department && level > 1 ? "" : id + ".level_" + level + (level % 2 == 1 ? ".reward" : ".special") }).ToList() };
                result.Register(definition);
                foreach (var level in definition.Levels.Where(value => value.AbilityId.Length > 0))
                    result.BindAbility(level.AbilityId, context => new[] { ResourceEffectSpecFactory.Gain(context.Event.PlayerId, ResourceType.GoldVoucher, 1) });
            }
            foreach (string id in departmentIds)
            {
                result.Register(new DepartmentDefinition { DefinitionId = id, EnterpriseRuntimeId = "enterprise.rhine_lab",
                    Rewards = Enumerable.Range(2, 4).Select(level => new DepartmentRewardDefinition { Level = level, AbilityId = id + ".level_" + level + ".reward" }).ToList() });
                foreach (int level in Enumerable.Range(2, 4)) result.BindAbility(id + ".level_" + level + ".reward", context => new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) });
            }
            result.Freeze(); return result;
        }
        private void AddBoardItems()
        {
            foreach (string id in enterpriseIds.Concat(departmentIds))
            {
                if (state.ItemZones.Items.Any(item => item.DefinitionId == id)) continue;
                var zone = FindZone(state, id.StartsWith("enterprise.department.") ? "departments" : "reserve");
                var item = new ItemInstanceState { ItemId = StableIdFactory.Create("item", state.GameId, id, "ui_contract", "0"), DefinitionId = id,
                    TypeId = "core.board_card", PhysicalKind = ItemPhysicalKind.Card, ZoneId = zone.ZoneId };
                state.ItemZones.Items.Add(item);
                var order = state.ItemZones.Orders.Find(value => value.ZoneId == zone.ZoneId); order?.ItemIds.Add(item.ItemId);
            }
        }
        private void PublishFourBoards()
        {
            foreach (string id in enterpriseIds.Take(4))
            {
                var board = Item(id);
                Place(board, FindZone(state, "enterprises").ZoneId);
                foreach (var player in state.Players.ToArray())
                {
                    var declaration = new CardZoneDefinition { LocalZoneId = "cooperation_" + player.PlayerId, ZoneTemplateId = "core.zone.board_marker_track",
                        AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                        CapacityPolicy = "bounded", Capacity = 1, OrderingKind = ZoneOrderingKind.Track, EntryPolicyId = "core.owned_player_marker",
                        ExitPolicyId = "core.explicit_transfer", VisibilityPolicyId = ItemZoneVisibility.Public, CarrierExitPolicyId = "core.preserve_contents" };
                    Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, board.ItemId, declaration, out string zone, out string reason), Is.True, reason);
                    var marker = state.ItemZones.Items.First(value => value.ZoneId == FindZone(state, "player_markers", player.PlayerId).ZoneId);
                    Place(marker, zone); marker.SlotId = "level_" + (id == "enterprise.rhine_lab" ? 1 : player.PlayerId + 1);
                }
            }
            var rhine = Item("enterprise.rhine_lab"); var department = Item(departmentIds[0]);
            state.ItemZones.Relations.Add(new ItemRelationState { RelationId = StableIdFactory.Create("relation", rhine.ItemId, "active_department"),
                RelationTypeId = EnterpriseStateQuery.ActiveDepartmentRelation, SourceItemId = rhine.ItemId, TargetItemId = department.ItemId });
            RefreshView();
        }
        private ItemInstanceState Item(string definition) => state.ItemZones.Items.First(value => value.DefinitionId == definition);
        private void Place(ItemInstanceState item, string zoneId)
        {
            foreach (var order in state.ItemZones.Orders) order.ItemIds.Remove(item.ItemId);
            item.ZoneId = zoneId; item.SlotId = string.Empty;
            state.ItemZones.Orders.Find(value => value.ZoneId == zoneId)?.ItemIds.Add(item.ItemId);
        }
        private void RefreshView() => view = GameStateViewProjector.ProjectForPlayer(state, viewer);
        private InteractionRequest Open() => state.EffectRuntime.InteractionRequests.Last(value => value.Status == "open");
        private InteractionRequestProjection Projection() => InteractionRequestProjector.ProjectForPlayer(Open(), viewer);
        private void Run(EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(spec);
            var result = engine.RunUntilQuiescent(); Assert.That(result.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); RefreshView();
        }
        private void Apply(GameCommand command)
        {
            var result = new AnswerInteractionCommandHandler(registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.LogMessage); RefreshView();
            Call(enterpriseRenderer, "NotifyCommandSettled", command.CommandId); Call(characterRenderer, "NotifyCommandSettled", command.CommandId);
            Call(adapter, "NotifyCommandSettled");
        }
        private void RenderDetails()
        {
            Call(Property(hud, "MainModules"), "Render", state, view, viewer, Property(Property(hud, "DialogRegistry"), "CardVisualCatalog"));
            Call(adapter, "Refresh");
        }
        private ItemInstanceState[] Cards(string role, int player = -1) => state.ItemZones.Items
            .Where(item => item.ZoneId == FindZone(state, role, player).ZoneId).ToArray();
        private void AddOperators(int count)
        {
            var definition = Cards("hand", 1)[0].DefinitionId;
            for (int index = 0; index < count; index++)
            {
                var item = new ItemInstanceState { ItemId = StableIdFactory.Create("item", state.GameId, "operator", "ui_contract", index.ToString()),
                    DefinitionId = definition, TypeId = "core.operator_card", PhysicalKind = ItemPhysicalKind.Card };
                state.ItemZones.Items.Add(item); Place(item, FindZone(state, "operator_deck").ZoneId);
            }
        }
        private Component EnterpriseDialog(bool department = false) => Field(enterpriseRenderer, department ? "department" : "enterprise") as Component;
        private void ChooseAndConfirm(Component dialog, params string[] ids)
        {
            foreach (string id in ids) Call(dialog, "Choose", id);
            ((Button)Field(dialog, "confirm")).onClick.Invoke();
        }
        private IEnumerator Screenshot(string name)
        {
            var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(1920, 1080) });
            for (int frame = 0; frame < 8; frame++) yield return null;
            Canvas.ForceUpdateCanvases();
            string directory = Path.Combine(UnityEngine.Application.dataPath, "..", "Logs", "ExpansionValidation", "PlayerDetails", "enterprise-screenshots");
            Directory.CreateDirectory(directory);
            string path = Path.GetFullPath(Path.Combine(directory, name + "-合同数据.png"));
            ScreenCapture.CaptureScreenshot(path);
            for (int frame = 0; frame < 12; frame++) yield return null;
            Assert.That(File.Exists(path), Is.True, "实际场景截图未生成：" + path);
        }

        [UnityTest] public IEnumerator BothActualScenesUseConfiguredSourceAndPreserveNegativeScore()
        {
            foreach (string scene in new[] { "ThreePlayerScene", "SampleScene" })
            {
                if (SceneManager.GetActiveScene().name != scene)
                {
                    var launch = Presentation("GameLaunchContext").GetMethod("Ensure").Invoke(null, null);
                    var config = new GameRuleConfiguration { PlayerCount = 4, MapId = StaticMapDefinitions.FourPlayerMapId };
                    var seats = Enumerable.Range(1, 4).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "合同玩家" + id }).ToList();
                    Call(launch, "Configure", LaunchMode.Local, 1, "", seats, config);
                    yield return SceneManager.LoadSceneAsync(scene); yield return null;
                }
                var actualHud = Find("GameplayInteractionHudView"); var actualController = Find("MobileCityInteractionController");
                ((Behaviour)actualController).enabled = false;
                var args = new object[] { null }; Assert.That((bool)Call(actualHud, "TryValidateConfiguration", args), Is.True, args[0] as string);
                Assert.That((bool)Call(actualHud, "IsBoundTo", actualController), Is.True);
                Assert.That(actualHud.GetComponentsInChildren<Component>(true), Is.All.Not.Null);
                var modules = Property(actualHud, "MainModules"); Call(modules, "RenderScore", -12);
                Assert.That(((Text)Field(modules, "localScoreValue")).text, Is.EqualTo("-12"));
                yield return Screenshot(scene + "-实际HUD");
            }
        }
        [UnityTest] public IEnumerator InitialEnterpriseMultiChoiceUsesCandidateIdsAndHidesOnHotseat()
        {
            state.SetupRecipe.EnterpriseEntrance = true; state.EffectRuntime.PlayerOrderSnapshot = state.Players.Select(value => value.PlayerId).ToList();
            Run(ExpansionSetupFlow.PreparationSpec(1)); Route();
            var request = Open(); var dialog = EnterpriseDialog(); Assert.That(dialog.gameObject.activeInHierarchy, Is.True);
            Assert.That((bool)Call(enterpriseRenderer, "CanRender", InteractionRequestProjector.ProjectForPlayer(request, 2)), Is.False);
            ChooseAndConfirm(dialog, request.CandidateIds[0]);
            Assert.That(commands.Count, Is.EqualTo(1)); Assert.That(commands[0].Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(commands[0].OptionIds, Is.EqualTo(new[] { request.CandidateIds[0] }));
            ((Button)Field(dialog, "confirm")).onClick.Invoke(); Assert.That(commands.Count, Is.EqualTo(1));
            yield return Screenshot("开局企业选择");
            viewer = 2; RefreshView(); Route();
            Assert.That(dialog.gameObject.activeInHierarchy, Is.False); Assert.That(Property(Property(dialog, "Draft"), "Projection"), Is.Null);
        }
        [UnityTest] public IEnumerator DirectSpecialPageConsumesFrozenHostRangeInOneSelection()
        {
            PublishFourBoards(); var target = Item("enterprise.hyde_brothers");
            Run(EffectSpec.Create(EnterpriseSpecialSelectionExecutor.UpgradeThenActivateTypeId,
                Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "levels", NormalizedValue.CreateInteger(2), "enterpriseItemIds", Strings(new[] { target.ItemId })), 1));
            var request = Projection(); Assert.That(request.InteractionTypeId, Is.EqualTo("enterprise.special"));
            Route(); var projection = Call(enterpriseRenderer, "BuildProjection", request); var targets = (Array)Field(projection, "Targets");
            Assert.That(targets.Length, Is.EqualTo(request.CandidateIds.Count));
            foreach (var item in targets) Assert.That((string)Field(item, "Description"), Does.Contain(string.Format((string)Field(Property(adapter, "Copy"), "LevelRangeFormat"), 2, 4)));
            var dialog = EnterpriseDialog();
            Call(dialog, "Choose", request.CandidateIds[0]);
            yield return Screenshot("单页直接特效");
            Call(dialog, "Choose", request.CandidateIds[0]);
            ChooseAndConfirm(dialog, request.CandidateIds[0]); Apply(commands.Last()); Route();
            Assert.That(state.EffectRuntime.InteractionRequests.Count(value => value.InteractionTypeId == "enterprise.special"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(value => value.Status == "open"), Is.False);
            Assert.That(new EnterpriseStateQuery(definitions).GetLevel(state, 1, target.ItemId), Is.EqualTo(4));
        }
        [UnityTest] public IEnumerator DepartmentRequestRestoresSameIdentityAndSubmitsOnce()
        {
            PublishFourBoards();
            Run(EffectSpec.Create(LuaDomainEffectTypeIds.SwitchDepartment, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"),
                "enterpriseItemId", NormalizedValue.CreateString(Item("enterprise.rhine_lab").ItemId)), 1));
            var request = Projection();
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); state = recovered.State; RefreshView(); Route();
            Assert.That(Projection().InteractionId, Is.EqualTo(request.InteractionId));
            var dialog = EnterpriseDialog(true); ChooseAndConfirm(dialog, request.CandidateIds[0]);
            ((Button)Field(dialog, "confirm")).onClick.Invoke(); Assert.That(commands.Count, Is.EqualTo(1));
            yield return Screenshot("恢复科室选择");
            Apply(commands[0]); Route(); Assert.That(dialog.gameObject.activeInHierarchy, Is.False);
            Assert.That(new EnterpriseStateQuery(definitions).GetActiveDepartmentItemId(state, Item("enterprise.rhine_lab").ItemId), Is.EqualTo(request.CandidateIds[0]));
        }
        [UnityTest] public IEnumerator StaleEnterpriseInputCannotMoveMarkersOrSubmit()
        {
            PublishFourBoards(); Run(EffectSpec.Create(LuaDomainEffectTypeIds.SwitchDepartment, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"),
                "enterpriseItemId", NormalizedValue.CreateString(Item("enterprise.rhine_lab").ItemId)), 1)); Route();
            var old = Projection(); var dialog = EnterpriseDialog(true); Call(dialog, "Choose", old.CandidateIds[0]);
            state.EffectRuntime.StateRevision++; Open().StateRevision++; RefreshView();
            ((Button)Field(dialog, "confirm")).onClick.Invoke(); Assert.That(commands, Is.Empty);
            var result = new AnswerInteractionCommandHandler(registry).Handle(state, EffectInteractionCommands.Answer(old, 1, new[] { old.CandidateIds[0] }));
            Assert.That(result.Succeeded, Is.False); Assert.That(new EnterpriseStateQuery(definitions).GetActiveDepartmentItemId(state, Item("enterprise.rhine_lab").ItemId), Is.EqualTo(Item(departmentIds[0]).ItemId));
            yield return null;
        }
        [UnityTest] public IEnumerator PublicBoardsShowAllPlayersAndSharedDepartmentWithoutPrivateCandidates()
        {
            PublishFourBoards(); var modules = Property(hud, "MainModules"); Call(modules, "Render", state, view, viewer, Property(Property(hud, "DialogRegistry"), "CardVisualCatalog"));
            Call(adapter, "Refresh"); Call(adapter, "OpenPublicEnterprises");
            var dialog = Field(adapter, "publicBoards") as Component;
            var projected = Property(Property(dialog, "Draft"), "Projection");
            Assert.That(((Array)Field(projected, "Boards")).Length, Is.EqualTo(5));
            Assert.That((bool)Field(projected, "ReadOnly"), Is.True); Assert.That(((Button)Field(dialog, "confirm")).interactable, Is.False);
            var board = Call(adapter, "ProjectBoard", view, Item("enterprise.hyde_brothers").ItemId, "enterprise.hyde_brothers");
            Assert.That((int[])Field(board, "PlayerLevels"), Is.EqualTo(new[] { 2, 3, 4 }));
            var rhine = Call(adapter, "ProjectBoard", view, Item("enterprise.rhine_lab").ItemId, "enterprise.rhine_lab");
            Assert.That((string)Field(rhine, "Summary"), Does.Contain((string)Call(adapter, "Name", departmentIds[0])));
            yield return Screenshot("公开四板与共享科室");
        }
        [UnityTest] public IEnumerator TwoPlayerEnterpriseChoicePreservesBothCandidateIdentities()
        {
            var source = new GameState { GameId = state.GameId, MapId = StaticMapDefinitions.ThreePlayerMapId, ContentPackHash = state.ContentPackHash, ContentDefinitions = state.ContentDefinitions,
                SetupRecipe = GameStateCloneService.DeepClone(state.SetupRecipe),
                RuleConfiguration = new GameRuleConfiguration { PlayerCount = 2, MapId = StaticMapDefinitions.ThreePlayerMapId, ContentPackHash = state.ContentPackHash },
                Players = state.Players.Take(2).ToList(), Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1 };
            state = new ItemZoneStateFactory().CreateForSetup(source, StaticMapDefinitions.CreateThreePlayerMap(), new[] {
                new ItemRecipeEntry { EntryId = "enterprise_contract", DefinitionId = enterpriseIds[0], TypeId = "core.board_card", Quantity = 1, InitialZoneRole = "reserve" },
                new ItemRecipeEntry { EntryId = "markers_contract", DefinitionId = "component.marker", TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "player_markers" } });
            AddBoardItems();
            state.SetupRecipe.EnterpriseEntrance = true; state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            Run(ExpansionSetupFlow.PreparationSpec(1)); Route(); var request = Open();
            Assert.That(request.MinSelections, Is.EqualTo(2)); Assert.That(request.MaxSelections, Is.EqualTo(2));
            ChooseAndConfirm(EnterpriseDialog(), request.CandidateIds[1], request.CandidateIds[0]);
            Assert.That(commands.Single().OptionIds, Is.EqualTo(new[] { request.CandidateIds[1], request.CandidateIds[0] }));
            Apply(commands.Single());
            Assert.That(Open().AnsweringPlayerId, Is.EqualTo(2));
            Assert.That(state.ItemZones.Items.Count(item => item.ZoneId == FindZone(state, "enterprises").ZoneId), Is.Zero);
            viewer = 2; RefreshView(); Route(); var second = Open();
            ChooseAndConfirm(EnterpriseDialog(), second.CandidateIds[0], second.CandidateIds[1]); Apply(commands.Last());
            Assert.That(state.ItemZones.Items.Count(item => item.ZoneId == FindZone(state, "enterprises").ZoneId), Is.EqualTo(4));
            yield return null;
        }
        [UnityTest] public IEnumerator DispatchCandidatesStayPrivateAndOnlyHostMovesAcceptedAndReturnedCards()
        {
            AddOperators(3); Run(OperatorDispatchFlow.CreateSpec(1, 2)); Route(); var candidate = Open().CandidateIds[0];
            var incoming = state.ItemZones.Items.Single(item => item.ItemId == candidate); var originalZone = incoming.ZoneId;
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).ItemZones.Items.Any(item => item.ItemId == candidate), Is.False);
            Assert.That((bool)Call(characterRenderer, "CanRender", InteractionRequestProjector.ProjectForPlayer(Open(), 2)), Is.False);
            var projection = Projection(); Call(characterRenderer, "SubmitCandidates", projection, new[] { candidate });
            Assert.That(incoming.ZoneId, Is.EqualTo(originalZone));
            Apply(commands.Single()); Route();
            Assert.That(state.ItemZones.Items.Single(item => item.ItemId == candidate).ZoneId, Is.EqualTo(FindZone(state, "hand", 1).ZoneId));
            Assert.That(Open().InteractionTypeId, Is.EqualTo("operator.dispatch.return"));
            yield return Screenshot("特派合法换出");
            var outgoing = Open().CandidateIds.First(id => id != candidate); var old = Projection();
            var unauthorized = EffectInteractionCommands.Answer(old, 1, new[] { outgoing }); unauthorized.PlayerId = 2;
            var rejected = new AnswerInteractionCommandHandler(registry).Handle(state, unauthorized);
            Assert.That(rejected.Succeeded, Is.False);
            Call(characterRenderer, "SubmitCandidates", old, new[] { outgoing }); Apply(commands.Last()); Route();
            Assert.That(state.ItemZones.Items.Single(item => item.ItemId == outgoing).ZoneId, Is.EqualTo(FindZone(state, "retired").ZoneId));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).Events.Any(value => value.EventType == "OperatorDispatchReturnShown" && Text(value.Payload, "itemId") == outgoing), Is.True);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open"), Is.False);
        }
        [UnityTest] public IEnumerator DeclinedDispatchAwardsOnlyAfterHostAnswerAndHotseatClearsPrivatePage()
        {
            AddOperators(2); int score = state.FindPlayer(1).Score;
            Run(OperatorDispatchFlow.CreateSpec(1, 2)); Route(); var projection = Projection();
            yield return Screenshot("特派私有候选");
            viewer = 2; RefreshView(); Route();
            Assert.That((string)Field(characterRenderer, "renderedInteractionId"), Is.Empty);
            Call(characterRenderer, "SubmitDecline", projection); Assert.That(commands, Is.Empty);
            viewer = 1; RefreshView(); Route(); Call(characterRenderer, "SubmitDecline", Projection());
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score)); Apply(commands.Single()); Route();
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 1));
            Assert.That(Cards("operator_deck").Length, Is.EqualTo(2));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).ItemZones.Items.Any(item => item.TypeId == "core.operator_card"), Is.False);
        }
        [UnityTest] public IEnumerator PersistentStorageShowsRealInstancesOwnersEmptySlotsAndBothModeProgress()
        {
            var carrier = Cards("hand", 1)[0]; Place(carrier, FindZone(state, "persistent", 1).ZoneId);
            string Storage(string local, string template, ItemPhysicalKind kind)
            {
                var declaration = new CardZoneDefinition { LocalZoneId = local, ZoneTemplateId = template,
                    AcceptedItemKinds = new List<ItemPhysicalKind> { kind }, AcceptedTypeIds = kind == ItemPhysicalKind.Card ? new List<string> { "core.facility_card" } : new List<string>(), CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered,
                    EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.return_contents_before_exit", VisibilityPolicyId = ItemZoneVisibility.Public };
                Assert.That(new ItemZoneStateFactory().TryCreateCardZone(state, carrier.ItemId, declaration, out string zone, out string reason), Is.True, reason); return zone;
            }
            string cubes = Storage("stored_cubes", "core.zone.card_cube_storage", ItemPhysicalKind.Cube);
            string facilities = Storage("stored_facilities", "core.zone.card_facility_storage", ItemPhysicalKind.Card);
            Storage("empty_facilities", "core.zone.card_facility_storage", ItemPhysicalKind.Card);
            var components = new[] { Cards("player_markers", 1)[0], Cards("player_markers", 2)[0], Cards("facility_deck")[0] };
            Place(components[0], cubes); Place(components[1], cubes); Place(components[2], facilities);
            state.CharacterUses.Add(new CharacterUseRuntimeState { UseId = "use.ui_contract", CardInstanceId = carrier.ItemId, ExecutingPlayerId = 1,
                ModeOrder = new List<string> { "strategy", "tactic" }, CompletedModes = new List<string> { "strategy" },
                Modes = new List<CharacterModeContract> { new CharacterModeContract { Mode = "strategy", SkillType = "normal" },
                    new CharacterModeContract { Mode = "tactic", AbilityId = "storage.fixture.tactic", Activated = true, SkillType = "persistent", SubscriptionActive = true } } });
            adapter.GetType().GetProperty("DetailDefinitions").SetValue(adapter, new[] { new PlayerDetailDisplayDefinition {
                DefinitionId = carrier.DefinitionId, AbilityId = "storage.fixture.tactic", DisplayName = "牌载区域测试载体",
                EffectText = "持续效果：容纳真实立方体与设施，保留各物品的原所有者。", Activation = PlayerDetailActivation.Subscription,
                DisplayCategory = PlayerDetailDisplayCategory.StorageArea, StorageLocalZoneIds = new List<string> { "stored_cubes", "stored_facilities", "empty_facilities" } } });
            RefreshView(); RenderDetails();
            var entry = ((IReadOnlyList<PlayerDetailEntry>)Property(adapter, "DetailEntries")).Single();
            Assert.That(entry.SourceItemId, Is.EqualTo(carrier.ItemId)); Assert.That(entry.StorageZoneIds.Count, Is.EqualTo(3));
            var row = ((IEnumerable)Property(adapter, "Rows")).Cast<Component>().Single();
            Assert.That(((Text)Property(row, "DescriptionText")).text, Is.EqualTo(entry.EffectText));
            foreach (var component in components) Assert.That(entry.EffectText, Does.Not.Contain(component.ItemId));
            Call(adapter, "InspectDetail", entry);
            Assert.That((IReadOnlyList<string>)Property(adapter, "InspectedItemIds"), Is.EquivalentTo(components.Select(item => item.ItemId)));
            Assert.That(components[1].OwnerSubject.PlayerId, Is.EqualTo(2));
            Assert.That(hud.GetComponentsInChildren<Text>(true).Any(text => text.gameObject.activeInHierarchy && text.text == (string)Field(Property(adapter, "Copy"), "EmptySlot")), Is.True);
            Assert.That(commands, Is.Empty);
            Assert.That(view.CharacterUses.Single().ModeOrder.Count, Is.EqualTo(2));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).CharacterUses, Is.Empty);
            Assert.That(Cards("hand", 1).Any(item => item.ItemId == carrier.ItemId), Is.False);
            var modules = Property(hud, "MainModules");
            Call(modules, "ToggleLocalDetail");
            yield return Screenshot("持续区与真实牌载实例");
            var detailScroll = ((RectTransform)Call(modules, "GetLocalDetailHost", 1)).GetComponentInParent<ScrollRect>();
            Assert.That(detailScroll, Is.Not.Null); Canvas.ForceUpdateCanvases(); detailScroll.verticalNormalizedPosition = 0;
            yield return Screenshot("牌载明细滚动末端");
            viewer = 2; RefreshView(); Call(adapter, "Refresh");
            Assert.That(((IReadOnlyList<PlayerDetailEntry>)Property(adapter, "DetailEntries")).Single().DetailPlayerId, Is.EqualTo(1));
            Assert.That(view.CharacterUses, Is.Empty);
        }
        [UnityTest] public IEnumerator SupplySlotIdentitySurvivesEmptySlotRefillAndPendingPlacement()
        {
            var dialogs = Property(hud, "DialogRegistry"); var dialog = (Component)Call(dialogs, "InstantiateFacilityBuild", Property(Property(hud, "Frame"), "ContentRect"));
            var modelType = Presentation("Workflows.BuildFacilityDraftViewModel"); var constructor = modelType.GetConstructors().Single();
            var dispatchType = constructor.GetParameters()[8].ParameterType;
            var dispatch = typeof(EnterpriseGameplayUiTests).GetMethod(nameof(Ignore), BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(dispatchType.GetGenericArguments()[0]).CreateDelegate(dispatchType);
            var model = constructor.Invoke(new object[] { Enum.Parse(Presentation("BuildFacilityDraftPhase"), "Selecting"),
                new List<BuildFacilityOptionQueryResult>(), null, null, -1, "", "", new List<int>(), dispatch, true });
            void Show()
            {
                Call(dialog, "RefreshSupplyStatus", state, viewer);
                Call(dialog, "Show", model, state.Map.Facilities, viewer, Property(dialogs, "CardVisualCatalog"), Property(dialogs, "EffectDialogLayoutProfile"), "", "",
                    (Func<bool>)(() => true), (Action<string>)(_ => { }), (Action<int>)(_ => { }), (Action<string>)(_ => { }), (Action)(() => { }), (Action)(() => { }), true);
            }
            Show(); var original = ((IEnumerable)Property(dialog, "SupplyItemIds")).Cast<string>().ToArray();
            var originalSlots = ((IEnumerable)Property(dialog, "SupplySlots")).Cast<RectTransform>().ToArray();
            Assert.That(original.Length, Is.EqualTo(6)); Assert.That(original, Is.All.Not.Empty);
            var second = state.ItemZones.Items.Single(item => item.ItemId == original[1]);
            var stagingDeclaration = new CardZoneDefinition { LocalZoneId = "pending_placement", ZoneTemplateId = "core.zone.effect_staging",
                AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Card }, CapacityPolicy = "unbounded", OrderingKind = ZoneOrderingKind.Unordered,
                EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents", VisibilityPolicyId = ItemZoneVisibility.Public };
            string staging = null;
            registry.Register(new EffectRegistration("ui.contract.reserve_facility", context =>
            {
                if (context.Node.FlowStage == string.Empty)
                {
                    Assert.That(new ItemZoneStateFactory().TryCreateEffectStaging(context.State, context.Node.EffectId, stagingDeclaration, out staging, out string reason), Is.True, reason);
                    var item = context.State.ItemZones.Items.Single(value => value.ItemId == second.ItemId);
                    new ItemTransferService().RelocateItems(context, new ItemTransferRequest { ItemIds = new List<string> { item.ItemId }, ExecutingPlayerId = 1,
                        ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId }, Destination = new ZoneRef { ZoneId = staging }, InsertionKind = "unordered", OperationProfileId = "core.facility_card_transfer", RequiredCount = 1 });
                }
                else FacilitySupplyService.RefillSlot(context, FindZone(context.State, "facility_supply_1").ZoneId);
                var request = new EffectInteractionSpec { InteractionTypeId = "lua.choice", AnsweringPlayerId = 1, Visibility = "owner", PromptKey = "facility.pending_placement",
                    MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id" };
                request.CandidateIds.Add(context.Node.FlowStage == string.Empty ? "refill" : second.ItemId);
                return EffectStepResult.Continue(context.Node.FlowStage == string.Empty ? "refill_later" : "await_placement").AddInteraction(request);
            }, EffectExecutorKind.IntrinsicFlow));
            Run(EffectSpec.Create("ui.contract.reserve_facility", playerId: 1)); Show();
            Assert.That(((IEnumerable)Property(dialog, "SupplyItemIds")).Cast<string>().ElementAt(1), Is.Empty);
            Apply(EffectInteractionCommands.Answer(Projection(), 1, new[] { "refill" })); Show();
            var filled = ((IEnumerable)Property(dialog, "SupplyItemIds")).Cast<string>().ToArray();
            Assert.That(filled[1], Is.Not.Empty.And.Not.EqualTo(second.ItemId));
            Assert.That(filled, Does.Not.Contain(second.ItemId));
            foreach (int index in new[] { 0, 2, 3, 4, 5 }) Assert.That(filled[index], Is.EqualTo(original[index]));
            Assert.That(((IEnumerable)Property(dialog, "SupplySlots")).Cast<RectTransform>(), Is.EqualTo(originalSlots));
            Assert.That(state.ItemZones.Items.Single(value => value.ItemId == second.ItemId).ZoneId, Is.EqualTo(staging));
            model = constructor.Invoke(new object[] { Enum.Parse(Presentation("BuildFacilityDraftPhase"), "Placing"),
                new List<BuildFacilityOptionQueryResult>(), null, FacilityCardDatabase.Get(second.DefinitionId), 0, "", "", new List<int> { 0 }, dispatch, true });
            Show(); Assert.That(((RawImage[])Field(dialog, "cityCards"))[0].texture, Is.Not.Null);
            Assert.That(((IEnumerable)Property(dialog, "SupplyItemIds")).Cast<string>(), Is.EqualTo(filled));
            yield return Screenshot("六槽供应原位补牌"); Call(dialog, "Hide"); UnityEngine.Object.Destroy(dialog.gameObject);
        }
        [UnityTest] public IEnumerator RealSettlementOptionUsesHostIdentityAndRejectsStaleRepeatedInput()
        {
            var cards = Cards("hand", 1).Take(2).ToArray();
            foreach (var card in cards)
            {
                var definition = CharacterCardDatabase.Get(state, card.DefinitionId);
                foreach (string mode in new[] { "strategy", "tactic" })
                {
                    string ability = mode == "strategy" ? definition.StrategyAbilityId : definition.TacticAbilityId;
                    if (registry.CharacterAbilityContracts.ContainsKey(ability)) continue;
                    string subscription = "ui.contract." + ability;
                    registry.CharacterActivationSubscriptions[ability] = subscription;
                    registry.CharacterAbilityContracts[ability] = new CharacterModeContract { Mode = mode, AbilityId = ability, SkillType = mode == "tactic" ? "persistent" : "normal",
                        SettlementActionId = mode == "tactic" ? CharacterSettlementOptionQuery.DoubleUseActionId : "", TitleKey = "character.double_use.title" };
                    registry.RegisterEventHandler(new EffectEventHandlerRegistration(subscription, CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType, ability, subscription,
                        context => new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, 1) }, EffectHandlerRole.Primary));
                }
            }
            registry.Register("ui.contract.cover", context => CharacterLifecycleService.Cover(context, Text(context.Node.NormalizedArguments, "cardId"), true));
            void Cover(string id) => Run(EffectSpec.Create("ui.contract.cover", Object("cardId", NormalizedValue.CreateString(id)), 1));
            Cover(cards[0].ItemId); Run(CharacterLifecycleService.CreateUseSpec(1, cards[0].ItemId, "tactic"));
            Cover(cards[1].ItemId); Run(CharacterLifecycleService.CreateUseSpec(1, cards[1].ItemId));
            var option = view.CharacterSettlementOptions.Single(); Assert.That(option.SourceItemId, Is.EqualTo(cards[0].ItemId));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).CharacterSettlementOptions, Is.Empty);
            RenderDetails(); Route(); var row = ((IDictionary)Field(adapter, "settlementRows"))[option.OptionId];
            Assert.That(row, Is.Not.Null);
            Assert.That(((IEnumerable)Property(adapter, "Rows")).Cast<Component>().Any(value => (string)Property(value, "ItemId") == option.OptionId), Is.False);
            var button = (Button)Property(row, "StatusButton"); var hostOption = SettlementOptionView.FromOption(option);
            Call(adapter, "InvokeSettlement", hostOption); Call(adapter, "InvokeSettlement", hostOption);
            Assert.That(commands.Count, Is.EqualTo(1)); Assert.That(commands[0].Kind, Is.EqualTo(GameCommandKind.InvokeSettlementOption));
            Assert.That(commands[0].Parameters["useId"], Is.EqualTo(option.TargetUseId));
            state.EffectRuntime.StateRevision++; RefreshView(); Call(adapter, "NotifyCommandSettled");
            Call(adapter, "InvokeSettlement", hostOption); Assert.That(commands.Count, Is.EqualTo(1));
            var rejected = new InvokeSettlementOptionCommandHandler(registry).Handle(state, commands[0]); Assert.That(rejected.Succeeded, Is.False);
            Assert.That(state.ItemZones.Items.Single(item => item.ItemId == cards[0].ItemId).ZoneId, Is.EqualTo(FindZone(state, "persistent", 1).ZoneId));
            Assert.That(((RectTransform)Field(adapter, "settlementHost")).gameObject.activeSelf, Is.True);
            yield return Screenshot("真实Host结算插槽合同");
        }
        [UnityTest] public IEnumerator NestedEnterpriseDetailsReturnToSameRequestWithoutAnswering()
        {
            state.SetupRecipe.EnterpriseEntrance = true; Run(ExpansionSetupFlow.PreparationSpec(1)); Route();
            var dialog = EnterpriseDialog(); string requestId = Open().InteractionId, boardId = Open().CandidateIds[0];
            Call(dialog, "OpenDetails", boardId); Assert.That(((GameObject)Field(dialog, "detailPage")).activeSelf, Is.True);
            Call(dialog, "ReturnFromDetails"); Assert.That(((GameObject)Field(dialog, "detailPage")).activeSelf, Is.False);
            Assert.That(Open().InteractionId, Is.EqualTo(requestId)); Assert.That(commands, Is.Empty);
            yield return null;
        }
        [UnityTest] public IEnumerator EmptyDispatchClosesWithoutRewardAndMissingEffectNoticeDoesNotExposeHands()
        {
            int score = state.FindPlayer(1).Score; Run(OperatorDispatchFlow.CreateSpec(1, 2)); Route();
            Assert.That(state.EffectRuntime.InteractionRequests.Any(value => value.Status == "open"), Is.False);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score));
            state.Logs.Add(new GameLogEntry { Message = "incomplete_effect_skipped: enterprise.fixture", Visibility = GameStateVisibilityPolicy.Public });
            RefreshView(); RenderDetails();
            Assert.That(((IEnumerable)Property(adapter, "Rows")).Cast<Component>().Any(value => (string)Property(value, "ItemId") == "missing-effect"), Is.False);
            Assert.That(view.Logs.Any(log => log.Message == state.Logs.Last().Message), Is.True);
            Assert.That(view.ItemZones.Items.Any(item => item.ZoneId == FindZone(state, "hand", 2).ZoneId), Is.False);
            yield return null;
        }
        [UnityTest] public IEnumerator HandAndDiscardBindInstancesWithoutLimitingOperatorsAndGeneratedCardsToFive()
        {
            var hand = Cards("hand", 1); string definition = hand[0].DefinitionId;
            foreach (string type in new[] { "core.operator_card", "core.generated_character_card" })
            {
                var item = new ItemInstanceState { ItemId = StableIdFactory.Create("item", state.GameId, "extra_character", type), DefinitionId = definition,
                    TypeId = type, PhysicalKind = ItemPhysicalKind.Card, OwnerSubject = ItemSubjectRef.Player(1), ControllerSubject = ItemSubjectRef.Player(1) };
                state.ItemZones.Items.Add(item); Place(item, FindZone(state, "hand", 1).ZoneId);
            }
            Place(hand[1], FindZone(state, "discard", 1).ZoneId); RefreshView();
            var presenter = Activator.CreateInstance(Presentation("Workflows.CharacterCardPanelPresenter"));
            var model = Call(presenter, "BuildProjectedView", state, viewer, view);
            var projectedHand = ((IEnumerable)Property(model, "HandCards")).Cast<object>().ToArray();
            var ids = projectedHand.Select(item => (string)Property(item, "CardId")).ToArray();
            Assert.That(ids, Is.Unique); Assert.That(ids.Length, Is.EqualTo(hand.Length + 1));
            foreach (var id in ids) Assert.That(view.ItemZones.Items.Any(item => item.ItemId == id), Is.True);
            Assert.That(view.Players.Find(player => player.PlayerId == 1).RetainedBasicCharacterCount, Is.EqualTo(hand.Length));
            var panel = Property(hud, "CharacterHandPanel"); Call(panel, "Render", viewer, model);
            Assert.That(((IEnumerable)Property(panel, "OrderedHand")).Cast<object>().Select(item => (string)Property(item, "CardId")), Is.EqualTo(ids));
            Call(panel, "OpenDiscardPreview"); yield return Screenshot("实例手弃牌与扩展持有数量");
            var unauthorized = Call(presenter, "BuildProjectedView", state, 1, GameStateViewProjector.ProjectForPlayer(state, 2));
            Assert.That(((IEnumerable)Property(unauthorized, "HandCards")).Cast<object>(), Is.Empty);
            viewer = 2; RefreshView(); var otherModel = Call(presenter, "BuildProjectedView", state, viewer, view); Call(panel, "Render", viewer, otherModel);
            Assert.That((bool)Property(panel, "IsDiscardPreviewOpen"), Is.False);
        }
    }
}
