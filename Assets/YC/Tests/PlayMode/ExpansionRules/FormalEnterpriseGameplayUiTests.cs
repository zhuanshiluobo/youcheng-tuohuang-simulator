using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 正式定义、Lua 与实际场景的受控画面；完整对局证据另由独立 Player 进程生成。
    public sealed class FormalEnterpriseGameplayUiTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private Component controller;
        private GameSession session;
        private EffectRegistry registry;
        private GameState State => session.State;
        private static Type Presentation(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YC.Presentation." + name)).First(t => t != null);
        private static object Get(object value, string name) => value.GetType().GetField(name, Members).GetValue(value);
        private static object Call(object value, string name, params object[] args) => value.GetType().GetMethods(Members).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args);
        private static string Output => Path.GetFullPath(Environment.GetEnvironmentVariable("YC_ENTERPRISE_UI_EVIDENCE") ??
            Path.Combine(UnityEngine.Application.dataPath, "../Logs/ExpansionValidation/EnterpriseExpansionIntegration/formal-scenes"));
        private InteractionRequest Request => State.EffectRuntime.InteractionRequests.FirstOrDefault(r => r.Status == "open" && !r.IsInternalMainlineInteraction());

        [UnityTest]
        public IEnumerator FormalDefinitionsRenderSetupDispatchPersistentStorageAndSupplyInActualScene()
        {
            Directory.CreateDirectory(Output);
            var context = Presentation("GameLaunchContext").GetMethod("Ensure").Invoke(null, null);
            var configuration = new GameRuleConfiguration { PlayerCount = 2, MapId = StaticMapDefinitions.ThreePlayerMapId, EnabledExpansionIds = new List<string> { "core", "enterprise" } };
            var seats = new List<PlayerSeat> { new PlayerSeat { PlayerId = 1, PlayerName = "正式内容玩家一" }, new PlayerSeat { PlayerId = 2, PlayerName = "正式内容玩家二" } };
            Presentation("GameLaunchContext").GetMethod("Configure", new[] { typeof(LaunchMode), typeof(int), typeof(string), typeof(IList<PlayerSeat>), typeof(GameRuleConfiguration) })
                .Invoke(context, new object[] { LaunchMode.Local, 1, "", seats, configuration });
            yield return SceneManager.LoadSceneAsync("ThreePlayerScene"); yield return null;
            controller = UnityEngine.Object.FindObjectOfType(Presentation("MobileCityInteractionController")) as Component;
            Assert.That(controller, Is.Not.Null);
            session = (GameSession)Get(controller, "session");
            registry = (EffectRegistry)Get(((List<IGameCommandHandler>)Get(session, "commandHandlers")).OfType<AnswerInteractionCommandHandler>().Single(), "registry");
            var captured = new HashSet<string>();
            for (int count = 0; State.Phase != GamePhase.ActionRound1 && count < 80; count++)
            {
                var request = Request; Assert.That(request, Is.Not.Null, State.Phase.ToString());
                ShowFor(request.AnsweringPlayerId);
                if (captured.Add(request.InteractionTypeId)) yield return Capture("正式开局-" + request.InteractionTypeId);
                Answer(request);
            }
            Assert.That(State.Phase, Is.EqualTo(GamePhase.ActionRound1));
            Assert.That(captured, Does.Contain("setup.department.choose"));
            Assert.That(State.ItemZones.Items.Count(i => State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.LocalZoneId.StartsWith("facility_supply_"))), Is.EqualTo(6));
            ShowFor(1);
            var adapter = UnityEngine.Object.FindObjectOfType(Presentation("GameplayExpansionUi"));
            Call(adapter, "OpenPublicEnterprises"); yield return Capture("正式公开企业与科室"); Call(adapter, "Clear");
            Call(controller, "OnBuildActionClicked"); yield return Capture("正式六槽供应");
            Call(controller, "CancelBuildFacilityDialog");
            Call(controller, "CloseFacilitySupplyInspection");
            var facilityPage = Get(controller, "facilityBuildPage"); if (facilityPage != null) Call(facilityPage, "Hide");

            // 受控分支沿同一正式状态派生：重置调度器，保留正式配方生成的全部实例和卡区。
            State.EffectRuntime = new EffectRuntimeState { PlayerOrderSnapshot = new List<int> { 1, 2 } };
            State.Phase = GamePhase.ActionRound1; State.ActionRound = State.Round = 1; State.CurrentPlayerId = 1;
            var deck = FindZone(State, "operator_deck");
            var dorothy = State.ItemZones.Items.Single(i => i.DefinitionId == "operator.dorothy");
            Assert.That(dorothy.ZoneId, Is.EqualTo(deck.ZoneId), "受控特派需先在开局选入罗德岛，使正式配方激活干员牌堆。");
            var order = State.ItemZones.Orders.Single(o => o.ZoneId == deck.ZoneId);
            order.ItemIds.Remove(dorothy.ItemId); order.ItemIds.Insert(0, dorothy.ItemId); order.OrderRevision++;
            Run(OperatorDispatchFlow.CreateSpec(State, 1));
            Assert.That(Request.InteractionTypeId, Is.EqualTo("operator.dispatch.choose"));
            ShowFor(1); yield return Capture("正式特派私密候选");
            Assert.That(GameStateViewProjector.ProjectForPlayer(State, 2).Interactions, Is.Empty);
            var archive = session.CreateHostArchive(); string requestId = Request.GetStableInteractionId();
            Assert.That(session.RecoverHost(archive, registry).Succeeded, Is.True);
            Assert.That(Request.GetStableInteractionId(), Is.EqualTo(requestId));
            Answer(Request, dorothy.ItemId);
            ShowFor(1); yield return Capture("正式特派换出"); Answer(Request);
            Assert.That(dorothy.ItemId, Is.Not.Empty);
            dorothy = State.ItemZones.Items.Single(i => i.DefinitionId == "operator.dorothy");
            Assert.That(dorothy.ZoneId, Is.EqualTo(FindZone(State, "hand", 1).ZoneId));

            foreach (var card in State.ItemZones.Items.Where(i => i.ZoneId == FindZone(State, "covered", 1).ZoneId).ToArray())
                Run(MoveSpec(1, new[] { card.ItemId }, card.ZoneId, FindZone(State, "hand", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            State.CharacterCoverSlots.RemoveAll(s => s.PlayerId == 1);
            State.FindPlayer(1).UsedCharacterThisRound = false;
            Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(Request, dorothy.ItemId);
            Run(CharacterLifecycleService.CreateUseSpec(1, dorothy.ItemId, "tactic"));
            Assert.That(Request.CandidateIds, Does.Contain("4")); Answer(Request, "4");
            while (Request != null) Answer(Request);
            var persistent = State.ItemZones.Items.Single(i => i.ItemId == dorothy.ItemId);
            Assert.That(persistent.ZoneId, Is.EqualTo(FindZone(State, "persistent", 1).ZoneId));
            Assert.That(State.ItemZones.Items.Count(i => State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.ParentId == persistent.ItemId && z.LocalZoneId == "markers")), Is.EqualTo(4));
            ShowFor(1);
            var hud = Get(controller, "gameplayInteractionHud");
            var modules = hud.GetType().GetProperty("MainModules").GetValue(hud);
            Call(modules, "ToggleLocalDetail");
            yield return Capture("正式多萝西持续区与牌载标记");
            File.WriteAllText(Path.Combine(Output, "controlled-host-archive.json"), HostRecoveryService.SerializeArchive(session.CreateHostArchive()));
            Assert.That(new ItemZoneQuery().TryValidate(State, out string reason), Is.True, reason);
        }

        private void ShowFor(int player)
        {
            controller.GetType().GetField("localPlayerId", Members).SetValue(controller, player);
            Call(controller, "SynchronizeInteractionFromState");
        }
        private void Run(EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(State, registry); engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, State.EffectRuntime.LastFaultMessage);
        }
        private void Answer(InteractionRequest request, string chosen = null)
        {
            Assert.That(request, Is.Not.Null);
            var command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString();
            if (chosen != null) command.OptionIds.Add(chosen);
            else
            {
                var skip = request.CandidateIds.FirstOrDefault(id => id == "choice.skip" || id == "decline");
                if (skip != null && request.MinSelections <= 1) command.OptionIds.Add(skip);
                else command.OptionIds.AddRange(request.CandidateIds.OrderByDescending(id => State.ItemZones.Items.Any(i => i.ItemId == id && (i.DefinitionId == "enterprise.rhine_lab" || i.DefinitionId == "enterprise.rhodes_island"))).Take(Math.Max(1, request.MinSelections)));
            }
            var result = session.Submit(command);
            Assert.That(result.Succeeded, Is.True, request.InteractionTypeId + ":" + result.Validation?.Reason);
        }
        private IEnumerator Capture(string name)
        {
            var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(1920, 1080) });
            for (int frame = 0; frame < 8; frame++) yield return null;
            Canvas.ForceUpdateCanvases();
            string path = Path.Combine(Output, name + ".png"); ScreenCapture.CaptureScreenshot(path);
            File.WriteAllText(Path.Combine(Output, name + ".json"), JsonConvert.SerializeObject(GameStateViewProjector.ProjectForPlayer(State, (int)Get(controller, "localPlayerId")), Formatting.Indented));
            for (int frame = 0; frame < 12; frame++) yield return null;
            Assert.That(File.Exists(path), Is.True, path);
        }
    }
}
