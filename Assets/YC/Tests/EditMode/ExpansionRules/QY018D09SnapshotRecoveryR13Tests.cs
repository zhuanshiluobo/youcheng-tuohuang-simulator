using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Application.Setup;
using YC.Domain.Commands;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Economy;
using YC.Domain.Effects;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 仅原正式档副本和原记录合法命令的隔离回归；不计正常UI或完整局验收。
    public sealed class QY018D09SnapshotRecoveryR13Tests
    {
        private static string Root => Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
        private static string Evidence => Path.Combine(Root, "prompt/企业家内容实施规划-20261019/实施记录/QY-018-D09-PAYMENT-SNAPSHOT-FAILURE-R13");
        private static string Read(string name) => File.ReadAllText(Path.Combine(Evidence, name));
        private static void Record(string name, object value) => File.WriteAllText(Path.Combine(Evidence, name), JsonConvert.SerializeObject(value, Formatting.Indented));

        // 正式启动同样的通用服务接线，不运行场景生成器或替代规则。
    static (EffectRegistry registry, List<IGameCommandHandler> handlers) Configure(ExternalContentPack pack, GameRuleConfiguration config, string mapId, int seed)
    {
        var map=new MapQueryService(StaticMapDefinitions.Resolve(mapId));
        var deck=new EventDeckService(seed); var tokens=new ResourceTokenService();
        var registry=new EffectRegistry();
        RouteResourceSelectionEffectExecutor.Register(registry,map);
        var catalog=ActiveContentCatalogBuilder.Build(pack,config);
        catalog.Register(registry);
        var influence=new InfluenceService(map);
        InfluenceEffectExecutor.Register(registry,influence); ResourceEffectExecutor.Register(registry);
        var travel=new TravelCostService(map);
        var movement=new CityMovementService(map,influence,travel,deck,tokens);
        CityMoveEffectExecutor.Register(registry,map,influence,movement,travel,tokens);
        var budget=new MainActionBudgetService(); var lifecycle=new SpecialActionLifecycleService();
        var sale=new ResourceSaleService(); var turn=new TurnOrderService();
        var character=new CharacterCardService(turn,sale,map,influence,movement);
        var rounds=new RoundExecutionService(turn,character,budget,lifecycle,registry,null,new FinalScoringService(map));
        var advance=new RoundAdvanceService(rounds);
        var moveHandler=new MoveCityCommandHandler(movement,advance,registry);
        var special=new SpecialActionOptionQueryService(map,influence,movement,lifecycle,budget);
        CityStyleSpecialActionEffectExecutor.Register(registry,special,new CandidatePolicyRegistry(),map,influence,movement);
        var exploration=new ExplorationService(map,influence,deck,tokens);
        ExplorationEffectExecutor.Register(registry,map,exploration,deck,tokens);
        EventCardEffectExecutor.Register(registry,map,influence,deck,tokens);
        FacilityEntryEffectExecutor.Register(registry);
        RoundStartedRedZoneRule.Register(registry,map);
        var handlers=new List<IGameCommandHandler>{
            new DeployInfluenceCommandHandler(influence,advance,registry),new DispatchInfluenceCommandHandler(influence,advance,registry),
            new UseSpecialActionCommandHandler(registry,special,moveHandler),moveHandler,
            new ExploreLocationCommandHandler(exploration,advance,registry),
            new BuildFacilityCommandHandler(BuildFacilityService.CreateForEffectTree(),advance,registry),
            new DeclareCityStyleCommandHandler(new DeclareCityStyleService(),registry),
            new SetupCommandHandler(map,deck,tokens,turn,rounds),new CoverCharacterCardCommandHandler(character,registry),
            new UseCharacterCardCommandHandler(character,registry),new AnswerInteractionCommandHandler(registry,rounds),
            new InvokeSettlementOptionCommandHandler(registry,rounds),new PreparePaymentDraftCommandHandler(registry,rounds),
            new EndActionCommandHandler(advance,new FinalScoringService(map),registry,rounds,rounds.Advance),
            new CollectResourceCommandHandler(new ResourceCollectionService(map),advance)};
        if(catalog.ContentPackHash!=config.ContentPackHash) throw new Exception("活动内容身份不符："+catalog.ContentPackHash+" != "+config.ContentPackHash);
        return(registry,handlers);
    }


        private static GameSession Session(GameState state, EffectRegistry registry, List<IGameCommandHandler> handlers,
            GameSessionLogContinuationDto continuation)
        {
            var session = new GameSession(state);
            session.ConfigurePaymentSourceQueries(registry);
            foreach (var handler in handlers) session.RegisterHandler(handler);
            Assert.That(session.TryPrepareLocalRecoveryState(state, continuation, out var prepared, out var fault), Is.True, fault);
            Assert.That(session.TryCommitPreparedRecovery(prepared, out fault), Is.True, fault);
            return session;
        }

        private static PaymentSourceQuery Query(GameState state)
        {
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.InteractionTypeId == "effect.resource.payment_choice");
            var payment = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == request.OwnerEffectId).Payment;
            return new PaymentSourceQuery { PlayerId = request.AnsweringPlayerId, OwnerEffectId = request.OwnerEffectId,
                InteractionId = request.GetStableInteractionId(), StateRevision = state.EffectRuntime.StateRevision,
                PaymentGeneration = payment.PaymentGeneration, SnapshotId = ItemTransferEffectExecutor.Text(
                    ItemTransferEffectExecutor.Value(request.PromptParameters, "paymentSourceQuery"), "snapshotId") };
        }

        private static PaymentSourcePageResponse Page(GameSession session, PaymentSourceQuery query, int authenticatedPlayer = 3)
            => session.QueryPaymentSourcePage(authenticatedPlayer, new PaymentSourcePageRequest { Query = query, RequestId = PaymentSourcePageProtocol.Id(query) });

        [Test]
        public void RealSavedCollectionAndNestedPaymentCanQueryAndRecoverWithoutDuplicatePaymentOrBody()
        {
            var envelope = JObject.Parse(Read("输入/6899-正式MatchSave.json"));
            var payload = JObject.Parse((string)envelope["Payload"]);
            string archive = (string)payload["ArchiveJson"];
            var raw = JObject.Parse(archive)["Snapshot"]["State"];
            var pack = ExternalContentPack.Load(Path.Combine(Root, "Builds/QY017-R31-20261024-a001/tuohuang_Data/StreamingAssets/Content/core"));
            var services = Configure(pack, raw["RuleConfiguration"].ToObject<GameRuleConfiguration>(), (string)raw["MapId"], (int)raw["EventDeckSeed"]);
            var recovered = HostRecoveryService.RecoverJson(archive, services.registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            var session = Session(recovered.State, services.registry, services.handlers,
                SimulationConfirmedRecoveryInstaller.ReadContinuation((string)payload["ContinuationJson"]));
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo(6899));
            var commands = JArray.Parse(Read("原正常合法命令-只供隔离重演.json")).ToObject<List<GameCommand>>();
            foreach (var command in commands)
            {
                var result = session.Submit(command);
                Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
                // 最后采集刚创建的统一授勋收尾也必须立即可保存，不归咎之后的投资付款。
                Assert.DoesNotThrow(() => HostRecoveryService.CreateSnapshot(session.State));
            }
            Assert.That(session.State.EffectRuntime.StateRevision, Is.EqualTo(6927));
            var query = Query(session.State);
            Assert.That(query.OwnerEffectId, Is.EqualTo("effect_af0e68a987206a082abcd6de4107cfe3f0f4fa905941820d4c8a8076bf1a52c3"));
            string beforeQueries = RuleStateDigestService.Compute(session.State);
            var page = Page(session, query);
            Assert.That(page.Reason, Is.Empty);
            Assert.That(page.Page, Is.Not.Null);
            Assert.That(page.Page.SourceCount, Is.Zero, "实际无替代来源是合法空页");
            Assert.That(page.Page.Sources, Is.Empty);
            Assert.That(page.Page.SnapshotId, Is.EqualTo(query.SnapshotId));

            Assert.That(Page(session, query, 2).Reason, Is.EqualTo("payment_source_request_invalid"));
            var stale = GameStateCloneService.DeepClone(query); stale.StateRevision--;
            Assert.That(Page(session, stale).Reason, Is.EqualTo("state_revision_mismatch"));
            var cursor = GameStateCloneService.DeepClone(query); cursor.SourceIndex = 1;
            Assert.That(Page(session, cursor).Reason, Is.EqualTo("payment_source_query_cursor_invalid"));

            // 负例专用克隆故意改活动分支，不能用来制造或验收合法6927。
            var foreignBranch = GameStateCloneService.DeepClone(session.State);
            var active = SettlementHostProtocol.FindActiveWindow(foreignBranch);
            active.ActiveTargetEffectId = active.Entries.Single(e => e.SourceDefinitionId == "infected.award.permanent_support").TargetEffectId;
            var badBranch = new EffectTreeExecutor(foreignBranch, services.registry);
            Assert.That(badBranch.TryQueryPaymentSources(query, out _, out var branchReason), Is.False);
            Assert.That(branchReason, Is.EqualTo("settlement_window_not_active"));
            Assert.That(RuleStateDigestService.Compute(session.State), Is.EqualTo(beforeQueries));

            var export = session.ExportConfirmedRecoveryPoint(session.EnableConfirmedRecoveryCapture());
            string saved = HostRecoveryService.SerializeArchive(export.Archive);
            var restored = HostRecoveryService.RecoverJson(saved, services.registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            var resumed = Session(restored.State, services.registry, services.handlers, export.Continuation);
            Assert.That(Page(resumed, Query(resumed.State)).Reason, Is.Empty);
            Assert.That(resumed.State.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(17));
            Assert.That(resumed.State.FindPlayer(3).Resources.Iron, Is.EqualTo(1));

            // 非法统一来源版本同时被无registry保存和有registry恢复拒绝，保留严格来源合同。
            var invalidArchive = GameStateCloneService.DeepClone(export.Archive);
            var target = invalidArchive.Snapshot.State.EffectRuntime.EffectNodes.Single(n => n.EffectId ==
                "effect_bfde3efd36eb8743328d2284df07248d1de1f555fbc680b468d571a809af1bf7");
            target.NormalizedArguments.Properties.Single(p => p.Name == "declarationVersion").Value = NormalizedValue.CreateString("invalid-regression-version");
            var badSave = Assert.Throws<InvalidOperationException>(() => HostRecoveryService.CreateSnapshot(invalidArchive.Snapshot.State));
            Assert.That(badSave.Message, Does.Contain("settlement_origin_invalid:"));
            var badRestore = HostRecoveryService.Recover(invalidArchive, services.registry);
            Assert.That(badRestore.Succeeded, Is.False);
            Assert.That(badRestore.Diagnostic, Is.Not.Empty);

            var request = resumed.State.EffectRuntime.InteractionRequests.Single(r => r.Status == "open" && r.GetStableInteractionId() == query.InteractionId);
            var paymentCommand = new GameCommand { CommandId = "qy018-r13-isolated-payment-once", Kind = GameCommandKind.AnswerInteraction, PlayerId = 3,
                OptionIds = new List<string> { request.CandidateIds.Single() }, Parameters = new Dictionary<string, string> {
                    ["interactionId"] = request.GetStableInteractionId(), ["expectedRevision"] = request.StateRevision.ToString() } };
            var paid = resumed.Submit(paymentCommand);
            Assert.That(paid.Succeeded, Is.True, paid.Validation.Reason);
            Assert.That(resumed.State.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(11));
            Assert.That(resumed.State.FindPlayer(3).Resources.Iron, Is.Zero);
            var owner = resumed.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == query.OwnerEffectId);
            Assert.That(owner.Payment.IsCommitted, Is.True);
            Assert.That(owner.Payment.FinalReceipt, Is.Not.Null);
            string afterPayment = RuleStateDigestService.Compute(resumed.State);
            string randomAfter = JsonConvert.SerializeObject(resumed.State.RandomRuntime);
            int nodesAfter = resumed.State.EffectRuntime.EffectNodes.Count;
            var replay = resumed.Submit(paymentCommand);
            Assert.That(RuleStateDigestService.Compute(resumed.State), Is.EqualTo(afterPayment));
            Assert.That(resumed.State.EffectRuntime.EffectNodes.Count, Is.EqualTo(nodesAfter));
            Assert.That(JsonConvert.SerializeObject(resumed.State.RandomRuntime), Is.EqualTo(randomAfter));
            var closedRequestRetry = new GameCommand { CommandId = "qy018-r13-isolated-closed-payment-fresh-command",
                Kind = paymentCommand.Kind, PlayerId = paymentCommand.PlayerId, OptionIds = paymentCommand.OptionIds.ToList(),
                Parameters = new Dictionary<string, string>(paymentCommand.Parameters) };
            closedRequestRetry.Parameters["expectedRevision"] = resumed.State.EffectRuntime.StateRevision.ToString();
            var closedRetry = resumed.Submit(closedRequestRetry);
            Assert.That(closedRetry.Succeeded, Is.False, "新命令ID及当前修订也不能重复消费已付请求");
            Assert.That(RuleStateDigestService.Compute(resumed.State), Is.EqualTo(afterPayment));
            Assert.That(JsonConvert.SerializeObject(resumed.State.RandomRuntime), Is.EqualTo(randomAfter));
            var paidRestored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(resumed.State)), services.registry);
            Assert.That(paidRestored.Succeeded, Is.True, paidRestored.Diagnostic);
            Assert.That(paidRestored.State.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(11));
            Assert.That(JsonConvert.SerializeObject(paidRestored.State.RandomRuntime), Is.EqualTo(randomAfter));
            Record("原生新回归实际结果.json", new { scope = "隔离规则回归，非正常UI/完整局", page, branchReason, badSave = badSave.Message,
                badRestore.Diagnostic, paid.Succeeded, replaySucceeded = replay.Succeeded, closedRequestRetrySucceeded = closedRetry.Succeeded, nodesAfter, randomAfter,
                revisionAfterPayment = resumed.State.EffectRuntime.StateRevision, finalReceipt = owner.Payment.FinalReceipt,
                diagnostics = resumed.State.EffectRuntime.Diagnostics });
        }
    }
}
