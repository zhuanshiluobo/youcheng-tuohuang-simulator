using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 技术隔离：复用004的内存机制夹具（不调用其CopyContent/Begin）；实际请求/投影/答案和源资产。
    // 该夹具的立即正文为观察用处理器，不计五位Lua正文、G1或正常开局验收。
    public sealed class CommonUiIntegrationTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject hud, eventHost;
        private object adapter, modeRenderer, foundation, frame;
        private EffectRegistry registry;
        private EffectTreeExecutor engine;
        private GameState state;
        private GameStateView visible;
        private int viewer = 1;
        private readonly List<GameCommand> commands = new List<GameCommand>();
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object target, string name) => target.GetType().GetField(name, Flags)?.GetValue(target) ??
            target.GetType().GetProperty(name, Flags)?.GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethods(Flags)
            .First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args);
        private object Registry => hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
        private IInteractionRequestRenderer Renderer => (IInteractionRequestRenderer)modeRenderer;
        private InteractionRequest Request(string type) => state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open" && r.InteractionTypeId == type && r.AnsweringPlayerId == viewer);
        private InteractionRequestProjection Projection(string type) => InteractionRequestProjector.ProjectForPlayer(Request(type), viewer);
        private Button[] ActiveButtons => hud.GetComponentsInChildren<Button>().Where(b => b.gameObject.activeInHierarchy).ToArray();
        private Button Button(string name) => ActiveButtons.Single(b => b.gameObject.name == name);
        private string Marker(Button button) => (string)button.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == "PlayerAutomationId")?.GetType().GetField("Id")
            ?.GetValue(button.GetComponents<Component>().First(c => c != null && c.GetType().Name == "PlayerAutomationId"));
        private Button[] CardButtons => ActiveButtons.Where(b => Marker(b)?.StartsWith("selection.card.", StringComparison.Ordinal) == true).ToArray();
        private object EnterprisePreview => Read(modeRenderer, "enterprisePreview");
        private EnterpriseSelectionDraft PreviewDraft => (EnterpriseSelectionDraft)Read(EnterprisePreview, "Draft");
        private Button PreviewContinue => (Button)Read(EnterprisePreview, "ReadOnlyContinueButton");
        private static Action CaptureClick(Button button)
        {
            // 原页会复用按钮和UnityEvent；冻结本次的实际回调，不能把已改绑的新事件当作旧回调。
            var calls = ((IEnumerable)typeof(UnityEngine.Events.UnityEventBase).GetMethod("PrepareInvoke", Flags)
                .Invoke(button.onClick, null)).Cast<object>().ToArray();
            return () => { foreach (var call in calls) call.GetType().GetMethod("Invoke", new[] { typeof(object[]) }).Invoke(call, new object[] { Array.Empty<object>() }); };
        }

        [UnitySetUp]
        public IEnumerator PrepareSourceHud()
        {
            commands.Clear(); viewer = 1;
            var theme = UnityEditor.AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog"));
            T("UiTheme").GetMethod("Initialize").Invoke(null, new object[] { theme });
            hud = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            adapter = hud.GetComponent(T("GameplayExpansionUi"));
            if (EventSystem.current == null) eventHost = new GameObject("技术测试事件系统", typeof(EventSystem), typeof(StandaloneInputModule));
            yield return null;
        }
        private void CreateFoundation(bool diy = false, bool missingPreview = false)
        {
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            Assert.That(pack.ContentHash, Does.Match("^[0-9a-f]{64}$"), "本轮实际内容身份由运行证据冻结，不能固定为旧内容版本。");
            YC.Domain.Facilities.FacilityCardDatabase.InitializeExternal(pack.CreateFacilities());
            YC.Domain.CityStyles.CityStyleDatabase.InitializeExternal(pack.CreateCityStyles());
            YC.Domain.SpecialActions.SpecialActionDatabase.InitializeExternal(pack.CreateSpecialActions());
            YC.Domain.Cards.EventCardDatabase.InitializeExternal(pack.CreateEvents());
            YC.Domain.Cards.CharacterCardDatabase.Initialize(pack.CreateCharacters());
            frame = Read(hud.GetComponent(T("GameplayInteractionHudView")), "Frame");
            Call(frame, "ConfigureGameplayPresentation", pack.Presentation, pack.Definitions.ToDictionary(d => d.DefinitionId, d => d.DisplayName));
            TestContext.WriteLine("正式输入ContentHash=" + pack.ContentHash + "；仅内存机制隔离，不启用正式模式。");
            var type = Type.GetType("YC.Tests.EditMode.ExpansionRules.EntrepreneurModeFlowTests, YC.Tests.EditMode", true);
            foundation = Activator.CreateInstance(type); Set(foundation, "pack", pack);
            var definitions = EntrepreneurContentCatalog.Read(pack.Definitions);
            if (missingPreview) foreach (var d in definitions) d.RoundGroups.Single(g => g.RoundNumber == 3).PreviewCandidateId = "";
            Set(foundation, "official", definitions); Set(foundation, "entrepreneurRecipe", pack.SetupRecipes.Single(r => r.RecipeId == "entrepreneur"));
            var artifacts = Type.GetType("YC.Tests.EditMode.ExpansionRules.IsolatedTestArtifacts, YC.Tests.EditMode", true)
                .GetMethod("BeginCase", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, null);
            Set(foundation, "artifacts", artifacts);
            var loaded = new[] { adapter.GetType().Assembly, typeof(GameState).Assembly, typeof(AnswerInteractionCommandHandler).Assembly,
                typeof(ExternalContentPack).Assembly, typeof(IInteractionRequestRenderer).Assembly, foundation.GetType().Assembly, GetType().Assembly }
                .Distinct().Select(a => new { name = a.GetName().Name, path = a.Location,
                    sha256 = BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(File.ReadAllBytes(a.Location))).Replace("-", "").ToLowerInvariant() }).ToArray();
            Call(artifacts, "WriteLabeledText", "实际加载程序集", JsonConvert.SerializeObject(loaded, Formatting.Indented));
            state = (GameState)Call(foundation, "IsolatedState", 2, false, diy); registry = (EffectRegistry)Read(foundation, "registry");
            Assert.That(state.ContentPackHash, Is.EqualTo(pack.ContentHash));
            engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(ExpansionSetupFlow.PreparationSpec(1, false)); Pump();
            ConfigureAdapter(); Refresh();
            modeRenderer = Activator.CreateInstance(T("EntrepreneurSelectionUiCoordinator"), new object[] {
                adapter, Registry, Read(frame, "ContentRect"), new Func<int>(() => viewer), new Action<GameCommand>(commands.Add), new Func<bool>(() => true) });
        }
        private void ConfigureAdapter() => Call(adapter, "Configure", new Func<GameStateView>(() => visible), new Func<int>(() => viewer),
            registry.EnterpriseDefinitions, new Dictionary<string, YC.Domain.Cards.CharacterModeContract>(), new Action<GameCommand>(commands.Add));
        private void Pump() { var result = engine.RunUntilQuiescent(); Assert.That(result.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); }
        private void Refresh()
        {
            RoundExecutionService.RefreshConcurrentInteractionRevisions(state);
            visible = GameStateViewProjector.ProjectForPlayer(state, viewer);
            Call(frame, "BindGameplaySnapshot", visible);
            Call(Read(hud.GetComponent(T("GameplayInteractionHudView")), "MainModules"), "Render", null, visible, viewer, Read(Registry, "CardVisualCatalog"));
            Call(adapter, "Refresh");
        }
        private void AnswerDirect(InteractionRequest request, string id)
        {
            RoundExecutionService.RefreshConcurrentInteractionRevisions(state);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId,
                NormalizedValue.CreateArray(new[] { NormalizedValue.CreateString(id) }), out var reason), Is.True, reason); Pump(); Refresh();
        }
        private void ApplyUi()
        {
            var command = commands.Single();
            var result = new AnswerInteractionCommandHandler(registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason); Pump();
            Call(modeRenderer, "NotifyCommandSettled", command.CommandId); commands.Clear(); Refresh();
        }
        private void CompletePeople(string preferred = "entrepreneur.ability.M13")
        {
            foreach (var preview in state.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.PreviewInteraction).ToArray()) AnswerDirect(preview, "continue");
            while (state.EntrepreneurSetup.Stage != "complete")
            {
                var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.ChooseInteraction);
                AnswerDirect(request, state.EntrepreneurSetup.Players.Count == 0 ? preferred : request.CandidateIds.First());
            }
        }

        [UnityTest]
        public IEnumerator MissingAssignedCardProjectionKeepsOriginalPreviewRequestOpen()
        {
            CreateFoundation(); var request = Projection(EntrepreneurFlow.PreviewInteraction);
            var ids = visible.Entrepreneurs.OwnEnterpriseCandidateItemIds.ToArray();
            Assert.That(ids.Length, Is.EqualTo(4)); Assert.That(visible.ItemZones.Items.Any(i => ids.Contains(i.ItemId)), Is.False);
            Assert.That(visible.Entrepreneurs.OwnEnterpriseCandidates.Select(c => c.ItemId), Is.EqualTo(ids));
            var hostBefore = JsonConvert.SerializeObject(state);
            Renderer.Render(request); yield return null;
            var staleContinue = CaptureClick(PreviewContinue);
            // 004已修复生产投影。以下只主动破坏显示DTO，不再把正常缺项当作前提，也不改Host。
            foreach (var scenario in new[] { "missing", "partial", "reordered", "duplicate", "wrong-id", "empty-definition", "unknown-definition", "wrong-content-type" })
            {
                visible = GameStateViewProjector.ProjectForPlayer(state, viewer);
                var cards = visible.Entrepreneurs.OwnEnterpriseCandidates;
                switch (scenario)
                {
                    case "missing": visible.Entrepreneurs.OwnEnterpriseCandidates = null; break;
                    case "partial": cards.RemoveAt(cards.Count - 1); break;
                    case "reordered": cards.Reverse(); break;
                    case "duplicate": cards[1].ItemId = cards[0].ItemId; break;
                    case "wrong-id": cards[0].ItemId += ".invalid"; break;
                    case "empty-definition": cards[0].DefinitionId = ""; break;
                    case "unknown-definition": cards[0].DefinitionId = "qy017.invalid.definition"; break;
                    case "wrong-content-type": cards[0].DefinitionId = "entrepreneur.ability.M13"; break;
                }
                staleContinue(); Assert.That(commands, Is.Empty, scenario + "旧回调不能提交");
                Renderer.Render(Projection(EntrepreneurFlow.PreviewInteraction)); yield return null;
                Assert.That(CardButtons, Is.Empty, scenario + "不能用部分预览充数");
                Assert.That(PreviewDraft.Projection.Boards, Is.Empty, scenario);
                Assert.That(PreviewContinue.IsInteractable(), Is.False, scenario);
                Assert.That(commands, Is.Empty); Assert.That(Request(EntrepreneurFlow.PreviewInteraction).Status, Is.EqualTo("open"));
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(hostBefore), scenario + "只破坏显示输入，不改变Host");
                TestContext.WriteLine("显示负例=" + scenario + "；原请求保持open且Host未变");
            }
            CommonUiTestCapture.Save(hud, "主动缺失无效显示输入保留请求");
            foreach (var role in new[] { GameStateViewer.Host, GameStateViewer.Spectator, GameStateViewer.Unknown, GameStateViewer.Player(999) })
            {
                visible = GameStateViewProjector.Project(state, role);
                Assert.That(Renderer.CanRender(request), Is.False); Renderer.Render(request); yield return null;
                Assert.That(CardButtons, Is.Empty); Assert.That(commands, Is.Empty);
            }
            Refresh(); Renderer.Render(Projection(EntrepreneurFlow.PreviewInteraction)); yield return null;
            Assert.That(PreviewDraft.Projection.Boards.Select(b => b.Id), Is.EqualTo(ids));
            Assert.That(PreviewContinue.IsInteractable(), Is.True); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(hostBefore));
        }

        [UnityTest]
        public IEnumerator PreviewIsReadOnlyHotseatClearsPrivateCardsAndRealContinueKeepsAssignments()
        {
            CreateFoundation(); var assigned = visible.Entrepreneurs.OwnEnterpriseCandidateItemIds.ToArray();
            var assignments = state.EntrepreneurSetup.EnterpriseAssignments.ToDictionary(a => a.PlayerId, a => a.CandidateItemIds.ToArray());
            var hostBefore = JsonConvert.SerializeObject(state);
            var randomBefore = JsonConvert.SerializeObject(state.RandomRuntime);
            var first = Projection(EntrepreneurFlow.PreviewInteraction); Assert.That(Renderer.CanRender(first), Is.True);
            Renderer.Render(first); yield return null;
            TestContext.WriteLine("候选投影=" + string.Join("|", visible.Entrepreneurs.OwnEnterpriseCandidates.Select(c => c.ItemId + ":" + c.DefinitionId)));
            Assert.That(visible.ItemZones.Items.Any(i => assigned.Contains(i.ItemId)), Is.False, "隐藏储备仍不开放");
            Assert.That(CardButtons, Is.Empty, "U02不能再用卡牌选择页");
            Assert.That(PreviewDraft.Projection.Boards.Select(b => b.Id), Is.EqualTo(assigned), "原企业界面保留Host原分配顺序");
            Assert.That(PreviewDraft.Projection.ReadOnly, Is.True); Assert.That(PreviewDraft.CanConfirm, Is.False);
            TestContext.WriteLine("继承004内存夹具使用安装包指纹，不把其详情活动版本不匹配计生产详情验收；正式详情由新程序正常U02单独检查。");
            CommonUiTestCapture.Save(hud, "本人原分配全量预览");
            var staleContinue = CaptureClick(PreviewContinue);
            viewer = 2; Refresh(); Renderer.Render(Projection(EntrepreneurFlow.PreviewInteraction)); yield return null;
            staleContinue(); Assert.That(commands, Is.Empty);
            Assert.That(Renderer.CanRender(first), Is.False);
            Assert.That(PreviewDraft.Projection.Boards.Select(b => b.Id).Intersect(assigned), Is.Empty);
            Assert.That(PreviewDraft.Projection.Boards.Select(b => b.Id), Is.EqualTo(assignments[2]));
            Call(EnterprisePreview, "Choose", assignments[2].First());
            yield return null; Assert.That(commands, Is.Empty);
            Call(EnterprisePreview, "OpenDetails", assignments[2].First()); yield return null;
            Call(EnterprisePreview, "ReturnFromDetails"); yield return null;
            Renderer.Render(Projection(EntrepreneurFlow.PreviewInteraction)); yield return null;
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(hostBefore), "只读、返回和热座不推进Host");
            PreviewContinue.onClick.Invoke(); Assert.That(commands.Count, Is.EqualTo(1)); ApplyUi();
            Assert.That(state.EntrepreneurSetup.Players, Is.Empty);
            viewer = 1; Refresh(); Renderer.Render(Projection(EntrepreneurFlow.PreviewInteraction)); yield return null;
            PreviewContinue.onClick.Invoke(); ApplyUi();
            Assert.That(state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.ChooseInteraction).AnsweringPlayerId, Is.EqualTo(2));
            Assert.That(state.EntrepreneurSetup.EnterpriseAssignments.Single(a => a.PlayerId == 1).CandidateItemIds, Is.EqualTo(assigned));
            var order = new List<int>();
            while (state.EntrepreneurSetup.Stage != "complete")
            {
                var choice = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.ChooseInteraction);
                viewer = choice.AnsweringPlayerId; order.Add(viewer); Refresh(); Renderer.Render(Projection(EntrepreneurFlow.ChooseInteraction)); yield return null;
                var beforeInspect = JsonConvert.SerializeObject(state);
                Button("Inspect Assigned Enterprises").onClick.Invoke(); yield return null;
                Assert.That(PreviewDraft.Projection.Boards.Select(b => b.Id), Is.EqualTo(assignments[viewer]));
                Assert.That(commands, Is.Empty); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(beforeInspect));
                PreviewContinue.onClick.Invoke(); yield return null;
                CardButtons.First().onClick.Invoke(); yield return null;
                Button("Confirm Selection").onClick.Invoke(); ApplyUi();
            }
            Assert.That(order, Is.EqualTo(new[] { 2, 1 }));
            foreach (var assignment in assignments)
            {
                Assert.That(state.EntrepreneurSetup.EnterpriseAssignments.Single(a => a.PlayerId == assignment.Key).CandidateItemIds, Is.EqualTo(assignment.Value));
                var enterpriseChoice = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" &&
                    r.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction && r.AnsweringPlayerId == assignment.Key);
                Assert.That(enterpriseChoice.CandidateIds, Is.EquivalentTo(assignment.Value));
            }
            Assert.That(JsonConvert.SerializeObject(state.RandomRuntime), Is.EqualTo(randomBefore), "只读/continue/选人未重抽企业候选");
            TestContext.WriteLine("UI逆序选人=2,1；原企业候选逐项保留并进入正式企业选择；GameId=" + state.GameId);
        }

        [UnityTest]
        public IEnumerator AbilityOnlyChoiceDeckReturnAndFiveSlotsUseRealProjectionWithoutActivation()
        {
            CreateFoundation();
            foreach (var preview in state.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.PreviewInteraction).ToArray()) AnswerDirect(preview, "continue");
            viewer = 2; Refresh(); Renderer.Render(Projection(EntrepreneurFlow.ChooseInteraction)); yield return null;
            var ids = Request(EntrepreneurFlow.ChooseInteraction).CandidateIds;
            Assert.That(CardButtons.Select(Marker), Is.EquivalentTo(ids.Select(id => "selection.card." + id)));
            var card = CardButtons.Single(b => Marker(b) == "selection.card.entrepreneur.ability.M13");
            card.onClick.Invoke(); yield return null;
            card = CardButtons.Single(b => Marker(b) == "selection.card.entrepreneur.ability.M13");
            var count = state.ItemZones.Items.Count;
            ((IPointerClickHandler)card).OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Right });
            yield return null;
            Assert.That(CardButtons.Length, Is.EqualTo(9)); Assert.That(commands, Is.Empty); Assert.That(state.ItemZones.Items.Count, Is.EqualTo(count));
            CommonUiTestCapture.Save(hud, "完整牌组九张");
            Button("Cancel Selection").onClick.Invoke(); yield return null;
            Assert.That((string)Read(modeRenderer, "selected"), Is.EqualTo("entrepreneur.ability.M13"));
            Button("Confirm Selection").onClick.Invoke(); ApplyUi();
            var own = visible.Entrepreneurs.Players.Single(p => p.PlayerId == viewer);
            Assert.That(own.DefinitionId, Is.EqualTo("entrepreneur.ability.M13"));
            var slots = (RectTransform[])Read(adapter, "entrepreneurSlotRoots"); Assert.That(slots.Length, Is.EqualTo(5));
            Assert.That(own.RoundSlots.All(s => s.DisplayState == "unselected_preview" && s.SelectedCandidateId == ""), Is.True);
            foreach (var root in slots)
            {
                var slot = root.GetComponent(T("GameplayCardSlotView")); Assert.That(((RawImage)Read(slot, "Artwork")).texture, Is.Not.Null);
            }
            Assert.That(state.EntrepreneurSetup.Players.Single(p => p.PlayerId == viewer).RoundGroups.All(g => g.SelectedCandidateId == ""), Is.True);
            yield return null; CommonUiTestCapture.Save(hud, "企业家原五槽预览");
        }

        [UnityTest]
        public IEnumerator RealRoundRequestKeepsAllDiyCandidatesAndMissingPreviewDoesNotChooseFirst()
        {
            CreateFoundation(true, true); CompletePeople(); viewer = 2; Refresh();
            var own = visible.Entrepreneurs.Players.Single(p => p.PlayerId == viewer);
            var slot = own.RoundSlots.Single(s => s.RoundNumber == 3);
            Assert.That(slot.DisplayCandidateId, Is.Empty); Assert.That(slot.Candidates.Count, Is.EqualTo(3));
            var roots = (RectTransform[])Read(adapter, "entrepreneurSlotRoots");
            Assert.That(((RawImage)Read(roots[2].GetComponent(T("GameplayCardSlotView")), "Artwork")).enabled, Is.False);
            roots[2].GetComponentInChildren<Button>().onClick.Invoke(); yield return null;
            Assert.That(CardButtons.Length, Is.EqualTo(3), "未选回合槽必须保留投影声明的全部DIY候选");
            Button("Cancel Selection").onClick.Invoke(); yield return null;
            engine = (EffectTreeExecutor)Call(foundation, "RoundChoice", state, 3); state = engine.State;
            viewer = state.EffectRuntime.InteractionRequests.First(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.FaceInteraction).AnsweringPlayerId;
            Refresh(); Renderer.Render(Projection(EntrepreneurFlow.FaceInteraction)); yield return null;
            Assert.That(CardButtons.Length, Is.EqualTo(3)); Assert.That(commands, Is.Empty);
            var actualB = visible.Entrepreneurs.Players.Single(p => p.PlayerId == viewer).RoundSlots.Single(s => s.RoundNumber == 3)
                .Candidates.Single(c => c.DisplayOrder == 1);
            CardButtons.Single(b => Marker(b) == "selection.card." + actualB.CandidateId).onClick.Invoke(); yield return null;
            Button("Confirm Selection").onClick.Invoke(); ApplyUi();
            var group = state.EntrepreneurSetup.Players.Single(p => p.PlayerId == viewer).RoundGroups.Single(g => g.RoundNumber == 3);
            Assert.That(group.SelectedCandidateId, Is.Not.Empty); Assert.That(group.ActiveFaceId, Is.Not.Empty);
            Assert.That(group.ImmediateEntries.All(e => e.Status == "completed"), Is.True);
            var actual = visible.Entrepreneurs.Players.Single(p => p.PlayerId == viewer).RoundSlots.Single(s => s.RoundNumber == 3);
            var selected = actual.Candidates.Single(c => c.CandidateId == actual.SelectedCandidateId);
            TestContext.WriteLine("正式所选面=" + selected.FaceId + "；投影显示=" + actual.DisplayFaceId + "；图片=" + selected.Artwork);
            Assert.That(((RawImage)Read(roots[2].GetComponent(T("GameplayCardSlotView")), "Artwork")).texture.name, Is.EqualTo(selected.Artwork));
            Call(adapter, "OpenSourceCard", group.Candidates.Single(c => c.CandidateId == group.SelectedCandidateId).EntityItemId, visible);
            var detail = Read(adapter, "cardViewer");
            Assert.That(((RawImage)Read(detail, "cardImage")).texture.name, Is.EqualTo(selected.Artwork));
            Call(detail, "Close"); Assert.That(commands, Is.Empty);
            yield return null;
        }

        [UnityTest]
        public IEnumerator DeclaredAuxiliaryFacilitiesUseOriginalFullSpritesThroughExistingCatalog()
        {
            CreateFoundation(); CompletePeople(); viewer = 2; Refresh();
            var own = visible.Entrepreneurs.Players.Single(p => p.PlayerId == viewer);
            var catalog = Read(Registry, "CardVisualCatalog");
            var definitions = state.ContentDefinitions.Entrepreneurs.Single(d => d.DefinitionId == own.DefinitionId).Components
                .Where(c => c.TypeId == "core.facility_card").Select(c => c.DefinitionId).Distinct().ToArray();
            Assert.That(definitions.Length, Is.EqualTo(4));
            foreach (var definition in definitions)
            {
                var sprite = (Sprite)Call(catalog, "GetFacility", definition);
                Assert.That(sprite, Is.Not.Null, definition);
                var declared = ((ExternalContentPack)T("ExternalContentRuntime").GetProperty("Pack").GetValue(null)).Definitions.Single(d => d.DefinitionId == definition);
                if (declared.ArtworkSlice != null)
                {
                    var registered = (Sprite)T("ExternalContentRuntime").GetMethod("GetRegisteredArtworkSprite").Invoke(null, new object[] { declared.ArtworkSpriteId });
                    Assert.That(sprite, Is.SameAs(registered), "追加设施必须复用声明的完整Sprite");
                }
                else Assert.That(sprite.name, Is.EqualTo(declared.Artwork));
            }
            Assert.That(commands, Is.Empty); yield return null;
        }

        [UnityTest]
        public IEnumerator ActualGangPlacementFeedsFixedColorToOriginalMapCubeRenderer()
        {
            CreateFoundation(); CompletePeople("entrepreneur.ability.P09"); viewer = 2; Refresh();
            var map = YC.Domain.Maps.StaticMapDefinitions.CreateThreePlayerMap();
            var query = new YC.Domain.Maps.MapQueryService(map);
            InfluenceEffectExecutor.Register(registry, state, query);
            var subject = new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = viewer };
            var slot = registry.InfluenceService.GetLegalPlacementSlotIds(state, subject, "entrepreneur.gang_marker").First();
            var id = engine.CreateRoot(InfluenceEffectSpecFactory.PlaceInfluence(viewer, slot, itemTypeId: "entrepreneur.gang_marker")); Pump();
            Assert.That(engine.GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed)); Refresh();
            var placed = state.Map.Influences.Single(i => i.SlotId == slot);
            var expected = (Color)T("CubeColorResolver").GetMethod("Resolve", new[] { typeof(GameStateView), typeof(string), typeof(float) })
                .Invoke(null, new object[] { visible, placed.InfluenceId, 1f });
            var mapAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Map/MapView.prefab").GetComponent(T("MapView"));
            var presenter = Activator.CreateInstance(T("MapViewPresenter"), new object[] { null, mapAsset, query, registry.InfluenceService });
            var pieceObject = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Map/Pieces/InfluencePiece.prefab"));
            var slotObject = new GameObject("隔离地图槽", typeof(SpriteRenderer));
            try
            {
                Call(presenter, "RefreshInfluenceSlotRenderer", state, slotObject.GetComponent<SpriteRenderer>(), pieceObject.GetComponent(T("MapPieceVisual")), slot, placed, -1);
                var block = new MaterialPropertyBlock();
                foreach (var mesh in pieceObject.GetComponentsInChildren<Renderer>(true))
                {
                    mesh.GetPropertyBlock(block); var actual = block.GetColor(Shader.PropertyToID("_Color"));
                    Assert.That(actual.r, Is.EqualTo(expected.r).Within(.001f));
                    Assert.That(actual.g, Is.EqualTo(expected.g).Within(.001f));
                    Assert.That(actual.b, Is.EqualTo(expected.b).Within(.001f));
                    Assert.That(actual.a, Is.EqualTo(expected.a).Within(.001f)); block.Clear();
                }
                var own = visible.Entrepreneurs.Players.Single(p => p.PlayerId == viewer);
                var entry = ((IEnumerable<PlayerDetailEntry>)Read(adapter, "DetailEntries")).Single(e => e.SourceItemId == own.AbilityItemId);
                var markers = (Array)Call(adapter, "DetailMarkers", visible, entry);
                Assert.That((int)Read(markers.GetValue(0), "Count"), Is.EqualTo(3));
                Assert.That(commands, Is.Empty);
            }
            finally { UnityEngine.Object.Destroy(pieceObject); UnityEngine.Object.Destroy(slotObject); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator EmptyDeclaredCarrierShowsZeroWithoutSubscriptionAndStorageCardsStayReadOnly()
        {
            CreateFoundation(); CompletePeople("entrepreneur.ability.A01"); viewer = 2; Refresh();
            var own = visible.Entrepreneurs.Players.Single(p => p.PlayerId == viewer);
            var entry = ((IEnumerable<PlayerDetailEntry>)Read(adapter, "DetailEntries")).Single(e => e.SourceItemId == own.AbilityItemId);
            var markers = (Array)Call(adapter, "DetailMarkers", visible, entry);
            Assert.That(markers.Length, Is.EqualTo(1)); Assert.That((int)Read(markers.GetValue(0), "Count"), Is.Zero);
            Assert.That(entry.EffectTextRef, Is.Not.Null);
            var ownerMask = (int)Read(markers.GetValue(0), "Mask");
            viewer = 1; Refresh();
            markers = (Array)Call(adapter, "DetailMarkers", visible, entry);
            Assert.That((int)Read(markers.GetValue(0), "Mask"), Is.EqualTo(ownerMask), "看对手详情仍取真实所有者颜色");
            visible.Players.RemoveAll(p => p.PlayerId == entry.DetailPlayerId);
            LogAssert.Expect(LogType.Error, "玩家详情标记缺少真实所有者：" + entry.DetailPlayerId);
            markers = (Array)Call(adapter, "DetailMarkers", visible, entry);
            Assert.That(markers, Is.Empty, "异常引用不得降级成默认红色");
            yield return null;
        }

        [UnityTest]
        public IEnumerator RealBlueprintStorageOpensAllNineCardsAndReturnsWithoutBuildOrAnswer()
        {
            CreateFoundation(); CompletePeople(); viewer = 2; Refresh();
            var own = visible.Entrepreneurs.Players.Single(p => p.PlayerId == viewer);
            var entry = ((IEnumerable<PlayerDetailEntry>)Read(adapter, "DetailEntries")).Single(e => e.SourceItemId == own.AbilityItemId);
            Assert.That(entry.EntryKind, Is.EqualTo(PlayerDetailEntryKind.StorageArea));
            var expected = visible.ItemZones.Items.Where(i => entry.StorageZoneIds.Contains(i.ZoneId)).Select(i => i.ItemId).ToArray();
            var facilities = state.Map.Facilities.Count;
            Call(adapter, "InspectDetail", entry); yield return null;
            Assert.That(CardButtons.Length, Is.EqualTo(9));
            Assert.That(CardButtons.Select(Marker), Is.EquivalentTo(expected.Select(i => "selection.card." + i)));
            CommonUiTestCapture.Save(hud, "图纸真实九张库存只读");
            ((IPointerClickHandler)CardButtons.First()).OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left });
            yield return null;
            var viewerComponent = hud.GetComponentsInChildren(T("CardViewer"), true).Cast<Component>().Single(v => (bool)Read(v, "IsShowing"));
            Call(viewerComponent, "Close"); yield return null;
            Button("Cancel Selection").onClick.Invoke(); yield return null;
            Assert.That(commands, Is.Empty); Assert.That(state.Map.Facilities.Count, Is.EqualTo(facilities));
        }

        [UnityTest]
        public IEnumerator DeclaredSourceInventoryUsesRealOwnedCubesTypedTextAndReadOnlyReturn()
        {
            CreateFoundation(); CompletePeople("entrepreneur.ability.P09"); viewer = 2; Refresh();
            var own = visible.Entrepreneurs.Players.Single(p => p.PlayerId == viewer);
            var entry = ((IEnumerable<PlayerDetailEntry>)Read(adapter, "DetailEntries")).Single(e => e.SourceItemId == own.AbilityItemId);
            Assert.That(entry.EffectTextRef, Is.Not.Null);
            Assert.That(Call(adapter, "ResolveGameplayText", entry.EffectTextRef), Is.Not.Null);
            var markers = (Array)Call(adapter, "DetailMarkers", visible, entry);
            Assert.That(markers.Length, Is.EqualTo(1));
            Assert.That((int)Read(markers.GetValue(0), "Count"), Is.EqualTo(4));
            Assert.That((bool)Read(markers.GetValue(0), "FixedColor"), Is.True);
            var tint = (Color)Read(markers.GetValue(0), "Tint");
            Assert.That(tint.r, Is.EqualTo(160f / 255f).Within(.001f));
            Assert.That(tint.g, Is.EqualTo(32f / 255f).Within(.001f));
            Assert.That(tint.b, Is.EqualTo(240f / 255f).Within(.001f));
            var row = ((IDictionary)Read(adapter, "rows"))[entry.EntryId];
            var images = (Image[])Read(row, "markerImages");
            Assert.That(images[0].color, Is.EqualTo(tint));
            Assert.That(((Text[])Read(row, "markerCounts"))[0].text, Is.EqualTo("4"));
            Call(adapter, "InspectDetail", entry); yield return null;
            Assert.That((bool)Read(Read(adapter, "cardViewer"), "IsShowing"), Is.True);
            Assert.That(commands, Is.Empty); Call(Read(adapter, "cardViewer"), "Close");
            var cubes = visible.ItemZones.Items.Where(i => i.TypeId == "entrepreneur.gang_marker" && i.OwnerSubject.PlayerId == viewer).ToArray();
            var transfer = new ItemTransferRequest { ExecutingPlayerId = viewer, ItemIds = cubes.Select(i => i.ItemId).ToList(),
                ExpectedSource = new ItemPositionRef { ZoneId = cubes.First().ZoneId }, Destination = new ZoneRef { ZoneId = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.box_retired").ZoneId },
                RequiredCount = cubes.Length, OperationProfileId = "core.cube_storage", InsertionKind = "unordered" };
            registry.Register("qy017.isolated.empty.inventory", c => { new ItemTransferService().RelocateItems(c, transfer); return EffectStepResult.Completed(); });
            engine.CreateRoot(EffectSpec.Create("qy017.isolated.empty.inventory", playerId: viewer)); Pump(); Refresh();
            entry = ((IEnumerable<PlayerDetailEntry>)Read(adapter, "DetailEntries")).Single(e => e.SourceItemId == own.AbilityItemId);
            markers = (Array)Call(adapter, "DetailMarkers", visible, entry);
            Assert.That((int)Read(markers.GetValue(0), "Count"), Is.Zero);
            row = ((IDictionary)Read(adapter, "rows"))[entry.EntryId];
            Assert.That(((Text[])Read(row, "markerCounts"))[0].text, Is.EqualTo("0"));
            Assert.That(commands, Is.Empty);
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            Renderer?.Clear(); if (foundation != null) Call(foundation, "End");
            if (hud != null) UnityEngine.Object.Destroy(hud); if (eventHost != null) UnityEngine.Object.Destroy(eventHost);
            foundation = adapter = modeRenderer = null; yield return null;
        }
    }
}
