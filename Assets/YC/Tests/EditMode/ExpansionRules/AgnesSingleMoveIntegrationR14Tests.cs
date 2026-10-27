using System;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>固定R17下层的规则隔离；不计正常开局、联机或界面验收。</summary>
    public sealed class AgnesSingleMoveIntegrationR14Tests
    {
        public string LastEvidence = "";
        private const string Iron = "Effect.GainResource({recipient=ctx.playerId,resourceType='Iron',amount=1})";
        // 真实移动先消耗已冻结的一枚库存，制造后续单枚来源变化；不注入或替换Host状态。
        private const string Drain = @"local supply=nil; local id=nil;
for _,z in ipairs(Global.Content.FindZones({templateId='core.zone.player_supply',owner=ctx.playerId})) do supply=z.id end
local stock=Global.Content.FindInstances({zoneId=supply,typeId='core.player_marker'});
local ids={};for _,item in ipairs(stock) do if item.zoneId==supply then ids[#ids+1]=item.id end end;table.sort(ids);id=ids[1];
local retired=Global.Content.FindZones({templateId='core.zone.box_retired'})[1].id;
local drain=Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds={id},expectedSource={zoneId=supply},destination={zoneId=retired},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=1});";
        private const string DrainContinuation = "if ctx.payload.continuationContext~=nil and ctx.payload.continuationContext.stage=='command_selected' then " + Drain + " table.insert(effects,1,drain) end;";
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
        private void Evidence(AgnesContentTestFixture f)
        {
            LastEvidence = JsonConvert.SerializeObject(new { scope = "纯.NET真实生产沙箱/内核规则隔离", f.Pack.ContentHash, f.State.GameId,
                stored = f.Stored, gold = f.State.FindPlayer(1).Resources.GoldVoucher, iron = f.State.FindPlayer(1).Resources.Iron,
                f.State.EffectRuntime.LastFaultMessage, nodes = f.State.EffectRuntime.EffectNodes,
                invocations = f.State.EffectRuntime.AbilityActionInvocations, bindings = f.State.EffectRuntime.ContinuationBindings,
                requests = f.State.EffectRuntime.InteractionRequests });
            f.Artifacts.WriteLabeledText("R14断言前实际证据", LastEvidence);
        }
        private void Finished(AgnesContentTestFixture f, int stored, int gold, int failedMoves, bool paid, int moveCount)
        {
            Evidence(f);
            Require(f.Stored == stored, "存储数量不符");
            Require(f.State.FindPlayer(1).Resources.GoldVoucher == gold, "付款金额不符");
            Require(f.State.FindPlayer(1).Resources.Iron == 1, "后继收益未恰好一次");
            var moves = f.State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId).ToArray();
            Require(moves.Length == moveCount, "移动节点总数不符");
            Require(moves.Count(n => n.Status == EffectNodeStatus.Failed) == failedMoves, "单枚失败数量不符");
            Require(moves.Where(n => n.Status == EffectNodeStatus.Failed).All(n => n.FailureReason == "source_zone_changed"), "真实失败原因不符");
            Require(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == EffectTypeIds.Condition) == (paid ? 1 : 0), "存在多余条件包装或付款条件丢失");
            Require(f.State.EffectRuntime.AbilityActionInvocations.All(i => i.Phase == "completed"), "行动未正常完成");
            Require(!f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), "残留必答请求");
            Require(string.IsNullOrEmpty(f.State.EffectRuntime.LastFaultMessage), "Host发生故障");
            Require(f.State.EffectRuntime.ContinuationBindings.All(b => b.Status == ContinuationBindingStatus.Completed), "后继收据未完成");
            f.AssertNoTokenPaymentSelection();
            var before = JsonConvert.SerializeObject(new { f.Stored, f.State.FindPlayer(1).Resources, nodes = f.State.EffectRuntime.EffectNodes.Select(n => n.EffectId),
                bindings = f.State.EffectRuntime.ContinuationBindings, calls = f.State.EffectRuntime.AbilityActionInvocations });
            f.Restore("完成后恢复"); f.Pump();
            var after = JsonConvert.SerializeObject(new { f.Stored, f.State.FindPlayer(1).Resources, nodes = f.State.EffectRuntime.EffectNodes.Select(n => n.EffectId),
                bindings = f.State.EffectRuntime.ContinuationBindings, calls = f.State.EffectRuntime.AbilityActionInvocations });
            Require(before == after, "完成后恢复发生重复执行或收据变化");
        }
        private static void SubmitWithoutPump(AgnesContentTestFixture f, string answer)
        {
            RoundExecutionService.RefreshConcurrentInteractionRevisions(f.State);
            var request = f.Open;
            Require(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                NormalizedValue.CreateString(answer), out string reason), reason);
        }
        private static void AdvanceUntil(AgnesContentTestFixture f, Func<bool> predicate)
        {
            for (int guard = 0; guard < 3000; guard++)
            {
                if (predicate()) return;
                Require(f.Engine.Advance(), "未到目标边界即静止：" + f.State.EffectRuntime.LastFaultMessage);
                Require(string.IsNullOrEmpty(f.State.EffectRuntime.LastFaultMessage), f.State.EffectRuntime.LastFaultMessage);
            }
            throw new InvalidOperationException("有界推进超出3000步");
        }
        private static bool HasPendingChunkChildren(AgnesContentTestFixture f, EffectNodeRuntimeState node, EffectPendingOutcome outcome) =>
            node.PendingOutcome == outcome && node.ContinuationContext?.Properties.Any(p => p.Name == "stage" && p.Value.StringValue == "command_moves") == true &&
            node.ChildEffectIds.Count > 0 && f.State.EffectRuntime.EffectNodes.Any(child => node.ChildEffectIds.Contains(child.EffectId) && child.Status == EffectNodeStatus.Created);
        [Test] public void PrivateProbeDetailsReferenceTheSameRealSource()
        {
            using var f = new AgnesContentTestFixture(); f.Host("return {}"); f.Start(); Evidence(f);
            var d = f.Pack.Definitions.Single(x => x.DefinitionId == AgnesContentTestFixture.Definition);
            Require(d.Data["playerDetails"].Count() == 1 && (string)d.Data["playerDetails"][0]["abilityId"] == AgnesContentTestFixture.Probe, "私有详情来源不一致");
        }
        [Test] public void FreeShortageKeepsFailedLeafSuccessfulSiblingsAndSuccessorOnce()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.gainCommand(ctx,{},3);e[#e+1]=" + Iron + ";return e", supply: 3, continuationEffects: DrainContinuation);
            f.Start(); f.Answer("3"); Finished(f, 2, 100, 1, false, 4);
        }
        [Test] public void PaidShortageKeepsFullCostAndPaymentPendingRecovery()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.purchaseCommand(ctx,3);" + Drain + "table.insert(e,1,drain);e[#e+1]=" + Iron + ";return e", supply: 3);
            f.Start(); var id = f.Open.GetStableInteractionId(); f.Restore("付款确认前恢复");
            Require(f.Open.GetStableInteractionId() == id, "恢复改变请求身份");
            f.Answer("continue"); Finished(f, 2, 94, 1, true, 4);
        }
        [Test] public void InsufficientMoneyDoesNotPartPayOrMove()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.purchaseCommand(ctx,3);table.insert(e,1,Effect.PayResource({player=ctx.playerId,cost={GoldVoucher=95}}));e[#e+1]=" + Iron + ";return e", supply: 3);
            f.Start(); Evidence(f);
            Require(f.Stored == 0 && f.State.FindPlayer(1).Resources.GoldVoucher == 5, "不足整额费用时发生扣款或移动");
            var request = f.Open;
            Require(!f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                NormalizedValue.CreateString("continue"), out string reason), "不足费用却接受确认");
            Require(!string.IsNullOrEmpty(reason), "不足费用的拒绝缺少原因");
            f.Artifacts.WriteLabeledText("不足整额费用的真实拒绝", JsonConvert.SerializeObject(new { reason }));
            f.Answer("decline"); Evidence(f);
            Require(f.Stored == 0 && f.State.FindPlayer(1).Resources.GoldVoucher == 5, "放弃后发生部分扣款或移动");
            Require(f.State.FindPlayer(1).Resources.Iron == 1, "付款失败阻塞独立后继");
            Require(!f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId), "费用未完成却执行右式");
        }
        [Test] public void PaidDeclinePreservesCostAndSuccessor()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.purchaseCommand(ctx,3);e[#e+1]=" + Iron + ";return e", supply: 3);
            f.Start(); f.Answer("decline"); Finished(f, 0, 100, 0, true, 0);
        }
        [Test] public void FreeDeclineDoesNotCreateMoveOrCondition()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.gainCommand(ctx,{},3);e[#e+1]=" + Iron + ";return e", supply: 3);
            f.Start(); f.Decline(); Finished(f, 0, 100, 0, false, 0);
        }
        [Test] public void FreeChunkFailureContinuesEveryFrozenSingleMove()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.gainCommand(ctx,{},29);e[#e+1]=" + Iron + ";return e", supply: 29, continuationEffects: DrainContinuation);
            f.Start(); f.Answer("29"); Finished(f, 28, 100, 1, false, 30);
        }
        [Test] public void FreeChoiceAndPartialMovesRecoverWithoutRepeating()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.gainCommand(ctx,{},29);e[#e+1]=" + Iron + ";return e", supply: 29);
            f.Start(); var request = f.Open.GetStableInteractionId(); f.Restore("数量确认前恢复");
            Require(f.Open.GetStableInteractionId() == request, "数量请求身份改变"); SubmitWithoutPump(f, "29");
            AdvanceUntil(f, () => f.Stored == 1);
            f.Restore("已实际取得一枚且其余尚未完成"); f.Pump(); Finished(f, 29, 100, 0, false, 29);
        }
        [Test] public void PaidFailedMovePendingChildrenRecoverWithCostAndSuccessorOnce()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.purchaseCommand(ctx,29);" + Drain + "table.insert(e,1,drain);e[#e+1]=" + Iron + ";return e", supply: 29);
            f.Start(); SubmitWithoutPump(f, "continue");
            AdvanceUntil(f, () => f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ItemTransferEffectExecutor.TypeId &&
                n.FailureReason == "source_zone_changed" && HasPendingChunkChildren(f, n, EffectPendingOutcome.Failed)));
            Require(f.State.FindPlayer(1).Resources.GoldVoucher == 42, "失败边界前未整额付款");
            f.Restore("实际首枚失败且续接子节点待执行"); f.Pump(); Finished(f, 28, 42, 1, true, 30);
        }
        [Test] public void OriginalMaximumNAndNinetyNineChoiceStillWorkInRealSandbox()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.gainCommand(ctx,{},200);e[#e+1]=" + Iron + ";return e", supply: 100);
            f.Start(); Require(f.Open.CandidateIds.Count == 99 && f.Open.CandidateIds.First() == "99", "原有合法数量范围变化");
            f.Answer("99"); Finished(f, 99, 100, 0, false, 99);
        }
        [Test] public void ZeroSupplyDoesNotCreateEmptyPage()
        {
            using var f = new AgnesContentTestFixture();
            f.Host("local e=A.gainCommand(ctx,{},3);e[#e+1]=" + Iron + ";return e", supply: 0);
            f.Start(); Finished(f, 0, 100, 0, false, 0);
        }
        [Test] public void FormalA09EntryUsesFormalIdentityAndContinuationReceipts()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A09", "2"); Evidence(f);
            Require(f.Stored == 2, "正式A09未取得选择数量");
            Require(f.State.EffectRuntime.EffectNodes.Any(n => n.SourceId == "entrepreneur.round.A.1" && n.ContinuationAbilityId == "entrepreneur.A09.immediate.1"), "实际节点未绑定正式A09");
            Require(f.State.EffectRuntime.ContinuationBindings.Any(b => b.AbilityId == "entrepreneur.A09.immediate.1" && b.Status == ContinuationBindingStatus.Completed && !string.IsNullOrEmpty(b.ContentHash)), "缺少正式后继身份收据");
        }
    }
}
