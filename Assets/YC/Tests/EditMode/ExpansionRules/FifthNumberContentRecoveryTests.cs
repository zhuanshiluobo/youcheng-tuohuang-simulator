using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.Scoring;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>真实正式请求与Host恢复；不计正常程序/联机验收，本轮未运行。</summary>
    public sealed class FifthNumberContentRecoveryTests
    {
        private IsolatedTestArtifacts artifacts;
        private FifthNumberContentTestFixture f;
        [SetUp] public void Begin() { artifacts = IsolatedTestArtifacts.BeginCase(); f = null; }
        [TearDown] public void End() { f?.Snapshot("第五号恢复用例最终状态"); artifacts?.Finish(); }

        [Test] public void SetupRestoresTheOriginalTwoInstancesWithoutASecondDelivery()
        {
            f = new FifthNumberContentTestFixture(artifacts);
            string[] items = f.State.ItemZones.Items.Where(i => i.ZoneId == f.Storage.ZoneId).Select(i => i.ItemId).OrderBy(s => s).ToArray();
            string receipt = f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).SetupReceiptId;
            f.Restore("T09设置后"); f.Pump(); f.Restore("T09设置后重复恢复"); f.Pump();
            Assert.That(f.State.ItemZones.Items.Where(i => i.ZoneId == f.Storage.ZoneId).Select(i => i.ItemId).OrderBy(s => s), Is.EqualTo(items));
            Assert.That(f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).SetupReceiptId, Is.EqualTo(receipt));
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ContentPrimitiveEffectExecutor.RetiredMarkerLoadTypeId &&
                Text(n.NormalizedArguments, "cardInstanceId") == f.Card), Is.False, "没有T09 Lua二次装载。");
        }

        [TestCase(1, false)][TestCase(2, false)][TestCase(1, true)][TestCase(2, true)]
        public void OwnBypassAcceptsOnlyTheOwnerForBothActorsAndRejectsOldAnswers(int actor, bool replace)
        {
            f = new FifthNumberContentTestFixture(artifacts, concurrent: true); string item = f.Place();
            f.Depart(item, actor, replace, actionWindow: true);
            var response = f.State.EffectRuntime.DeferredResponses.Single(r => r.AbilityId == "entrepreneur.T09.departure" && r.Status == DeferredResponseStatus.Pending);
            Assert.That(response.IsBypass, Is.True); Assert.That(response.AnsweringPlayerId, Is.EqualTo(1));
            var request = f.StoreRequest(); string id = request.GetStableInteractionId(); int revision = request.StateRevision;
            string zone = f.State.ItemZones.Items.Single(i => i.ItemId == item).ZoneId;
            Assert.That(f.Engine.TrySubmitInteraction(id, 2, revision, NormalizedValue.CreateString("store"), out _), Is.False);
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == item).ZoneId, Is.EqualTo(zone));
            f.Restore("本人旁路收容待答"); request = f.State.EffectRuntime.InteractionRequests.Single(q => q.GetStableInteractionId() == id);
            f.Answer(request, NormalizedValue.CreateString("store")); f.Restore("本人旁路收容已答"); f.Pump();
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == item).ZoneId, Is.EqualTo(f.Storage.ZoneId));
            Assert.That(f.Engine.TrySubmitInteraction(id, 1, revision, NormalizedValue.CreateString("store"), out _), Is.False);
            Assert.That(f.State.EffectRuntime.DeferredResponses.Single(r => r.DeferredResponseId == response.DeferredResponseId).Status, Is.EqualTo(DeferredResponseStatus.Stored));
        }

        [TestCase("t")][TestCase("dorothy")][TestCase("supply")]
        public void EachIndependentStorageOpportunityRestoresItsAnswerAndUniqueCustody(string destination)
        {
            f = new FifthNumberContentTestFixture(artifacts, dorothy: true); f.ActivateDorothy(); string item = f.Place();
            f.Depart(item); f.CompleteDestinations(destination, restoreEach: true);
            var departure = f.State.EffectRuntime.InfluenceDepartures.Single(d => d.ItemId == item);
            string zone = f.State.ItemZones.Items.Single(i => i.ItemId == item).ZoneId;
            int transferred = f.State.EffectRuntime.EffectNodes.SelectMany(n => n.PositionChanges).Count(p => p.ItemId == item);
            f.Restore("全部独立机会结清"); f.Pump();
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == item).ZoneId, Is.EqualTo(zone));
            Assert.That(f.State.EffectRuntime.EffectNodes.SelectMany(n => n.PositionChanges).Count(p => p.ItemId == item), Is.EqualTo(transferred));
            Assert.That(f.State.EffectRuntime.DeferredResponses.Where(r => r.DepartureId == departure.DepartureId).All(r => r.Status != DeferredResponseStatus.Pending && r.Status != DeferredResponseStatus.Queued), Is.True);
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open" &&
                f.State.EffectRuntime.DeferredResponses.Any(r => r.DepartureId == departure.DepartureId && InfluenceDepartureProtocol.IsWithin(f.State, q.OwnerEffectId, r.ResponseRootEffectId))), Is.False);
        }

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void StoreChoiceOwnsCancellationAndExecutesOnlyTheActualStoreSubtree(bool replace, bool decline)
        {
            f = new FifthNumberContentTestFixture(artifacts);
            string item = f.Place(); f.Depart(item, 1, replace);
            var request = f.StoreRequest(); string requestId = request.GetStableInteractionId();
            var choice = f.Engine.GetNode(request.OwnerEffectId); string choiceId = choice.EffectId;
            string staged = f.State.ItemZones.Items.Single(i => i.ItemId == item).ZoneId;
            Assert.That(request.InteractionTypeId, Is.EqualTo("effect.choice.execute"));
            Assert.That(request.CandidateIds, Is.EqualTo(new[] { "store" }));
            Assert.That(request.MinSelections, Is.Zero); Assert.That(request.MaxSelections, Is.EqualTo(1));
            Assert.That(request.AllowDecline, Is.True); Assert.That(choice.ChildEffectIds, Is.Empty);
            Assert.That(choice.NestedGroupSizes, Is.EqualTo(new[] { 1 }));
            Assert.That(choice.NestedEffects.Single().EffectTypeId, Is.EqualTo(ContentPrimitiveEffectExecutor.StoreRemoved));
            Assert.That(Text(choice.NestedEffects.Single().NormalizedArguments, "influenceItemId"), Is.EqualTo(item));
            f.Restore("真实T09可选存放待答"); request = f.StoreRequest();
            Assert.That(request.GetStableInteractionId(), Is.EqualTo(requestId));
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == item).ZoneId, Is.EqualTo(staged));
            f.Answer(request, decline ? NormalizedValue.CreateBoolean(false) : NormalizedValue.CreateString("store"));
            var marker = f.State.ItemZones.Items.Single(i => i.ItemId == item);
            if (decline)
            {
                var departure = f.State.EffectRuntime.InfluenceDepartures.Single(d => d.ItemId == item);
                Assert.That(departure.Status, Is.EqualTo("returned"));
                Assert.That(departure.DestinationZoneId, Is.EqualTo(marker.OriginalSupplyZoneId));
                if (replace)
                {
                    // 原替换流程可以在归还后合法重用该方块；取消存放不取消原替换正文。
                    Assert.That(marker.ZoneId, Is.EqualTo(departure.OriginalPosition.ZoneId));
                    Assert.That(f.State.EffectRuntime.EffectNodes.Where(n => InfluenceDepartureProtocol.IsWithin(f.State, n.EffectId, departure.SourceEffectId))
                        .SelectMany(n => n.PositionChanges).Any(p => p.ItemId == item && p.From.ZoneId == marker.OriginalSupplyZoneId &&
                            p.To.ZoneId == departure.OriginalPosition.ZoneId), Is.True);
                }
                else Assert.That(marker.ZoneId, Is.EqualTo(marker.OriginalSupplyZoneId));
            }
            else Assert.That(marker.ZoneId, Is.EqualTo(f.Storage.ZoneId));
            Assert.That(f.Engine.GetNode(choiceId).ChildEffectIds.Count, Is.EqualTo(decline ? 0 : 1));
            int changes = f.State.EffectRuntime.EffectNodes.SelectMany(n => n.PositionChanges).Count(p => p.ItemId == item);
            f.Restore("真实T09存放或放弃已结清"); f.Pump();
            Assert.That(f.State.EffectRuntime.EffectNodes.SelectMany(n => n.PositionChanges).Count(p => p.ItemId == item), Is.EqualTo(changes));
            Assert.That(f.State.ItemZones.Items.Single(i => i.ItemId == item).ZoneId, Is.EqualTo(marker.ZoneId));
        }

        [Test] public void PaidMandatoryRewardRestoresWithoutRefundRepaymentOrAbandonment()
        {
            f = new FifthNumberContentTestFixture(artifacts); for (int i = 0; i < 4; i++) f.CollectOne();
            f.OpenCleanup(); int pure = f.State.FindPlayer(1).Resources.PureOriginium;
            f.InvokeCleanup("qy012.restore.exchange"); f.Answer("continue");
            var request = f.Open(); Assert.That(request.AllowDecline, Is.False); Assert.That(f.Stored, Is.EqualTo(3));
            string id = request.GetStableInteractionId();
            string[] paid = f.State.EffectRuntime.EffectNodes.Where(n => n.Status == EffectNodeStatus.Completed && n.ItemMovementV4 != null && n.DefinitionVersion == ItemTransferEffectExecutor.TransferDefinitionVersion &&
                n.PositionChanges.Count == 3 && n.PositionChanges.All(c => c.From.ZoneId == f.Storage.ZoneId)).Select(n => n.EffectId).ToArray();
            Assert.That(paid.Length, Is.EqualTo(1));
            Assert.That(f.Engine.TrySubmitInteraction(id, 1, request.StateRevision, NormalizedValue.CreateBoolean(false), out _), Is.False);
            Assert.That(f.Stored, Is.EqualTo(3));
            f.Restore("已付3枚收益待选"); request = f.State.EffectRuntime.InteractionRequests.Single(q => q.GetStableInteractionId() == id);
            f.AnswerCandidate(request, "resource"); f.Restore("本次兑换已完成"); f.Pump();
            Assert.That(f.Stored, Is.EqualTo(3)); Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + 1));
            Assert.That(f.State.EffectRuntime.EffectNodes.Where(n => n.Status == EffectNodeStatus.Completed && n.ItemMovementV4 != null && n.DefinitionVersion == ItemTransferEffectExecutor.TransferDefinitionVersion &&
                n.PositionChanges.Count == 3 && n.PositionChanges.All(c => c.From.ZoneId == f.Storage.ZoneId)).Select(n => n.EffectId), Is.EqualTo(paid));
            foreach (string paidNodeId in paid)
                Assert.That(ItemMovementV4.TryValidateState(f.State, f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == paidNodeId), out string reason), Is.True, reason);
            f.InvokeCleanup("qy012.restore.exchange.second"); f.Answer("continue"); f.Answer("place");
            var place = f.PlacementRequest(); f.Restore("第二次兑换放置待答");
            place = f.State.EffectRuntime.InteractionRequests.Single(q => q.GetStableInteractionId() == place.GetStableInteractionId());
            f.AnswerCandidate(place, place.CandidateIds.First()); Assert.That(f.Stored, Is.Zero);
            Assert.That(CleanupSourceService.Invocations(f.State).Where(i => i.SourceRef.ItemId == f.Card).Select(i => i.InvocationId).Distinct().Count(), Is.EqualTo(2));
        }

        [Test] public void T04RestoresBetweenTheTwoPlacementsWithoutReplayingDispatchOrTheFirstPlace()
        {
            f = new FifthNumberContentTestFixture(artifacts); f.ChooseFace("T04");
            var dispatch = f.Open("operator.dispatch.choose"); string dispatchNode = dispatch.OwnerEffectId;
            var candidates = dispatch.CandidateIds.ToArray(); f.Restore("特派抽三已保存");
            dispatch = f.Open("operator.dispatch.choose"); Assert.That(dispatch.CandidateIds, Is.EqualTo(candidates));
            Assert.That(dispatch.OwnerEffectId, Is.EqualTo(dispatchNode)); f.Answer(dispatch, NormalizedValue.CreateBoolean(false));
            string firstId = f.PlacementRequest().OwnerEffectId; f.AnswerCandidate(f.PlacementRequest(), f.PlacementRequest().CandidateIds.First());
            var second = f.PlacementRequest(); string secondId = second.OwnerEffectId; int count = f.State.Map.Influences.Count(i => i.PlayerId == 1);
            f.Restore("两次独立放置之间");
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == firstId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.PlacementRequest().OwnerEffectId, Is.EqualTo(secondId));
            f.AnswerCandidate(f.PlacementRequest(), f.PlacementRequest().CandidateIds.First()); f.Restore("双放置已完成"); f.Pump();
            Assert.That(f.State.Map.Influences.Count(i => i.PlayerId == 1), Is.EqualTo(count + 1));
            Assert.That(f.Group(5).ImmediateEntries.Select(e => e.ReceiptId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == OperatorDispatchFlow.TypeId && n.PlayerId == 1), Is.EqualTo(1));
        }

        [Test] public void AllPlayerUpgradeKeepsTheSelectedEnterpriseAcrossPlayerBoundaryRecovery()
        {
            f = new FifthNumberContentTestFixture(artifacts); f.ChooseFace("T05");
            var request = f.Open(); string enterprise = request.CandidateIds.First();
            Assert.That(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateString(enterprise), out string reason), Is.True, reason);
            bool boundary = false;
            for (int i = 0; i < 1000; i++)
            {
                var root = f.State.EffectRuntime.EffectNodes.LastOrDefault(n => n.EffectTypeId == EnterpriseEffectExecutor.UpgradeAllPlayersTypeId);
                if (root != null && Number(root.ContinuationData, "completedCount") == 1) { boundary = true; break; }
                var open = f.State.EffectRuntime.InteractionRequests.FirstOrDefault(q => q.Status == "open" && q.AnsweringPlayerId == 1);
                if (open != null)
                    Assert.That(f.Engine.TrySubmitInteraction(open.GetStableInteractionId(), open.AnsweringPlayerId, open.StateRevision,
                        FifthNumberContentTestFixture.AutomaticAnswer(open), out reason), Is.True, reason);
                else Assert.That(f.Engine.Advance(), Is.True, f.Engine.LastDiagnostic);
            }
            Assert.That(boundary, Is.True, "必须真的到达第一位已结清、第二位未完成的边界。");
            var query = new EnterpriseStateQuery(f.Registry.EnterpriseDefinitions); int level = query.GetLevel(f.State, 1, enterprise);
            f.Restore("全员升级玩家边界"); f.Pump(); f.ResolveOpenRequests();
            Assert.That(query.GetLevel(f.State, 1, enterprise), Is.EqualTo(level));
            var result = f.State.EffectRuntime.EffectNodes.Last(n => n.EffectTypeId == EnterpriseEffectExecutor.UpgradeAllPlayersTypeId).NormalizedResult;
            Assert.That(Text(result, "selectedEnterpriseItemId"), Is.EqualTo(enterprise));
            Assert.That(Value(result, "completedPlayerIds").Items.Select(v => v.IntegerValue), Is.EqualTo(new long[] { 1, 2 }));
        }

        [Test] public void TerminalScoringStagesRestoreWithoutRepeatingTheSpecialOrResourceScore()
        {
            f = new FifthNumberContentTestFixture(artifacts); f.ChooseFace("T01"); f.FinishOtherPlayer();
            f.State.FindPlayer(1).Resources.PureOriginium = 8; f.StartFinal();
            var stages = new[] { FinalScoringSessionService.Special, FinalScoringSessionService.RegionCommit,
                FinalScoringSessionService.ResourceSnapshot, FinalScoringSessionService.ResourceCommit, FinalScoringSessionService.Complete };
            foreach (string stage in stages)
            {
                bool reached = false;
                for (int i = 0; i < 1000; i++)
                {
                    var owner = f.State.FinalScoring?.Session == null ? null : f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == f.State.FinalScoring.Session.OwnerEffectId);
                    if (owner?.FlowStage == stage) { reached = true; break; }
                    Assert.That(f.Engine.Advance(), Is.True, f.Engine.LastDiagnostic);
                }
                Assert.That(reached, Is.True, stage); f.Restore("T01终局阶段-" + stage);
                Assert.That(f.Engine.Advance(), Is.True, f.Engine.LastDiagnostic);
            }
            f.Pump(); int score = f.State.FindPlayer(1).Score;
            f.Restore("T01终局完成"); f.Pump();
            Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score));
            Assert.That(f.State.FinalScoring.Session.Players.Single(p => p.PlayerId == 1).SpecialScore, Is.EqualTo(2));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "FinalScoringStarted"), Is.EqualTo(1));
        }

        [Test] public void MissingSavedCustodyContextIsRejectedByFormalHostRecovery()
        {
            f = new FifthNumberContentTestFixture(artifacts); string item = f.Place(); f.Depart(item);
            f.Restore("坏档负例合法控制");
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State)));
            int changed = 0;
            foreach (var obj in archive.Descendants().OfType<JObject>().Where(o =>
                (string)o["EffectTypeId"] == ContentPrimitiveEffectExecutor.StoreRemoved).ToList())
            {
                // 旧值回调上下文已删除；破坏真实Store待执行规格必需的离场实例，不改权限或版本。
                var properties = obj["NormalizedArguments"]?["Properties"] as JArray;
                if (properties == null) continue;
                foreach (var field in properties.OfType<JObject>().Where(v => (string)v["Name"] == "influenceItemId").ToList())
                { field.Remove(); changed++; }
            }
            Assert.That(changed, Is.GreaterThan(0));
            var rejected = HostRecoveryService.RecoverJson(archive.ToString(), f.Registry);
            Assert.That(rejected.Succeeded, Is.False); artifacts.WriteLabeledText("损坏T09续接拒绝", rejected.Diagnostic);
        }
    }
}
