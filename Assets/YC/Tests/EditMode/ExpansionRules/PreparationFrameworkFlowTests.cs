using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Application.Gameplay;
using YC.Domain.Effects;
using YC.Domain.Commands;
using YC.Domain.Enterprises;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Multiplayer;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>真实实体和效果树的隔离夹具；不代替从菜单进入的完整游戏验收。</summary>
    public sealed class PreparationFrameworkFlowTests
    {
        [Test]
        public void RawLegacyStateWithoutRandomRuntimeDoesNotRequirePrivateLedger()
        {
            Assert.That(PrivateCommandLedgerPolicy.HasRawFields(new JObject { ["RandomRuntime"] = JValue.CreateNull() }), Is.True);
            Assert.That(PrivateCommandLedgerPolicy.HasRawFields(new JObject()), Is.True);
            Assert.That(PrivateCommandLedgerPolicy.HasRawFields(new JObject { ["RandomRuntime"] = "invalid" }), Is.False);
            Assert.That(PrivateCommandLedgerPolicy.HasRawFields(new JObject { ["RandomRuntime"] = new JObject {
                ["AlgorithmVersion"] = GameplayRandomRuntime.PrivateAlgorithmVersion } }), Is.False);
        }
        private const string StepHandler = "fixture.preparation", Choice = "fixture.preparation.choice";
        private sealed class WindowFixtureHandler : IGameCommandHandler
        {
            public bool CanHandle(GameCommand command) => command?.Kind == GameCommandKind.PreparePaymentDraft;
            public CommandResult Handle(GameState state, GameCommand command) =>
                CommandResult.SuccessResult(new List<YC.Domain.Events.GameEvent>(), "隔离窗口夹具");
        }
        private static PreparationStepDeclaration Step(string id) => new PreparationStepDeclaration { StepId = id,
            SourcePackId = "fixture.pack", SourcePackVersion = "1.0.0", Kind = "builtin", HandlerId = StepHandler,
            HandlerVersion = "1.0.0", Execution = "once" };

        private static GameState State(out EffectRegistry registry, List<string> calls, bool requireFixed = false, bool departmentWithWaitingUpgrade = false, bool withOperators = false, bool privateRandom = false)
        {
            var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.FourPlayerMapId, PlayerCount = 3,
                EnabledExpansionIds = new List<string> { "core" } };
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            var catalog = ActiveContentCatalogBuilder.Build(pack, config);
            config.ContentPackHash = catalog.ContentPackHash;
            var recipe = catalog.SetupRecipe;
            var definitions = catalog.ContentDefinitions;
            recipe.EnterpriseEntrance = true;
            var enterprise = new PreparationEnterprisePlan { PlayerCounts = new List<PreparationEnterpriseCountRule> {
                new PreparationEnterpriseCountRule { PlayerCount = 3, Draw = 2, Keep = 1, ExtraRandom = 1, FinalCount = 4 }
            } };
            if (requireFixed) enterprise.Fixed.Add(new PreparationFixedEnterprise { DefinitionId = "fixture.company.0", OwnerOrderIndex = 0, RequireConfirmation = true });
            recipe.Preparation = new PreparationFlowPlan { ModeId = config.RuleModeId, Enterprise = enterprise,
                BeforeDistribution = new List<PreparationStepDeclaration> { Step("before") },
                BetweenDistributionAndConfirmation = new List<PreparationStepDeclaration> { Step("middle") },
                AfterConfirmation = new List<PreparationStepDeclaration> { Step("after") } };
            var boards = new EnterpriseDefinitions();
            foreach (string id in Enumerable.Range(0, 8).Select(i => "fixture.company." + i))
            {
                recipe.Add(id, id, "core.board_card", "reserve");
                definitions.EnterpriseDefinitionIds.Add(id);
                boards.Register(new EnterpriseDefinition { DefinitionId = id, RuntimeId = id, DisplayNameKey = "示例企业" + id.Substring(id.LastIndexOf('.') + 1),
                    DepartmentRewardPolicy = departmentWithWaitingUpgrade && id == "fixture.company.0" ? "active_department" : "",
                    Levels = Enumerable.Range(0, 6).Select(level => new EnterpriseLevelDefinition { Level = level,
                        Kind = level == 0 ? "empty" : "reward", AbilityId = level == 0 ? "" : id + ".reward." + level }).ToList() });
                foreach (int level in Enumerable.Range(1, 5))
                    if (departmentWithWaitingUpgrade && id == "fixture.company.0" && level == 1)
                        boards.BindAbility(id + ".reward." + level, context => new[] {
                            EffectSpec.Create(Choice, playerId: context.Event.PlayerId), ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) });
                    else
                        boards.BindAbility(id + ".reward." + level, context => new[] { ResourceEffectSpecFactory.GainScore(context.Event.PlayerId, 1) });
            }
            if (departmentWithWaitingUpgrade)
                foreach (string id in Enumerable.Range(0, 3).Select(index => "fixture.department." + index))
                {
                    recipe.Entries.Add(new SetupRecipeEntry { EntryId = id, DefinitionId = id, TypeId = "core.board_card",
                        InitialZoneRole = "departments", Quantity = 1,
                        Condition = new SetupRecipeCondition { Kind = "enterprise_selected", RuntimeId = "fixture.company.0" } });
                    boards.Register(new DepartmentDefinition { DefinitionId = id, EnterpriseRuntimeId = "fixture.company.0",
                        Rewards = Enumerable.Range(2, 4).Select(level => new DepartmentRewardDefinition { Level = level, AbilityId = id + ".reward." + level }).ToList() });
                }
            if (withOperators)
                recipe.Entries.Add(new SetupRecipeEntry { EntryId = "fixture.initial_operators", DefinitionId = definitions.Characters[0].CardId,
                    TypeId = "core.operator_card", InitialZoneRole = "operator_deck", Quantity = 4,
                    Condition = new SetupRecipeCondition { Kind = "enterprise_selected", RuntimeId = "fixture.company.0" } });
            boards.Freeze();
            var seats = Enumerable.Range(1, 3).Select(id => new PlayerSeat { PlayerId = id }).ToList();
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, seats, config, 321,
                recipe, definitions, null, "preparation-framework-fixture",
                privateRandom ? GameplayRandomRuntime.CreatePrivate("preparation-framework-fixture") : null, catalog);
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2, 3 };
            registry = new EffectRegistry { SetupRecipe = GameStateCloneService.DeepClone(recipe), ContentDefinitions = definitions, EnterpriseDefinitions = boards };
            ResourceEffectExecutor.Register(registry);
            GenericLuaEffectExecutor.Register(registry);
            ExpansionSetupFlow.Register(registry);
            PreparationFrameworkFlow.Register(registry, maximum => 0);
            registry.RegisterPreparationStep(new PreparationStepRegistration(Step("before"), (context, args) => {
                string id = Text(context.Node.NormalizedArguments, "stepId");
                calls.Add(id + ":" + context.State.PreparationRuntime.Stage);
                if (id == "before") Assert.That(context.State.PreparationRuntime.Distribution, Is.Null);
                if (id == "middle") Assert.That(context.State.PreparationRuntime.Distribution.Assignments.All(item => !item.Confirmed), Is.True);
                if (id == "after") Assert.That(context.State.PreparationRuntime.Distribution.Assignments.All(item => item.Confirmed), Is.True);
                return new List<EffectSpec> { EffectSpec.Create(Choice, playerId: context.Node.PlayerId) };
            }));
            registry.Register(Choice, context => context.GetLatestInteractionAnswer() != null
                ? EffectStepResult.Completed()
                : EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = Choice, AnsweringPlayerId = context.Node.PlayerId, Visibility = "owner", AnswerSchema = "boolean" }));
            return state;
        }

        private static void Run(EffectTreeExecutor engine)
        {
            var report = engine.RunUntilQuiescent();
            Assert.That(report.Faulted, Is.False, engine.Runtime.LastFaultMessage);
        }
        private static InteractionRequest Request(GameState state, int player, string type) => state.EffectRuntime.InteractionRequests
            .Single(item => item.Status == "open" && item.AnsweringPlayerId == player && item.InteractionTypeId == type);

        [Test]
        public void PrivateTerminalWaitsForEverySeatBeforeCaptureAndRelease()
        {
            var terminal = new PrivateTerminalAgreement(new[] { 1, 2, 3 });
            Assert.That(terminal.Begin(17), Is.True);
            Assert.That(terminal.AcknowledgePrepared(1, 16), Is.False);
            Assert.That(terminal.AcknowledgePrepared(4, 17), Is.False);
            Assert.That(terminal.AcknowledgePrepared(1, 17), Is.True);
            Assert.That(terminal.AcknowledgePrepared(2, 17), Is.True);
            Assert.That(terminal.ReadyToCapture, Is.False);
            Assert.That(terminal.MarkCaptured(17), Is.False);
            // 捕获之前掉线或有新提交，旧确认不能用于另一轮协商。
            terminal.CancelBeforeCapture();
            Assert.That(terminal.Begin(18), Is.True);
            Assert.That(terminal.AcknowledgePrepared(3, 17), Is.False);
            foreach (int player in new[] { 1, 2, 3 }) Assert.That(terminal.AcknowledgePrepared(player, 18), Is.True);
            Assert.That(terminal.ReadyToCapture, Is.True);
            Assert.That(terminal.MarkCaptured(18), Is.True);
            Assert.That(terminal.StageFor(1), Is.EqualTo(2));
            Assert.That(terminal.AcknowledgeRelease(1, 18), Is.True);
            Assert.That(terminal.StageFor(1), Is.EqualTo(3));
            Assert.That(terminal.AllReleased, Is.False);
            // 捕获后连接重建不能撤销已确认的终局检查点。
            terminal.CancelBeforeCapture();
            Assert.That(terminal.Sequence, Is.EqualTo(18));
            Assert.That(terminal.AcknowledgeRelease(2, 17), Is.False);
            Assert.That(terminal.AcknowledgeRelease(2, 18), Is.True);
            Assert.That(terminal.AcknowledgeRelease(3, 18), Is.True);
            Assert.That(terminal.AllReleased, Is.True);
        }

        [Test]
        public void PrivateNetworkFixtureChoosesARealPlacementTarget()
        {
            var state = State(out _, new List<string>());
            var influence = new YC.Domain.Influence.InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)));
            Assert.That(influence.GetLegalPlacementSlotIds(state, 1), Is.Not.Empty);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(value => value.GetName().Name == "Assembly-CSharp");
            var type = assembly.GetType("YC.Presentation.NetworkGameValidationDriver", true);
            var root = new UnityEngine.GameObject("私密联机夹具合法部署测试");
            try
            {
                var driver = root.AddComponent(type);
                var method = type.GetMethod("PrivateFixtureMainAction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var command = (GameCommand)method.Invoke(driver, new object[] { state, 1, state.EffectRuntime.StateRevision });
                Assert.That(command, Is.Not.Null);
                Assert.That(command.Kind, Is.EqualTo(GameCommandKind.DeployInfluence));
                Assert.That(command.TargetId, Is.Not.Empty);
                Assert.That(influence.CanPlace(state, 1, command.TargetId).IsValid, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void PrivateNetworkFixtureDoesNotRepeatedlyCancelCharacterMode()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(value => value.GetName().Name == "Assembly-CSharp");
            var type = assembly.GetType("YC.Presentation.NetworkGameValidationDriver", true);
            var method = type.GetMethod("Answer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var request = new InteractionView { Kind = "character.use.mode", InteractionId = "choice",
                StateRevision = 12, CandidateIds = new List<string> { "cancel", "strategy", "tactic" }, MinSelections = 1 };
            var command = (GameCommand)method.Invoke(null, new object[] { request, 3, new GameStateView() });
            Assert.That(command.OptionIds, Is.EqualTo(new[] { "strategy" }));
            Assert.That(command.PlayerId, Is.EqualTo(3));
            Assert.That(command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter], Is.EqualTo("12"));
        }

        [Test]
        public void PrivateNetworkFixtureDeclinesOptionalBuildWithoutForgingPayment()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(value => value.GetName().Name == "Assembly-CSharp");
            var type = assembly.GetType("YC.Presentation.NetworkGameValidationDriver", true);
            var method = type.GetMethod("Answer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var request = new InteractionView { Kind = "facility.build.normal", InteractionId = "build-choice",
                StateRevision = 14, CandidateIds = new List<string> { "facility" }, MinSelections = 1, AllowDecline = true };
            var command = (GameCommand)method.Invoke(null, new object[] { request, 2, new GameStateView() });
            Assert.That(command.OptionIds, Is.Empty);
            Assert.That(command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter], Is.EqualTo("false"));
            Assert.That(command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter], Is.EqualTo("14"));
        }

        [Test]
        public void PrivateNetworkFixtureTriesAnotherVisibleSingleChoiceAfterRejection()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(value => value.GetName().Name == "Assembly-CSharp");
            var type = assembly.GetType("YC.Presentation.NetworkGameValidationDriver", true);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var answer = type.GetMethod("Answer", flags);
            var key = type.GetMethod("PrivateFixtureDecisionKey", flags);
            var next = type.GetMethod("ChooseUnrejectedSingleCandidate", flags);
            var request = new InteractionView { Kind = "event_card.option", InteractionId = "event-choice", StateRevision = 15,
                CandidateIds = new List<string> { "first", "second", "third" }, MinSelections = 1, MaxSelections = 1 };
            var command = (GameCommand)answer.Invoke(null, new object[] { request, 2, new GameStateView() });
            var rejected = new HashSet<string> { (string)key.Invoke(null, new object[] { command, 15 }) };
            next.Invoke(null, new object[] { command, request, 15, rejected });
            Assert.That(command.OptionIds, Is.EqualTo(new[] { "second" }));
            rejected.Add((string)key.Invoke(null, new object[] { command, 15 }));
            next.Invoke(null, new object[] { command, request, 15, rejected });
            Assert.That(command.OptionIds, Is.EqualTo(new[] { "third" }));
            Assert.That(command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter], Is.EqualTo("15"));
        }

        [Test]
        public void PrivateNetworkFixtureUsesCurrentSettlementContract()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(value => value.GetName().Name == "Assembly-CSharp");
            var type = assembly.GetType("YC.Presentation.NetworkGameValidationDriver", true);
            var method = type.GetMethod("PrivateFixtureSettlement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var view = new GameStateView { ActiveSettlementWindowId = "window", StateRevision = 12,
                SettlementEndAction = new YC.Domain.Presentation.SettlementEndActionView { WindowId = "window", StateRevision = 11, CanCloseCleanupWindow = true } };
            Assert.That(method.Invoke(null, new object[] { view, 2 }), Is.Null, "过期结算版本不能提交");
            view.SettlementEndAction.StateRevision = 12;
            var close = (GameCommand)method.Invoke(null, new object[] { view, 2 });
            Assert.That(close.Kind, Is.EqualTo(GameCommandKind.EndAction));
            Assert.That(close.Parameters["windowId"], Is.EqualTo("window"));
            Assert.That(close.Parameters["expectedRevision"], Is.EqualTo("12"));
            view.CharacterSettlementOptions.Add(new YC.Domain.Cards.CharacterSettlementOption { WindowId = "window", VisibleToViewer = true,
                IsEnabled = true, AnsweringPlayerId = 2, StateRevision = 12, OptionId = "option", TargetUseId = "use" });
            var choose = (GameCommand)method.Invoke(null, new object[] { view, 2 });
            Assert.That(choose.Kind, Is.EqualTo(GameCommandKind.InvokeSettlementOption));
            Assert.That(choose.Parameters["optionId"], Is.EqualTo("option"));
            Assert.That(choose.Parameters["expectedRevision"], Is.EqualTo("12"));
        }

        [Test]
        public void PrivateNetworkFixtureCanChooseAnAlternativeFromLateGameProjection()
        {
            string path = Path.GetFullPath(Path.Combine("TestResults", "Preparation-framework-r1-20261114", "private-mirror-r11", "host", "saves", "auto-1.json"));
            if (!File.Exists(path)) Assert.Ignore("此环境没有后期隔离联机存档夹具。");
            var envelope = JObject.Parse(File.ReadAllText(path));
            var payload = JObject.Parse((string)envelope["Payload"]);
            var archive = JObject.Parse((string)payload["ArchiveJson"]);
            var raw = typeof(HostRecoveryService).GetMethod("ReadRawStateForPrivateCheckpoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            // 这里只复用后期局面测试私密投影，不验证旧存档恢复；在内存副本移除旧调用许可字段，绝不改源档。
            var fixture = (JObject)archive["Snapshot"]["State"].DeepClone();
            foreach (var action in fixture["ContentDefinitions"]?["AbilitySources"]?.Children<JObject>()
                .SelectMany(source => source["Actions"]?.Children<JObject>() ?? Enumerable.Empty<JObject>()) ?? Enumerable.Empty<JObject>())
                action.Remove("DirectCallPolicy");
            var saved = (GameState)raw.Invoke(null, new object[] { fixture });
            Assert.That(saved.Round, Is.EqualTo(6));
            var view = GameStateViewProjector.ProjectForPlayer(saved, 4);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(value => value.GetName().Name == "Assembly-CSharp");
            var factory = assembly.GetType("YC.Presentation.PlayerUiReadStateFactory", true);
            var create = factory.GetMethod("CreateFromPrivateView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var projected = (GameState)create.Invoke(null, new object[] { view, saved.ContentDefinitions, 4, saved.GameId, saved.ContentPackHash });
            Assert.That(projected, Is.Not.Null);
            var type = assembly.GetType("YC.Presentation.NetworkGameValidationDriver", true);
            var method = type.GetMethod("PrivateFixtureMainAction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var root = new UnityEngine.GameObject("后期主要行动夹具");
            try
            {
                var driver = root.AddComponent(type);
                var action = (GameCommand)method.Invoke(driver, new object[] { projected, 4, view.StateRevision });
                Assert.That(action, Is.Not.Null, "夹具需要从本人可见状态找到规则允许的行动");
                if (action.Kind == GameCommandKind.BuildFacility)
                {
                    var build = new YC.Domain.Facilities.BuildFacilityService();
                    string id = action.Parameters["facilityId"];
                    string mode = action.Parameters[BuildFacilityCommandHandler.PaymentModeParameter];
                    int slot = int.Parse(action.Parameters["cityBoardSlotIndex"]);
                    Assert.That(build.ValidateFacility(projected, 4, id).IsValid, Is.True);
                    Assert.That(build.ValidatePaymentMode(projected, 4, id, mode).IsValid, Is.True);
                    Assert.That(build.ValidateCityBoardSlot(projected, 4, id, slot).IsValid, Is.True);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void Answer(GameState state, EffectRegistry registry, InteractionRequest request, NormalizedValue answer)
        {
            request.StateRevision = state.EffectRuntime.StateRevision; // 孤立夹具模拟正式发布刷新。
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, answer, out string reason), Is.True, reason);
            Run(engine);
        }

        [Test]
        public void WindowsWaitForDistributionConfirmationAndCompleteInitializationBeforeAfterWindow()
        {
            var calls = new List<string>();
            var state = State(out var registry, calls);
            var engine = new EffectTreeExecutor(state, registry);
            string rootId = engine.CreateRoot(PreparationFrameworkFlow.Spec(1));
            Run(engine);
            Assert.That(calls, Is.EqualTo(new[] { "before:before" }));
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(state, registry, out var recoveryReason, false), Is.True, recoveryReason);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            Assert.That(calls.Last(), Is.EqualTo("middle:middle"));
            Assert.That(state.PreparationRuntime.Distribution.Assignments, Has.Count.EqualTo(3));
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(state, registry, out recoveryReason, false), Is.True, recoveryReason);
            var prematureOrder = GameStateCloneService.DeepClone(state);
            prematureOrder.EffectRuntime.PlayerOrderSnapshot = new List<int> { 2, 3, 1 };
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(prematureOrder, registry, out _, false), Is.False,
                "准备尚未结束时不能提前切换玩家顺序");
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("confirming"));
            Assert.That(state.PreparationRuntime.Distribution.Assignments.All(item => !item.Confirmed), Is.True);
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(state, registry, out recoveryReason, false), Is.True, recoveryReason);
            foreach (int player in new[] { 2, 1, 3 })
            {
                var request = Request(state, player, ExpansionSetupFlow.EnterpriseChoiceInteraction);
                Assert.That(request.MinSelections, Is.EqualTo(1));
                Answer(state, registry, request, NormalizedValue.CreateString(request.CandidateIds[0]));
                Assert.That(PreparationFrameworkFlow.TryValidateRuntime(state, registry, out recoveryReason, false), Is.True, recoveryReason);
            }
            Assert.That(calls, Is.EqualTo(new[] { "before:before", "middle:middle", "after:after" }));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("after"));
            string zone = FindZone(state, "enterprises").ZoneId;
            var boards = state.ItemZones.Items.Where(item => item.ZoneId == zone).ToList();
            Assert.That(boards, Has.Count.EqualTo(4));
            Assert.That(state.ItemZones.Zones.Count(item => item.TemplateId == "core.zone.board_marker_track" && boards.Any(board => board.ItemId == item.ParentId)), Is.EqualTo(12));
            Assert.That(state.EffectRuntime.EffectNodes.Count(item => item.EffectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise && item.Status == EffectNodeStatus.Completed), Is.EqualTo(3));
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("complete"));
            Assert.That(state.EffectRuntime.EffectNodes.Single(item => item.EffectId == rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(state, registry, out recoveryReason, false), Is.True, recoveryReason);
            var laterRound = GameStateCloneService.DeepClone(state);
            laterRound.EffectRuntime.PlayerOrderSnapshot = new List<int> { 2, 3, 1 };
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(laterRound, registry, out recoveryReason, false), Is.True,
                "后续回合起始玩家轮转不能改变历史准备阶段的原始分发顺序：" + recoveryReason);
            laterRound.PreparationRuntime.Distribution.PlayerOrder = new List<int> { 2, 3, 1 };
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(laterRound, registry, out _, false), Is.False,
                "不能把当前回合顺序伪装为历史企业分发顺序");
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
        }

        [TestCase("private-entrepreneur-r1", true)]
        [TestCase("private-standard-r2", false)]
        public void ActualPrivateSaveKeepsPreparationValidWhenLaterRoundOrderRotates(string fixtureName, bool entrepreneur)
        {
            string path = Path.GetFullPath(Path.Combine("TestResults", "Preparation-enable-20261117-r1", fixtureName, "host", "saves", "auto-1.json"));
            if (!File.Exists(path)) Assert.Ignore("此环境没有对应的隔离联机存档夹具。");
            var envelope = JObject.Parse(File.ReadAllText(path));
            var payload = JObject.Parse((string)envelope["Payload"]);
            var archive = JObject.Parse((string)payload["ArchiveJson"]);
            // 此处只隔离验证准备历史；完整恢复另需当时的效果注册表和内容装配。
            var readRaw = typeof(HostRecoveryService).GetMethod("ReadRawStateForPrivateCheckpoint",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var state = (GameState)readRaw.Invoke(null, new object[] { (JObject)archive["Snapshot"]["State"] });
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(state, null, out var reason), Is.True, reason);
            var initial = state.EffectRuntime.PlayerOrderSnapshot.ToList();
            Assert.That(initial.Count, Is.GreaterThan(1));
            state.EffectRuntime.PlayerOrderSnapshot = initial.Skip(1).Concat(initial.Take(1)).ToList();
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(state, null, out reason), Is.True, reason);
            if (entrepreneur)
                Assert.That(EntrepreneurRecovery.TryValidateEntrepreneurState(state, null, out reason), Is.True, reason);
            state.PreparationRuntime.Distribution.PlayerOrder = state.EffectRuntime.PlayerOrderSnapshot.ToList();
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(state, null, out _, false), Is.False,
                "历史分发顺序被改写时仍须拒绝恢复");
        }

        [Test]
        public void WrongFixedEnterpriseIsRejectedBeforeClosingTheRequest()
        {
            var state = State(out var registry, new List<string>(), true);
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(PreparationFrameworkFlow.Spec(1));
            Run(engine);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            var request = Request(state, 1, ExpansionSetupFlow.EnterpriseChoiceInteraction);
            var assignment = state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 1);
            Assert.That(Value(request.PromptParameters, "requiredEnterpriseItemIds").Items.Select(item => item.StringValue), Is.EqualTo(assignment.RequiredItemIds));
            string wrong = request.CandidateIds.Single(id => !assignment.RequiredItemIds.Contains(id));
            request.StateRevision = state.EffectRuntime.StateRevision;
            engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(wrong), out _), Is.False);
            Assert.That(Request(state, 1, ExpansionSetupFlow.EnterpriseChoiceInteraction).Status, Is.EqualTo("open"));
            Assert.That(state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 1).Confirmed, Is.False);
            Answer(state, registry, Request(state, 1, ExpansionSetupFlow.EnterpriseChoiceInteraction),
                NormalizedValue.CreateString(assignment.RequiredItemIds.Single()));
            Assert.That(state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 1).Confirmed, Is.True);
        }

        [Test]
        public void MiddleRequestsProjectOnlyTheAnsweringPlayersOwnEnterpriseCandidates()
        {
            var state = State(out var registry, new List<string>());
            state.SetupRecipe.Preparation.BetweenDistributionAndConfirmation[0].Execution = "eachIndependent";
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(PreparationFrameworkFlow.Spec(1));
            Run(engine);
            Assert.That(GameStateViewProjector.Project(state, GameStateViewer.Player(1)).Preparation, Is.Null);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("middle"));

            foreach (int player in new[] { 1, 2, 3 })
            {
                var view = GameStateViewProjector.Project(state, GameStateViewer.Player(player));
                var assignment = state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == player);
                var request = Request(state, player, Choice);
                Assert.That(view.Preparation, Is.Not.Null);
                Assert.That(view.Preparation.OwnEnterpriseCandidates.Select(item => item.ItemId), Is.EqualTo(assignment.CandidateItemIds));
                Assert.That(view.Preparation.OwnEnterpriseCandidates.Select(item => item.DefinitionId),
                    Is.EqualTo(assignment.CandidateItemIds.Select(id => state.ItemZones.Items.Single(item => item.ItemId == id).DefinitionId)));
                Assert.That(view.Preparation.Requests.Select(item => item.InteractionId), Is.EqualTo(new[] { request.GetStableInteractionId() }));
                Assert.That(view.Preparation.OwnEnterpriseCandidates.Select(item => item.ItemId).Intersect(
                    state.PreparationRuntime.Distribution.Assignments.Where(item => item.PlayerId != player)
                        .SelectMany(item => item.CandidateItemIds)), Is.Empty);
                string transmittedView = Newtonsoft.Json.JsonConvert.SerializeObject(view);
                foreach (var other in state.PreparationRuntime.Distribution.Assignments.Where(item => item.PlayerId != player)
                    .SelectMany(item => item.CandidateItemIds))
                    Assert.That(transmittedView, Does.Not.Contain(other), "其他玩家的企业实例不能出现在本人完整视图中。");
            }
            var spectator = GameStateViewProjector.Project(state, GameStateViewer.Spectator);
            Assert.That(spectator.Preparation, Is.Null);
            string spectatorView = Newtonsoft.Json.JsonConvert.SerializeObject(spectator);
            foreach (var item in state.PreparationRuntime.Distribution.Assignments.SelectMany(assignment => assignment.CandidateItemIds))
                Assert.That(spectatorView, Does.Not.Contain(item), "观众完整视图不能包含私密企业实例。");
            Assert.That(GameStateViewProjector.Project(state, GameStateViewer.Host).Preparation, Is.Null);

            var altered = GameStateCloneService.DeepClone(state);
            Request(altered, 1, Choice).OwnerEffectId = altered.PreparationRuntime.OwnerEffectId;
            Assert.That(GameStateViewProjector.Project(altered, GameStateViewer.Player(1)).Preparation, Is.Null);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            Assert.That(GameStateViewProjector.Project(state, GameStateViewer.Player(1)).Preparation, Is.Null);
            Assert.That(GameStateViewProjector.Project(state, GameStateViewer.Player(2)).Preparation, Is.Not.Null);
            Answer(state, registry, Request(state, 2, Choice), NormalizedValue.CreateBoolean(true));
            Answer(state, registry, Request(state, 3, Choice), NormalizedValue.CreateBoolean(true));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("confirming"));
            Assert.That(GameStateViewProjector.Project(state, GameStateViewer.Player(3)).Preparation, Is.Null);
        }

        [Test]
        public void NestedMiddleCardRequestKeepsOwnCandidatesAcrossRecovery()
        {
            const string handler = "fixture.nested_prepare", parentType = "fixture.nested_parent", cardType = "fixture.nested_card";
            var state = State(out var registry, new List<string>());
            var step = Step("nested");
            step.HandlerId = handler;
            state.SetupRecipe.Preparation.BetweenDistributionAndConfirmation = new List<PreparationStepDeclaration> { step };
            registry.SetupRecipe = GameStateCloneService.DeepClone(state.SetupRecipe);
            registry.RegisterPreparationStep(new PreparationStepRegistration(step, (context, args) =>
                new List<EffectSpec> { EffectSpec.Create(parentType, playerId: context.Node.PlayerId) }));
            registry.Register(parentType, context => context.Node.FlowStage == string.Empty
                ? EffectStepResult.Continue("nested").AddChild(EffectSpec.Create(cardType, playerId: context.Node.PlayerId))
                : EffectStepResult.Completed());
            registry.Register(cardType, context => {
                if (context.GetLatestInteractionAnswer() != null) return EffectStepResult.Completed();
                var choice = new EffectInteractionSpec { InteractionTypeId = cardType, AnsweringPlayerId = context.Node.PlayerId,
                    Visibility = "owner", AnswerSchema = "candidate_ids", MinSelections = 1, MaxSelections = 1 };
                choice.CandidateIds.AddRange(new[] { "fixture.card.a", "fixture.card.b" });
                return EffectStepResult.Continue("waiting").AddInteraction(choice);
            });
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            var expected = state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 1).CandidateItemIds.ToArray();
            var request = Request(state, 1, cardType);
            void AssertOwnProjection(GameState current)
            {
                var own = GameStateViewProjector.Project(current, GameStateViewer.Player(1));
                Assert.That(own.Preparation, Is.Not.Null);
                Assert.That(own.Preparation.OwnEnterpriseCandidates.Select(item => item.ItemId), Is.EqualTo(expected));
                Assert.That(own.Preparation.Requests.Single().InteractionId, Is.EqualTo(request.GetStableInteractionId()));
                Assert.That(GameStateViewProjector.Project(current, GameStateViewer.Player(2)).Preparation, Is.Null);
            }
            AssertOwnProjection(state);
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            state = restored.State;
            AssertOwnProjection(state);
            Answer(state, registry, Request(state, 1, cardType), NormalizedValue.CreateString("fixture.card.a"));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("confirming"));
            Assert.That(state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 1).CandidateItemIds, Is.EqualTo(expected));
            Assert.That(GameStateViewProjector.Project(state, GameStateViewer.Player(1)).Preparation, Is.Null);
        }

        [Test]
        public void PublicHistoryCannotExposeAnotherPlayersUnconfirmedEnterprise()
        {
            var state = State(out var registry, new List<string>());
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(PreparationFrameworkFlow.Spec(1));
            Run(engine);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            var otherItemId = state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 2).CandidateItemIds[0];
            var otherDefinition = state.ItemZones.Items.Single(item => item.ItemId == otherItemId).DefinitionId;
            var otherName = registry.EnterpriseDefinitions.FindEnterprise(otherDefinition).DisplayNameKey;
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "fixture.public-history",
                EffectTypeId = "fixture.public", PlayerId = -1, Visibility = "public", Status = EffectNodeStatus.Completed,
                NormalizedArguments = Object("item", NormalizedValue.CreateString(otherItemId)),
                NormalizedResult = Object("definition", NormalizedValue.CreateString(otherDefinition)) });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "fixture.public-display",
                EffectTypeId = "fixture.public", PlayerId = -1, Visibility = "public", Status = EffectNodeStatus.Completed,
                Presentation = new YC.Domain.Presentation.EffectPresentationDefinition { Succeeded = new YC.Domain.Presentation.GameplayTextRef("fixture.message",
                    new YC.Domain.Presentation.GameplayTextArgument { Name = "name", Kind = "text", Value = NormalizedValue.CreateString(otherName) }) } });
            state.EffectRuntime.RuleEvents.Add(new RuleEvent { EventId = "fixture.public-event", EventType = "fixture.public",
                Visibility = "public", TargetEntityId = otherName });
            state.Logs.Add(new GameLogEntry { Visibility = "public", Message = "夹具公开记录 " + otherItemId });
            state.Logs.Add(new GameLogEntry { Visibility = "public", Message = "夹具公开记录企业名称：" + otherName });

            var ownView = GameStateViewProjector.Project(state, GameStateViewer.Player(1), null, null, registry);
            var serialized = Newtonsoft.Json.JsonConvert.SerializeObject(ownView);
            Assert.That(ownView.Preparation, Is.Not.Null);
            Assert.That(serialized, Does.Not.Contain(otherItemId));
            Assert.That(serialized, Does.Not.Contain(otherDefinition));
            Assert.That(serialized, Does.Not.Contain(otherName));
            var ownerView = GameStateViewProjector.Project(state, GameStateViewer.Player(2), null, null, registry);
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(ownerView), Does.Contain(otherItemId));
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(ownerView), Does.Contain(otherName));
            var spectator = Newtonsoft.Json.JsonConvert.SerializeObject(GameStateViewProjector.Project(state, GameStateViewer.Spectator, null, null, registry));
            Assert.That(spectator, Does.Not.Contain(otherItemId));
            Assert.That(spectator, Does.Not.Contain(otherName));
            var duplicateNames = new EnterpriseDefinitions();
            foreach (string id in Enumerable.Range(0, 8).Select(index => "fixture.company." + index))
            {
                var definition = registry.EnterpriseDefinitions.FindEnterprise(id);
                definition.DisplayNameKey = "示例同名企业";
                duplicateNames.Register(definition);
            }
            duplicateNames.Freeze();
            registry.EnterpriseDefinitions = duplicateNames;
            state.Logs.Add(new GameLogEntry { Visibility = "public", Message = "本人可见的同名内容：示例同名企业" });
            var sameNameView = GameStateViewProjector.Project(state, GameStateViewer.Player(1), null, null, registry);
            Assert.That(sameNameView.Logs.Any(log => log.Message.Contains("示例同名企业")), Is.True);
        }

        [Test]
        public void UnselectedEnterpriseNameRemainsPrivateAfterConfirmation()
        {
            var state = State(out var registry, new List<string>());
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            foreach (int player in new[] { 1, 2, 3 })
            {
                var request = Request(state, player, ExpansionSetupFlow.EnterpriseChoiceInteraction);
                Answer(state, registry, request, NormalizedValue.CreateString(request.CandidateIds[0]));
            }
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("after"));
            var assignment = state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 2);
            var unselected = assignment.CandidateItemIds.Single(id => !assignment.SelectedItemIds.Contains(id));
            var definition = state.ItemZones.Items.Single(item => item.ItemId == unselected).DefinitionId;
            var name = registry.EnterpriseDefinitions.FindEnterprise(definition).DisplayNameKey;
            state.Logs.Add(new GameLogEntry { Visibility = "public", Message = "夹具记录未选择的企业：" + name });
            string View(GameStateViewer viewer) => Newtonsoft.Json.JsonConvert.SerializeObject(GameStateViewProjector.Project(state, viewer, null, null, registry));
            Assert.That(View(GameStateViewer.Player(1)), Does.Not.Contain(name));
            Assert.That(View(GameStateViewer.Spectator), Does.Not.Contain(name));
            Assert.That(View(GameStateViewer.Player(2)), Does.Contain(name));
            var session = new GameSession(state);
            Assert.Throws<InvalidOperationException>(() => session.ProjectForPlayerWithContent(1, registry));
            registry.ContentPackHash = state.ContentPackHash;
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(session.ProjectForPlayerWithContent(1, registry)), Does.Not.Contain(name));
            var incomplete = new EffectRegistry { ContentPackHash = state.ContentPackHash };
            var error = Assert.Throws<InvalidOperationException>(() => session.ProjectForPlayerWithContent(1, incomplete));
            Assert.That(error.Message, Is.EqualTo("private_view_enterprise_catalog_incomplete"));
        }

        [Test]
        public void PrivateViewChannelBindsSeatAndRejectsStaleOrForeignSnapshots()
        {
            var state = State(out var registry, new List<string>());
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            var otherItem = state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 2).CandidateItemIds[0];
            var otherDefinition = state.ItemZones.Items.Single(item => item.ItemId == otherItem).DefinitionId;
            var otherName = registry.EnterpriseDefinitions.FindEnterprise(otherDefinition).DisplayNameKey;
            state.Logs.Add(new GameLogEntry { Visibility = "public", Message = "夹具中的秘密企业：" + otherName });
            var beforeProjection = GameStateCloneService.DeepClone(state);
            registry.ContentPackHash = state.ContentPackHash;
            var session = new GameSession(state);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            Assert.That(bindings.TryBindConnection(11, 111, out _), Is.True);
            Assert.That(bindings.TryBindConnection(22, 222, out _), Is.True);
            var publisher = new PrivatePlayerViewPublisher(session, registry, bindings, 7);
            Assert.That(publisher.TryBind(11, "连接一"), Is.True);
            Assert.That(publisher.TryBind(22, "连接二"), Is.True);
            Assert.That(publisher.TryBind(33, "未认证连接"), Is.False);
            Assert.That(publisher.TryCreate(11, "旧令牌", 0, out _, out _), Is.False);
            Assert.That(publisher.TryCreate(11, "连接一", 0, out var first, out string fault), Is.True, fault);
            Assert.That(publisher.TryCreate(22, "连接二", 0, out var second, out fault), Is.True, fault);
            var ownItem = state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 1).CandidateItemIds[0];
            string serialized = Newtonsoft.Json.JsonConvert.SerializeObject(first);
            Assert.That(serialized, Does.Contain(ownItem));
            Assert.That(serialized, Does.Not.Contain(otherItem));
            Assert.That(serialized, Does.Not.Contain(otherDefinition));
            Assert.That(serialized, Does.Not.Contain(otherName));
            Assert.That(GameStateCloneService.AreEquivalent(state, beforeProjection), Is.True);
            Assert.That(typeof(PrivatePlayerViewSnapshot).GetFields().Any(field => field.FieldType == typeof(GameState)), Is.False);

            var receiver = new PrivatePlayerViewReceiver(state.GameId, state.ContentPackHash, 7, 1, "连接一",
                state.RuleConfiguration, first.View.LaunchPlayerOrder);
            Assert.That(receiver.Receive(first, false, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.NeedsResynchronization));
            Assert.That(receiver.Receive(first, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied));
            var bytes = PrivatePlayerViewWireProtocol.Encode(first);
            var assembler = new GameStateViewTransfer.Assembler(); string completed = null;
            foreach (var part in GameStateViewTransfer.Split(bytes))
                Assert.That(assembler.TryAppend(1, true, bytes.Length, part.Offset, part.ToArray(), out completed), Is.True);
            Assert.That(PrivatePlayerViewWireProtocol.TryDecode(completed, out var decoded), Is.True);
            Assert.That(receiver.Receive(decoded, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Duplicate));
            Assert.That(PrivatePlayerViewWireProtocol.TryDecode("{\"ProtocolVersion\":1," + completed.Substring(1), out _), Is.False);
            var missingOrder = JObject.Parse(completed);
            ((JObject)missingOrder["View"]).Remove("LaunchPlayerOrder");
            Assert.That(PrivatePlayerViewWireProtocol.TryDecode(missingOrder.ToString(), out _), Is.False);
            var missingCandidates = JObject.Parse(completed);
            ((JObject)missingCandidates["View"]["Preparation"]).Remove("OwnEnterpriseCandidates");
            Assert.That(PrivatePlayerViewWireProtocol.TryDecode(missingCandidates.ToString(), out _), Is.False);
            var extraState = JObject.Parse(completed); extraState["State"] = new JObject();
            Assert.That(PrivatePlayerViewWireProtocol.TryDecode(extraState.ToString(), out _), Is.False);
            Assert.That(PrivatePlayerViewWireProtocol.TryDecode(completed.Substring(0, completed.Length - 1), out _), Is.False);
            Assert.That(receiver.Receive(second, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Rejected));
            var conflict = GameStateCloneService.DeepClone(first);
            conflict.View.Logs.Add(new GameLogView { Message = "冲突快照" });
            Assert.That(receiver.Receive(conflict, false, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Rejected));
            var malformed = GameStateCloneService.DeepClone(first);
            malformed.View.Players[0].CityStyleUsedSlotIndexes = null;
            Assert.That(receiver.Receive(malformed, false, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Rejected));
            var detached = receiver.ReadView(); detached.Logs.Add(new GameLogView { Message = "本地改动" });
            Assert.That(receiver.ReadView().Logs.Any(log => log.Message == "本地改动"), Is.False);
            Assert.That(publisher.TryCreate(11, "连接一", 1, out var next, out fault), Is.True, fault);
            Assert.That(receiver.Receive(next, false, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied));
            Assert.That(publisher.TryCreate(11, "连接一", 3, out var later, out fault), Is.True, fault);
            Assert.That(receiver.Receive(later, false, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.NeedsResynchronization));
            Assert.That(receiver.Receive(later, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied));
            Assert.That(receiver.Receive(first, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Rejected));
            bindings.UnbindConnection(11);
            Assert.That(publisher.TryCreate(11, "连接一", 4, out _, out _), Is.False);
            Assert.That(bindings.TryBindConnection(111, 111, out _), Is.True);
            Assert.That(publisher.TryBind(111, "重连令牌"), Is.True);
            Assert.That(publisher.TryCreate(111, "重连令牌", 3, out var reconnected, out fault), Is.True, fault);
            Assert.That(receiver.Receive(reconnected, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Rejected));
            var resumed = new PrivatePlayerViewReceiver(state.GameId, state.ContentPackHash, 7, 1, "重连令牌",
                state.RuleConfiguration, first.View.LaunchPlayerOrder);
            Assert.That(resumed.Receive(reconnected, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied));
        }

        [Test]
        public void PrivateViewTransferBoundsInFlightBytesAndRecoversReorderedChunks()
        {
            var state = State(out var registry, new List<string>());
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            registry.ContentPackHash = state.ContentPackHash;
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            Assert.That(bindings.TryBindConnection(11, 111, out _), Is.True);
            var publisher = new PrivatePlayerViewPublisher(new GameSession(state), registry, bindings, 3);
            Assert.That(publisher.TryBind(11, "窗口令牌"), Is.True);
            Assert.That(publisher.TryCreate(11, "窗口令牌", 0, out var source, out string fault), Is.True, fault);
            source.View.Logs.Add(new GameLogView { Message = new string('测', 30000) });
            var sender = new PrivatePlayerViewSendWindow(source, 8, true);
            source.View.Logs.Clear(); // 发送字节必须与调用者后续更改隔离。
            Assert.That(sender.TotalBytes, Is.GreaterThan(PrivatePlayerViewSendWindow.MaximumInFlightBytes));
            var assembler = new PrivatePlayerViewPartAssembler(state.GameId, state.ContentPackHash, 3, 1, "窗口令牌");
            var batch = new List<PrivatePlayerViewPart>();
            while (sender.TryTake(out var part)) batch.Add(part);
            Assert.That(batch, Has.Count.EqualTo(4));
            Assert.That(sender.SentBytes - sender.AcknowledgedBytes, Is.EqualTo(PrivatePlayerViewSendWindow.MaximumInFlightBytes));
            Assert.That(assembler.Receive(batch[1], out var ack, out _, out fault), Is.EqualTo(PrivatePlayerViewPartStatus.Buffered), fault);
            Assert.That(ack.ReceivedBytes, Is.Zero);
            Assert.That(assembler.Receive(batch[0], out ack, out _, out fault), Is.EqualTo(PrivatePlayerViewPartStatus.Buffered), fault);
            Assert.That(ack.ReceivedBytes, Is.EqualTo(GameStateViewTransfer.ChunkBytes));
            var wrongAck = GameStateCloneService.DeepClone(ack); wrongAck.ConnectionToken = "错误连接";
            Assert.That(sender.TryAcknowledge(wrongAck), Is.False);
            var conflict = GameStateCloneService.DeepClone(batch[0]); conflict.Payload[0] ^= 1;
            Assert.That(assembler.Receive(conflict, out _, out _, out _), Is.EqualTo(PrivatePlayerViewPartStatus.Rejected));
            Assert.That(assembler.Receive(batch[0], out ack, out _, out fault), Is.EqualTo(PrivatePlayerViewPartStatus.Buffered), fault);
            Assert.That(sender.TryAcknowledge(ack), Is.True);
            Assert.That(sender.Unacknowledged().Count(), Is.EqualTo(3));
            for (int i = 1; i < batch.Count; i++)
            {
                Assert.That(assembler.Receive(batch[i], out ack, out _, out fault), Is.EqualTo(PrivatePlayerViewPartStatus.Buffered), fault);
                Assert.That(sender.TryAcknowledge(ack), Is.True);
            }
            PrivatePlayerViewSnapshot completed = null;
            PrivatePlayerViewPart lastPart = null;
            while (completed == null)
            {
                Assert.That(sender.TryTake(out var part), Is.True, "窗口应随确认释放容量");
                lastPart = part;
                var status = assembler.Receive(part, out ack, out completed, out fault);
                Assert.That(status, Is.EqualTo(completed == null ? PrivatePlayerViewPartStatus.Buffered : PrivatePlayerViewPartStatus.Completed), fault);
                Assert.That(sender.TryAcknowledge(ack), Is.True);
            }
            Assert.That(sender.AcknowledgedBytes, Is.EqualTo(sender.TotalBytes));
            Assert.That(sender.Unacknowledged(), Is.Empty);
            Assert.That(completed.View.Logs.Any(log => log.Message.Length == 30000), Is.True);
            Assert.That(assembler.Receive(lastPart, out ack, out _, out fault), Is.EqualTo(PrivatePlayerViewPartStatus.AlreadyCompleted), fault);
            Assert.That(ack.ReceivedBytes, Is.EqualTo(sender.TotalBytes));
            var badDuplicate = GameStateCloneService.DeepClone(lastPart); badDuplicate.Payload[0] ^= 1;
            Assert.That(assembler.Receive(badDuplicate, out _, out _, out _), Is.EqualTo(PrivatePlayerViewPartStatus.Rejected));
            var receiver = new PrivatePlayerViewReceiver(state.GameId, state.ContentPackHash, 3, 1, "窗口令牌", state.RuleConfiguration, completed.View.LaunchPlayerOrder);
            Assert.That(receiver.Receive(completed, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied));
            var damagedSender = new PrivatePlayerViewSendWindow(completed, 9, true);
            var damagedAssembler = new PrivatePlayerViewPartAssembler(state.GameId, state.ContentPackHash, 3, 1, "窗口令牌");
            bool rejectedDamage = false;
            while (damagedSender.TryTake(out var part))
            {
                if (part.Offset + part.Payload.Length == part.TotalBytes) part.Payload[0] ^= 1;
                var status = damagedAssembler.Receive(part, out ack, out var damaged, out fault);
                if (status == PrivatePlayerViewPartStatus.Rejected)
                {
                    Assert.That(damaged, Is.Null);
                    Assert.That(fault, Is.EqualTo("private_view_payload_conflict"));
                    rejectedDamage = true; break;
                }
                Assert.That(damagedSender.TryAcknowledge(ack), Is.True);
            }
            Assert.That(rejectedDamage, Is.True);
        }

        [Test]
        public void UnconfirmedEnterpriseCannotBeMovedOutOfReserve()
        {
            var state = State(out var registry, new List<string>());
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(PreparationFrameworkFlow.Spec(1));
            Run(engine);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            void RejectMovedCandidate(GameState actual)
            {
                Assert.That(PreparationFrameworkFlow.TryValidateRuntime(actual, registry, out _, false), Is.True);
                var changed = GameStateCloneService.DeepClone(actual);
                var itemId = changed.PreparationRuntime.Distribution.Assignments[0].CandidateItemIds[0];
                changed.ItemZones.Items.Single(item => item.ItemId == itemId).ZoneId = FindZone(changed, "enterprises").ZoneId;
                Assert.That(PreparationFrameworkFlow.TryValidateRuntime(changed, registry, out _, false), Is.False);
            }
            RejectMovedCandidate(state);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            RejectMovedCandidate(state);
        }

        [Test]
        public void MainlineRestoresPendingWindowsWithoutRepeatingDistributionOrSelection()
        {
            var calls = new List<string>();
            var state = State(out var registry, calls);
            var mainline = new RoundExecutionService(registry);
            var started = mainline.StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            GameState Restore(GameState current)
            {
                Assert.That(RoundExecutionService.TryValidateSetupTasks(current, registry, out string reason), Is.True, reason);
                var archive = HostRecoveryService.CreateArchive(current);
                var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry);
                Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
                return restored.State;
            }
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == PreparationFrameworkFlow.TypeId), Is.EqualTo(1));
            state = Restore(state);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            var assignments = state.PreparationRuntime.Distribution.Assignments.SelectMany(item => item.CandidateItemIds).ToArray();
            state = Restore(state);
            Assert.That(state.PreparationRuntime.Distribution.Assignments.SelectMany(item => item.CandidateItemIds), Is.EqualTo(assignments));
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            var first = Request(state, 1, ExpansionSetupFlow.EnterpriseChoiceInteraction);
            Answer(state, registry, first, NormalizedValue.CreateString(first.CandidateIds[0]));
            state = Restore(state);
            Assert.That(state.PreparationRuntime.Distribution.Assignments.Count(item => item.Confirmed), Is.EqualTo(1));
            Assert.That(state.PreparationRuntime.Distribution.Assignments.SelectMany(item => item.CandidateItemIds), Is.EqualTo(assignments));
            foreach (int player in new[] { 2, 3 })
            {
                var request = Request(state, player, ExpansionSetupFlow.EnterpriseChoiceInteraction);
                Answer(state, registry, request, NormalizedValue.CreateString(request.CandidateIds[0]));
            }
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("after"));
            state = Restore(state);
            Assert.That(calls, Is.EqualTo(new[] { "before:before", "middle:middle", "after:after" }));
            Assert.That(state.PreparationRuntime.Distribution.Assignments.SelectMany(item => item.CandidateItemIds), Is.EqualTo(assignments));
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise && node.Status == EffectNodeStatus.Completed), Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RecoveryDuringDepartmentAndInitialUpgradeDoesNotRepeatInitialization(bool withOperators)
        {
            var calls = new List<string>();
            var state = State(out var registry, calls, requireFixed: true, departmentWithWaitingUpgrade: true, withOperators: withOperators);
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            var distributed = state.PreparationRuntime.Distribution.Assignments.SelectMany(item => item.CandidateItemIds).ToArray();
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            foreach (int player in new[] { 1, 2, 3 })
            {
                var assignment = state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == player);
                var selected = player == 1 ? assignment.RequiredItemIds.Single() : assignment.CandidateItemIds[0];
                Answer(state, registry, Request(state, player, ExpansionSetupFlow.EnterpriseChoiceInteraction), NormalizedValue.CreateString(selected));
            }
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("initializing"));
            var departmentChoice = Request(state, 1, "setup.department.choose");
            var departments = departmentChoice.CandidateIds.ToArray();
            Assert.That(departments.Length, Is.EqualTo(3));
            string[] Markers(GameState current) => current.ItemZones.Items
                .Where(item => item.TypeId == "core.player_marker")
                .Select(item => item.ItemId + ":" + item.ZoneId + ":" + item.SlotId).OrderBy(value => value).ToArray();
            var markersAtDepartment = Markers(state);
            GameState Restore(GameState current)
            {
                var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(current)), registry);
                Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
                return recovered.State;
            }
            state = Restore(state);
            Assert.That(Request(state, 1, "setup.department.choose").CandidateIds, Is.EquivalentTo(departments));
            Assert.That(Markers(state), Is.EqualTo(markersAtDepartment));
            Assert.That(state.PreparationRuntime.Distribution.Assignments.SelectMany(item => item.CandidateItemIds), Is.EqualTo(distributed));
            Answer(state, registry, Request(state, 1, "setup.department.choose"), NormalizedValue.CreateString(departments[0]));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("initializing"));
            Assert.That(Request(state, 1, Choice), Is.Not.Null);
            var relations = state.ItemZones.Relations.Where(item => item.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation).ToArray();
            Assert.That(relations.Length, Is.EqualTo(1));
            Assert.That(relations[0].TargetItemId, Is.EqualTo(departments[0]));
            var markersAtUpgrade = Markers(state);
            int scoreAtUpgrade = state.FindPlayer(1).Score;
            var deckAtUpgrade = new ItemZoneQuery().GetItemIds(state, FindZone(state, "operator_deck").ZoneId).ToArray();
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == DeckShuffleEffectExecutor.TypeId), Is.EqualTo(withOperators ? 1 : 0));
            var missingUpgrade = GameStateCloneService.DeepClone(state);
            var owner = missingUpgrade.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == PreparationFrameworkFlow.TypeId);
            var omitted = missingUpgrade.EffectRuntime.EffectNodes.Last(node => node.ParentEffectId == owner.EffectId &&
                node.EffectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise && node.ChildEffectIds.Count == 0);
            owner.ChildEffectIds.Remove(omitted.EffectId);
            missingUpgrade.EffectRuntime.EffectNodes.Remove(omitted);
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(missingUpgrade, registry, out _), Is.False,
                "缺少应执行的初始升级时不能恢复到后置流程。");
            var missingShuffleFact = GameStateCloneService.DeepClone(state);
            var missingFactOwner = missingShuffleFact.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == PreparationFrameworkFlow.TypeId);
            missingFactOwner.ContinuationData.Properties.RemoveAll(entry => entry.Name == "initialOperatorShuffleRequired");
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(missingShuffleFact, registry, out _), Is.False);
            if (withOperators)
            {
                var missingShuffle = GameStateCloneService.DeepClone(state);
                var missingShuffleOwner = missingShuffle.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == PreparationFrameworkFlow.TypeId);
                var shuffle = missingShuffle.EffectRuntime.EffectNodes.Single(node => node.ParentEffectId == missingShuffleOwner.EffectId && node.EffectTypeId == DeckShuffleEffectExecutor.TypeId);
                missingShuffleOwner.ChildEffectIds.Remove(shuffle.EffectId);
                missingShuffle.EffectRuntime.EffectNodes.Remove(shuffle);
                Assert.That(PreparationFrameworkFlow.TryValidateRuntime(missingShuffle, registry, out _), Is.False);
            }
            state = Restore(state);
            Assert.That(Markers(state), Is.EqualTo(markersAtUpgrade));
            Assert.That(new ItemZoneQuery().GetItemIds(state, FindZone(state, "operator_deck").ZoneId), Is.EqualTo(deckAtUpgrade));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(scoreAtUpgrade));
            Assert.That(state.ItemZones.Relations.Count(item => item.RelationTypeId == EnterpriseStateQuery.ActiveDepartmentRelation), Is.EqualTo(1));
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("after"));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(scoreAtUpgrade + 1));
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise && node.Status == EffectNodeStatus.Completed), Is.EqualTo(3));
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == DeckShuffleEffectExecutor.TypeId), Is.EqualTo(withOperators ? 1 : 0));
            Assert.That(new ItemZoneQuery().GetItemIds(state, FindZone(state, "operator_deck").ZoneId), Is.EqualTo(deckAtUpgrade));
            Assert.That(calls, Is.EqualTo(new[] { "before:before", "middle:middle", "after:after" }));
        }

        [Test]
        public void RecoveryRejectsMissingFrozenPlanOrDistributionFieldsBeforeApplyingDefaults()
        {
            var state = State(out var registry, new List<string>());
            var result = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(result.IsValid, Is.True, result.Reason);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            string json = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            var baseline = HostRecoveryService.RecoverJson(json, registry);
            Assert.That(baseline.Succeeded, Is.True, baseline.Diagnostic);
            foreach (string path in new[] {
                "Snapshot.State.SetupRecipe.Preparation.Version",
                "Snapshot.State.SetupRecipe.Preparation.BeforeDistribution[0].Completion",
                "Snapshot.State.SetupRecipe.Preparation.BeforeDistribution[0].Arguments.Kind",
                "Snapshot.State.SetupRecipe.Preparation.Enterprise.PlayerCounts[0].Draw",
                "Snapshot.State.PreparationRuntime.Version",
                "Snapshot.State.PreparationRuntime.EffectiveEnterprise.PlayerCounts[0].Keep",
                "Snapshot.State.PreparationRuntime.Distribution.Assignments[0].Confirmed",
                "Snapshot.State.PreparationRuntime.Distribution.Assignments[0].CandidateItemIds"
            })
            {
                var raw = JObject.Parse(json);
                var token = raw.SelectToken(path);
                Assert.That(token?.Parent, Is.TypeOf<JProperty>(), path);
                ((JProperty)token.Parent).Remove();
                var rejected = HostRecoveryService.RecoverJson(raw.ToString(), registry);
                Assert.That(rejected.Succeeded, Is.False, path);
                Assert.That(rejected.FaultCode, Is.EqualTo(HostRecoveryService.SnapshotInvalid), path);
                Assert.That(rejected.Diagnostic, Is.EqualTo("preparation_flow.v1:raw_fields_missing"), path);
            }
        }

        [Test]
        public void FrozenLuaStepRunsThroughExistingEffectAndCompletionBeforeTheMiddleWindow()
        {
            var state = State(out var registry, new List<string>());
            var pack = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            const string path = "lua/departments/enterprise.department.engineering.level_3.reward.lua";
            string hash = LuaContentHasher.ComputeSha256(pack.GetScript(path));
            var step = new PreparationStepDeclaration { StepId = "lua_before", SourcePackId = "core",
                SourcePackVersion = (string)pack.Manifest["version"], Kind = "lua", HandlerId = "fixture.lua_prepare",
                HandlerVersion = "1.0.0", Execution = "once", ScriptPath = path, ScriptHash = hash,
                Completion = new PreparationCompletionDeclaration { HandlerId = "fixture.lua_prepare_done", HandlerVersion = "1.0.0",
                    ScriptPath = path, ScriptHash = hash } };
            state.SetupRecipe.Preparation.BeforeDistribution = new List<PreparationStepDeclaration> { step };
            registry.SetupRecipe = GameStateCloneService.DeepClone(state.SetupRecipe);
            PreparationLuaCatalog.Register(registry, pack, state.SetupRecipe.Preparation);
            int original = state.FindPlayer(1).Resources.Iron;
            var result = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(original + 4));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("middle"));
            Assert.That(state.EffectRuntime.ContinuationBindings.Count(binding => binding.HandlerId == step.Completion.HandlerId &&
                binding.Status == ContinuationBindingStatus.Completed), Is.EqualTo(1));
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            Assert.That(restored.State.FindPlayer(1).Resources.Iron, Is.EqualTo(original + 4));
        }

        [Test]
        public void AuthorExampleLoadsFromItsPackAndGrantsEachPlayerIronThroughThePreparationFlow()
        {
            // 真实示例包和 Lua 经正常冻结装配；规则状态为隔离夹具，不计可见程序验收。
            string path = Path.Combine(Directory.GetCurrentDirectory(), "tools", "packaging", "examples", "准备阶段模式");
            var example = DiyPackSnapshot.Load(path);
            var core = ExternalContentPack.Load(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core"));
            string version = (string)core.Manifest["version"];
            var resolved = DiyContentResolution.Resolve("example.preparation.mode", new[] { "core" }, "map-three-players",
                new[] { example }, version, core.ContentHash, RuleModeReadiness.ImplementedCapabilities, GameplayOptionsCatalog.CoreOnly(core));
            var configuration = new GameRuleConfiguration { RuleModeId = "example.preparation.mode", PlayerCount = 3,
                MapId = "map-three-players", EnabledExpansionIds = new List<string> { "core" },
                OfficialPreparationContract = GameRuleConfiguration.OfficialPreparationVersion, LockedContentPacks = resolved.CreateRuleLocks() };
            var catalog = ActiveContentCatalogBuilder.Build(core, configuration, resolved);
            var examplePlan = catalog.SetupRecipe.Preparation;
            Assert.That(examplePlan.BeforeDistribution.Single().Execution, Is.EqualTo("eachInOrder"));

            var state = State(out var registry, new List<string>());
            state.RuleConfiguration.RuleModeId = configuration.RuleModeId;
            state.SetupRecipe.Preparation.ModeId = configuration.RuleModeId;
            state.SetupRecipe.Preparation.BeforeDistribution = examplePlan.BeforeDistribution;
            registry.SetupRecipe = GameStateCloneService.DeepClone(state.SetupRecipe);
            PreparationLuaCatalog.Register(registry, catalog.FrozenPack, state.SetupRecipe.Preparation);
            var before = state.Players.ToDictionary(player => player.PlayerId, player => player.Resources.Iron);
            var result = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), state.EffectRuntime.LastFaultMessage);
            foreach (var player in state.Players)
                Assert.That(player.Resources.Iron, Is.EqualTo(before[player.PlayerId] + 1), "玩家 " + player.PlayerId);
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("middle"));
        }

        [Test]
        public void JsonRecoveryMatchesDetachedArchiveRecoveryWithoutMutatingTheSource()
        {
            var state = State(out var registry, new List<string>());
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            var archive = HostRecoveryService.CreateArchive(state);
            string before = HostRecoveryService.SerializeArchive(archive);
            var detached = HostRecoveryService.Recover(archive, registry);
            var streamed = HostRecoveryService.RecoverJson(before, registry);
            Assert.That(detached.Succeeded, Is.True, detached.Diagnostic);
            Assert.That(streamed.Succeeded, Is.True, streamed.Diagnostic);
            Assert.That(RuleStateDigestService.Compute(streamed.State), Is.EqualTo(RuleStateDigestService.Compute(detached.State)));
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(before), "公开 DTO 恢复不得修改调用方存档");
            streamed.State.FindPlayer(1).Resources.Iron++;
            Assert.That(RuleStateDigestService.Compute(streamed.State), Is.Not.EqualTo(RuleStateDigestService.Compute(detached.State)));
            Assert.That(HostRecoveryService.SerializeArchive(archive), Is.EqualTo(before), "恢复结果也不得共享调用方的可变状态");
        }

        [Test]
        public void PublicRecoveryPreparationCopiesInputWhileBootstrapCanTransferAnOwnedState()
        {
            var initial = State(out var registry, new List<string>());
            var started = new RoundExecutionService(registry).StartEntrance(initial);
            Assert.That(started.IsValid, Is.True, started.Reason);
            var owned = GameStateCloneService.DeepClone(initial);
            var callerInput = GameStateCloneService.DeepClone(initial);
            var callerSession = new GameSession(GameStateCloneService.DeepClone(initial));
            int originalIron = callerInput.FindPlayer(1).Resources.Iron;
            Assert.That(callerSession.TryPrepareLocalRecoveryState(callerInput, new GameSessionLogContinuationDto(),
                out var copied, out var fault), Is.True, fault);
            callerInput.FindPlayer(1).Resources.Iron++;
            Assert.That(callerSession.TryCommitPreparedRecovery(copied, out fault), Is.True, fault);
            Assert.That(callerSession.State.FindPlayer(1).Resources.Iron, Is.EqualTo(originalIron));

            var ownerSession = new GameSession(GameStateCloneService.DeepClone(initial));
            var method = typeof(GameSession).GetMethod("TryPrepareOwnedRecoveryState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            var arguments = new object[] { owned, new GameSessionLogContinuationDto(), null, null, null };
            Assert.That((bool)method.Invoke(ownerSession, arguments), Is.True, (string)arguments[4]);
            Assert.That(ownerSession.TryCommitPreparedRecovery((PreparedGameSessionRecovery)arguments[3], out fault), Is.True, fault);
            Assert.That(ownerSession.State, Is.SameAs(owned), "启动装配独占移交不应再复制整份状态");
            Assert.That(RuleStateDigestService.Compute(ownerSession.State), Is.EqualTo(RuleStateDigestService.Compute(callerSession.State)));
        }

        [Test]
        public void HistoryProjectionKeepsRecoveryAndMalformedArchiveDecisionsIdentical()
        {
            var state = State(out var registry, new List<string>());
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            string original = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            var method = typeof(HostRecoveryService).GetMethod("RecoverJsonCore",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            void Compare(string json, string label)
            {
                HostRecoveryResult Run(bool projected) => (HostRecoveryResult)method.Invoke(null,
                    new object[] { json, registry, null, null, projected, false });
                var old = Run(false); var current = Run(true);
                Assert.That(current.Succeeded, Is.EqualTo(old.Succeeded), label);
                Assert.That(current.FaultCode, Is.EqualTo(old.FaultCode), label);
                if (old.Succeeded)
                {
                    Assert.That(RuleStateDigestService.Compute(current.State), Is.EqualTo(RuleStateDigestService.Compute(old.State)), label);
                    Assert.That(JsonConvert.SerializeObject(current.State.EffectRuntime.Journal),
                        Is.EqualTo(JsonConvert.SerializeObject(old.State.EffectRuntime.Journal)), label + "：完整命令历史");
                    Assert.That(JsonConvert.SerializeObject(current.State.EffectRuntime.RuleEvents),
                        Is.EqualTo(JsonConvert.SerializeObject(old.State.EffectRuntime.RuleEvents)), label + "：完整事件历史");
                }
            }
            Compare(original, "有效归档");
            Compare("/*前置注释*/" + original + " /*尾部注释*/", "注释");
            var raw = JObject.Parse(original);
            ((JObject)raw["Snapshot"]["State"]).Remove("InfluenceLocks");
            Compare(raw.ToString(Newtonsoft.Json.Formatting.None), "缺少必需的锁定字段");
            raw = JObject.Parse(original);
            ((JObject)raw["Snapshot"]["State"]["ItemZones"]).Remove("FacilityLayerVersion");
            Compare(raw.ToString(Newtonsoft.Json.Formatting.None), "缺少必需的设施叠放版本");
            raw = JObject.Parse(original);
            raw["Snapshot"]["State"]["EffectRuntime"]["Journal"] = "不是数组";
            Compare(raw.ToString(Newtonsoft.Json.Formatting.None), "历史数组类型错误");
            raw = JObject.Parse(original);
            raw["Snapshot"]["State"]["EffectRuntime"]["RuleEvents"] = new JArray(new JObject { ["UnknownEventField"] = true });
            Compare(raw.ToString(Newtonsoft.Json.Formatting.None), "历史事件含未知字段");
            raw = JObject.Parse(original);
            raw["Snapshot"]["State"]["EffectRuntime"]["Journal"] = new JArray(new JObject { ["DuplicateProbe"] = 1 });
            string duplicate = raw.ToString(Newtonsoft.Json.Formatting.None)
                .Replace("\"DuplicateProbe\":1", "\"DuplicateProbe\":1,\"DuplicateProbe\":2");
            Assert.That(duplicate, Does.Contain("\"DuplicateProbe\":2"));
            Compare(duplicate, "被省略历史中的重复键");
            Compare(original.Substring(0, original.Length - 1), "截断归档");
        }

        [Test]
        public void ConcurrentEnterpriseRequestsRetainTheirOwnAuthorityAndEnforceRequiredCandidates()
        {
            var state = State(out var registry, new List<string>(), true);
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            var first = Request(state, 1, ExpansionSetupFlow.EnterpriseChoiceInteraction);
            var own = state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 1);
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, CommandId = "preparation-original", PlayerId = 1 };
            command.Parameters["interactionId"] = first.GetStableInteractionId();
            command.Parameters["expectedRevision"] = first.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.OptionIds.Add(own.RequiredItemIds.Single());
            Assert.That(InteractionRequestValidity.TryReadRequestAuthority(state, command, out var original, out var validation), Is.True, validation.Reason);
            var second = Request(state, 2, ExpansionSetupFlow.EnterpriseChoiceInteraction);
            Answer(state, registry, second, NormalizedValue.CreateString(second.CandidateIds[0]));
            Assert.That(InteractionRequestValidity.TryReadRequestAuthority(state, command, out var current, out validation), Is.True, validation.Reason);
            Assert.That(current.Digest, Is.EqualTo(original.Digest));
            Assert.That(InteractionRequestValidity.TryValidateAnswer(state, command, original.Digest, out validation), Is.True, validation.Reason);
            command.OptionIds[0] = own.CandidateItemIds.Single(id => !own.RequiredItemIds.Contains(id));
            Assert.That(InteractionRequestValidity.TryValidateAnswer(state, command, original.Digest, out validation), Is.False);
            Assert.That(validation.Reason, Is.EqualTo("enterprise_required_candidate_missing"));
            var changed = GameStateCloneService.DeepClone(state);
            changed.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 1).CandidateItemIds.Clear();
            Assert.That(InteractionRequestValidity.TryReadRequestAuthority(changed, command, out _, out _), Is.False);
        }

        [TestCase(PlayerStageSchedulingMode.Sequential)]
        [TestCase(PlayerStageSchedulingMode.Concurrent)]
        public void EmptyPreparationWindowsPublishInitialEnterpriseRequestsAtCurrentRevision(PlayerStageSchedulingMode mode)
        {
            var state = State(out var registry, new List<string>());
            state.RuleConfiguration.PlayerStageSchedulingMode = mode;
            state.SetupRecipe.Preparation.BeforeDistribution.Clear();
            state.SetupRecipe.Preparation.BetweenDistributionAndConfirmation.Clear();
            state.SetupRecipe.Preparation.AfterConfirmation.Clear();
            var round = new RoundExecutionService(registry);
            var started = round.StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            var requests = state.EffectRuntime.InteractionRequests.Where(item => item.Status == "open" &&
                item.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction).ToArray();
            Assert.That(requests, Has.Length.EqualTo(3));
            Assert.That(requests.Select(item => item.StateRevision), Is.All.EqualTo(state.EffectRuntime.StateRevision),
                "正式启动后每个玩家的企业选择都应能按当前请求版本提交。");
            if (mode == PlayerStageSchedulingMode.Sequential)
            {
                var detached = GameStateCloneService.DeepClone(state);
                var detachedRequest = detached.EffectRuntime.InteractionRequests.Single(item => item.InteractionId == requests[0].InteractionId);
                var owner = detached.EffectRuntime.EffectNodes.Single(item => item.EffectId == detachedRequest.OwnerEffectId);
                var parent = detached.EffectRuntime.EffectNodes.Single(item => item.EffectId == owner.ParentEffectId);
                parent.ChildSchedulingGroups.Single(item => item.StableGroupKey == PreparationFrameworkFlow.GroupKey).ChildEffectIds.Remove(owner.EffectId);
                int oldRevision = detachedRequest.StateRevision;
                detached.EffectRuntime.StateRevision++;
                RoundExecutionService.RefreshConcurrentInteractionRevisions(detached);
                Assert.That(detachedRequest.StateRevision, Is.EqualTo(oldRevision), "脱离原分发任务的请求不能获得新版本。");
            }
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            var first = requests.Single(item => item.AnsweringPlayerId == 1);
            var command = new GameCommand { CommandId = "preparation-initial-selection", Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            command.Parameters["interactionId"] = first.GetStableInteractionId();
            command.Parameters["expectedRevision"] = first.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.OptionIds.Add(first.CandidateIds[0]);
            var result = session.Submit(command);
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            var second = Request(state, 2, ExpansionSetupFlow.EnterpriseChoiceInteraction);
            Assert.That(second.StateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
            var secondCommand = new GameCommand { CommandId = "preparation-second-selection", Kind = GameCommandKind.AnswerInteraction, PlayerId = 2 };
            secondCommand.Parameters["interactionId"] = second.GetStableInteractionId();
            secondCommand.Parameters["expectedRevision"] = second.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            secondCommand.OptionIds.Add(second.CandidateIds[0]);
            var secondResult = session.Submit(secondCommand);
            Assert.That(secondResult.Succeeded, Is.True, secondResult.Validation.Reason);
        }

        [TestCase(PlayerStageSchedulingMode.Sequential)]
        [TestCase(PlayerStageSchedulingMode.Concurrent)]
        public void FrameworkEnterpriseRequestsPublishCurrentRevisionsThroughNormalSession(PlayerStageSchedulingMode mode)
        {
            var state = State(out var registry, new List<string>());
            state.RuleConfiguration.PlayerStageSchedulingMode = mode;
            var round = new RoundExecutionService(registry);
            Assert.That(round.StartEntrance(state).IsValid, Is.True);
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            for (int step = 0; step < 2; step++)
            {
                var request = Request(session.State, 1, Choice);
                var command = new GameCommand { CommandId = "preparation-window-" + step,
                    Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
                command.Parameters["interactionId"] = request.GetStableInteractionId();
                command.Parameters["expectedRevision"] = request.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                command.Parameters["answerValue"] = "true";
                var result = session.Submit(command);
                Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            }
            var requests = state.EffectRuntime.InteractionRequests.Where(item => item.Status == "open" &&
                item.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction).ToArray();
            Assert.That(requests, Has.Length.EqualTo(3));
            Assert.That(requests.Select(item => item.StateRevision), Is.All.EqualTo(state.EffectRuntime.StateRevision),
                "正式会话发布的并行选择请求必须可按各自显示版本提交。");
            var first = requests.Single(item => item.AnsweringPlayerId == 1);
            var selection = new GameCommand { CommandId = "preparation-selection-first",
                Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            selection.Parameters["interactionId"] = first.GetStableInteractionId();
            selection.Parameters["expectedRevision"] = first.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            selection.OptionIds.Add(first.CandidateIds[0]);
            var answer = session.Submit(selection);
            Assert.That(answer.Succeeded, Is.True, answer.Validation.Reason);
        }

        [Test]
        public void PrivateLedgerBeyondOldLimitAcceptsNewCommandAndRetainsOldDuplicateFact()
        {
            var calls = new List<string>();
            var state = State(out var registry, calls, privateRandom: true);
            var round = new RoundExecutionService(registry);
            Assert.That(round.StartEntrance(state).IsValid, Is.True);
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            session.BeginPrivateCommandAuthority(false);
            var request = Request(state, 1, Choice);
            var command = new GameCommand { CommandId = "private.oldest", Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            command.Parameters["interactionId"] = request.GetStableInteractionId();
            command.Parameters["expectedRevision"] = state.EffectRuntime.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters["answerValue"] = "true";
            var probe = GameStateCloneService.DeepClone(state);
            OperationWindowService.Synchronize(probe);
            Assert.That(OperationWindowService.TryResolveCommandContext(probe, command, out var window, out var validation), Is.True, validation.Reason);
            var original = new OperationCommandProposalDto { GameId = state.GameId, SimulationEpoch = 1, MembershipVersion = 1,
                ClientSubmissionSequence = 1, ObservedGlobalSequence = 0, ObservedStateRevision = state.EffectRuntime.StateRevision,
                OperationWindowId = window.OperationWindowId, WindowGeneration = window.WindowGeneration, Command = GameCommandDto.FromCommand(command) };
            string oldDigest = OperationCommandCanonicalizer.CanonicalProposalDigest(original);
            var ledger = state.PrivateCommandLedger;
            // 仅为隔离边界定位填入有效历史，不把这些合成收据当作正常对局。
            for (int index = 0; index < 16384; index++) ledger.Receipts.Add(new PrivateCommandReceipt {
                CommandId = index == 0 ? command.CommandId : "private.history." + index,
                ProposalDigest = index == 0 ? oldDigest : new string('a', 64), PlayerId = 1,
                OperationWindowId = window.OperationWindowId, WindowGeneration = window.WindowGeneration,
                SubmissionSequence = index + 1, GlobalSequence = index + 1, StateRevision = state.EffectRuntime.StateRevision });
            ledger.CommittedSequence = ledger.Receipts.Count;
            Assert.That(PrivateCommandLedgerPolicy.TryValidate(state, out var ledgerReason), Is.True, ledgerReason);
            var next = GameStateCloneService.DeepClone(original);
            next.Command.CommandId = "private.after-old-limit";
            next.ClientSubmissionSequence = 16385; next.ObservedGlobalSequence = 16384;
            var result = session.SubmitAuthenticatedPrivateCommand(next, 1, 1, 1, state.ContentPackHash);
            Assert.That(result.Status, Is.EqualTo(PrivateCommandStatus.Committed), result.Reason);
            Assert.That(state.PrivateCommandLedger.CommittedSequence, Is.EqualTo(16385));
            Assert.That(session.SubmitAuthenticatedPrivateCommand(original, 1, 1, 1, state.ContentPackHash).Status, Is.EqualTo(PrivateCommandStatus.Duplicate));
            var conflict = GameStateCloneService.DeepClone(original);
            conflict.Command.Parameters.Single(item => item.Key == "answerValue").Value = "false";
            Assert.That(session.SubmitAuthenticatedPrivateCommand(conflict, 1, 1, 1, state.ContentPackHash).Reason, Is.EqualTo("private_command_id_conflict"));
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            var resumed = new GameSession(restored.State);
            resumed.BeginPrivateCommandAuthority(true);
            Assert.That(resumed.SubmitAuthenticatedPrivateCommand(original, 1, 1, 1, state.ContentPackHash).Status, Is.EqualTo(PrivateCommandStatus.Duplicate));
        }

        [Test]
        public void PrivateCommandReceiptCommitsWithRulesAndSurvivesReconnectAndRecovery()
        {
            var calls = new List<string>();
            var state = State(out var registry, calls, privateRandom: true);
            var round = new RoundExecutionService(registry);
            var started = round.StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            session.BeginPrivateCommandAuthority(false);
            session.EnableConfirmedRecoveryCapture();
            int notifications = 0;
            session.StateCommitted += () => { notifications++; Assert.That(session.State.PrivateCommandLedger.Receipts, Has.Count.EqualTo(1)); };

            GameCommand BooleanAnswer(GameState current, string id)
            {
                var request = Request(current, 1, Choice);
                var command = new GameCommand { CommandId = id, Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
                command.Parameters["interactionId"] = request.GetStableInteractionId();
                command.Parameters["expectedRevision"] = current.EffectRuntime.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                command.Parameters["answerValue"] = "true";
                return command;
            }
            OperationCommandProposalDto Proposal(GameState current, GameCommand command, long submission, long observed, long epoch)
            {
                var probe = GameStateCloneService.DeepClone(current);
                OperationWindowService.Synchronize(probe);
                Assert.That(OperationWindowService.TryResolveCommandContext(probe, command, out var context, out var validation), Is.True, validation.Reason);
                return new OperationCommandProposalDto { GameId = current.GameId, SimulationEpoch = epoch, MembershipVersion = 1,
                    ClientSubmissionSequence = submission, ObservedGlobalSequence = observed, ObservedStateRevision = current.EffectRuntime.StateRevision,
                    OperationWindowId = context.OperationWindowId, WindowGeneration = context.WindowGeneration, Command = GameCommandDto.FromCommand(command) };
            }
            var command1 = BooleanAnswer(state, "private.before");
            var proposal = Proposal(state, command1, 1, 0, 1);
            Assert.That(session.Submit(command1).Succeeded, Is.False);
            Assert.That(session.SubmitAuthenticatedPrivateCommand(proposal, 2, 1, 1, state.ContentPackHash).Status, Is.EqualTo(PrivateCommandStatus.Rejected));
            var wrongWindow = GameStateCloneService.DeepClone(proposal); wrongWindow.OperationWindowId += ".wrong";
            Assert.That(session.SubmitAuthenticatedPrivateCommand(wrongWindow, 1, 1, 1, state.ContentPackHash).Status, Is.EqualTo(PrivateCommandStatus.Rejected));
            var committed = session.SubmitAuthenticatedPrivateCommand(proposal, 1, 1, 1, state.ContentPackHash);
            Assert.That(committed.Status, Is.EqualTo(PrivateCommandStatus.Committed), committed.Reason);
            Assert.That(committed.GlobalSequence, Is.EqualTo(1));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("middle"));
            Assert.That(notifications, Is.EqualTo(1));
            int invoked = calls.Count;
            proposal.SimulationEpoch = 2;
            Assert.That(session.SubmitAuthenticatedPrivateCommand(proposal, 1, 2, 1, state.ContentPackHash).Status, Is.EqualTo(PrivateCommandStatus.Duplicate));
            Assert.That(calls.Count, Is.EqualTo(invoked));
            var conflict = GameStateCloneService.DeepClone(proposal);
            conflict.Command.Parameters.Single(item => item.Key == "answerValue").Value = "false";
            Assert.That(session.SubmitAuthenticatedPrivateCommand(conflict, 1, 2, 1, state.ContentPackHash).Reason, Is.EqualTo("private_command_id_conflict"));
            Assert.That(session.TryReadCommittedRecoveryPoint(command1.CommandId, out var point), Is.True);
            string json = HostRecoveryService.SerializeArchive(session.ExportConfirmedRecoveryPoint(point).Archive);
            var recovered = HostRecoveryService.RecoverJson(json, registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.PrivateCommandLedger.Receipts.Single().CommandId, Is.EqualTo(command1.CommandId));
            var raw = JObject.Parse(json);
            ((JProperty)raw.SelectToken("Snapshot.State.PrivateCommandLedger.Receipts[0].ProposalDigest").Parent).Remove();
            var damaged = HostRecoveryService.RecoverJson(raw.ToString(), registry);
            Assert.That(damaged.Succeeded, Is.False);
            Assert.That(damaged.Diagnostic, Is.EqualTo("private_command_ledger.v1:raw_fields_missing"));
            var missingKey = JObject.Parse(json);
            ((JObject)missingKey["Snapshot"]["State"]["RandomRuntime"]).Remove("PrivateKeyHex");
            Assert.That(HostRecoveryService.RecoverJson(missingKey.ToString(), registry).Succeeded, Is.False);
            var missingLedger = JObject.Parse(json);
            ((JObject)missingLedger["Snapshot"]["State"]).Remove("PrivateCommandLedger");
            var unsafeResume = HostRecoveryService.RecoverJson(missingLedger.ToString(), registry);
            Assert.That(unsafeResume.Succeeded, Is.False);
            Assert.That(unsafeResume.Diagnostic, Is.EqualTo("private_command_ledger.v1:raw_fields_missing"));
            var missingCount = JObject.Parse(json);
            ((JObject)missingCount["Snapshot"]["State"]["PrivateCommandLedger"]).Remove("CommittedSequence");
            Assert.That(HostRecoveryService.RecoverJson(missingCount.ToString(), registry).Succeeded, Is.False);
            var truncated = JObject.Parse(json);
            ((JArray)truncated["Snapshot"]["State"]["PrivateCommandLedger"]["Receipts"]).Clear();
            Assert.That(HostRecoveryService.RecoverJson(truncated.ToString(), registry).Succeeded, Is.False);

            var resumed = new GameSession(recovered.State);
            resumed.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            resumed.BeginPrivateCommandAuthority(true);
            Assert.That(resumed.SubmitAuthenticatedPrivateCommand(proposal, 1, 2, 1, state.ContentPackHash).Status, Is.EqualTo(PrivateCommandStatus.Duplicate));
            var next = Proposal(resumed.State, BooleanAnswer(resumed.State, "private.middle"), 2, 1, 2);
            var invalid = GameStateCloneService.DeepClone(next);
            invalid.Command.Parameters.Single(item => item.Key == "answerValue").Value = "invalid";
            Assert.That(resumed.SubmitAuthenticatedPrivateCommand(invalid, 1, 2, 1, state.ContentPackHash).Status, Is.EqualTo(PrivateCommandStatus.Rejected));
            Assert.That(resumed.State.PrivateCommandLedger.Receipts, Has.Count.EqualTo(1));
            Assert.That(resumed.State.PreparationRuntime.Stage, Is.EqualTo("middle"));
            var secondResult = resumed.SubmitAuthenticatedPrivateCommand(next, 1, 2, 1, state.ContentPackHash);
            Assert.That(secondResult.Status, Is.EqualTo(PrivateCommandStatus.Committed), secondResult.Reason);
            Assert.That(resumed.State.PreparationRuntime.Stage, Is.EqualTo("confirming"));
            Assert.That(resumed.State.PrivateCommandLedger.Receipts, Has.Count.EqualTo(2));

            registry.ContentPackHash = state.ContentPackHash;
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            Assert.That(bindings.TryBindConnection(11, 111, out _), Is.True);
            Assert.That(bindings.TryBindConnection(22, 222, out _), Is.True);
            var publisher = new PrivatePlayerViewPublisher(resumed, registry, bindings, 2);
            Assert.That(publisher.TryBind(11, "seat1"), Is.True);
            Assert.That(publisher.TryBind(22, "seat2"), Is.True);
            Assert.That(publisher.TryCreate(11, "seat1", 2, out var firstSnapshot, out var initialReason), Is.True, initialReason);
            Assert.That(publisher.TryCreate(22, "seat2", 2, out var secondSnapshot, out initialReason), Is.True, initialReason);
            var receiver = new PrivatePlayerViewReceiver(state.GameId, state.ContentPackHash, 2, 1, "seat1", state.RuleConfiguration, firstSnapshot.View.LaunchPlayerOrder);
            var forged = GameStateCloneService.DeepClone(firstSnapshot);
            forged.Windows.Single(item => item.Requests.Count > 0).Requests[0].InteractionId = "other-player-request";
            Assert.That(receiver.Receive(forged, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Rejected));
            Assert.That(receiver.Receive(firstSnapshot, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied));
            Assert.That(receiver.NextSubmissionSequence, Is.EqualTo(3));
            string wire = Newtonsoft.Json.JsonConvert.SerializeObject(firstSnapshot);
            Assert.That(PrivatePlayerViewWireProtocol.TryDecode(wire, out var parsed), Is.True);
            Assert.That(receiver.Receive(parsed, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Duplicate));
            var missingWindows = JObject.Parse(wire); missingWindows.Remove("Windows");
            Assert.That(PrivatePlayerViewWireProtocol.TryDecode(missingWindows.ToString(), out _), Is.False);
            var secondReceiver = new PrivatePlayerViewReceiver(state.GameId, state.ContentPackHash, 2, 2, "seat2", state.RuleConfiguration, secondSnapshot.View.LaunchPlayerOrder);
            Assert.That(secondReceiver.Receive(secondSnapshot, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied));
            var firstTracker = new PrivateClientCommandTracker(receiver);
            var secondTracker = new PrivateClientCommandTracker(secondReceiver);
            OperationCommandProposalDto EnterpriseAnswer(PrivatePlayerViewSnapshot snapshot, PrivatePlayerViewReceiver client, PrivateClientCommandTracker tracker)
            {
                int player = snapshot.PlayerId;
                var request = snapshot.View.Interactions.Single(item => item.AnsweringPlayerId == player && item.Status == "open" &&
                    item.Kind == ExpansionSetupFlow.EnterpriseChoiceInteraction);
                var answer = new GameCommand { CommandId = "private.company." + player, Kind = GameCommandKind.AnswerInteraction, PlayerId = player };
                answer.Parameters["interactionId"] = request.InteractionId;
                answer.Parameters["expectedRevision"] = snapshot.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
                answer.OptionIds.Add(request.CandidateIds[0]);
                Assert.That(tracker.TryBegin(answer, null, out var built, out var failure), Is.True, failure);
                Assert.That(tracker.TryBegin(answer, null, out _, out _), Is.False);
                Assert.That(built.Command.Parameters.Any(item => item.Key == InteractionRequestValidity.DigestParameter), Is.True);
                Assert.That(answer.Parameters.ContainsKey(InteractionRequestValidity.DigestParameter), Is.False);
                answer.Parameters["interactionId"] = "other-player-request";
                Assert.That(client.TryBuildProposal(answer, null, out _, out _), Is.False);
                return built;
            }
            var firstPlayer = EnterpriseAnswer(firstSnapshot, receiver, firstTracker);
            var secondPlayer = EnterpriseAnswer(secondSnapshot, secondReceiver, secondTracker);
            Assert.That(publisher.TrySubmit(11, "expired", firstPlayer, out _), Is.False);
            Assert.That(publisher.TrySubmit(22, "seat2", firstPlayer, out var wrongSeat), Is.True);
            Assert.That(wrongSeat.Status, Is.EqualTo(PrivateCommandStatus.Rejected));
            Assert.That(publisher.TrySubmit(22, "seat2", secondPlayer, out var acceptedSecond), Is.True);
            Assert.That(acceptedSecond.Status, Is.EqualTo(PrivateCommandStatus.Committed), acceptedSecond.Reason);
            Assert.That(secondTracker.TryRecordResult(acceptedSecond), Is.True);
            Assert.That(secondTracker.TryTakeCompleted(out _), Is.False);
            Assert.That(publisher.TryCreate(22, "seat2", 3, out var secondAfter, out var afterReason), Is.True, afterReason);
            Assert.That(secondReceiver.Receive(secondAfter, false, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied));
            Assert.That(secondTracker.TryTakeCompleted(out var secondDone), Is.True);
            Assert.That(secondDone.CommandId, Is.EqualTo(secondPlayer.Command.CommandId));
            Assert.That(publisher.TryCreate(11, "seat1", 2, out _, out _), Is.False);
            Assert.That(publisher.TrySubmit(11, "seat1", firstPlayer, out var acceptedFirst), Is.True);
            Assert.That(acceptedFirst.Status, Is.EqualTo(PrivateCommandStatus.Committed), acceptedFirst.Reason);
            Assert.That(acceptedFirst.GlobalSequence, Is.EqualTo(4));
            var forgedResult = GameStateCloneService.DeepClone(acceptedFirst); forgedResult.PlayerId = 2;
            Assert.That(firstTracker.TryRecordResult(forgedResult), Is.False);
            Assert.That(firstTracker.TryRecordResult(acceptedFirst), Is.True);
            Assert.That(firstTracker.TryTakeCompleted(out _), Is.False);
            Assert.That(resumed.State.PreparationRuntime.Distribution.Assignments.Where(item => item.PlayerId <= 2).All(item => item.Confirmed), Is.True);
            Assert.That(PrivateCommandLedgerPolicy.TryValidate(resumed.State, out string ledgerReason), Is.True, ledgerReason);
            bindings.UnbindConnection(11);
            Assert.That(publisher.TrySubmit(11, "seat1", firstPlayer, out _), Is.False);
            Assert.That(bindings.TryBindConnection(111, 111, out _), Is.True);
            var reconnectedPublisher = new PrivatePlayerViewPublisher(resumed, registry, bindings, 3);
            Assert.That(reconnectedPublisher.TryBind(111, "seat1-new"), Is.True);
            var reconnectedReceiver = new PrivatePlayerViewReceiver(state.GameId, state.ContentPackHash, 3, 1, "seat1-new", state.RuleConfiguration, firstSnapshot.View.LaunchPlayerOrder);
            Assert.That(firstTracker.TryAttachReceiver(reconnectedReceiver), Is.True);
            Assert.That(firstTracker.ReadPendingForRetry(), Is.Null);
            Assert.That(reconnectedPublisher.TryCreate(111, "seat1-new", 4, out var ownView, out var viewReason), Is.True, viewReason);
            Assert.That(reconnectedReceiver.Receive(ownView, true, out _), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied));
            var retry = firstTracker.ReadPendingForRetry();
            Assert.That(retry.SimulationEpoch, Is.EqualTo(3));
            Assert.That(retry.Command.CommandId, Is.EqualTo(firstPlayer.Command.CommandId));
            Assert.That(reconnectedPublisher.TrySubmit(111, "seat1-new", retry, out var duplicate), Is.True);
            Assert.That(duplicate.Status, Is.EqualTo(PrivateCommandStatus.Duplicate));
            Assert.That(firstTracker.TryRecordResult(duplicate), Is.True);
            Assert.That(firstTracker.TryTakeCompleted(out var firstDone), Is.True);
            Assert.That(firstDone.GlobalSequence, Is.EqualTo(4));
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(ownView), Does.Not.Contain(resumed.State.RandomRuntime.PrivateKeyHex));
            string otherCandidate = resumed.State.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 3).CandidateItemIds[0];
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(ownView), Does.Not.Contain(otherCandidate));
        }

        [Test]
        public void PrivateHostWaitsForInstalledViewsAndPausesUntilDisconnectedSeatResynchronizes()
        {
            var state = State(out var registry, new List<string>(), privateRandom: true);
            state.Logs.Add(new GameLogEntry { Message = "夹具日志" });
            var round = new RoundExecutionService(registry);
            var started = round.StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            registry.ContentPackHash = state.ContentPackHash;
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            session.BeginPrivateCommandAuthority(false);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            var host = new PrivateHostCoordinator(session, registry, bindings, 1, 1);
            var receivers = new Dictionary<int, PrivatePlayerViewReceiver>();
            void Bind(int player, int connection, ulong steam, string token)
            {
                Assert.That(bindings.TryBindConnection(connection, steam, out _), Is.True);
                Assert.That(host.TryBind(connection, token), Is.True);
                receivers[player] = new PrivatePlayerViewReceiver(state.GameId, state.ContentPackHash, 1, player, token,
                    state.RuleConfiguration, state.LaunchPlayerOrder);
            }
            PrivatePlayerViewSnapshot Install(int player, int connection, string token)
            {
                var assembler = new PrivatePlayerViewPartAssembler(state.GameId, state.ContentPackHash, 1, player, token);
                PrivatePlayerViewSnapshot snapshot = null;
                long id = 0;
                bool initial = false;
                while (host.TryTakePart(connection, token, out var part))
                {
                    id = part.TransferId; initial = part.IsInitial;
                    var status = assembler.Receive(part, out var acknowledgment, out var completed, out var reason);
                    Assert.That(status, Is.Not.EqualTo(PrivatePlayerViewPartStatus.Rejected), reason);
                    if (completed != null)
                    {
                        snapshot = completed;
                        Assert.That(host.TryConfirmInstalled(connection, PrivateViewInstalledReceipt.From(snapshot, id)), Is.False,
                            "字节尚未确认时不能声称完成安装。");
                    }
                    Assert.That(host.TryAcknowledgePart(connection, acknowledgment), Is.True);
                }
                Assert.That(snapshot, Is.Not.Null);
                Assert.That(receivers[player].Receive(snapshot, initial, out var receiveReason), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied), receiveReason);
                var installed = PrivateViewInstalledReceipt.From(snapshot, id);
                Assert.That(host.TryConfirmInstalled(connection, installed), Is.True);
                Assert.That(host.TryConfirmInstalled(connection, installed), Is.True, "安装确认重发应幂等。");
                return snapshot;
            }
            Bind(1, 11, 111, "one"); Bind(2, 22, 222, "two"); Bind(3, 33, 333, "three");
            Assert.That(host.Ready, Is.False);
            var first = Install(1, 11, "one"); Install(2, 22, "two");
            Assert.That(host.Ready, Is.False);
            Install(3, 33, "three"); Assert.That(host.Ready, Is.True);
            string reconnectRequest = Guid.NewGuid().ToString("N");
            Assert.That(host.TryApproveReconnect(11, "wrong-token", reconnectRequest), Is.False);
            Assert.That(host.TryApproveReconnect(11, "one", reconnectRequest), Is.True);
            Assert.That(host.Ready, Is.False);
            Assert.That(host.TryApproveReconnect(11, "one", reconnectRequest), Is.True, "同一重连请求应可安全重试。");
            Assert.That(host.CancelReconnect(22, "two", reconnectRequest), Is.False);
            Assert.That(host.CancelReconnect(11, "one", reconnectRequest), Is.True);
            Assert.That(host.Ready, Is.True);
            var request = first.View.Interactions.Single(item => item.AnsweringPlayerId == 1 && item.Status == "open" && item.Kind == Choice);
            var command = new GameCommand { CommandId = "host-before", Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            command.Parameters["interactionId"] = request.InteractionId;
            command.Parameters["expectedRevision"] = first.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters["answerValue"] = "true";
            var tracker = new PrivateClientCommandTracker(receivers[1]);
            Assert.That(tracker.TryBegin(command, null, out var proposal, out var buildReason), Is.True, buildReason);
            var commandEnvelope = new PrivateCommandEnvelope { GameId = state.GameId, ContentPackHash = state.ContentPackHash,
                ConnectionToken = "one", SessionEpoch = 1, MembershipVersion = 1, PlayerId = 1, Proposal = proposal };
            var commandJson = System.Text.Encoding.UTF8.GetString(PrivateCommandWireProtocol.Encode(commandEnvelope));
            Assert.That(PrivateCommandWireProtocol.TryDecodeCommand(commandJson, out var decoded), Is.True);
            Assert.That(PrivateCommandWireProtocol.TryDecodeCommand("{\"ProtocolVersion\":1," + commandJson.Substring(1), out _), Is.False);
            var missingCommandField = JObject.Parse(commandJson); ((JObject)missingCommandField["Proposal"]["Command"]).Remove("OptionIds");
            Assert.That(PrivateCommandWireProtocol.TryDecodeCommand(missingCommandField.ToString(), out _), Is.False);
            var foreignCommand = JObject.Parse(commandJson); foreignCommand["Proposal"]["Command"]["PlayerId"] = 2;
            Assert.That(PrivateCommandWireProtocol.TryDecodeCommand(foreignCommand.ToString(), out _), Is.False);
            var wrongContent = GameStateCloneService.DeepClone(decoded); wrongContent.ContentPackHash = "other-content";
            Assert.That(host.TrySubmitEnvelope(11, wrongContent, out _), Is.False);
            Assert.That(host.TrySubmitEnvelope(22, decoded, out _), Is.False);
            Assert.That(host.TrySubmitEnvelope(11, decoded, out var resultEnvelope), Is.True);
            var committed = resultEnvelope.Result;
            Assert.That(committed.Status, Is.EqualTo(PrivateCommandStatus.Committed), committed.Reason);
            var resultJson = System.Text.Encoding.UTF8.GetString(PrivateCommandWireProtocol.Encode(resultEnvelope));
            Assert.That(PrivateCommandWireProtocol.TryDecodeResult(resultJson, out var decodedResult), Is.True);
            var missingResultField = JObject.Parse(resultJson); ((JObject)missingResultField["Result"]).Remove("CommandId");
            Assert.That(PrivateCommandWireProtocol.TryDecodeResult(missingResultField.ToString(), out _), Is.False);
            Assert.That(tracker.TryRecordResult(decodedResult.Result), Is.True);
            Assert.That(tracker.TryTakeCompleted(out _), Is.False);
            string secondReconnect = Guid.NewGuid().ToString("N");
            Assert.That(host.TryApproveReconnect(22, "two", secondReconnect), Is.True);
            Assert.That(host.Ready, Is.False);
            host.Disconnect(22);
            Assert.That(host.Ready, Is.False);
            Assert.That(host.TrySubmit(11, "one", proposal, out _), Is.False);
            Bind(2, 222, 222, "two-new");
            Assert.That(host.Ready, Is.False);
            var newSecond = Install(2, 222, "two-new");
            Assert.That(newSecond.CommittedSequence, Is.EqualTo(1));
            Assert.That(host.Ready, Is.True);
            var updatedFirst = Install(1, 11, "one");
            Assert.That(updatedFirst.CommittedSequence, Is.EqualTo(1));
            Assert.That(tracker.TryTakeCompleted(out var finished), Is.True);
            Assert.That(finished.CommandId, Is.EqualTo(command.CommandId));
            Assert.That(host.TrySubmit(11, "one", proposal, out var repeated), Is.True);
            Assert.That(repeated.Status, Is.EqualTo(PrivateCommandStatus.Duplicate));
            Assert.That(host.CommittedSequence, Is.EqualTo(1));
            string foreign = state.PreparationRuntime.Distribution.Assignments.Single(item => item.PlayerId == 1).CandidateItemIds[0];
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(newSecond), Does.Not.Contain(foreign));
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(newSecond), Does.Not.Contain(state.RandomRuntime.PrivateKeyHex));
            session.State.Logs[0].Message = "未经事务改动的日志"; // 仅隔离夹具：规则摘要默认排除日志正文。
            Assert.That(host.TrySubmit(11, "one", proposal, out _), Is.False);
            Assert.That(host.Fault, Is.EqualTo("private_host_untracked_state_change"));
        }

        [Test]
        public void PrivateCommandPayloadUsesBoundedAuthenticatedChunksAndRejectsConflicts()
        {
            var data = new byte[90000]; new Random(31415).NextBytes(data);
            var expected = (byte[])data.Clone();
            var sender = new PrivatePayloadSendWindow("payload-game", "payload-content", "seat-token", 4, 2, 1, "command", 7, data);
            data[0] ^= 0xff;
            var receiver = new PrivatePayloadAssembler("payload-game", "payload-content", "seat-token", 4, 2, 1, "command");
            var firstBatch = new List<PrivatePayloadPart>();
            while (sender.TryTake(out var part)) firstBatch.Add(part);
            Assert.That(firstBatch, Has.Count.EqualTo(4));
            Assert.That(sender.SentBytes - sender.AcknowledgedBytes, Is.EqualTo(PrivatePayloadSendWindow.MaximumInFlightBytes));
            Assert.That(receiver.Receive(firstBatch[1], out var earlyAck, out _), Is.EqualTo(PrivatePayloadReceiveStatus.Buffered));
            Assert.That(earlyAck.ReceivedBytes, Is.Zero);
            Assert.That(sender.TryAcknowledge(earlyAck), Is.True);
            var wrongToken = GameStateCloneService.DeepClone(earlyAck); wrongToken.ConnectionToken = "expired";
            Assert.That(sender.TryAcknowledge(wrongToken), Is.False);
            var wrongPurpose = GameStateCloneService.DeepClone(firstBatch[0]); wrongPurpose.Purpose = "result";
            Assert.That(receiver.Receive(wrongPurpose, out _, out _), Is.EqualTo(PrivatePayloadReceiveStatus.Rejected));
            byte[] completed = null;
            void Receive(PrivatePayloadPart part)
            {
                var status = receiver.Receive(part, out var ack, out var payload);
                Assert.That(status, Is.EqualTo(PrivatePayloadReceiveStatus.Buffered).Or.EqualTo(PrivatePayloadReceiveStatus.Completed));
                Assert.That(sender.TryAcknowledge(ack), Is.True);
                if (payload != null) completed = payload;
            }
            Receive(firstBatch[0]);
            var conflicting = GameStateCloneService.DeepClone(firstBatch[0]); conflicting.Payload[0] ^= 1;
            Assert.That(receiver.Receive(conflicting, out _, out _), Is.EqualTo(PrivatePayloadReceiveStatus.Rejected));
            foreach (var part in firstBatch.Skip(1)) Receive(part);
            PrivatePayloadPart last = null;
            while (sender.TryTake(out var part)) { last = part; Receive(part); }
            Assert.That(sender.AcknowledgedBytes, Is.EqualTo(sender.TotalBytes));
            Assert.That(completed, Is.EqualTo(expected));
            Assert.That(receiver.Receive(last, out var duplicateAck, out _), Is.EqualTo(PrivatePayloadReceiveStatus.AlreadyCompleted));
            Assert.That(duplicateAck.ReceivedBytes, Is.EqualTo(sender.TotalBytes));
            var forged = GameStateCloneService.DeepClone(last); forged.Payload[0] ^= 1;
            Assert.That(receiver.Receive(forged, out _, out _), Is.EqualTo(PrivatePayloadReceiveStatus.Rejected));
            var corruptedSender = new PrivatePayloadSendWindow("payload-game", "payload-content", "seat-token", 4, 2, 1, "command", 8, new byte[] { 1, 2, 3 });
            Assert.That(corruptedSender.TryTake(out var clean), Is.True);
            var corrupted = GameStateCloneService.DeepClone(clean); corrupted.Payload[0] ^= 1;
            Assert.That(receiver.Receive(corrupted, out _, out _), Is.EqualTo(PrivatePayloadReceiveStatus.Rejected));
            Assert.That(receiver.Receive(clean, out _, out var verified), Is.EqualTo(PrivatePayloadReceiveStatus.Completed));
            Assert.That(verified, Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void PrivateClientReconnectsWithSamePendingCommandAndCompletesAfterOwnView()
        {
            var state = State(out var registry, new List<string>(), privateRandom: true);
            var round = new RoundExecutionService(registry);
            var started = round.StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            registry.ContentPackHash = state.ContentPackHash;
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            session.BeginPrivateCommandAuthority(false);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            var host = new PrivateHostCoordinator(session, registry, bindings, 1, 1);
            PrivateClientEndpoint Bind(int player, int connection, ulong steam, string token, PrivateClientCommandTracker pending = null)
            {
                Assert.That(bindings.TryBindConnection(connection, steam, out _), Is.True);
                Assert.That(host.TryBind(connection, token), Is.True);
                return new PrivateClientEndpoint(state.GameId, state.ContentPackHash, token, 1, 1, player, state.RuleConfiguration, state.LaunchPlayerOrder, pending);
            }
            void Install(PrivateClientEndpoint client, int connection, string token)
            {
                bool installed = false;
                while (host.TryTakePart(connection, token, out var part))
                {
                    Assert.That(client.ReceiveView(part, out var acknowledgment, out var receipt, out var reason), Is.True, reason);
                    Assert.That(host.TryAcknowledgePart(connection, acknowledgment), Is.True);
                    if (receipt == null) continue;
                    Assert.That(host.TryConfirmInstalled(connection, receipt), Is.True);
                    installed = true;
                }
                Assert.That(installed, Is.True);
            }
            PrivateCommandEnvelope SendCommand(PrivateClientEndpoint client, int player, string token)
            {
                var assembler = new PrivatePayloadAssembler(state.GameId, state.ContentPackHash, token, 1, 1, player, "command");
                byte[] bytes = null;
                while (client.TryTakeCommandPart(out var part))
                {
                    var status = assembler.Receive(part, out var acknowledgment, out var completed);
                    Assert.That(status, Is.Not.EqualTo(PrivatePayloadReceiveStatus.Rejected));
                    Assert.That(client.TryAcknowledgeCommand(acknowledgment), Is.True);
                    if (completed != null) bytes = completed;
                }
                Assert.That(bytes, Is.Not.Null);
                Assert.That(PrivateCommandWireProtocol.TryDecodeCommand(System.Text.Encoding.UTF8.GetString(bytes), out var envelope), Is.True);
                return envelope;
            }
            var one = Bind(1, 11, 111, "one"); var two = Bind(2, 22, 222, "two"); var three = Bind(3, 33, 333, "three");
            Install(one, 11, "one"); Install(two, 22, "two"); Install(three, 33, "three");
            Assert.That(host.Ready, Is.True);
            var visible = one.ReadView();
            var request = visible.Interactions.Single(item => item.AnsweringPlayerId == 1 && item.Status == "open" && item.Kind == Choice);
            var command = new GameCommand { CommandId = "endpoint.pending", Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            command.Parameters["interactionId"] = request.InteractionId;
            command.Parameters["expectedRevision"] = visible.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters["answerValue"] = "true";
            Assert.That(one.TryBeginCommand(command, null, out var beginReason), Is.True, beginReason);
            var envelope = SendCommand(one, 1, "one");
            Assert.That(host.TrySubmitEnvelope(11, envelope, out var committed), Is.True);
            Assert.That(committed.Result.Status, Is.EqualTo(PrivateCommandStatus.Committed));
            Assert.That(one.TryTakeCompleted(out _), Is.False);

            var pending = one.RetainPendingTracker();
            host.Disconnect(11); Assert.That(host.Ready, Is.False);
            var replacement = Bind(1, 111, 111, "one-new", pending);
            Assert.That(replacement.TryPrepareRetry(out _), Is.False);
            Install(replacement, 111, "one-new"); Assert.That(host.Ready, Is.True);
            Assert.That(replacement.TryPrepareRetry(out var retryReason), Is.True, retryReason);
            var retry = SendCommand(replacement, 1, "one-new");
            Assert.That(retry.Proposal.Command.CommandId, Is.EqualTo(command.CommandId));
            Assert.That(host.TrySubmitEnvelope(111, retry, out var duplicate), Is.True);
            Assert.That(duplicate.Result.Status, Is.EqualTo(PrivateCommandStatus.Duplicate));
            var sender = new PrivatePayloadSendWindow(state.GameId, state.ContentPackHash, "one-new", 1, 1, 1,
                "result", 1, PrivateCommandWireProtocol.Encode(duplicate));
            PrivatePayloadPart last = null;
            while (sender.TryTake(out var part))
            {
                last = part;
                Assert.That(replacement.ReceiveResult(part, out var acknowledgment, out var reason), Is.True, reason);
                Assert.That(sender.TryAcknowledge(acknowledgment), Is.True);
            }
            Assert.That(replacement.TryTakeCompleted(out var complete), Is.True);
            Assert.That(complete.GlobalSequence, Is.EqualTo(1));
            Assert.That(replacement.HasPendingCommand, Is.False);
            Assert.That(replacement.ReceiveResult(last, out _, out _), Is.True);
            Assert.That(host.CommittedSequence, Is.EqualTo(1));
        }

        [Test]
        public void PrivateAutomaticSaveCandidateWaitsForAllInstalledViewsAndActualWriteAcknowledgment()
        {
            var state = State(out var registry, new List<string>(), privateRandom: true);
            state.Phase = GamePhase.ActionRound1; state.Round = 1; state.ActionRound = 1;
            state.CurrentPlayerId = state.StartPlayerId = 1;
            registry.ContentPackHash = state.ContentPackHash;
            var round = new RoundExecutionService(registry);
            var started = round.PrepareActionWindowForSmoke(state, 1);
            Assert.That(started.IsValid, Is.True, started.Reason);
            round.MarkMainActionComplete(state, 1);
            var session = new GameSession(state);
            session.RegisterHandler(new EndActionCommandHandler(new RoundAdvanceService(round),
                new YC.Domain.Scoring.FinalScoringService(new MapQueryService(StaticMapDefinitions.CreateFourPlayerMap())), registry, round, round.Advance));
            session.BeginPrivateCommandAuthority(false);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            var host = new PrivateHostCoordinator(session, registry, bindings, 1, 1);
            var clients = new Dictionary<int, PrivateClientEndpoint>();
            void Bind(int player, int connection, ulong steam, string token)
            {
                Assert.That(bindings.TryBindConnection(connection, steam, out _), Is.True);
                Assert.That(host.TryBind(connection, token), Is.True);
                clients[player] = new PrivateClientEndpoint(state.GameId, state.ContentPackHash, token, 1, 1, player, state.RuleConfiguration, state.LaunchPlayerOrder);
            }
            void Install(int player, int connection, string token)
            {
                bool installed = false;
                while (host.TryTakePart(connection, token, out var part))
                {
                    Assert.That(clients[player].ReceiveView(part, out var ack, out var receipt, out var reason), Is.True, reason);
                    Assert.That(host.TryAcknowledgePart(connection, ack), Is.True);
                    if (receipt == null) continue;
                    Assert.That(host.TryConfirmInstalled(connection, receipt), Is.True); installed = true;
                }
                Assert.That(installed, Is.True);
            }
            Bind(1, 11, 111, "one"); Bind(2, 22, 222, "two"); Bind(3, 33, 333, "three");
            Install(1, 11, "one"); Install(2, 22, "two"); Install(3, 33, "three");
            Assert.That(host.HasAutomaticSaveCandidate, Is.False);
            var command = new GameCommand { Kind = GameCommandKind.EndAction, CommandId = "fixture.end-action", PlayerId = 1 };
            Assert.That(clients[1].TryBeginCommand(command, null, out var beginReason), Is.True, beginReason);
            var assembler = new PrivatePayloadAssembler(state.GameId, state.ContentPackHash, "one", 1, 1, 1, "command");
            PrivateCommandEnvelope envelope = null;
            while (clients[1].TryTakeCommandPart(out var part))
            {
                Assert.That(assembler.Receive(part, out var ack, out var bytes), Is.Not.EqualTo(PrivatePayloadReceiveStatus.Rejected));
                Assert.That(clients[1].TryAcknowledgeCommand(ack), Is.True);
                if (bytes != null) Assert.That(PrivateCommandWireProtocol.TryDecodeCommand(System.Text.Encoding.UTF8.GetString(bytes), out envelope), Is.True);
            }
            Assert.That(host.TrySubmitEnvelope(11, envelope, out var result), Is.True);
            Assert.That(result.Result.Status, Is.EqualTo(PrivateCommandStatus.Committed), result.Result.Reason);
            Assert.That(host.HasAutomaticSaveCandidate, Is.True);
            Assert.That(host.AtInstalledBoundary, Is.False);
            Assert.That(host.TryCaptureInstalledBoundary(out _, out _), Is.False);
            Install(1, 11, "one"); Install(2, 22, "two"); Install(3, 33, "three");
            Assert.That(host.AtInstalledBoundary, Is.True);
            Assert.That(host.TryCaptureInstalledBoundary(out var capture, out var captureReason), Is.True, captureReason);
            Assert.That(capture.Archive.Snapshot.State.PrivateCommandLedger.CommittedSequence, Is.EqualTo(1));
            host.ConfirmAutomaticSaveWritten(0);
            Assert.That(host.HasAutomaticSaveCandidate, Is.True);
            host.ConfirmAutomaticSaveWritten(1);
            Assert.That(host.HasAutomaticSaveCandidate, Is.False);
        }

        [Test]
        public void PrivatePresentationInstallsOnlyTheAuthenticatedPlayersView()
        {
            var state = State(out var registry, new List<string>(), privateRandom: true);
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            registry.ContentPackHash = state.ContentPackHash;
            var session = new GameSession(state); session.BeginPrivateCommandAuthority(false);
            var presentation = AppDomain.CurrentDomain.GetAssemblies().First(assembly => assembly.GetName().Name == "Assembly-CSharp");
            var transportType = presentation.GetType("YC.Infrastructure.Multiplayer.MirrorCommandTransport", true);
            var launchType = presentation.GetType("YC.Presentation.GameLaunchContext", true);
            var submissionType = presentation.GetType("YC.Presentation.CommandSubmissionController", true);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            presentation.GetType("Mirror.GeneratedNetworkCode", true).GetMethod("InitReadWriters", flags).Invoke(null, null);
            var transportInstance = transportType.GetProperty("Instance", flags);
            object previousTransport = transportInstance.GetValue(null);
            var root = new UnityEngine.GameObject("私密展示绑定夹具"); root.SetActive(false);
            var transport = root.AddComponent(transportType);
            var launch = root.AddComponent(launchType);
            object controller = null;
            int refreshed = 0;
            try
            {
                transportInstance.GetSetMethod(true).Invoke(null, new[] { transport });
                launchType.GetField("Mode").SetValue(launch, LaunchMode.Host);
                launchType.GetField("LocalPlayerId").SetValue(launch, 1);
                launchType.GetField("GameId").SetValue(launch, state.GameId);
                var seats = state.Players.Select(player => new PlayerSeat { PlayerId = player.PlayerId, SteamId = (ulong)(player.PlayerId * 111), Color = player.Color }).ToList();
                launchType.GetField("Players").SetValue(launch, seats);
                controller = Activator.CreateInstance(submissionType, flags, null, new object[] { session, launch, 1, root,
                    (Action)(() => refreshed++), (Action<string>)(_ => { }), (Action<string>)(_ => { }), null, null }, null);
                submissionType.GetMethod("Initialize", flags, null, new[] { typeof(EffectRegistry) }, null).Invoke(controller, new object[] { registry });
                Assert.That(submissionType.GetProperty("InitializationFailure", flags).GetValue(controller), Is.Null);
                Assert.That(submissionType.GetProperty("UsesNetworkProjection", flags).GetValue(controller), Is.True);
                Assert.That(submissionType.GetProperty("UsesDeterministicSimulation", flags).GetValue(controller), Is.False);
                Assert.That(submissionType.GetProperty("CanSubmitForLocalPlayer", flags).GetValue(controller), Is.False);
                Assert.That(submissionType.GetProperty("AwaitingInitialPrivateProjection", flags).GetValue(controller), Is.True);
                var view = session.ProjectForPlayerWithContent(1, registry);
                Assert.That(submissionType.GetMethod("InstallPrivateView", flags).Invoke(controller, new object[] { view }), Is.True);
                Assert.That(submissionType.GetProperty("AwaitingInitialPrivateProjection", flags).GetValue(controller), Is.False);
                var displayed = (GameState)submissionType.GetProperty("PublishedState", flags).GetValue(controller);
                Assert.That(displayed, Is.Not.Null);
                Assert.That(displayed.RandomRuntime?.PrivateKeyHex, Is.Null);
                Assert.That(displayed.GameId, Is.EqualTo(state.GameId));
                var foreign = GameStateCloneService.DeepClone(view); foreign.ViewerPlayerId = 2;
                Assert.That(submissionType.GetMethod("InstallPrivateView", flags).Invoke(controller, new object[] { foreign }), Is.False);
                Assert.That(submissionType.GetProperty("PublishedState", flags).GetValue(controller), Is.SameAs(displayed));
                Assert.That(refreshed, Is.EqualTo(1));
            }
            finally
            {
                if (controller != null) submissionType.GetMethod("Dispose", flags).Invoke(controller, null);
                transportInstance.GetSetMethod(true).Invoke(null, new[] { previousTransport });
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PrivateHostCheckpointCapturesOnlyAnInstalledBoundaryAndRejectsTampering()
        {
            var state = State(out var registry, new List<string>(), privateRandom: true);
            for (int i = 0; i < state.Players.Count; i++) state.Players[i].Color = (PlayerColor)i;
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            registry.ContentPackHash = state.ContentPackHash;
            var session = new GameSession(state); session.BeginPrivateCommandAuthority(false);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            var host = new PrivateHostCoordinator(session, registry, bindings, 1, 1);
            void BindAndInstall(int player, int connection, ulong steam, string token)
            {
                Assert.That(bindings.TryBindConnection(connection, steam, out _), Is.True);
                Assert.That(host.TryBind(connection, token), Is.True);
                var endpoint = new PrivateClientEndpoint(state.GameId, state.ContentPackHash, token, 1, 1, player, state.RuleConfiguration, state.LaunchPlayerOrder);
                while (host.TryTakePart(connection, token, out var part))
                {
                    Assert.That(endpoint.ReceiveView(part, out var ack, out var receipt, out var reason), Is.True, reason);
                    Assert.That(host.TryAcknowledgePart(connection, ack), Is.True);
                    if (receipt != null) Assert.That(host.TryConfirmInstalled(connection, receipt), Is.True);
                }
            }
            BindAndInstall(1, 11, 111, "one"); BindAndInstall(2, 22, 222, "two");
            Assert.That(host.TryCaptureInstalledBoundary(out _, out _), Is.False);
            BindAndInstall(3, 33, 333, "three");
            Assert.That(host.TryCaptureInstalledBoundary(out var capture, out var captureReason), Is.True, captureReason);
            var header = new MatchSaveData { Mode = LaunchMode.Host, LocalPlayerId = 1, HostSteamId = 111,
                GameId = state.GameId, ContentHash = state.ContentPackHash,
                Seats = state.Players.Select(player => new MatchSaveSeat { PlayerId = player.PlayerId, SteamId = (ulong)(player.PlayerId * 111),
                    PlayerName = player.Name, Color = player.Color }).ToList() };
            var save = MatchSaveData.CreateFromPrivateCaptureAsync(header, capture, 1, 1).GetAwaiter().GetResult();
            Assert.That(save.SimulationCheckpointJson, Is.Null);
            Assert.That(save.PrivateCheckpointJson, Is.Not.Empty);
            Assert.That(save.PrivateCheckpointJson, Does.Not.Contain(state.RandomRuntime.PrivateKeyHex));
            var checkpoint = PrivateHostCheckpoint.Parse(save.PrivateCheckpointJson);
            Assert.That(checkpoint.CommittedSequence, Is.Zero);
            var menuResume = ResumeRoomOptions.FromSave(save);
            Assert.That(menuResume.IsPrivate, Is.True);
            Assert.That(menuResume.MatchesPrivateSave(save), Is.True);
            Assert.That(menuResume.RandomRuntime.PrivateKeyHex, Is.Null);
            Assert.That(menuResume.RandomRuntime.Streams, Is.Empty);
            // 唯一隔离目录内通过正式存档队列实际落盘，再读取并按正式恢复路径校验；不触碰用户槽位。
            string directory = Path.GetFullPath(Path.Combine("TestResults", "Preparation-framework-r1-20261114",
                "private-save-disk-fixture-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            var presentation = AppDomain.CurrentDomain.GetAssemblies().First(assembly => assembly.GetName().Name == "Assembly-CSharp");
            var storeType = presentation.GetType("YC.Infrastructure.Persistence.MatchSaveStore", true);
            var store = Activator.CreateInstance(storeType, new object[] { directory });
            storeType.GetMethod("BeginMatch").Invoke(store, new object[] { state.GameId, true });
            var task = (System.Threading.Tasks.Task)storeType.GetMethod("SavePreparedAsync").Invoke(store, new object[] {
                (Func<System.Threading.Tasks.Task<MatchSaveData>>)(() => System.Threading.Tasks.Task.FromResult(save)),
                state.GameId, state.ContentPackHash, 0, System.Threading.CancellationToken.None });
            task.GetAwaiter().GetResult();
            using var diskValidation = new MatchSaveData.ValidationScope(reuseUnchangedValidation: true);
            var read = (MatchSaveData)storeType.GetMethod("ReadWithValidation").Invoke(store, new object[] { 0, diskValidation });
            Assert.That(read.PrivateCheckpointJson, Is.EqualTo(save.PrivateCheckpointJson));
            var privateResume = ResumeRoomOptions.FromPrivateSave(read);
            var validatedResume = ResumeRoomOptions.FromSaveWithValidation(read, diskValidation);
            Assert.That(privateResume.IsPrivate, Is.True);
            Assert.That(privateResume.MatchesPrivateSave(read), Is.True);
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(validatedResume),
                Is.EqualTo(Newtonsoft.Json.JsonConvert.SerializeObject(privateResume)));
            Assert.That(privateResume.RandomRuntime.PrivateKeyHex, Is.Null);
            Assert.That(privateResume.RandomRuntime.Streams, Is.Empty);
            Assert.Throws<InvalidOperationException>(() => privateResume.Options(true, 1));
            string publicMetadata = Newtonsoft.Json.JsonConvert.SerializeObject(privateResume);
            Assert.That(publicMetadata, Does.Not.Contain(state.RandomRuntime.PrivateKeyHex));
            Assert.That(publicMetadata, Does.Not.Contain(checkpoint.StateDigest));
            var room = new RoomState { RoomId = "private-resume-fixture", HostPlayerId = 1, LocalPlayerId = 1,
                Configuration = privateResume.Configuration.Clone(), Resume = privateResume.Clone() };
            foreach (var seat in privateResume.Seats)
            {
                var slot = room.SeatSlots.Single(value => RoomConfigurationService.GetSeatColor(value.SeatIndex) == seat.Color);
                slot.Status = "occupied"; slot.Occupant = seat.ToSeat();
            }
            string roomJson = RoomStateSerializer.Serialize(room);
            Assert.That(roomJson, Does.Not.Contain(state.RandomRuntime.PrivateKeyHex));
            Assert.That(roomJson, Does.Not.Contain(checkpoint.StateDigest));
            Assert.That(RoomStateSerializer.TryDeserialize(roomJson, out var receivedRoom, out var roomReason), Is.True, roomReason);
            Assert.That(receivedRoom.Resume.MatchesPrivateSave(read), Is.True);
            var changedResume = privateResume.Clone(); changedResume.ConfirmedGlobalSequence++;
            Assert.That(changedResume.MatchesPrivateSave(read), Is.False);
            Assert.Throws<InvalidOperationException>(() => changedResume.Validate());
            var recovery = HostRecoveryService.RecoverJson(read.ArchiveJson, registry);
            Assert.That(recovery.Succeeded, Is.True, recovery.Diagnostic);
            var restored = new GameSession(recovery.State);
            restored.BeginPrivateCommandAuthority(true);
            Assert.That(restored.State.RandomRuntime.PrivateKeyHex, Is.EqualTo(state.RandomRuntime.PrivateKeyHex));
            Assert.That(restored.State.PrivateCommandLedger.CommittedSequence, Is.Zero);
            using var validation = new MatchSaveData.ValidationScope(reuseUnchangedValidation: true);
            validation.Validate(save);
            var original = save.PrivateCheckpointJson;
            var modified = JObject.Parse(original); modified["StateDigest"] = new string('0', 64);
            save.PrivateCheckpointJson = modified.ToString();
            Assert.Throws<InvalidOperationException>(() => validation.Validate(save), "缓存不得忽略被替换的私密检查点。");
            save.PrivateCheckpointJson = original;
            var archive = save.ArchiveJson; save.ArchiveJson += " ";
            Assert.Throws<InvalidOperationException>(() => save.Validate(), "存档原包与检查点必须属于同一固定捕获。");
            save.ArchiveJson = archive; save.Validate();

            // 比较流式私密存档校验与原完整树路径，包括重签归档后的非法字段和账本缺项。
            var oldDefault = Newtonsoft.Json.JsonConvert.DefaultSettings;
            var hash = typeof(PrivateHostCheckpoint).GetMethod("Hash", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            void CheckArchive(string json, bool expected)
            {
                var updated = JObject.Parse(original);
                updated["ArchiveDigest"] = (string)hash.Invoke(null, new object[] { json });
                save.ArchiveJson = json;
                save.PrivateCheckpointJson = updated.ToString(Newtonsoft.Json.Formatting.None);
                bool Accept(bool projected)
                {
                    Newtonsoft.Json.JsonConvert.DefaultSettings = projected ? null : () => new Newtonsoft.Json.JsonSerializerSettings();
                    try { save.Validate(); return true; }
                    catch (Exception) { return false; }
                }
                Assert.That(Accept(true), Is.EqualTo(expected), "流式私密校验结果错误");
                Assert.That(Accept(false), Is.EqualTo(expected), "原完整树校验结果错误");
            }
            try
            {
                CheckArchive(archive + " ", true);
                var extra = JObject.Parse(archive);
                ((JObject)extra["Snapshot"]["State"])["未知字段"] = 1;
                CheckArchive(extra.ToString(Newtonsoft.Json.Formatting.None), false);
                var missing = JObject.Parse(archive);
                ((JObject)missing["Snapshot"]["State"]["PrivateCommandLedger"]).Remove("Receipts");
                CheckArchive(missing.ToString(Newtonsoft.Json.Formatting.None), false);
                string duplicate = archive.Replace("\"EffectRuntime\":{", "\"重复字段\":1,\"重复字段\":2,\"EffectRuntime\":{");
                Assert.That(duplicate, Is.Not.EqualTo(archive));
                CheckArchive(duplicate, false);
            }
            finally
            {
                Newtonsoft.Json.JsonConvert.DefaultSettings = oldDefault;
                save.ArchiveJson = archive; save.PrivateCheckpointJson = original;
            }
        }

        [Test]
        public void PrivateRegionPreviewUsesInstalledAuthenticatedStateWithoutChangingIt()
        {
            var state = State(out var registry, new List<string>(), privateRandom: true);
            Assert.That(new RoundExecutionService(registry).StartEntrance(state).IsValid, Is.True);
            registry.ContentPackHash = state.ContentPackHash;
            var session = new GameSession(state); session.BeginPrivateCommandAuthority(false);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111),
                new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            var host = new PrivateHostCoordinator(session, registry, bindings, 1, 1);
            var revision = state.EffectRuntime.StateRevision;
            void BindAndInstall(int player, int connection, ulong steam, string token)
            {
                Assert.That(bindings.TryBindConnection(connection, steam, out _), Is.True);
                Assert.That(host.TryBind(connection, token), Is.True);
                var client = new PrivateClientEndpoint(state.GameId, state.ContentPackHash, token, 1, 1,
                    player, state.RuleConfiguration, state.LaunchPlayerOrder);
                bool installed = false;
                while (host.TryTakePart(connection, token, out var part))
                {
                    Assert.That(client.ReceiveView(part, out var ack, out var receipt, out var reason), Is.True, reason);
                    Assert.That(host.TryAcknowledgePart(connection, ack), Is.True);
                    if (receipt != null) { Assert.That(host.TryConfirmInstalled(connection, receipt), Is.True); installed = true; }
                }
                Assert.That(installed, Is.True);
            }
            BindAndInstall(1, 11, 111, "one");
            Assert.That(host.TryQueryRegionPreview(11, "one", revision, out _), Is.False, "所有玩家安装视图前不应提供预览。");
            BindAndInstall(2, 22, 222, "two"); BindAndInstall(3, 33, 333, "three");
            var before = RuleStateDigestService.Compute(state);
            Assert.That(host.TryQueryRegionPreview(11, "wrong", revision, out _), Is.False);
            Assert.That(host.TryQueryRegionPreview(11, "one", revision + 1, out _), Is.False);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.That(host.TryQueryRegionPreview(11, "one", revision, out var preview), Is.True);
            watch.Stop(); TestContext.WriteLine("隔离初始局区控预览耗时：{0} 毫秒", watch.ElapsedMilliseconds);
            Assert.That(preview.Regions.Count, Is.EqualTo(StaticMapDefinitions.Resolve(state.MapId).Regions.Count));
            var expectedScore = preview.Regions[0].ScoreValue;
            preview.Regions[0].ScoreValue = int.MinValue;
            Assert.That(host.TryQueryRegionPreview(22, "two", revision, out var other), Is.True);
            Assert.That(other.Regions[0].ScoreValue, Is.EqualTo(expectedScore), "客户端不能修改房主缓存。");
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));
            host.Disconnect(33);
            Assert.That(host.TryQueryRegionPreview(11, "one", revision, out _), Is.False);
        }

        [Test]
        public void PrivatePaymentChannelWaitsForAllSeatsAndReturnsOnlyTheQuerierResult()
        {
            var state = State(out var registry, new List<string>(), privateRandom: true);
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            registry.ContentPackHash = state.ContentPackHash;
            var session = new GameSession(state); session.ConfigurePaymentSourceQueries(registry); session.BeginPrivateCommandAuthority(false);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            var host = new PrivateHostCoordinator(session, registry, bindings, 1, 1);
            void BindAndInstall(int player, int connection, ulong steam, string token)
            {
                Assert.That(bindings.TryBindConnection(connection, steam, out _), Is.True);
                Assert.That(host.TryBind(connection, token), Is.True);
                var client = new PrivateClientEndpoint(state.GameId, state.ContentPackHash, token, 1, 1, player, state.RuleConfiguration, state.LaunchPlayerOrder);
                bool installed = false;
                while (host.TryTakePart(connection, token, out var part))
                {
                    Assert.That(client.ReceiveView(part, out var ack, out var receipt, out var reason), Is.True, reason);
                    Assert.That(host.TryAcknowledgePart(connection, ack), Is.True);
                    if (receipt == null) continue;
                    Assert.That(host.TryConfirmInstalled(connection, receipt), Is.True); installed = true;
                }
                Assert.That(installed, Is.True);
            }
            BindAndInstall(1, 11, 111, "one"); BindAndInstall(2, 22, 222, "two"); BindAndInstall(3, 33, 333, "three");
            var channel = new PrivateHostPaymentChannel(host, 11, state.GameId, state.ContentPackHash, "one", 1, 1, 1);
            var clientPayment = new PrivateClientPaymentChannel(state.GameId, state.ContentPackHash, "one", 1, 1, 1);
            var query = new PaymentSourceQuery { PlayerId = 1, StateRevision = state.EffectRuntime.StateRevision, PaymentGeneration = 1,
                OwnerEffectId = "fixture.not-open", InteractionId = "fixture.not-open", SnapshotId = "fixture.not-open" };
            string before = RuleStateDigestService.Compute(state);
            Assert.That(clientPayment.Read(query), Is.Null);
            host.Disconnect(33);
            while (clientPayment.TryTakeRequest(out var part))
            {
                var wrong = GameStateCloneService.DeepClone(part); wrong.PlayerId = 2;
                Assert.That(channel.Receive(wrong, out _), Is.EqualTo(PrivateHostCommandReceiveStatus.Rejected));
                Assert.That(channel.Receive(part, out var ack), Is.EqualTo(PrivateHostCommandReceiveStatus.Accepted));
                Assert.That(clientPayment.Acknowledge(ack), Is.True);
            }
            Assert.That(channel.HasPending, Is.True);
            Assert.That(channel.Pump(), Is.True);
            Assert.That(channel.TryTakeResult(out _), Is.False);
            BindAndInstall(3, 333, 333, "three-new");
            Assert.That(channel.Pump(), Is.True, channel.Fault);
            bool changed = false;
            while (channel.TryTakeResult(out var part))
            {
                Assert.That(clientPayment.Receive(part, out var ack, out bool complete), Is.True, clientPayment.Failure);
                Assert.That(channel.Acknowledge(ack), Is.True); changed |= complete;
            }
            Assert.That(changed, Is.True);
            Assert.That(clientPayment.Failure, Is.Not.Empty, "不存在的付款请求只能得到明确失败，不能伪造空页。");
            Assert.That(RuleStateDigestService.Compute(state), Is.EqualTo(before));
            Assert.That(host.CommittedSequence, Is.Zero);
        }

        [Test]
        public void PrivateOrdinaryCommandWithMultipleVisibleWindowsIsResolvedAgainstCurrentHostState()
        {
            var state = State(out var registry, new List<string>(), privateRandom: true);
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            registry.ContentPackHash = state.ContentPackHash;
            var session = new GameSession(state); session.RegisterHandler(new WindowFixtureHandler());
            session.BeginPrivateCommandAuthority(false);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            Assert.That(bindings.TryBindConnection(11, 111, out _), Is.True);
            var publisher = new PrivatePlayerViewPublisher(session, registry, bindings, 1, 1);
            Assert.That(publisher.TryBind(11, "one"), Is.True);
            Assert.That(publisher.TryCreate(11, "one", 0, out var snapshot, out var reason), Is.True, reason);
            var request = snapshot.View.Interactions.Single(item => item.AnsweringPlayerId == 1 && item.Status == "open" && item.Kind == Choice);
            var actual = snapshot.Windows.Single(window => window.Requests.Any(item => item.InteractionId == request.InteractionId));
            // 仅在隔离投影中模拟第二个本人窗口；房主仍依实际请求解析，不凭客户端决定目标。
            snapshot.Windows.Add(new PrivateOperationWindowView { OperationWindowId = "fixture.other-window", WindowKind = "fixture", WindowGeneration = 1 });
            var receiver = new PrivatePlayerViewReceiver(state.GameId, state.ContentPackHash, 1, 1, "one", state.RuleConfiguration, state.LaunchPlayerOrder);
            Assert.That(receiver.Receive(snapshot, true, out reason), Is.EqualTo(PrivatePlayerViewReceiveStatus.Applied), reason);
            var command = new GameCommand { Kind = GameCommandKind.PreparePaymentDraft, CommandId = "fixture.ordinary", PlayerId = 1 };
            command.Parameters["interactionId"] = request.InteractionId;
            Assert.That(receiver.TryBuildProposal(command, null, out var proposal, out reason), Is.True, reason);
            Assert.That(proposal.OperationWindowId, Is.EqualTo(PrivateCommandWindowSelection.HostResolvedId));
            var forbidden = GameStateCloneService.DeepClone(proposal);
            forbidden.Command.CommandId = "fixture.answer"; forbidden.Command.Kind = GameCommandKind.AnswerInteraction;
            Assert.That(publisher.TrySubmit(11, "one", forbidden, out var answer), Is.True);
            Assert.That(answer.Status, Is.EqualTo(PrivateCommandStatus.Rejected));
            Assert.That(answer.Reason, Is.EqualTo("private_command_window_resolution_not_allowed"));
            Assert.That(publisher.TrySubmit(11, "one", proposal, out var committed), Is.True);
            Assert.That(committed.Status, Is.EqualTo(PrivateCommandStatus.Committed), committed.Reason);
            Assert.That(state.PrivateCommandLedger.Receipts.Single().OperationWindowId, Is.EqualTo(actual.OperationWindowId));
            var stale = GameStateCloneService.DeepClone(proposal);
            stale.ClientSubmissionSequence = 2; stale.Command.CommandId = "fixture.stale";
            Assert.That(publisher.TrySubmit(11, "one", stale, out var rejected), Is.True);
            Assert.That(rejected.Status, Is.EqualTo(PrivateCommandStatus.Rejected));
            Assert.That(rejected.Reason, Is.EqualTo("private_command_observation_stale"));
            Assert.That(state.PrivateCommandLedger.Receipts, Has.Count.EqualTo(1));
        }

        [Test]
        public void PrivateViewInstallationReceiptCanBeRetriedAfterAllBytesWereAcknowledged()
        {
            var state = State(out var registry, new List<string>(), privateRandom: true);
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            registry.ContentPackHash = state.ContentPackHash;
            var session = new GameSession(state);
            session.BeginPrivateCommandAuthority(false);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            Assert.That(bindings.TryBindConnection(11, 111, out _), Is.True);
            var host = new PrivateHostCoordinator(session, registry, bindings, 1, 1);
            Assert.That(host.TryBind(11, "one"), Is.True);
            var client = new PrivateClientEndpoint(state.GameId, state.ContentPackHash, "one", 1, 1, 1, state.RuleConfiguration, state.LaunchPlayerOrder);
            PrivateViewInstalledReceipt lost = null;
            while (host.TryTakePart(11, "one", out var part))
            {
                Assert.That(client.ReceiveView(part, out var ack, out var receipt, out var reason), Is.True, reason);
                Assert.That(host.TryAcknowledgePart(11, ack), Is.True);
                if (receipt != null) lost = receipt;
            }
            Assert.That(lost, Is.Not.Null);
            Assert.That(host.ReadyPlayers, Is.Zero);
            Assert.That(host.ReadUnacknowledged(11, "one"), Is.Empty);
            var probe = host.ReadInstallationProbe(11, "one");
            Assert.That(probe, Is.Not.Null);
            Assert.That(client.ReceiveView(probe, out var repeatAck, out var repeatReceipt, out var retryReason), Is.True, retryReason);
            Assert.That(host.TryAcknowledgePart(11, repeatAck), Is.True);
            Assert.That(host.TryConfirmInstalled(11, repeatReceipt), Is.True);
            Assert.That(host.ReadyPlayers, Is.EqualTo(1));
            Assert.That(host.ReadInstallationProbe(11, "one"), Is.Null);
        }

        [Test]
        public void PrivateCommandChannelWaitsForDisconnectedSeatAndDeliversOneResult()
        {
            var state = State(out var registry, new List<string>(), privateRandom: true);
            var round = new RoundExecutionService(registry);
            var started = round.StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            registry.ContentPackHash = state.ContentPackHash;
            var session = new GameSession(state);
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry, round));
            session.BeginPrivateCommandAuthority(false);
            var bindings = new SteamIdentityBindingRegistry();
            bindings.ReplaceLobbySeats(new[] { new KeyValuePair<int, ulong>(1, 111), new KeyValuePair<int, ulong>(2, 222), new KeyValuePair<int, ulong>(3, 333) });
            var host = new PrivateHostCoordinator(session, registry, bindings, 1, 1);
            PrivateClientEndpoint Bind(int player, int connection, ulong steam, string token)
            {
                Assert.That(bindings.TryBindConnection(connection, steam, out _), Is.True);
                Assert.That(host.TryBind(connection, token), Is.True);
                return new PrivateClientEndpoint(state.GameId, state.ContentPackHash, token, 1, 1, player, state.RuleConfiguration, state.LaunchPlayerOrder);
            }
            void Install(PrivateClientEndpoint client, int connection, string token)
            {
                bool installed = false;
                while (host.TryTakePart(connection, token, out var part))
                {
                    Assert.That(client.ReceiveView(part, out var ack, out var receipt, out var reason), Is.True, reason);
                    Assert.That(host.TryAcknowledgePart(connection, ack), Is.True);
                    if (receipt == null) continue;
                    Assert.That(host.TryConfirmInstalled(connection, receipt), Is.True);
                    installed = true;
                }
                Assert.That(installed, Is.True);
            }
            var one = Bind(1, 11, 111, "one"); var two = Bind(2, 22, 222, "two"); var three = Bind(3, 33, 333, "three");
            Install(one, 11, "one"); Install(two, 22, "two"); Install(three, 33, "three");
            var channel = new PrivateHostCommandChannel(host, 11, state.GameId, state.ContentPackHash, "one", 1, 1, 1);
            var view = one.ReadView();
            var request = view.Interactions.Single(item => item.AnsweringPlayerId == 1 && item.Status == "open" && item.Kind == Choice);
            var command = new GameCommand { CommandId = "channel.paused", Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            command.Parameters["interactionId"] = request.InteractionId;
            command.Parameters["expectedRevision"] = view.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters["answerValue"] = "true";
            Assert.That(one.TryBeginCommand(command, null, out var beginReason), Is.True, beginReason);
            host.Disconnect(33);
            Assert.That(host.Ready, Is.False);
            PrivatePayloadPart last = null;
            while (one.TryTakeCommandPart(out var part))
            {
                last = part;
                Assert.That(channel.Receive(part, out var ack), Is.EqualTo(PrivateHostCommandReceiveStatus.Accepted));
                Assert.That(one.TryAcknowledgeCommand(ack), Is.True);
            }
            Assert.That(last, Is.Not.Null);
            Assert.That(channel.Pump(), Is.True);
            Assert.That(channel.HasPendingCommand, Is.True);
            Assert.That(host.CommittedSequence, Is.Zero);
            var next = GameStateCloneService.DeepClone(last); next.TransferId++;
            Assert.That(channel.Receive(next, out var blockedAck), Is.EqualTo(PrivateHostCommandReceiveStatus.Backpressured));
            Assert.That(blockedAck, Is.Null);
            var forged = GameStateCloneService.DeepClone(last); forged.ConnectionToken = "another-seat";
            Assert.That(channel.Receive(forged, out _), Is.EqualTo(PrivateHostCommandReceiveStatus.Rejected));
            three = Bind(3, 333, 333, "three-new"); Install(three, 333, "three-new");
            Assert.That(host.Ready, Is.True);
            Assert.That(channel.Pump(), Is.True, channel.Fault);
            Assert.That(host.CommittedSequence, Is.EqualTo(1));
            Assert.That(channel.HasPendingCommand, Is.False);
            while (channel.TryTakeResult(out var resultPart))
            {
                Assert.That(one.ReceiveResult(resultPart, out var resultAck, out var reason), Is.True, reason);
                Assert.That(channel.TryAcknowledgeResult(resultAck), Is.True);
            }
            Assert.That(one.TryTakeCompleted(out _), Is.False);
            Install(one, 11, "one");
            Assert.That(one.TryTakeCompleted(out var result), Is.True);
            Assert.That(result.Status, Is.EqualTo(PrivateCommandStatus.Committed));
            Assert.That(channel.Receive(last, out var duplicateAck), Is.EqualTo(PrivateHostCommandReceiveStatus.Accepted));
            Assert.That(duplicateAck, Is.Not.Null);
            Assert.That(channel.Pump(), Is.True);
            Assert.That(host.CommittedSequence, Is.EqualTo(1));
        }

        [Test]
        public void CompletedBeforeEffectDeterminesFiveEnterprisesAndRestoresWithoutRedrawing()
        {
            const string handler = "fixture.output_handler", effect = "fixture.output_effect", requestType = "fixture.output_request";
            var state = State(out var registry, new List<string>());
            var step = Step("choose_pool"); step.HandlerId = handler;
            var plan = state.SetupRecipe.Preparation;
            plan.BeforeDistribution = new List<PreparationStepDeclaration> { step };
            plan.DistributionInputs = new List<PreparationOutputBinding> {
                new PreparationOutputBinding { StepId = step.StepId, EffectTypeId = effect, EffectStableKey = "pool:0",
                    ResultField = "definitions", PlayerScope = "once", Target = "candidateDefinitionIds" },
                new PreparationOutputBinding { StepId = step.StepId, EffectTypeId = effect, EffectStableKey = "pool:0",
                    ResultField = "extra", PlayerScope = "once", Target = "extraRandomCount" } };
            registry.SetupRecipe = GameStateCloneService.DeepClone(state.SetupRecipe);
            registry.RegisterPreparationStep(new PreparationStepRegistration(step, (context, args) => {
                var output = EffectSpec.Create(effect, playerId: context.Node.PlayerId); output.StableKey = "pool:0";
                return new List<EffectSpec> { output };
            }));
            registry.Register(effect, context => {
                if (context.GetLatestInteractionAnswer() != null)
                    return EffectStepResult.Completed(Object("definitions", Strings(Enumerable.Range(0, 8).Select(index => "fixture.company." + index)),
                        "extra", NormalizedValue.CreateInteger(2)));
                var interaction = new EffectInteractionSpec { InteractionTypeId = requestType, AnsweringPlayerId = context.Node.PlayerId,
                    Visibility = "owner", AnswerSchema = "candidate_ids", MinSelections = 1, MaxSelections = 1 };
                interaction.CandidateIds.Add("confirm");
                return EffectStepResult.Continue("waiting").AddInteraction(interaction);
            });
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(started.IsValid, Is.True, started.Reason);
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("before"));
            Assert.That(state.PreparationRuntime.Distribution, Is.Null);
            Answer(state, registry, Request(state, 1, requestType), NormalizedValue.CreateString("confirm"));
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("middle"));
            Assert.That(state.PreparationRuntime.EffectiveEnterprise.Pool.Kind, Is.EqualTo("fixed"));
            Assert.That(state.PreparationRuntime.EffectiveEnterprise.PlayerCounts.Single().FinalCount, Is.EqualTo(5));
            var drawn = state.PreparationRuntime.Distribution.Assignments.SelectMany(item => item.CandidateItemIds).ToArray();
            var resumed = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(resumed.Succeeded, Is.True, resumed.Diagnostic);
            state = resumed.State;
            Assert.That(state.PreparationRuntime.Distribution.Assignments.SelectMany(item => item.CandidateItemIds), Is.EqualTo(drawn));
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            foreach (int player in new[] { 1, 2, 3 })
            {
                var choice = Request(state, player, ExpansionSetupFlow.EnterpriseChoiceInteraction);
                Answer(state, registry, choice, NormalizedValue.CreateString(choice.CandidateIds[0]));
            }
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("after"));
            string zone = FindZone(state, "enterprises").ZoneId;
            var boards = state.ItemZones.Items.Where(item => item.ZoneId == zone).ToArray();
            Assert.That(boards.Length, Is.EqualTo(5));
            Assert.That(state.ItemZones.Zones.Count(item => item.TemplateId == "core.zone.board_marker_track" && boards.Any(board => board.ItemId == item.ParentId)), Is.EqualTo(15));
            resumed = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(resumed.Succeeded, Is.True, resumed.Diagnostic);
            Assert.That(resumed.State.PreparationRuntime.Distribution.Assignments.SelectMany(item => item.CandidateItemIds), Is.EqualTo(drawn));
            PlayerEntranceEffectExecutor.Register(registry, new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)));
            Answer(state, registry, Request(state, 1, Choice), NormalizedValue.CreateBoolean(true));
            var rotated = GameStateCloneService.DeepClone(state);
            rotated.EffectRuntime.PlayerOrderSnapshot = new List<int> { 2, 3, 1 };
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(rotated, registry, out var rotationReason), Is.True,
                "复核前置输出时也必须使用原准备顺序：" + rotationReason);
            var altered = GameStateCloneService.DeepClone(state);
            altered.PreparationRuntime.EffectiveEnterprise.PlayerCounts.Single().ExtraRandom = 1;
            Assert.That(PreparationFrameworkFlow.TryValidateRuntime(altered, registry, out _), Is.False);
        }

        [TestCase(7, true)]
        [TestCase(6, false)]
        public void BeforeOutputIsValidatedAgainstTheActualPlayerCount(int supplied, bool sufficient)
        {
            const string handler = "fixture.player_count_pool", effect = "fixture.player_count_result";
            var state = State(out var registry, new List<string>());
            var plan = state.SetupRecipe.Preparation;
            plan.Enterprise.PlayerCounts.Add(new PreparationEnterpriseCountRule { PlayerCount = 2, Draw = 4, Keep = 2, FinalCount = 4 });
            plan.Enterprise.PlayerCounts.Add(new PreparationEnterpriseCountRule { PlayerCount = 4, Draw = 2, Keep = 1, FinalCount = 4 });
            var step = Step("choose_seven"); step.HandlerId = handler;
            plan.BeforeDistribution = new List<PreparationStepDeclaration> { step };
            plan.DistributionInputs = new List<PreparationOutputBinding> { new PreparationOutputBinding {
                StepId = step.StepId, EffectTypeId = effect, EffectStableKey = "pool:seven", ResultField = "definitions",
                PlayerScope = "once", Target = "candidateDefinitionIds" } };
            registry.SetupRecipe = GameStateCloneService.DeepClone(state.SetupRecipe);
            registry.RegisterPreparationStep(new PreparationStepRegistration(step, (context, args) => {
                var result = EffectSpec.Create(effect, playerId: context.Node.PlayerId); result.StableKey = "pool:seven";
                return new List<EffectSpec> { result };
            }));
            registry.Register(effect, context => EffectStepResult.Completed(Object("definitions",
                Strings(Enumerable.Range(0, supplied).Select(index => "fixture.company." + index)))));
            var started = new RoundExecutionService(registry).StartEntrance(state);
            Assert.That(plan.Enterprise.PlayerCounts.Select(rule => rule.PlayerCount), Is.EquivalentTo(new[] { 2, 3, 4 }));
            if (!sufficient)
            {
                Assert.That(started.IsValid, Is.False);
                Assert.That(started.Reason, Does.Contain("preparation_enterprise_count_invalid:3"));
                return;
            }
            Assert.That(started.IsValid, Is.True, started.Reason);
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), state.EffectRuntime.LastFaultMessage);
            Assert.That(state.PreparationRuntime.Stage, Is.EqualTo("middle"));
            Assert.That(state.PreparationRuntime.EffectiveEnterprise.PlayerCounts.Select(rule => rule.PlayerCount), Is.EqualTo(new[] { 3 }));
            Assert.That(state.PreparationRuntime.Distribution.Assignments.SelectMany(assignment => assignment.CandidateItemIds).Count(), Is.EqualTo(6));
            Assert.That(state.PreparationRuntime.Distribution.ExtraItemIds, Has.Count.EqualTo(1));
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.PreparationRuntime.Distribution.Assignments.SelectMany(assignment => assignment.CandidateItemIds),
                Is.EqualTo(state.PreparationRuntime.Distribution.Assignments.SelectMany(assignment => assignment.CandidateItemIds)));
        }
    }
}
