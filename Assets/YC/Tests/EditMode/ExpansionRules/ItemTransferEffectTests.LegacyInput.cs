using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ItemTransferEffectTests
    {
        [TestCase(false)][TestCase(true)]
        public void LegacySingleInputUsesActualV4StateEventsReceiptAndRecovery(bool character)
        {
            var state = State(); var direct = GameStateCloneService.DeepClone(state); var registry = Registry();
            string source = Zone(state, character ? "hand" : "facility_deck", character ? 1 : -1).ZoneId;
            string target = Zone(state, character ? "discard" : "facility_supply_0", character ? 1 : -1).ZoneId;
            string id = new ItemZoneQuery().GetItemIds(state,source).First(), profile = character ? "core.character_card_transfer" : "core.facility_card_transfer";
            var old = MoveSpec(1,new[]{id},source,target,character ? "unordered" : "top",1,profile);
            if(character)Value(old.NormalizedArguments,"player").StringValue="0001";
            var original = old.NormalizedArguments.Clone();
            var node = Run(state,old,registry); var native = Run(direct,ItemMovementV4.TransferSpec(1,new[]{id},source,target,character ? "unordered" : "top",1,profile),registry);
            Assert.That(ItemMovementV4.IsV4(node.EffectTypeId,node.NormalizedArguments),Is.True);
            Assert.That(node.ItemMoveInput.OriginalArgumentsDigest,Is.EqualTo(ItemTransferService.MovementV4Digest(original)));
            Assert.That(Text(node.NormalizedArguments,"player"),Is.EqualTo("1"));
            Assert.That(Text(node.ItemMoveInput.OriginalArguments,"player"),Is.EqualTo(character?"0001":"1"));
            Assert.That(node.ItemMoveInput.CanonicalArgumentsDigest,Is.EqualTo(ItemTransferService.MovementV4Digest(native.NormalizedArguments)));
            Assert.That(ItemTransferService.MovementV4Digest(state.ItemZones),Is.EqualTo(ItemTransferService.MovementV4Digest(direct.ItemZones)));
            Assert.That(JsonConvert.SerializeObject(state.EffectRuntime.RuleEvents),Is.EqualTo(JsonConvert.SerializeObject(direct.EffectRuntime.RuleEvents)));
            Assert.That(node.NormalizedResult.ToDeterministicString(),Is.EqualTo(native.NormalizedResult.ToDeterministicString()));
            Assert.That(node.CommonItemMovementV4?.Receipt,Is.Not.Null); Assert.That(node.TransferBatchRequest,Is.Null);
            string before = ItemTransferService.MovementV4Digest(state.ItemZones); state = Restore(state,registry);
            Assert.That(new EffectTreeExecutor(state,registry).RunUntilQuiescent().Faulted,Is.False);
            Assert.That(ItemTransferService.MovementV4Digest(state.ItemZones),Is.EqualTo(before));
            Assert.That(state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==node.EffectId).ItemMoveInput.OriginalArgumentsDigest,Is.EqualTo(node.ItemMoveInput.OriginalArgumentsDigest));
        }

        [TestCase(false)][TestCase(true)]
        public void LegacySingleLuaTopAndNestedCompileToV4WithOriginalDigest(bool nested)
        {
            var state=State(); string id=Ids(state,"facility_deck")[0], source=Zone(state,"facility_deck").ZoneId, target=Zone(state,"facility_supply_0").ZoneId;
            string move="Effect.MoveItem({apiVersion=1,player=ctx.playerId,itemRefs={{itemId='"+id+"'}},expectedSource={zoneId='"+source+"'},destination={zoneId='"+target+"'},insertion={kind='top'},operationProfileId='core.facility_card_transfer',requiredCount=1})";
            string body=nested ? "Effect.Condition({decisionPlayer=ctx.playerId,ruleCondition=true,leftEffects={"+move+"},rightEffects={}})" : move;
            var invocation=new MoonSharpLuaRuntimeHost().Invoke(ShuffleScript("return function(ctx) return {"+body+"} end"),new LuaInvocationContext("input.normalization","isolated","p1",0,"1.0.0",2,0,0));
            Assert.That(invocation.IsSuccess,Is.True,invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation,new LuaEffectCompilationContext{PlayerId=1},out var specs,out var fault),Is.True,fault?.Diagnostic);
            var spec=nested ? specs.Single().NestedEffectGroups[0].Single() : specs.Single();
            Assert.That(Number(spec.NormalizedArguments,"apiVersion"),Is.EqualTo(4)); Assert.That(spec.ItemMoveInput,Is.Not.Null);
            var persisted=spec.ToPersistentDefinition(); var clone=persisted.Clone(); Assert.That(clone.ItemMoveInput,Is.Not.SameAs(persisted.ItemMoveInput));
            Assert.That(clone.ItemMoveInput.OriginalArgumentsDigest,Is.EqualTo(spec.ItemMoveInput.OriginalArgumentsDigest));
            var registry=Registry(); Run(state,specs.Single(),registry);
            if(nested)
            {
                Assert.That(Open(state).InteractionTypeId,Is.EqualTo("action.decline_effect"));
                Assert.That(Open(state).CandidateIds,Does.Contain("continue")); AnswerRequest(state,registry,"continue");
            }
            Assert.That(Ids(state,"facility_supply_0"),Is.EqualTo(new[]{id}));
        }

        [TestCase(false)][TestCase(true)]
        public void LegacySinglePendingSpecRestoresCanonicalDigestBeforeFirstMove(bool journal)
        {
            var state=State();var registry=Registry();var archive=HostRecoveryService.CreateArchive(state);
            string id=Ids(state,"facility_deck")[0];var old=MoveSpec(1,new[]{id},Zone(state,"facility_deck").ZoneId,Zone(state,"facility_supply_0").ZoneId,"top",1);
            var engine=new EffectTreeExecutor(state,registry);string root=engine.CreateRoot(EffectSpec.Condition(new[]{old},Array.Empty<EffectSpec>()));
            var pending=engine.GetNode(root).NestedEffects.Single();Assert.That(Number(pending.NormalizedArguments,"apiVersion"),Is.EqualTo(4));Assert.That(pending.ItemMoveInput,Is.Not.Null);
            if(journal)HostRecoveryService.AppendJournal(archive,state);else archive=HostRecoveryService.CreateArchive(state);
            var recovered=HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive),registry);Assert.That(recovered.Succeeded,Is.True,recovered.Diagnostic);
            Assert.That(new EffectTreeExecutor(recovered.State,registry).RunUntilQuiescent().Faulted,Is.False,recovered.State.EffectRuntime.LastFaultMessage);
            Assert.That(Ids(recovered.State,"facility_supply_0"),Is.EqualTo(new[]{id}));
            Assert.That(recovered.State.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==TypeId).ItemMoveInput.OriginalArgumentsDigest,Is.EqualTo(pending.ItemMoveInput.OriginalArgumentsDigest));
        }

        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)][TestCase(true,true)]
        public void LegacySingleRecoveryRejectsOldProtocolAndAlteredProvenance(bool journal,bool pending)
        {
            var state=State();var registry=Registry();var archive=HostRecoveryService.CreateArchive(state);
            var old=MoveSpec(1,new[]{Ids(state,"facility_deck")[0]},Zone(state,"facility_deck").ZoneId,Zone(state,"facility_supply_0").ZoneId,"top",1);
            var engine=new EffectTreeExecutor(state,registry);string root=engine.CreateRoot(pending ? EffectSpec.Condition(new[]{old},Array.Empty<EffectSpec>()) : old);
            if(!pending)Assert.That(engine.RunUntilQuiescent().Faulted,Is.False);
            if(journal)HostRecoveryService.AppendJournal(archive,state);else archive=HostRecoveryService.CreateArchive(state);
            string healthy=HostRecoveryService.SerializeArchive(archive);Assert.That(HostRecoveryService.RecoverJson(healthy,registry).Succeeded,Is.True);
            foreach(string mutation in new[]{"original_digest","canonical_digest","original_args","missing_protocol","old_v1","prior_v4"})
            {
                var raw=JObject.Parse(healthy);var savedState=journal ? raw["Journal"]["Entries"].Last["StateAfter"] : raw["Snapshot"]["State"];
                var owner=savedState["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==root);var node=pending ? owner["NestedEffects"].Single() : owner;
                var input=node["ItemMoveInput"];
                if(mutation=="original_digest")input["OriginalArgumentsDigest"]="forged";
                else if(mutation=="canonical_digest")input["CanonicalArgumentsDigest"]="forged";
                else if(mutation=="original_args")((JArray)input["OriginalArguments"]["Properties"]).Single(p=>(string)p["Name"]=="requiredCount")["Value"]["IntegerValue"]=0;
                else if(mutation=="missing_protocol")((JObject)input).Property("ProtocolVersion").Remove();
                else if(mutation=="old_v1"){node["NormalizedArguments"]=input["OriginalArguments"].DeepClone();node["ItemMoveInput"]=JValue.CreateNull();}
                else node["DefinitionVersion"]="4.10.0";
                string original=raw.ToString();var rejected=HostRecoveryService.RecoverJson(original,registry);
                Assert.That(rejected.Succeeded,Is.False,mutation);
                Assert.That(rejected.Diagnostic,Does.Contain(mutation=="old_v1"||mutation=="prior_v4" ? "item_move_contract_version_unsupported" : ItemMoveInputNormalization.InvalidProvenance),mutation);
                Assert.That(raw.ToString(),Is.EqualTo(original)); artifacts.WriteLabeledText("旧单组归一化坏档_"+mutation,original);
            }
        }

        // v4.1的empty/count前置拒绝负例按主计划改由v4.2真实Failed用例替代，原运行不回填。
        [TestCase("duplicate")][TestCase("field")]
        public void LegacySingleUnrepresentableInputsNeverMoveItemsOrLowerRequiredCount(string variant)
        {
            var state=State();var id=Ids(state,"facility_deck")[0];var spec=MoveSpec(1,variant=="duplicate" ? new[]{id,id} : new[]{id},Zone(state,"facility_deck").ZoneId,Zone(state,"facility_supply_0").ZoneId,"top",1);
            if(variant=="field")spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry{Name="randomSeed",Value=NormalizedValue.CreateInteger(1)});
            spec.NormalizedArguments.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));
            string before=JsonConvert.SerializeObject(new{state.ItemZones,state.RandomRuntime});int count=Number(spec.NormalizedArguments,"requiredCount");
            var error=Assert.Throws<InvalidOperationException>(()=>new EffectTreeExecutor(state,Registry()).CreateRoot(spec));
            Assert.That(error.Message,Does.Contain(variant=="field"?"invalid_transfer_arguments":"item_move_legacy_input_not_representable"));
            Assert.That(Number(spec.NormalizedArguments,"requiredCount"),Is.EqualTo(count));Assert.That(JsonConvert.SerializeObject(new{state.ItemZones,state.RandomRuntime}),Is.EqualTo(before));
        }
    }
}
