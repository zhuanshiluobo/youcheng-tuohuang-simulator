using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class InfectedCharacterEffectsTests
    {
        private static string Api32CoverObservation(GameState state)
        {
            var value = JObject.FromObject(new {
                state.ItemZones, state.Players, state.RandomRuntime,
                slots = state.CharacterCoverSlots.Select(s => new { s.PlayerId, s.CardInstanceId, s.AcceptedTypeId, s.Reusable, s.BlockedActionRoundSequence, s.AllowedActionRounds }),
                uses = state.CharacterUses.Select(u => new { u.CardInstanceId, u.ExecutingPlayerId, u.ActionRoundSequence, u.ModeOrder, u.CompletedModes, u.SkippedModes, u.DoubleUseGranted, u.SourceConsumed, u.Finished }),
                views = new {
                    host = new ItemZoneQuery().Project(state, GameStateViewer.Host), p1 = new ItemZoneQuery().Project(state, GameStateViewer.Player(1)),
                    p2 = new ItemZoneQuery().Project(state, GameStateViewer.Player(2)), spectator = new ItemZoneQuery().Project(state, GameStateViewer.Spectator)
                },
                events = state.EffectRuntime.RuleEvents.Where(e => e.EventType == "CharacterCardCovered").Select(e => new { e.EventType, e.PlayerId, e.Visibility, e.ResponseKind, e.Payload, e.HostOnlyPayload })
            });
            // 槽ID由各自真实执行节点生成；按同一实际卡/玩家映射，完整槽业务属性仍逐项比较。
            var aliases = state.CharacterCoverSlots.ToDictionary(s => s.SlotId, s => "cover-slot:" + s.PlayerId + ":" + s.CardInstanceId);
            foreach (var text in value.DescendantsAndSelf().OfType<JValue>().Where(v => v.Type == JTokenType.String))
                if (aliases.TryGetValue((string)text, out string alias)) text.Value = alias;
            return value.ToString(Formatting.None);
        }
        [Test]
        public void Api32AdditionalCoverComparesSameInitialBusinessStateSlotsViewsAndRecoveryWithRealFrozenSources()
        {
            var previous = pack;
            try
            {
                var dedicated = State(); string card = dedicated.ItemZones.Items.First(i => i.ZoneId == FindZone(dedicated, "hand", 1).ZoneId).ItemId;
                StartCard(dedicated, "hellagur", "tactic"); Answer(dedicated, card);
                string before = Api32CoverObservation(dedicated); var choices = Open(dedicated).CandidateIds.ToList();
                Answer(dedicated, card); var dedicatedRegistry = registry;
                var parent = dedicated.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == CharacterCoverEffectExecutor.EffectTypeId && Number(n.NormalizedArguments, "apiVersion") == 3);
                var child = dedicated.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == parent.EffectId && n.CharacterCoverMovementV4 != null);
                var direct = CoverProbe(out _, out string directCard); Assert.That(directCard, Is.EqualTo(card));
                Assert.That(Api32CoverObservation(direct), Is.EqualTo(before)); Assert.That(Open(direct).CandidateIds, Is.EqualTo(choices));
                string root = SubmitCoverProbe(direct, directCard); var directRegistry = registry; var native = direct.EffectRuntime.EffectNodes.Single(n => n.EffectId == root);
                Assert.That(ItemMovementV4.TryValidateState(dedicated, child, out var reason), Is.True, reason);
                Assert.That(ItemMovementV4.TryValidateState(direct, native, out reason), Is.True, reason);
                Assert.That(Text(parent.NormalizedResult, "coveredItemId"), Is.EqualTo(Text(native.NormalizedResult, "coveredItemId")));
                Assert.That(Text(parent.NormalizedResult, "coverSlotId"), Is.EqualTo(child.CharacterCoverMovementV4.SlotAfter.SlotId));
                Assert.That(Text(native.NormalizedResult, "coverSlotId"), Is.EqualTo(native.CharacterCoverMovementV4.SlotAfter.SlotId));
                Assert.That(child.CharacterCoverMovementV4.Receipt.OwnerEffectId, Is.EqualTo(child.EffectId)); Assert.That(native.CharacterCoverMovementV4.Receipt.OwnerEffectId, Is.EqualTo(native.EffectId));
                Assert.That(Api32CoverObservation(direct), Is.EqualTo(Api32CoverObservation(dedicated)));
                Assert.That(CharacterLifecycleService.GetAvailability(direct, 1, card).DisabledReasonCode, Is.EqualTo(CharacterLifecycleService.GetAvailability(dedicated, 1, card).DisabledReasonCode));
                artifacts.WriteLabeledText("API32附加盖放双来源真实输入", JsonConvert.SerializeObject(new { dedicated.ContentPackHash, directHash = direct.ContentPackHash, before, dedicatedState = dedicated, directState = direct }));
                registry = dedicatedRegistry; dedicated = Restore(dedicated); registry = directRegistry; direct = Restore(direct);
                Assert.That(Api32CoverObservation(direct), Is.EqualTo(Api32CoverObservation(dedicated)));
            }
            finally { pack = previous; }
        }
        private GameState CoverProbe(out ExternalContentPack previous,out string card)
        {
            previous=pack;const string path="lua/characters/infected.character.hellagur.tactic.lua";
            string original=pack.GetScript(path).Replace("\r\n","\n");
            const string old="Effect.CoverCharacterCard({ apiVersion = 3, player = ctx.playerId, cardInstanceId = ctx.payload.normalizedResult,\n            coverMode = 'additional', acceptedTypeId = 'core.character_card', reusable = false,\n            blockedActionRoundSequence = PlayerData.Get(ctx.playerId).actionRoundSequence })";
            Assert.That(original.Contains(old),Is.True,"实际原始赫拉格v3构造必须仍存在，不对错误文本空替换。");
            const string selector="{mode='explicit',itemIds={ctx.payload.normalizedResult},expectedSource={zoneId=Global.Content.GetInstance(ctx.payload.normalizedResult).zoneId}}";
            string replacement="Effect.MoveItem({apiVersion=4,player=ctx.playerId,operation='cover_character',selector="+selector+",cover={mode='additional',acceptedTypeId='core.character_card',reusable=false,blockedActionRoundSequence=PlayerData.Get(ctx.playerId).actionRoundSequence}})";
            string script=original.Replace(old,replacement).Replace("    end\n    return {}\nend","    elseif c.stage == 'done' then\n        local r=ctx.payload.normalizedResult\n        assert(r.changed and r.movedCount==1 and r.coveredItemId==r.movedItemIds[1])\n        local page=SourceData.GetTransferReceiptPage({receiptId=r.receiptId,offset=0,limit=1})\n        assert(page.total==1 and #page.items==1 and page.items[1].itemId==r.coveredItemId and page.items[1].toSlotId==r.coverSlotId)\n    end\n    return {}\nend");
            Assert.That(script.Contains("page.total==1"),Is.True);
            string root=artifacts.CopyContent(ContentRoot,"附加盖放公开来源隔离探针",new Dictionary<string,string>{{path,script}});
            pack=ExternalContentPack.Load(root).CreateActiveSnapshot(new GameRuleConfiguration{MapId=StaticMapDefinitions.ThreePlayerMapId,PlayerCount=3,EnabledExpansionIds=new List<string>{"core","enterprise","infected"}});
            artifacts.RecordContent("盖放冻结探针输入",root,pack.ContentHash);artifacts.WriteLabeledText("盖放探针边界","只在首次状态前替换私有冻结Lua中的构造；使用真实赫拉格来源/选择/完成续接，额外检查授权收据分页；不是正式卡面或正常对局/界面验收。");
            var state=State();card=state.ItemZones.Items.First(i=>i.ZoneId==FindZone(state,"hand",1).ZoneId).ItemId;
            StartCard(state,"hellagur","tactic");Answer(state,card);
            return state;
        }
        private string SubmitCoverProbe(GameState state,string card,string untilStage=null)
        {
            var engine=new EffectTreeExecutor(state,registry);var request=Open(state);Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString(card),out var reason),Is.True,reason);
            if(untilStage==null){Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);return state.EffectRuntime.EffectNodes.Last(n=>Text(n.NormalizedArguments,"operation")=="cover_character").EffectId;}
            for(int step=0;step<100;step++)
            {
                var node=state.EffectRuntime.EffectNodes.LastOrDefault(n=>Text(n.NormalizedArguments,"operation")=="cover_character");if(node?.FlowStage==untilStage)return node.EffectId;
                Assert.That(engine.Advance(),Is.True,engine.LastDiagnostic);
            }
            Assert.Fail("没有到达真实盖放阶段："+untilStage);return "";
        }
        [Test]
        public void CoverV4ExternalChoicePrecedesExplicitMoveAndAuthorizedReceipt()
        {
            ExternalContentPack previous=pack;
            try
            {
                var state=CoverProbe(out previous,out string card);var request=Open(state);
                Assert.That(request.CandidateIds,Does.Contain(card));Assert.That(request.MaxSelections,Is.EqualTo(1));Assert.That(request.AllowDecline,Is.False);
                Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==request.OwnerEffectId).EffectTypeId,Is.EqualTo(LuaDomainEffectTypeIds.Choice));
                Assert.That(state.EffectRuntime.EffectNodes.Where(n=>Text(n.NormalizedArguments,"operation")=="cover_character"&&Text(Value(n.NormalizedArguments,"cover"),"mode")=="additional"),Is.Empty);
                state=Restore(state);int events=state.EffectRuntime.RuleEvents.Count(e=>e.EventType=="CharacterCardCovered");string root=SubmitCoverProbe(state,card);

                var node=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root);var plan=node.CharacterCoverMovementV4;
                Assert.That(state.EffectRuntime.InteractionRequests.Where(r=>r.OwnerEffectId==root),Is.Empty);
                Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(plan.Receipt.Result.MovedItemIds,Is.EqualTo(new[]{card}));Assert.That(plan.Item.From.ZoneId,Is.EqualTo(FindZone(state,"discard",1).ZoneId));Assert.That(plan.Item.To.SlotId,Is.EqualTo(plan.SlotAfter.SlotId));
                Assert.That(node.PositionChanges.Single().To.SlotId,Is.EqualTo(state.ItemZones.Items.Single(i=>i.ItemId==card).SlotId));Assert.That(plan.SourceItemId,Is.EqualTo(Card(state,"hellagur").ItemId));Assert.That(CharacterLifecycleService.GetAvailability(state,1,card).CanUse,Is.False);
                Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType=="CharacterCardCovered"),Is.EqualTo(events),"附加盖放不冒发主盖牌事件");state=Restore(state);Drain(state);Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).PositionChanges.Count,Is.EqualTo(1));
            }
            finally{pack=previous;}
        }
        [Test]
        public void AdditionalCoverV3ActualSourceBlocksOnlyNewInstanceUntilNextActionRound()
        {
            var state=State();string card=state.ItemZones.Items.First(i=>i.ZoneId==FindZone(state,"hand",1).ZoneId&&i.DefinitionId!="texas").ItemId;
            StartCard(state,"hellagur","tactic");Answer(state,card);
            var sourceSlot=state.CharacterCoverSlots.Single(s=>s.CardInstanceId==Card(state,"hellagur").ItemId);
            Assert.That(sourceSlot.BlockedActionRoundSequence,Is.EqualTo(0));Answer(state,card);
            Assert.That(state.CharacterCoverSlots.Single(s=>s.CardInstanceId==card).BlockedActionRoundSequence,Is.EqualTo(CharacterLifecycleService.GetActionRoundSequence(state)));
            Assert.That(CharacterLifecycleService.GetAvailability(state,1,card).DisabledReasonCode,Is.EqualTo("character_instance_blocked"));
            Assert.That(state.CharacterCoverSlots.Single(s=>s.SlotId==sourceSlot.SlotId).BlockedActionRoundSequence,Is.EqualTo(0));
            state=Restore(state);state.ActionRound=2;state.Phase=GamePhase.ActionRound2;
            Assert.That(CharacterLifecycleService.GetAvailability(state,1,card).CanUse,Is.True);Assert.That(CharacterLifecycleService.GetActionRoundSequence(state),Is.EqualTo(2));
        }
        [TestCase("cover_bound")][TestCase("cover_committed")][TestCase("cover_finished")]
        public void CoverV4RestoresActualBoundCommitAndCompletionWithoutNewSlot(string stage)
        {
            ExternalContentPack previous=pack;
            try
            {
                var state=CoverProbe(out previous,out string card);string root=SubmitCoverProbe(state,card,stage);var slot=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root).CharacterCoverMovementV4.SlotAfter.SlotId;
                state=Restore(state);Assert.That(new EffectTreeExecutor(state,registry).RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);Drain(state);var node=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root);Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(node.CharacterCoverMovementV4.SlotAfter.SlotId,Is.EqualTo(slot));Assert.That(state.CharacterCoverSlots.Count(s=>s.SlotId==slot),Is.EqualTo(1));Assert.That(node.PositionChanges.Count,Is.EqualTo(1));Restore(state);
            }
            finally{pack=previous;}
        }
        [TestCase("receipt")][TestCase("slot")][TestCase("source")][TestCase("summary")][TestCase("missing_field")][TestCase("old_version")]
        public void CoverV4RejectsChangedIntentSlotReceiptAndRawDefaults(string fault)
        {
            ExternalContentPack previous=pack;
            try
            {
                var state=CoverProbe(out previous,out string card);string root=SubmitCoverProbe(state,card);var raw=JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));string clean=raw.ToString();var saved=raw["Snapshot"]["State"]["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==root);var plan=saved["CharacterCoverMovementV4"];
                if(fault=="receipt")plan["Receipt"]["ReceiptId"]="foreign";if(fault=="slot")plan["SlotAfter"]["Reusable"]=true;if(fault=="source")plan["SourceItemId"]="foreign";
                if(fault=="summary")saved["NormalizedResult"]["Properties"].Single(p=>(string)p["Name"]=="coverSlotId")["Value"]["StringValue"]="foreign";
                if(fault=="missing_field")((JObject)plan["Item"]["From"]).Property("SlotId").Remove();if(fault=="old_version")saved["DefinitionVersion"]="4.4.0";
                string bad=raw.ToString();artifacts.WriteLabeledText("附加盖放坏档_"+fault,bad);Assert.That(HostRecoveryService.RecoverJson(bad,registry).Succeeded,Is.False);Assert.That(raw.ToString(),Is.EqualTo(bad));Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
            }
            finally{pack=previous;}
        }
        [TestCase(false)][TestCase(true)]
        public void CoverV4OldAdditionalWrapperRejectedInSnapshotAndJournal(bool journal)
        {
            var state=State();string card=state.ItemZones.Items.First(i=>i.ZoneId==FindZone(state,"hand",1).ZoneId).ItemId;var archive=HostRecoveryService.CreateArchive(state);StartCard(state,"hellagur","tactic");Answer(state,card);Answer(state,card);var node=state.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==CharacterCoverEffectExecutor.EffectTypeId&&Number(n.NormalizedArguments,"apiVersion")==3);
            if(journal)HostRecoveryService.AppendJournal(archive,state,state.ContentPackHash);else archive=HostRecoveryService.CreateArchive(state);
            string clean=HostRecoveryService.SerializeArchive(archive);var raw=JObject.Parse(clean);var checkpoint=journal?raw["Journal"]["Entries"].Last(e=>e["StateAfter"] is JObject)["StateAfter"]:raw["Snapshot"]["State"];
            checkpoint["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==node.EffectId)["DefinitionVersion"]="v1";string bad=raw.ToString();artifacts.WriteLabeledText("旧v3盖放包装坏档_"+journal,bad);var rejected=HostRecoveryService.RecoverJson(bad,registry);Assert.That(rejected.Succeeded,Is.False);Assert.That(rejected.Diagnostic,Is.EqualTo("character_cover_contract_version_unsupported"));Assert.That(raw.ToString(),Is.EqualTo(bad));Assert.That(HostRecoveryService.RecoverJson(clean,registry).Succeeded,Is.True);
        }
        [Test]
        public void CoverV4BareCallCannotForgeSourceOrCoverFields()
        {
            var state=State();var item=state.ItemZones.Items.First(i=>i.ZoneId==FindZone(state,"hand",1).ZoneId);var args=Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("cover_character"),
                "selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(new[]{item.ItemId}),"expectedSource",Object("zoneId",NormalizedValue.CreateString(item.ZoneId))),"cover",Object("mode",NormalizedValue.CreateString("additional"),"acceptedTypeId",NormalizedValue.CreateString("core.character_card"),"reusable",NormalizedValue.CreateBoolean(false),"blockedActionRoundSequence",NormalizedValue.CreateInteger(1)));
            var node=Run(state,EffectSpec.Create(TypeId,args,1));Assert.That(node.FailureReason,Is.EqualTo("content_transaction_source_binding_required"));Assert.That(node.PositionChanges,Is.Empty);Assert.That(node.CharacterCoverMovementV4,Is.Null);
            var main=args.Clone();Value(Value(main,"cover"),"mode").StringValue="main";Value(Value(main,"cover"),"blockedActionRoundSequence").IntegerValue=0;Assert.That(ItemMovementV4.ValidateArguments(main),Is.Empty);
            var bare=Run(State(),EffectSpec.Create(TypeId,main,1));Assert.That(bare.FailureReason,Is.EqualTo("normal_cover_outside_mainline"));Assert.That(bare.PositionChanges,Is.Empty);Assert.That(bare.CharacterCoverMovementV4,Is.Null);
            foreach(string field in new[]{"destination","insertion","requiredCount","sourceItemId","coverSlotId"}){var invalid=args.Clone();invalid.Properties.Add(new NormalizedValueEntry{Name=field,Value=NormalizedValue.CreateString("forged")});invalid.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));Assert.That(ItemMovementV4.ValidateArguments(invalid),Is.EqualTo("item_move_v4_field_rejected"));}
        }
        [Test]
        public void CoverV4RejectsSelectionBeforePlanningEvenWithLargeSourcePool()
        {
            var state=State(additionalInitialHandCharacters:140);string before=JsonConvert.SerializeObject(state.ItemZones);
            var selector=Object("mode",NormalizedValue.CreateString("select"),"zoneIds",Strings(new[]{FindZone(state,"hand",1).ZoneId,FindZone(state,"discard",1).ZoneId}),
                "itemTypeId",NormalizedValue.CreateString("core.character_card"),"ownerFilter",NormalizedValue.CreateString("any"),"count",NormalizedValue.CreateInteger(1),"allowDecline",NormalizedValue.CreateBoolean(false));
            var args=Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString("1"),"operation",NormalizedValue.CreateString("cover_character"),"selector",selector,
                "cover",Object("mode",NormalizedValue.CreateString("additional"),"acceptedTypeId",NormalizedValue.CreateString("core.character_card"),"reusable",NormalizedValue.CreateBoolean(false),"blockedActionRoundSequence",NormalizedValue.CreateInteger(1)));
            Assert.That(ItemMovementV4.ValidateArguments(args),Is.EqualTo("item_move_selection_not_supported"));
            var node=Run(state,EffectSpec.Create(TypeId,args,1));Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(node.FailureReason,Is.EqualTo("item_move_selection_not_supported"));
            Assert.That(node.PositionChanges,Is.Empty);Assert.That(node.CharacterCoverMovementV4,Is.Null);Assert.That(state.EffectRuntime.InteractionRequests,Is.Empty);
            Assert.That(JsonConvert.SerializeObject(state.ItemZones),Is.EqualTo(before));
            artifacts.WriteLabeledText("旧附加盖放大选择合同已撤销","此用例只证明MoveItem不补选，即使源区有140个额外实例。原附加盖放140项内置选择不再有效；Host主盖放外部完整140项由独立用例验证，不宣称相同职责覆盖。");
        }
        [Test]
        public void CoverV4CopiedInvocationWithRealOwnerAndEventCannotAcquireReceiptScope()
        {
            ExternalContentPack previous=pack;
            try
            {
                var state=CoverProbe(out previous,out string card);string root=SubmitCoverProbe(state,card);var owner=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==root);var e=state.EffectRuntime.RuleEvents.Single(x=>x.EventId==owner.CompletedEventId);
                Assert.That(registry.TryGetContinuation(owner.ContinuationHandlerId,"EffectCompleted",out var registration),Is.True);
                var copied=new EffectEventHandlerContext(state,e,owner,registration);string source=pack.GetScript("lua/characters/infected.character.hellagur.tactic.lua");
                var handler=new LuaHandlerDefinition(owner.ContinuationHandlerId,"EffectCompleted","",new LuaScriptDefinition(owner.ContinuationContentId,owner.ContinuationAbilityId,owner.ContinuationHandlerId,owner.ContinuationDefinitionVersion,source,owner.ContinuationContentHash),EffectHandlerRole.Continuation,abilityId:owner.ContinuationAbilityId);
                var method=typeof(SourceFactsLuaCatalog).GetMethod("CharacterReceiptsForInvocation",BindingFlags.NonPublic|BindingFlags.Static);var denied=(LuaSnapshotValue)method.Invoke(null,new object[]{state,registry,handler,copied,1});Assert.That(denied.Kind,Is.EqualTo(LuaSnapshotValueKind.Null));
            }
            finally{pack=previous;}
        }
    }
}
