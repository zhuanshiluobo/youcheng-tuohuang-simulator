using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>本人候选显示事实的限定隔离检查；不运行UI、不复制Content或图片，不作为正常模式验收。</summary>
    public sealed class EntrepreneurCandidateProjectionTests
    {
        private ExternalContentPack pack;
        private readonly Dictionary<int, ActiveContentCatalog> catalogs = new Dictionary<int, ActiveContentCatalog>();
        private IsolatedTestArtifacts artifacts;
        private GameState state;
        private EffectRegistry registry;
        private RoundExecutionService rounds;

        [OneTimeSetUp] public void LoadCurrentInstalledContentOnce()
        {
            pack = ExternalContentPack.Current;
            Assert.That(pack.ContentHash, Is.EqualTo("882c2e2e38ab21218b2059108a70f8c43e7f4de242048616e9dcb794ad4e3d7e"), "本轮应使用36指定的整合内容");
        }
        [SetUp] public void StartCase()
        {
            artifacts = IsolatedTestArtifacts.BeginCase();
            artifacts.WriteLabeledText("当前输入身份", JsonConvert.SerializeObject(new { pack.RootPath, pack.ContentHash,
                scope = "安装内容直接加载一次；内存组合隔离；候选由实际Host准备树抽取，未手填映射", contentCopied = false, imagesCopied = false,
                host = HostSnapshotDto.CurrentSchemaVersion, journal = HostJournalDto.CurrentSchemaVersion, runtime = EffectRuntimeState.CurrentSchemaVersion }));
        }
        [TearDown] public void RecordCase() => artifacts?.Finish();

        private void StartHostPreparation(int players)
        {
            var config = new GameRuleConfiguration { PlayerCount = players, MapId = players == 4 ? StaticMapDefinitions.FourPlayerMapId : StaticMapDefinitions.ThreePlayerMapId,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            if (!catalogs.TryGetValue(players, out var catalog)) catalogs.Add(players, catalog = ActiveContentCatalogBuilder.Build(pack, config));
            var recipe = catalog.SetupRecipe; var definitions = catalog.ContentDefinitions;
            var installed = pack.Definitions;
            definitions.Entrepreneurs = EntrepreneurContentCatalog.Read(installed);
            Assert.That(definitions.Entrepreneurs.All(d => d.SetupHandlerRefs.Count == 0), Is.True, "不能跳过新增必做设置正文");
            recipe.Entries.AddRange(pack.SetupRecipes.Single(r => r.RecipeId == "entrepreneur").Entries);
            recipe.CardZoneDeclarations.AddRange(installed.Where(d => d.ContentType == "entrepreneur_ability" && d.CardZones.Count > 0)
                .Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = d.CardZones }));
            definitions.Components.AddRange(installed.Where(d => d.ContentType == "component" && !definitions.Components.Any(c => c.DefinitionId == d.DefinitionId))
                .Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = d.CubeAppearance }));
            definitions.Facilities.AddRange(installed.Where(d => d.ContentType == "facility" && d.ApplicableModes.Contains("entrepreneur"))
                .Select(d => { var value = d.Data.ToObject<FacilityCardDefinition>(); value.FacilityId = d.DefinitionId; return value; }));
            registry = new EffectRegistry(); catalog.Register(registry);
            registry.ContentPackHash = config.ContentPackHash = pack.ContentHash;
            registry.ContentDefinitions = GameStateCloneService.DeepClone(definitions); registry.SetupRecipe = GameStateCloneService.DeepClone(recipe);
            var seats = Enumerable.Range(1, players).Select(p => new PlayerSeat { PlayerId = p, PlayerName = "隔离玩家" + p }).ToList();
            state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Host, 1, seats, config, 2210, recipe, definitions,
                seats.Select(p => p.PlayerId).ToList(), "qy004.own-candidates.r1." + players, null);
            // G0不变。仅在内存隔离中进入原企业家准备子树；不启用正式房间模式、不代做卡面正文。
            var formalMode = config.Clone(); formalMode.RuleModeId = "entrepreneur"; formalMode.ContentPackHash = "";
            Assert.Throws<InvalidOperationException>(() => ActiveContentCatalogBuilder.Build(pack, formalMode));
            state.RuleConfiguration.RuleModeId = "entrepreneur";
            rounds = new RoundExecutionService(registry);
            var started = rounds.StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            Assert.That(state.EntrepreneurSetup.Stage, Is.EqualTo("preview"));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.PreviewInteraction), Is.EqualTo(players));
            artifacts.WriteLabeledText("Host实际分配", JsonConvert.SerializeObject(new { state.GameId, state.EntrepreneurSetup, state.RandomRuntime,
                preparation = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ExpansionSetupFlow.PreparationTypeId).ContinuationData }));
        }
        private GameStateView Project(GameStateViewer viewer) => new GameSession(state).ProjectFor(viewer);
        private void AssertOwn(GameStateView view, int player, int expected)
        {
            var assignment = state.EntrepreneurSetup.EnterpriseAssignments.Single(a => a.PlayerId == player);
            var cards = view.Entrepreneurs.OwnEnterpriseCandidates;
            Assert.That(cards.Count, Is.EqualTo(expected));
            Assert.That(cards.Select(c => c.ItemId), Is.EqualTo(assignment.CandidateItemIds));
            Assert.That(view.Entrepreneurs.OwnEnterpriseCandidateItemIds, Is.EqualTo(assignment.CandidateItemIds));
            Assert.That(cards.Select(c => c.DefinitionId), Is.EqualTo(assignment.CandidateItemIds.Select(id => state.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId)));
            foreach (var card in cards)
            {
                Assert.That(state.ContentDefinitions.EnterpriseDefinitionIds, Does.Contain(card.DefinitionId));
                Assert.That(JObject.FromObject(card).Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "ItemId", "DefinitionId" }), "只输出两项显示身份");
            }
        }
        private void Answer(InteractionRequest request, string selected)
        {
            RoundExecutionService.RefreshConcurrentInteractionRevisions(state);
            request = state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == request.GetStableInteractionId());
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId, OptionIds = new List<string> { selected } };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            var session = new GameSession(state); session.RegisterHandler(new AnswerInteractionCommandHandler(registry, rounds));
            var result = session.Submit(command); state = session.State;
            if (!result.Succeeded) artifacts.WriteLabeledText("首次命令失败状态", JsonConvert.SerializeObject(state));
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
        }
        private void Restore(string label)
        {
            artifacts.WriteLabeledText(label + "原始状态", JsonConvert.SerializeObject(state));
            var restored = artifacts.Recover(state, label, registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); state = restored.State;
        }
        [TestCase(2, 4)] [TestCase(3, 2)] [TestCase(4, 2)]
        public void HostAssignedCardsStayPrivateSerializableAndUnchangedThroughContinueAndRecovery(int players, int count)
        {
            StartHostPreparation(players);
            string before = JsonConvert.SerializeObject(state), random = JsonConvert.SerializeObject(state.RandomRuntime);
            var original = state.EntrepreneurSetup.EnterpriseAssignments.ToDictionary(a => a.PlayerId, a => a.CandidateItemIds.ToArray());
            var allAssigned = original.Values.SelectMany(ids => ids).ToHashSet();
            var unassigned = state.ItemZones.Items.Where(i => state.ContentDefinitions.EnterpriseDefinitionIds.Contains(i.DefinitionId) && !allAssigned.Contains(i.ItemId)).Select(i => i.ItemId).ToArray();
            Assert.That(unassigned.Length, Is.GreaterThan(0), "必须实际存在未分配企业作为隐私反例");
            foreach (int player in Enumerable.Range(1, players))
            {
                var view = Project(GameStateViewer.Player(player)); AssertOwn(view, player, count);
                Assert.That(view.Entrepreneurs.OwnEnterpriseCandidates.Any(c => unassigned.Contains(c.ItemId)), Is.False);
                Assert.That(view.ItemZones.Items.Any(i => allAssigned.Contains(i.ItemId) || unassigned.Contains(i.ItemId)), Is.False, "储备区身份仍隐藏");
                var transported = JsonConvert.DeserializeObject<GameStateView>(JsonConvert.SerializeObject(view)); AssertOwn(transported, player, count);
                var cloned = GameStateCloneService.DeepClone(view); AssertOwn(cloned, player, count);
                var previousShape = JObject.FromObject(view); ((JObject)previousShape["Entrepreneurs"]).Remove("OwnEnterpriseCandidates");
                Assert.That(previousShape.ToObject<GameStateView>().Entrepreneurs.OwnEnterpriseCandidates, Is.Empty, "旧显示消息缺字段时仍为非null空列表");
            }
            foreach (var viewer in new[] { GameStateViewer.Host, GameStateViewer.Spectator, GameStateViewer.Unknown, GameStateViewer.Player(999), new GameStateViewer { Role = GameStateViewerRole.Host, PlayerId = 1 } })
            {
                var view = Project(viewer);
                Assert.That(view.Entrepreneurs.OwnEnterpriseCandidates, Is.Empty); Assert.That(view.Entrepreneurs.OwnEnterpriseCandidateItemIds, Is.Empty);
            }
            var first = Project(GameStateViewer.Player(1)); var next = Project(GameStateViewer.Player(2));
            first.Entrepreneurs.OwnEnterpriseCandidates[0].DefinitionId = "isolated.view-only-change";
            first.Entrepreneurs.OwnEnterpriseCandidateItemIds[0] = "isolated.view-only-change";
            AssertOwn(Project(GameStateViewer.Player(1)), 1, count); AssertOwn(next, 2, count);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before), "所有查看、传输和修改投影都不推进权威状态");
            Restore("继续前恢复");
            AssertOwn(Project(GameStateViewer.Player(1)), 1, count);
            foreach (var preview in state.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.PreviewInteraction).ToArray())
                Answer(preview, "continue");
            Assert.That(state.EntrepreneurSetup.Stage, Is.EqualTo("choosing"));
            Assert.That(state.EntrepreneurSetup.Players, Is.Empty, "continue不提前选择企业家或企业");
            Restore("只读继续后恢复");
            while (state.EntrepreneurSetup.Stage != "complete")
            {
                var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == EntrepreneurFlow.ChooseInteraction);
                Answer(request, request.CandidateIds.First());
            }
            Restore("原初始企业选择恢复");
            foreach (int player in Enumerable.Range(1, players))
            {
                AssertOwn(Project(GameStateViewer.Player(player)), player, count);
                var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction && r.AnsweringPlayerId == player);
                Assert.That(request.CandidateIds, Is.EquivalentTo(original[player]));
                Assert.That(request.MinSelections, Is.EqualTo(players == 2 ? 2 : 1));
                Assert.That(state.EntrepreneurSetup.EnterpriseAssignments.Single(a => a.PlayerId == player).CandidateItemIds, Is.EqualTo(original[player]));
            }
            Assert.That(JsonConvert.SerializeObject(state.RandomRuntime), Is.EqualTo(random), "continue、选人、恢复不重抽企业");
            string archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            Assert.That(JObject.Parse(archive).Descendants().OfType<JProperty>().Any(p => p.Name == "OwnEnterpriseCandidates"), Is.False, "新增列表只属显示投影，不写权威存档");
            artifacts.WriteLabeledText("最终本人投影及原候选", JsonConvert.SerializeObject(new { original, unassigned, views = Enumerable.Range(1, players).Select(p => Project(GameStateViewer.Player(p))).ToList() }));
        }
        [TestCase("missing")] [TestCase("blank-definition")]
        public void InvalidOwnInstanceIsRejectedInsteadOfInventingOrExposingOtherCandidates(string damage)
        {
            StartHostPreparation(2);
            var item = state.ItemZones.Items.Single(i => i.ItemId == state.EntrepreneurSetup.EnterpriseAssignments.Single(a => a.PlayerId == 1).CandidateItemIds[0]);
            if (damage == "missing") state.ItemZones.Items.Remove(item); else item.DefinitionId = "";
            string before = JsonConvert.SerializeObject(state);
            var error = Assert.Throws<InvalidOperationException>(() => Project(GameStateViewer.Player(1)));
            Assert.That(error.Message, Does.StartWith("entrepreneur_candidate_projection_item_invalid:"));
            AssertOwn(Project(GameStateViewer.Player(2)), 2, 4);
            Assert.That(Project(GameStateViewer.Spectator).Entrepreneurs.OwnEnterpriseCandidates, Is.Empty);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            artifacts.WriteLabeledText("损坏候选明确拒绝", JsonConvert.SerializeObject(new { damage, error.Message }));
            state.EntrepreneurSetup.EnterpriseAssignments.RemoveAll(a => a.PlayerId == 1);
            var withoutAssignment = Project(GameStateViewer.Player(1));
            Assert.That(withoutAssignment.Entrepreneurs.OwnEnterpriseCandidates, Is.Empty);
            Assert.That(withoutAssignment.Entrepreneurs.OwnEnterpriseCandidateItemIds, Is.Empty, "真实玩家没有分配时不给候选");
        }
    }
}
