using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离规则验证；不计正常菜单开局或界面验收，不生成/覆盖任何UI资产。
    public sealed class ResourceLossAndPassengerStrategyTests
    {
        private const string Ability = "operator.passenger.strategy";
        private const string Completion = "content.operator.passenger.strategy.step_completed";
        private ExternalContentPack pack;
        private EffectRegistry registry;
        private GameState state;

        [SetUp]
        public void SetUp()
        {
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var configuration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise" } };
            pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"))
                .CreateActiveSnapshot(configuration);
            configuration.ContentPackHash = pack.ContentHash;
            Assert.That(pack.ActiveDefinitions.Any(definition => definition.DefinitionId == "operator.passenger"), Is.True,
                "企业扩活动快照缺少异客定义。");
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            ResourceEffectExecutor.Register(registry);
            GenericLuaEffectExecutor.Register(registry);
            ContentPrimitiveEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry, pack);
            Assert.That(registry.HasEventHandler("content.operator.passenger.strategy"), Is.True,
                "企业扩活动快照未注册异客策略处理器。");
            state = new ItemZoneStateFactory().CreateForSetup(new GameState
            {
                GameId = "game.loss_passenger.isolated", MapId = map.MapId, Round = 1, ActionRound = 1,
                CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1, ContentPackHash = pack.ContentHash,
                RuleConfiguration = configuration,
                ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = pack.CreateFacilities().ToList() },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Resources = new ResourceSet() },
                    new PlayerState { PlayerId = 2, Resources = new ResourceSet { Iron = 3 } } }
            }, map, Array.Empty<ItemRecipeEntry>());
        }

        private EffectTreeExecutor Engine() => new EffectTreeExecutor(state, registry);
        private void Drain() => Assert.That(Engine().RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        private string Run(EffectSpec spec) { string id = Engine().CreateRoot(spec); Drain(); return id; }
        private InteractionRequest Open() => state.EffectRuntime.InteractionRequests.Last(request => request.Status == "open");
        private void Answer(string value)
        {
            var request = Open();
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(value), out string reason), Is.True, reason);
            Drain();
        }
        private void RestoreJson()
        {
            var recovered = HostRecoveryService.RecoverJson(
                HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            state = recovered.State;
            Drain();
        }
        private static EffectSpec Loss(int amount, string resource = "iron", int target = 2) => EffectSpec.Create(ResourceLossEffectExecutor.TypeId,
            Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateStableReference("player", "1"),
                "targetPlayer", NormalizedValue.CreateStableReference("player", target.ToString()),
                "resourceType", NormalizedValue.CreateString(resource), "amount", NormalizedValue.CreateInteger(amount)), 1);

        [TestCase(7, 4, 4, 3)]
        [TestCase(1, 4, 1, 0)]
        [TestCase(0, 4, 0, 0)]
        [TestCase(3, 0, 0, 3)]
        [TestCase(3, (int)NormalizedValue.MaxInteger, 3, 0)]
        public void LossReportsActualAmountWithoutPaymentOrReceiver(int balance, int requested, int actual, int remaining)
        {
            if (requested == NormalizedValue.MaxInteger)
            {
                int invalid = checked((int)NormalizedValue.MaxInteger + 1);
                Assert.That(() => Loss(invalid), Throws.TypeOf<ArgumentOutOfRangeException>(), "规范整数上界之外必须明确拒绝。");
                Assert.That(Invoke("return function(ctx) return {Effect.LoseResource({apiVersion=1,player=ctx.playerId,targetPlayer='p2',resourceType='iron',amount=" + invalid + "})} end").IsSuccess,
                    Is.False, "Lua不能接受规范整数上界之外的请求量。");
            }
            state.FindPlayer(2).Resources.Iron = balance;
            string id = Run(Loss(requested));
            var node = Engine().GetNode(id);
            Assert.That(registry.Get(ResourceLossEffectExecutor.TypeId).ExecutorKind, Is.EqualTo(EffectExecutorKind.Atomic));
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Number(node.NormalizedResult, "requestedAmount"), Is.EqualTo(requested));
            Assert.That(Number(node.NormalizedResult, "amount"), Is.EqualTo(actual));
            Assert.That(Flag(node.NormalizedResult, "changed"), Is.EqualTo(actual > 0));
            Assert.That(Number(node.NormalizedResult, "targetPlayer"), Is.EqualTo(2));
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(remaining));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(state.EffectRuntime.EffectNodes.All(effect => effect.Payment == null), Is.True);
            RestoreJson();
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(remaining));
        }

        [TestCase("Originium", ResourceType.Originium)]
        [TestCase("originium-shard", ResourceType.OriginiumShard)]
        [TestCase("iron", ResourceType.Iron)]
        [TestCase("pure_originium", ResourceType.PureOriginium)]
        [TestCase("GoldVoucher", ResourceType.GoldVoucher)]
        public void LossSupportsEveryRegisteredResource(string resource, ResourceType type)
        {
            state.FindPlayer(2).Resources.Set(type, 2);
            string id = Run(Loss(3, resource));
            Assert.That(state.FindPlayer(2).Resources.Get(type), Is.Zero);
            Assert.That(Number(Engine().GetNode(id).NormalizedResult, "amount"), Is.EqualTo(2));
        }

        [Test]
        public void GoldLossKeepsRealHeldTollReceiptAndOnlyDebitsAvailableWallet()
        {
            state.FindPlayer(1).Resources.GoldVoucher = 4;
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "selection", PlayerId = 1 });
            var owner = new EffectNodeRuntimeState { EffectId = "escrow", ParentEffectId = "selection", PlayerId = 1,
                EffectTypeId = ExplorationEffectTypeIds.Explore, DefinitionVersion = "1.0.0", Status = EffectNodeStatus.Ready,
                NormalizedArguments = Object("paymentProtocol", NormalizedValue.CreateString(RouteTollEscrowService.Protocol),
                    "selectionOwnerEffectId", NormalizedValue.CreateStableReference("effect", "selection"),
                    "selectionSessionId", NormalizedValue.CreateString("session"), "selectionRevision", NormalizedValue.CreateInteger(0)) };
            state.EffectRuntime.EffectNodes.Add(owner);
            var escrow = new RouteTollEscrowService(new MapQueryService(StaticMapDefinitions.CreateThreePlayerMap()));
            var plan = NormalizedValue.CreateArray(new[] { Object("routeId", NormalizedValue.CreateStableReference("route", "A1"),
                "receiverSubjectKey", NormalizedValue.CreateString("bank"), "amount", NormalizedValue.CreateInteger(2)) });
            Assert.That(escrow.TryReserveValidatedPlan(state, owner, "selection", "session", 0, 4, plan, out _, out _).IsValid, Is.True);
            string receipt = JsonConvert.SerializeObject(owner.ContinuationData);
            var loss = new EffectNodeRuntimeState { EffectId = "gold-loss", PlayerId = 1, NormalizedArguments = Loss(99, "gold_voucher", 1).NormalizedArguments };
            state.EffectRuntime.EffectNodes.Add(loss);
            // 直接验证原子执行器，不驱动此隔离托管owner的其他业务阶段。
            var context = (EffectExecutionContext)Activator.CreateInstance(typeof(EffectExecutionContext), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { state, loss, registry, null }, null);
            var result = registry.Get(ResourceLossEffectExecutor.TypeId).Executor(context);
            Assert.That(Number(result.NormalizedResult, "amount"), Is.EqualTo(2));
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Available, Is.Zero);
            Assert.That(RouteTollEscrowService.QueryBalance(state, 1).Reserved, Is.EqualTo(2));
            Assert.That(JsonConvert.SerializeObject(owner.ContinuationData), Is.EqualTo(receipt));
        }

        [TestCase("-1")]
        [TestCase("0.5")]
        [TestCase("2147483648")]
        public void LossLuaRejectsInvalidAmount(string amount)
        {
            Assert.That(Invoke("return function(ctx) return {Effect.LoseResource({apiVersion=1,player=ctx.playerId,targetPlayer='p2',resourceType='iron',amount=" + amount + "})} end").IsSuccess, Is.False);
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(3));
        }

        [TestCase("targetPlayer='p999',resourceType='iron'")]
        [TestCase("targetPlayer='p2',resourceType='0'")]
        [TestCase("targetPlayer='p2',resourceType='unknown'")]
        public void LossLuaRejectsUnknownTargetOrResource(string fields)
        {
            Assert.That(Invoke("return function(ctx) return {Effect.LoseResource({apiVersion=1,player=ctx.playerId," + fields + ",amount=1})} end").IsSuccess, Is.False);
        }

        private void ActivatePassenger()
        {
            const string type = "test.passenger.activation";
            registry.Register(new EffectRegistration(type, context => EffectStepResult.Completed().AddEvent(new EffectEventRequest
            { EventType = CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType, RouteKey = Ability,
                PlayerId = context.Node.PlayerId, Payload = Object() })));
            Run(EffectSpec.Create(type, Object(), 1));
        }

        [TestCase(0)]
        [TestCase(2)]
        public void FormalStrategySelectsOpponentResourceAmountAndAwardsEachOnceAcrossRestore(int amount)
        {
            ActivatePassenger();
            Assert.That(Open().CandidateIds, Is.EqualTo(new[] { "player:2" }));
            AssertUncommitted(); RestoreJson();
            Answer("player:2");
            Assert.That(Open().CandidateIds, Is.EqualTo(new[] { "originium", "originium-shard", "iron" }));
            AssertUncommitted(); RestoreJson();
            Answer("iron");
            Assert.That(Open().InteractionTypeId, Is.EqualTo(ContentPrimitiveEffectExecutor.QuantityInteractionTypeId));
            Assert.That(Open().AnswerSchema, Is.EqualTo(ContentPrimitiveEffectExecutor.QuantityAnswerSchema));
            Assert.That(Open().CandidateIds, Is.Empty);
            Assert.That(Number(Open().PromptParameters, "maxAmount"), Is.EqualTo(3));
            Assert.That(Value(Open().PromptParameters, "rowIds").Items.Single().StringValue, Is.EqualTo("iron"));
            Assert.That(Value(Open().PromptParameters, "rowMaximums").Items.Single().IntegerValue, Is.EqualTo(3));
            AssertUncommitted(); RestoreJson();
            Answer(amount.ToString());
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open"), Is.False);
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(3 - amount));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(amount));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1));
            Assert.That(state.FindPlayer(2).Score, Is.EqualTo(1));
            var loss = state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == ResourceLossEffectExecutor.TypeId);
            Assert.That(Number(loss.NormalizedResult, "amount"), Is.EqualTo(amount));
            Assert.That(loss.NormalizedArguments.Properties.Select(entry => entry.Name), Does.Not.Contain("completionHandlerId"));
            Assert.That(loss.NormalizedArguments.Properties.Select(entry => entry.Name), Is.EquivalentTo(new[] { "apiVersion", "player", "targetPlayer", "resourceType", "amount" }));
            var gains = state.EffectRuntime.EffectNodes.Where(node => node.EffectTypeId == ResourceEffectTypeIds.Gain).ToList();
            Assert.That(gains.Count, Is.EqualTo(amount > 0 ? 1 : 0));
            if (amount > 0) Assert.That(Number(gains.Single().NormalizedArguments, "amount"), Is.EqualTo(Number(loss.NormalizedResult, "amount")));
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.EffectTypeId == ResourceEffectTypeIds.GainScore), Is.EqualTo(2));
            RestoreJson();
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(amount));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(1)); Assert.That(state.FindPlayer(2).Score, Is.EqualTo(1));
        }

        private void AssertUncommitted()
        {
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(state.Players.All(player => player.Score == 0), Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == ResourceLossEffectExecutor.TypeId), Is.False);
        }

        [Test]
        public void AmountRangeRejectsExcessAndRefreshesChangedStockWithoutTruncating()
        {
            ActivatePassenger(); Answer("player:2"); Answer("iron");
            var request = Open();
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision - 1,
                NormalizedValue.CreateString("1"), out _), Is.False);
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("4"), out _), Is.False);
            state.FindPlayer(2).Resources.Iron = 1;
            Answer("3");
            Assert.That(Number(Open().PromptParameters, "maxAmount"), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(1));
            Assert.That(state.Players.All(player => player.Score == 0), Is.True);
            Answer("1");
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.Zero);
        }

        [Test]
        public void StrategyCapsLargeInventoryAtFour()
        {
            state.FindPlayer(2).Resources.Iron = 7;
            ActivatePassenger(); Answer("player:2"); Answer("iron");
            Assert.That(Number(Open().PromptParameters, "maxAmount"), Is.EqualTo(4));
            var request = Open();
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("5"), out _), Is.False);
            Answer("4");
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(4));
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(3));
            Assert.That(state.Players.All(player => player.Score == 1), Is.True);
        }

        [Test]
        public void ZeroStockKeepsRealQuantityStepAndBothScores()
        {
            state.FindPlayer(2).Resources.Iron = 0;
            ActivatePassenger(); Answer("player:2"); Answer("iron");
            Assert.That(Number(Open().PromptParameters, "minAmount"), Is.Zero);
            Assert.That(Number(Open().PromptParameters, "maxAmount"), Is.Zero);
            Answer("0");
            Assert.That(state.Players.All(player => player.Score == 1), Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == ResourceEffectTypeIds.Gain), Is.False);
        }

        [Test]
        public void DefaultChoiceKeepsItsStringResultAndOrdinaryPayStillRequiresEnough()
        {
            string id = Run(EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString("1"),
                "options", Strings(new[] { "iron" })), 1));
            Answer("iron"); Assert.That(Engine().GetNode(id).NormalizedResult.StringValue, Is.EqualTo("iron"));
            string pay = Run(ResourceEffectSpecFactory.Pay(1, ResourceType.Iron, 1));
            Assert.That(Engine().GetNode(pay).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
        }

        [Test]
        public void ExplicitBindingRejectsArbitraryForeignAbilityAndNestedHandlers()
        {
            var definition = pack.ActiveDefinitions.Single(content => content.DefinitionId == "operator.passenger").Abilities.Single(ability => ability.AbilityId == Ability);
            string source = pack.GetScript(definition.Script);
            var script = new LuaScriptDefinition("content." + definition.SubscriptionId, Ability, definition.SubscriptionId,
                definition.Version, source, LuaContentHasher.ComputeSha256(source));
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(script, Context(definition.Version));
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            var compiler = new LuaEffectSpecCompiler(presentationPack: pack);
            var compilation = new LuaEffectCompilationContext { PlayerId = 1, AbilityId = Ability, CompletionHandlerId = Completion };
            foreach (string handler in new[] { "not.registered", "content.operator.silverash.strategy.completed" })
            {
                invocation.Effects[0].CompletionHandlerId = handler;
                Assert.That(compiler.TryCompile(invocation, compilation, out _, out _), Is.False);
            }
            invocation.Effects[0].CompletionHandlerId = Completion;
            compilation.AbilityId = "operator.silverash.strategy";
            Assert.That(compiler.TryCompile(invocation, compilation, out _, out _), Is.False);
            compilation.AbilityId = Ability;
            Assert.That(compiler.TryCompile(invocation, compilation, out _, out _), Is.True);
            var foreign = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("content.other", Ability, definition.SubscriptionId,
                definition.Version, source, LuaContentHasher.ComputeSha256(source)), Context(definition.Version));
            Assert.That(foreign.IsSuccess, Is.True, foreign.Diagnostic);
            foreign.Effects[0].CompletionHandlerId = Completion;
            Assert.That(compiler.TryCompile(foreign, compilation, out _, out _), Is.False);
            var nested = Invoke("return function(ctx) return {Effect.Choice({player=ctx.playerId,branches={{id='one',effects={Effect.LoseResource({apiVersion=1,player=ctx.playerId,targetPlayer='p2',resourceType='iron',amount=1,completionHandlerId='" + Completion + "'})}}}})} end");
            Assert.That(nested.IsSuccess, Is.True, nested.Diagnostic);
            Assert.That(compiler.TryCompile(nested, compilation, out _, out _), Is.False);
        }

        [Test]
        public void RemovedLuaEntryAndUnfinishedOldTypeCannotRun()
        {
            Assert.That(Invoke("return function(ctx) return {Effect.TransferResource({apiVersion=1,player=ctx.playerId,sourcePlayer='p2',resourceType='iron',maxAmount=4})} end").IsSuccess, Is.False);
            Assert.That(registry.TryGet("effect.resource.transfer", out _), Is.False);
            var oldRegistry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            oldRegistry.Register(new EffectRegistration("effect.resource.transfer", context => EffectStepResult.NoProgress(), EffectExecutorKind.IntrinsicFlow, "v1"));
            new EffectTreeExecutor(state, oldRegistry).CreateRoot(EffectSpec.Create("effect.resource.transfer", Object(), 1));
            string archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            var supported = HostRecoveryService.RecoverJson(archive, oldRegistry);
            Assert.That(supported.Succeeded, Is.True, supported.Diagnostic);
            Assert.That(supported.State.EffectRuntime.EffectNodes.Single().EffectTypeId, Is.EqualTo("effect.resource.transfer"));
            Assert.That(supported.State.EffectRuntime.EffectNodes.Single().Status, Is.EqualTo(EffectNodeStatus.Created));
            Assert.That(supported.State.EffectRuntime.EffectNodes.Single().PendingOutcome, Is.EqualTo(EffectPendingOutcome.None));
            var rejected = HostRecoveryService.RecoverJson(archive, registry);
            Assert.That(rejected.Succeeded, Is.False, "新注册表必须拒绝未完成的旧类型。");
            Assert.That(rejected.FaultCode, Is.EqualTo(HostRecoveryService.SnapshotInvalid));
            Assert.That(rejected.Diagnostic, Does.StartWith(HostRecoveryService.UnknownHandler));
            Assert.That(rejected.Diagnostic, Does.Contain("未知 Effect 类型 effect.resource.transfer"));
            Assert.That(state.FindPlayer(2).Resources.Iron, Is.EqualTo(3));
            Assert.That(supported.State.FindPlayer(2).Resources.Iron, Is.EqualTo(3));
            Assert.That(rejected.State.FindPlayer(2).Resources.Iron, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(state.Players.All(player => player.Score == 0), Is.True);
        }

        private static LuaInvocationContext Context(string version = "1.0.0") => new LuaInvocationContext(
            "event.loss_fixture", "CharacterEffectActivated", "p1", 0, version, 2, 0, 0);
        private static LuaInvocationResult Invoke(string source)
        {
            var script = new LuaScriptDefinition("content.loss_fixture", "ability.loss_fixture", "handler.loss_fixture", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            return new MoonSharpLuaRuntimeHost().Invoke(script, Context());
        }
    }
}
