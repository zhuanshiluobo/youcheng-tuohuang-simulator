using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 私有冻结输入中的协议探针，走真实SourceUse/完成续接；不计保罗卡面或正常玩法验收。
    public sealed class TransferReceiptPageTests
    {
        private const string PathInPack = "lua/entrepreneurs/paul.p05_p06.quick.lua", Ability = "entrepreneur.face.P05";
        private static string Script(int count, int required = 1) => @"return function(ctx)
            if ctx.eventType=='EffectCompleted' then
                if ctx.payload.continuationContext==nil or ctx.payload.continuationContext.stage~='receipt_probe' then return {} end
                local r=ctx.payload.normalizedResult;local offset=0;local total=0;local seen={};local offsets={};local sizes={}
                repeat
                    local page=SourceData.GetTransferReceiptPage({receiptId=r.receiptId,offset=offset,limit=8})
                    assert(page.total>8 and page.total<=" + count + @")
                    assert(#page.items<=8 and #page.items>0);table.insert(offsets,offset);table.insert(sizes,#page.items)
                    for _,row in ipairs(page.items) do assert(not seen[row.itemId]);seen[row.itemId]=true;total=total+1;assert(row.itemId==r.movedItemIds[total])
                        assert(row.fromZoneId~='' and row.toZoneId~='' and row.controllerBefore~=nil and row.controllerAfter~=nil)
                    end
                    offset=page.nextOffset
                until offset==-1
                assert(total==r.movedCount)
                local tail=SourceData.GetTransferReceiptPage({receiptId=r.receiptId,offset=total,limit=1});assert(#tail.items==0 and tail.nextOffset==-1)
                return {Effect.WithContext(Effect.GainResource({player=ctx.playerId,resourceType='gold_voucher',amount=total}),{stage='receipt_page_audit',receiptId=r.receiptId,total=total,pageOffsets=offsets,pageSizes=sizes})}
            end
            local deck=Global.Content.FindZones({localZoneId='facility_deck'})[1]
            local retired=Global.Content.FindZones({templateId='core.zone.box_retired'})[1]
            return {Effect.WithContext(Effect.MoveItem({apiVersion=4,player=ctx.playerId,operation='transfer',
                selector={mode='top',zoneId=deck.id,count=" + count + @",itemTypeId='core.facility_card'},
                destination={zoneId=retired.id},insertion={kind='unordered'},operationProfileId='core.facility_card_transfer',requiredCount=" + required + @"}),{stage='receipt_probe'})}
            end";
        [TestCase(9)][TestCase(99)]
        public void ActualFrozenSourceCompletionReadsAllRowsInBoundedReadOnlyPages(int maximum)
        {
            using var f=new PaulContentTestFixture(sourceOverrides: _=>new Dictionary<string,string>{{PathInPack,Script(maximum)}});
            VerifyActualReceipt(f, maximum, false);
        }
        [Test]
        public void ExactNinetyNineFrozenSourceCompletionReadsThirteenPagesAndRestoresAllRows()
        {
            using var f = new PaulContentTestFixture(sourceOverrides: _ => new Dictionary<string,string> { { PathInPack, Script(99, 99) } }, minimumFacilityDeckCount: 105);
            VerifyActualReceipt(f, 99, true);
        }
        private static void VerifyActualReceipt(PaulContentTestFixture f, int maximum, bool exact99)
        {
            f.Choose(5,"P05");int before=f.State.FindPlayer(1).Resources.GoldVoucher;
            var sourceOrder = f.State.ItemZones.Orders.Single(o => f.State.ItemZones.Zones.Any(z => z.ZoneId == o.ZoneId && z.LocalZoneId == "facility_deck")).ItemIds.ToList();
            var expectedIds = sourceOrder.Take(maximum).ToList();
            if (exact99) { Assert.That(sourceOrder.Count, Is.GreaterThanOrEqualTo(99)); Assert.That(expectedIds.Count, Is.EqualTo(99)); }
            f.Invoke(Ability);
            // 正常准备已会产生共同收据；只核对本次真实Lua续接交付的那份，不能按“全局唯一收据”定位。
            var audit = f.State.EffectRuntime.EffectNodes.Single(n => ItemTransferEffectExecutor.Text(n.ContinuationContext, "stage") == "receipt_page_audit").ContinuationContext;
            string receiptId=ItemTransferEffectExecutor.Text(audit,"receiptId");Assert.That(receiptId,Is.Not.Empty);
            var node=f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==ItemTransferEffectExecutor.TypeId&&n.CommonItemMovementV4?.Receipt?.ReceiptId==receiptId);
            Assert.That(node.ContinuationAbilityId,Is.EqualTo(Ability));Assert.That(ItemTransferEffectExecutor.Text(node.ContinuationContext,"stage"),Is.EqualTo("receipt_probe"));
            int total=node.CommonItemMovementV4.Receipt.Items.Count;
            Assert.That(total,Is.EqualTo(expectedIds.Count));
            Assert.That(node.CommonItemMovementV4.Receipt.Items.Select(i => i.ItemId), Is.EqualTo(expectedIds));
            Assert.That(node.CommonItemMovementV4.Receipt.LifecycleItems, Is.Empty);
            var offsets = ItemTransferEffectExecutor.Value(audit,"pageOffsets").Items.Select(v => (int)v.IntegerValue).ToArray();
            var sizes = ItemTransferEffectExecutor.Value(audit,"pageSizes").Items.Select(v => (int)v.IntegerValue).ToArray();
            Assert.That(offsets, Is.EqualTo(Enumerable.Range(0, (total+7)/8).Select(i => i*8))); Assert.That(sizes.Sum(), Is.EqualTo(total));
            if (exact99) { Assert.That(total,Is.EqualTo(99)); Assert.That(sizes,Is.EqualTo(Enumerable.Repeat(8,12).Concat(new[]{3}))); }
            f.Artifacts.WriteLabeledText("分页真实数量与页边界", Newtonsoft.Json.JsonConvert.SerializeObject(new { exact99, requestedMaximum=maximum, minimumRequired=exact99?99:1, availableBefore=sourceOrder.Count,
                actualMovedCount=node.CommonItemMovementV4.Receipt.Result.MovedCount, actualReceiptItems=total, expectedItemIds=expectedIds, receiptId=node.CommonItemMovementV4.Receipt.ReceiptId, offsets, sizes, normalGameAcceptance=false }));
            Assert.That(total,Is.InRange(9,maximum));Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(before+total));
            Assert.That(node.NormalizedResult.IsValid(),Is.True);Assert.That(node.CommonItemMovementV4.Receipt.Items.Select(i=>i.ItemId).Distinct().Count(),Is.EqualTo(total));
            f.Restore("完整转移分页完成待续接之后");f.Pump();Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher,Is.EqualTo(before+total));
            var declaration=f.Registry.AbilityDeclarations.Single(d=>d.AbilityId==Ability);var content=f.Pack.Definitions.Single(d=>d.DefinitionId==declaration.DefinitionId);
            var handler=content.AbilityHandlers.Single(h=>h.HandlerId==declaration.HandlerRef);string source=f.Pack.GetScript(handler.Script);
            var definition=new LuaHandlerDefinition(handler.CompletionHandlerId,"EffectCompleted","",new LuaScriptDefinition(content.DefinitionId,Ability,handler.CompletionHandlerId,handler.Version,source,LuaContentHasher.ComputeSha256(source)),EffectHandlerRole.Continuation,contentInstanceId:content.DefinitionId,abilityId:Ability);
            node=f.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==node.EffectId);var ruleEvent=f.State.EffectRuntime.RuleEvents.Single(e=>e.EventId==node.CompletedEventId);
            Assert.That(f.Registry.TryGetContinuation(handler.CompletionHandlerId,"EffectCompleted",out var registration),Is.True);
            var method=typeof(LuaEffectEventDispatcher).GetMethod("CreateInvocationContext",BindingFlags.Instance|BindingFlags.NonPublic);var dispatcher=new LuaEffectEventDispatcher(f.Registry);
            foreach(string damage in new[]{"valid","copied_owner","copied_event","wrong_hash"})
            {
                var call=new EffectEventHandlerContext(f.State,damage=="copied_event"?GameStateCloneService.DeepClone(ruleEvent):ruleEvent,damage=="copied_owner"?GameStateCloneService.DeepClone(node):node,registration);
                var candidate=damage=="wrong_hash"?new LuaHandlerDefinition(handler.CompletionHandlerId,"EffectCompleted","",new LuaScriptDefinition(content.DefinitionId,Ability,handler.CompletionHandlerId,handler.Version,source+"\n--changed",LuaContentHasher.ComputeSha256(source+"\n--changed")),EffectHandlerRole.Continuation,abilityId:Ability):definition;
                var context=(LuaInvocationContext)method.Invoke(dispatcher,new object[]{candidate,call});
                string probe="return function(ctx) local p=SourceData.GetTransferReceiptPage({receiptId='"+node.CommonItemMovementV4.Receipt.ReceiptId+"',offset=0,limit=8});assert(p.total=="+total+");return {} end";
                var invocation=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("receipt.probe","receipt.probe","receipt.probe",context.DefinitionVersion,probe,LuaContentHasher.ComputeSha256(probe)),context);
                Assert.That(invocation.IsSuccess,Is.EqualTo(damage=="valid"),damage+":"+invocation.Diagnostic);
                if(damage=="valid")
                {
                    string id=node.CommonItemMovementV4.Receipt.ReceiptId;
                    foreach(string input in new[]{"{receiptId='"+id+"',offset=-1,limit=1}","{receiptId='"+id+"',offset=0.5,limit=1}","{receiptId='"+id+"',offset=0,limit=9}","{receiptId='"+id+"',offset=0,limit=0}","{receiptId='"+id+"',offset=0,limit='1'}","{receiptId='"+id+"',offset="+(total+1)+",limit=1}","{receiptId='"+id+"',offset=0,limit=1,other=true}","{receiptId='foreign',offset=0,limit=1}"})
                    {
                        string bad="return function(ctx) SourceData.GetTransferReceiptPage("+input+");return {} end";
                        var rejected=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("receipt.bad","receipt.bad","receipt.bad",context.DefinitionVersion,bad,LuaContentHasher.ComputeSha256(bad)),context);
                        Assert.That(rejected.IsSuccess,Is.False,input);Assert.That(rejected.Diagnostic,Does.Contain("transfer_receipt_"));
                    }
                    string write="return function(ctx) local p=SourceData.GetTransferReceiptPage({receiptId='"+id+"',offset=0,limit=8});p.items[1].itemId='forged';return {} end";
                    var readOnly=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("receipt.write","receipt.write","receipt.write",context.DefinitionVersion,write,LuaContentHasher.ComputeSha256(write)),context);
                    Assert.That(readOnly.IsSuccess,Is.False);Assert.That(readOnly.Diagnostic,Does.Contain("nmc_read_only_snapshot"));
                }
            }
            // 真实后续移动把已操作实例放入秘密移出区：旧收据仍完整保存在Host，但当前Lua页不得泄漏隐藏身份。
            var fact=node.CommonItemMovementV4.Receipt.Items[0];string hidden=f.State.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.box_secret_retired").ZoneId;
            var args=ItemTransferEffectExecutor.Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("transfer"),
                "selector",ItemTransferEffectExecutor.Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",ItemTransferEffectExecutor.Strings(new[]{fact.ItemId}),"expectedSource",ItemTransferEffectExecutor.Object("zoneId",NormalizedValue.CreateString(fact.To.ZoneId))),
                "destination",ItemTransferEffectExecutor.Object("zoneId",NormalizedValue.CreateString(hidden)),"insertion",ItemTransferEffectExecutor.Object("kind",NormalizedValue.CreateString("unordered")),"operationProfileId",NormalizedValue.CreateString("core.facility_card_transfer"),"requiredCount",NormalizedValue.CreateInteger(1));
            f.Run(EffectSpec.Create(ItemTransferEffectExecutor.TypeId,args,1));
            var hiddenContext=(LuaInvocationContext)method.Invoke(dispatcher,new object[]{definition,new EffectEventHandlerContext(f.State,ruleEvent,node,registration)});
            string hiddenProbe="return function(ctx) SourceData.GetTransferReceiptPage({receiptId='"+node.CommonItemMovementV4.Receipt.ReceiptId+"',offset=0,limit=8});return {} end";
            var hiddenRead=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("receipt.hidden","receipt.hidden","receipt.hidden",hiddenContext.DefinitionVersion,hiddenProbe,LuaContentHasher.ComputeSha256(hiddenProbe)),hiddenContext);
            Assert.That(hiddenRead.IsSuccess,Is.False);Assert.That(hiddenRead.Diagnostic,Does.Contain("transfer_receipt_not_authorized"));
            Assert.That(node.CommonItemMovementV4.Receipt.Items.Count,Is.EqualTo(total));f.Restore("隐藏实例读取拒绝后原完整收据保持");
        }
    }
}
