using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        private static ItemTransferBatchRequest LegacySwap(GameState state,bool explicitTop=false,string operation="caller.batch")
        {
            string a=Zone(state,"facility_supply_0").ZoneId,b=Zone(state,"facility_supply_1").ZoneId;
            ItemTransferRequest Leg(string from,string to)=>new ItemTransferRequest{ExecutingPlayerId=1,
                ItemIds=new ItemZoneQuery().GetItemIds(state,from).ToList(),ExpectedSource=new ItemPositionRef{ZoneId=from},Destination=new ZoneRef{ZoneId=to},
                InsertionKind="top",OperationProfileId="core.facility_card_transfer",RequiredCount=1};
            var request=ItemTransferService.CreateBatch(state,1,operation,Leg(a,b),Leg(b,a));
            if(explicitTop)foreach(var expected in request.ExpectedOrders)expected.ExpectedTopItemId=new ItemZoneQuery().GetItemIds(state,expected.ZoneId).FirstOrDefault()??"";
            return request;
        }
        private static EffectSpec NativeSwap(ItemTransferBatchRequest request)=>EffectSpec.Create(TypeId,Object(
            "apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("transfer_batch"),
            "transfers",NormalizedValue.CreateArray(request.Transfers.Select(t=>Object("selector",ExplicitV4(t.ExpectedSource.ZoneId,t.ItemIds.ToArray()),
                "destination",Object("zoneId",NormalizedValue.CreateString(t.Destination.ZoneId)),"insertion",Object("kind",NormalizedValue.CreateString(t.InsertionKind)),
                "operationProfileId",NormalizedValue.CreateString(t.OperationProfileId),"carrierPolicy",NormalizedValue.CreateString(t.CarrierPolicyId),"requiredCount",NormalizedValue.CreateInteger(t.RequiredCount))).ToList()),
            "expectedOrders",Value(ItemTransferService.BatchArguments(request),"expectedOrders").Clone()),1);

        [TestCase(false)][TestCase(true)]
        public void LegacyBatchInputSharesAtomicStateEventsReceiptAndHostIdentity(bool explicitTop)
        {
            var state=State();Fill(state,2);var registry=Registry();var direct=GameStateCloneService.DeepClone(state);var request=LegacySwap(state,explicitTop);
            string a=Zone(state,"facility_supply_0").ZoneId,b=Zone(state,"facility_supply_1").ZoneId,first=Ids(state,"facility_supply_0").Single(),second=Ids(state,"facility_supply_1").Single();
            string random=JsonConvert.SerializeObject(state.RandomRuntime);var engine=new EffectTreeExecutor(state,registry);string id=engine.CreateRoot(BatchSpec(request));
            for(int step=0;step<100&&engine.GetNode(id).Status!=EffectNodeStatus.Completed;step++)
            {
                Assert.That(engine.Advance(),Is.True,state.EffectRuntime.LastFaultMessage);
                string x=Ids(state,"facility_supply_0").Single(),y=Ids(state,"facility_supply_1").Single();
                Assert.That((x==first&&y==second)||(x==second&&y==first),Is.True,"任何可观察步骤都不能出现半笔交换。");
            }
            var node=engine.GetNode(id);var native=Run(direct,NativeSwap(request),registry);
            Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(Ids(state,"facility_supply_0"),Is.EqualTo(new[]{second}));Assert.That(Ids(state,"facility_supply_1"),Is.EqualTo(new[]{first}));
            Assert.That(Number(node.NormalizedArguments,"apiVersion"),Is.EqualTo(4));Assert.That(Text(node.NormalizedArguments,"operation"),Is.EqualTo("transfer_batch"));
            Assert.That(Value(node.NormalizedArguments,"operationId"),Is.Null);Assert.That(Text(node.ItemMoveInput.OriginalArguments,"operationId"),Is.EqualTo(request.OperationId));
            Assert.That(Number(node.ItemMoveInput.OriginalArguments,"apiVersion"),Is.EqualTo(2));Assert.That(node.TransferBatchRequest,Is.Null);Assert.That(Value(node.ContinuationData,"transferBatchReceipt"),Is.Null);
            Assert.That(node.CommonItemMovementV4.Receipt.Items.Count,Is.EqualTo(2));Assert.That(node.CommonItemMovementV4.Receipt.OwnerEffectId,Is.EqualTo(id));Assert.That(node.CommonItemMovementV4.Receipt.OperationId,Is.Not.EqualTo(request.OperationId));
            Assert.That(ItemTransferService.MovementV4Digest(state.ItemZones),Is.EqualTo(ItemTransferService.MovementV4Digest(direct.ItemZones)));
            Assert.That(JsonConvert.SerializeObject(state.EffectRuntime.RuleEvents),Is.EqualTo(JsonConvert.SerializeObject(direct.EffectRuntime.RuleEvents)));
            Assert.That(node.NormalizedResult.ToDeterministicString(),Is.EqualTo(native.NormalizedResult.ToDeterministicString()));Assert.That(JsonConvert.SerializeObject(state.RandomRuntime),Is.EqualTo(random));
            state=Restore(state,registry);Assert.That(new EffectTreeExecutor(state,registry).RunUntilQuiescent().Faulted,Is.False);
        }

        [TestCase(false)][TestCase(true)]
        public void LegacyBatchLuaTopAndNestedCompileToActualV4(bool nested)
        {
            var state=State();Fill(state,2);var registry=Registry();var request=LegacySwap(state);
            string move="Effect.MoveItem("+LuaBatchLiteral(ItemTransferService.BatchArguments(request))+")";
            string body=nested?"Effect.Condition({ruleCondition=true,decisionPlayer=ctx.playerId,leftEffects={"+move+"},rightEffects={}})":move;
            var result=new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript("return function(ctx)return {"+body+"} end"),new LuaInvocationContext("batch.input","isolated","p1",0,"1.0.0",2,0,0));
            Assert.That(result.IsSuccess,Is.True,result.Diagnostic);Assert.That(new LuaEffectSpecCompiler().TryCompile(result,new LuaEffectCompilationContext{PlayerId=1},out var specs,out var fault),Is.True,fault?.Diagnostic);
            var spec=nested?specs.Single().NestedEffectGroups[0].Single():specs.Single();Assert.That(Number(spec.NormalizedArguments,"apiVersion"),Is.EqualTo(4));Assert.That(Number(spec.ItemMoveInput.OriginalArguments,"apiVersion"),Is.EqualTo(2));
            Run(state,specs.Single(),registry);if(nested){Assert.That(Open(state).CandidateIds,Does.Contain("continue"));AnswerRequest(state,registry,"continue");}
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.ItemMoveInput!=null&&Number(n.ItemMoveInput.OriginalArguments,"apiVersion")==2).CommonItemMovementV4.Receipt.Result.MovedCount,Is.EqualTo(2));
        }
        private static string LuaBatchLiteral(NormalizedValue value)=>value.Kind switch
        {
            NormalizedValueKind.String=>"'"+value.StringValue.Replace("\\","\\\\").Replace("'","\\'")+"'",
            NormalizedValueKind.Integer=>value.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            NormalizedValueKind.Array=>"{"+string.Join(",",value.Items.Select(LuaBatchLiteral))+"}",
            NormalizedValueKind.Object=>"{"+string.Join(",",value.Properties.Select(p=>p.Name+"="+LuaBatchLiteral(p.Value)))+"}",
            _=>throw new InvalidOperationException("本夹具只序列化批次合同中的字符串/整数/数组/对象。")
        };

        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)][TestCase(true,true)]
        public void LegacyBatchRecoversBeforeOrAfterCommitWithoutRepeating(bool journal,bool committed)
        {
            var state=State();Fill(state,2);var registry=Registry();var archive=HostRecoveryService.CreateArchive(state);var request=LegacySwap(state);string target=request.Transfers[0].Destination.ZoneId,item=request.Transfers[0].ItemIds[0];
            var engine=new EffectTreeExecutor(state,registry);string id=engine.CreateRoot(BatchSpec(request));
            if(committed)Assert.That(engine.RunUntilQuiescent().Faulted,Is.False);else for(int step=0;step<30&&engine.GetNode(id).FlowStage!="v4_bound";step++)Assert.That(engine.Advance(),Is.True);
            Assert.That(engine.GetNode(id).CommonItemMovementV4,Is.Not.Null);Assert.That(engine.GetNode(id).CommonItemMovementV4.Receipt!=null,Is.EqualTo(committed));
            if(journal)HostRecoveryService.AppendJournal(archive,state);else archive=HostRecoveryService.CreateArchive(state);
            var recovered=HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive),registry);Assert.That(recovered.Succeeded,Is.True,recovered.Diagnostic);state=recovered.State;
            Assert.That(new EffectTreeExecutor(state,registry).RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==item).ZoneId,Is.EqualTo(target));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id).PositionChanges.Count,Is.EqualTo(2));string before=ItemTransferService.MovementV4Digest(state.ItemZones);
            state=Restore(state,registry);new EffectTreeExecutor(state,registry).RunUntilQuiescent();Assert.That(ItemTransferService.MovementV4Digest(state.ItemZones),Is.EqualTo(before));
        }

        [TestCase("digest")][TestCase("revision")][TestCase("top")][TestCase("tail_changed")]
        public void LegacyBatchEmptyTopDoesNotBypassCompleteOrderFences(string mutation)
        {
            var state=State();Fill(state,2);var registry=Registry();var request=LegacySwap(state);string top=Ids(state,"facility_supply_0").Single();
            if(mutation=="digest")request.ExpectedOrders[0].Digest="forged";
            else if(mutation=="revision")request.ExpectedOrders[0].OrderRevision++;
            else if(mutation=="top")request.ExpectedOrders[0].ExpectedTopItemId="forged-top";
            else
            {
                artifacts.WriteLabeledText("隔离顺序变化","以真实共同转移在待测批次绑定前向同一来源区底部插入另一真实牌；不计正常对局或UI验收。");
                Run(state,ItemMovementV4.TransferSpec(1,new[]{Ids(state,"facility_deck")[0]},Zone(state,"facility_deck").ZoneId,Zone(state,"facility_supply_0").ZoneId,"bottom",1),registry);
                Assert.That(Ids(state,"facility_supply_0")[0],Is.EqualTo(top),"顶部相同也不能放过尾部/完整摘要变化。");
            }
            string before=JsonConvert.SerializeObject(new{state.ItemZones,state.RandomRuntime});var node=Run(state,BatchSpec(request),registry);
            Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.FailureReason,Is.EqualTo("transfer_batch_order_changed"));Assert.That(node.PositionChanges,Is.Empty);
            Assert.That(JsonConvert.SerializeObject(new{state.ItemZones,state.RandomRuntime}),Is.EqualTo(before));
        }

        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)][TestCase(true,true)]
        public void LegacyBatchPendingAndCompletedArchivesRejectProvenanceAndOldProtocol(bool journal,bool pending)
        {
            var state=State();Fill(state,2);var registry=Registry();var archive=HostRecoveryService.CreateArchive(state);var engine=new EffectTreeExecutor(state,registry);var old=BatchSpec(LegacySwap(state));
            string id=engine.CreateRoot(pending?EffectSpec.Condition(new[]{old},Array.Empty<EffectSpec>()):old);if(!pending)Assert.That(engine.RunUntilQuiescent().Faulted,Is.False);
            if(journal)HostRecoveryService.AppendJournal(archive,state);else archive=HostRecoveryService.CreateArchive(state);
            string healthy=HostRecoveryService.SerializeArchive(archive);Assert.That(HostRecoveryService.RecoverJson(healthy,registry).Succeeded,Is.True);
            foreach(string change in new[]{"original","canonical","operationId","missing_protocol","old_v2","prior_v4"})
            {
                var raw=JObject.Parse(healthy);var saved=journal?raw["Journal"]["Entries"].Last["StateAfter"]:raw["Snapshot"]["State"];var owner=saved["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==id);var node=pending?owner["NestedEffects"].Single():owner;var input=node["ItemMoveInput"];
                if(change=="original")input["OriginalArgumentsDigest"]="forged";
                else if(change=="canonical")input["CanonicalArgumentsDigest"]="forged";
                else if(change=="operationId")((JArray)input["OriginalArguments"]["Properties"]).Single(p=>(string)p["Name"]=="operationId")["Value"]["StringValue"]="forged-id";
                else if(change=="missing_protocol")((JObject)input).Property("ProtocolVersion").Remove();
                else if(change=="old_v2"){node["NormalizedArguments"]=input["OriginalArguments"].DeepClone();node["ItemMoveInput"]=JValue.CreateNull();}
                else node["DefinitionVersion"]="4.11.0";
                string original=raw.ToString();var rejected=HostRecoveryService.RecoverJson(original,registry);Assert.That(rejected.Succeeded,Is.False,change);
                Assert.That(rejected.Diagnostic,Does.Contain(change=="old_v2"||change=="prior_v4"?"item_move_contract_version_unsupported":ItemMoveInputNormalization.InvalidProvenance),change);Assert.That(raw.ToString(),Is.EqualTo(original));artifacts.WriteLabeledText("旧批次输入坏档_"+change,original);
            }
        }

        [TestCase("missing_zone")][TestCase("duplicate")][TestCase("profile")][TestCase("count")][TestCase("external_field")]
        public void LegacyBatchMalformedInputsCannotCreatePartialMovement(string mutation)
        {
            var state=State();Fill(state,2);var request=LegacySwap(state);
            if(mutation=="missing_zone")request.ExpectedOrders.RemoveAt(0);
            if(mutation=="duplicate")request.Transfers[1].ItemIds[0]=request.Transfers[0].ItemIds[0];
            if(mutation=="profile")request.Transfers[0].OperationProfileId="core.cube_storage";
            if(mutation=="count")request.Transfers[0].RequiredCount=2;
            var spec=BatchSpec(request);if(mutation=="external_field"){spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry{Name="randomSeed",Value=NormalizedValue.CreateInteger(1)});spec.NormalizedArguments.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));}
            string before=JsonConvert.SerializeObject(new{state.ItemZones,state.RandomRuntime});var engine=new EffectTreeExecutor(state,Registry());Assert.That(engine.TryCreateRoot(spec,"","",out _),Is.False,mutation);
            Assert.That(JsonConvert.SerializeObject(new{state.ItemZones,state.RandomRuntime}),Is.EqualTo(before));
        }

        [Test]
        public void LegacyBatchExternalOperationIdCannotReplaceActualHostReceiptIdentity()
        {
            var state=State();Fill(state,2);var registry=Registry();string before=ItemTransferService.MovementV4Digest(state.ItemZones);
            var first=Run(state,BatchSpec(LegacySwap(state,operation:"same-caller-id")),registry);var second=Run(state,BatchSpec(LegacySwap(state,operation:"same-caller-id")),registry);
            Assert.That(ItemTransferService.MovementV4Digest(state.ItemZones),Is.Not.EqualTo(before),"区域顺序版本真实递增，即使牌位换回也不是回滚。");
            Assert.That(first.CommonItemMovementV4.Receipt.OperationId,Is.Not.EqualTo(second.CommonItemMovementV4.Receipt.OperationId));
            Assert.That(first.CommonItemMovementV4.Receipt.Result.MovedCount,Is.EqualTo(2));Assert.That(second.CommonItemMovementV4.Receipt.Result.MovedCount,Is.EqualTo(2));
            state=Restore(state,registry);string after=ItemTransferService.MovementV4Digest(state.ItemZones);new EffectTreeExecutor(state,registry).RunUntilQuiescent();Assert.That(ItemTransferService.MovementV4Digest(state.ItemZones),Is.EqualTo(after));
        }
    }
}
