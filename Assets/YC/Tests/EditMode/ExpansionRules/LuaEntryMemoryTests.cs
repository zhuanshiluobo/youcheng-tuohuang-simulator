using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class LuaEntryMemoryTests
    {
        private IsolatedTestArtifacts artifacts;
        private GameState state;
        private const string Entry = "fixture.lua.shared_entry";
        private const string Shared = @"return function(ctx)
            local m=ctx.memory
            if ctx.eventType=='MemoryA' then
                m.count=(m.count or 0)+1; m.writer=ctx.playerId
                return {effects={},memory=m}
            end
            return {Effect.GainScore({player=ctx.playerId,amount=m.count or 1})}
        end";

        [SetUp] public void Begin() { artifacts=IsolatedTestArtifacts.BeginCase(); state=null; }
        [TearDown] public void End()
        {
            if(state!=null) artifacts.WriteLabeledText("memory实际状态",JsonConvert.SerializeObject(state));
            artifacts.Finish();
        }
        private static LuaScriptDefinition Definition(string source, string handler="handler", string entry=Entry) =>
            new LuaScriptDefinition("content."+handler,"ability."+handler,handler,"1.0.0",source,LuaContentHasher.ComputeSha256(source),entry);
        private static LuaInvocationContext Context(NormalizedValue memory=null) => new LuaInvocationContext("event","MemoryA","p1",0,"1.0.0",2,0,0)
            {Memory=memory??ItemTransferEffectExecutor.Object()};
        private static LuaInvocationResult Invoke(string body, NormalizedValue memory=null) =>
            new MoonSharpLuaRuntimeHost().Invoke(Definition("return function(ctx) "+body+" end"),Context(memory));
        private static long Number(NormalizedValue value,string key) => ItemTransferEffectExecutor.Number(value,key);

        [Test] public void MutableCopyOnlyWritesWhenReturnedAndReplacementDoesNotMerge()
        {
            var initial=ItemTransferEffectExecutor.Object("old",NormalizedValue.CreateInteger(7));
            var unchanged=Invoke("ctx.memory.old=99; return {}",initial);
            Assert.That(unchanged.IsSuccess,Is.True,unchanged.Diagnostic);
            Assert.That(unchanged.Memory,Is.Null); Assert.That(Number(initial,"old"),Is.EqualTo(7));
            var updated=Invoke("ctx.memory.old=99; return {effects={},memory=ctx.memory}",initial);
            Assert.That(updated.IsSuccess,Is.True,updated.Diagnostic);
            Assert.That(Number(updated.Memory,"old"),Is.EqualTo(99)); Assert.That(Number(initial,"old"),Is.EqualTo(7));
            var replaced=Invoke("return {effects={},memory={new=true, list={2,3}, nested={name='保存'}}}",initial);
            Assert.That(replaced.IsSuccess,Is.True,replaced.Diagnostic);
            Assert.That(ItemTransferEffectExecutor.Value(replaced.Memory,"old"),Is.Null);
            var roundtrip=Invoke("return {effects={},memory=ctx.memory}",replaced.Memory);
            Assert.That(roundtrip.IsSuccess,Is.True,roundtrip.Diagnostic);
            Assert.That(roundtrip.Memory.ToDeterministicString(),Is.EqualTo(replaced.Memory.ToDeterministicString()));
            var cleared=Invoke("return {effects={},memory={}}",initial);
            Assert.That(cleared.IsSuccess,Is.True,cleared.Diagnostic);
            Assert.That(cleared.Memory.Items,Is.Empty); Assert.That(cleared.Memory.Properties,Is.Empty);
            var omitted=Invoke("return {effects={}}",initial);
            Assert.That(omitted.IsSuccess,Is.True,omitted.Diagnostic); Assert.That(omitted.Memory,Is.Null);
        }

        [TestCase("return {effects={},memory=3}")]
        [TestCase("local m={};m.self=m;return {effects={},memory=m}")]
        [TestCase("return {effects={},memory={f=function() end}}")]
        [TestCase("return {effects={},memory={n=0/0}}")]
        [TestCase("return {effects={},memory={n=1.5}}")]
        [TestCase("return {effects={},memory={n=1e30}}")]
        [TestCase("return {effects={},memory={[2]='hole'}}")]
        [TestCase("return {effects={},memory={[1]=1,n=2}}")]
        [TestCase("local m={};for i=1,129 do m[i]=i end;return {effects={},memory=m}")]
        [TestCase("return {effects={},memory={s=string.rep('a',1025)}}")]
        [TestCase("return {effects={},memory={},extra=1}")]
        [TestCase("return {memory={n=1}}")]
        [TestCase("return {effects={false},memory={n=1}}")]
        public void InvalidMemoryOrEnvelopeReturnsNoEffectsOrMemory(string body)
        {
            var result=Invoke(body);
            Assert.That(result.IsSuccess,Is.False); Assert.That(result.Effects,Is.Empty); Assert.That(result.Memory,Is.Null);
        }

        private EffectRegistry Registry(string source=Shared)
        {
            var registry=new EffectRegistry {ContentPackHash="memory.fixture"}; ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            var dispatcher=new LuaEffectEventDispatcher(registry);
            dispatcher.Register(new LuaHandlerDefinition("memory.a","MemoryA","",Definition(source,"a"),EffectHandlerRole.Primary));
            dispatcher.Register(new LuaHandlerDefinition("memory.b","MemoryB","",Definition(source,"b"),EffectHandlerRole.Primary));
            foreach(var eventType in new[]{"MemoryA","MemoryB"})
                registry.Register("test."+eventType,c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest
                {EventType=eventType,PlayerId=c.Node.PlayerId,SemanticKey="invoke"}));
            return registry;
        }
        private void State()
        {
            var map=StaticMapDefinitions.CreateFourPlayerMap();
            state=new ItemZoneStateFactory().CreateForSetup(new GameState {
                GameId="game.lua.memory",MapId=map.MapId,ContentPackHash="memory.fixture",CurrentPlayerId=1,StartPlayerId=1,
                LaunchPlayerOrder=new List<int> {1,2},
                Phase=GamePhase.ActionRound1,Round=1,ActionRound=1,
                RuleConfiguration=new GameRuleConfiguration {MapId=map.MapId,PlayerCount=2,ContentPackHash="memory.fixture"},
                ContentDefinitions=new GameContentDefinitions(),
                Players=new List<PlayerState> {new PlayerState {PlayerId=1},new PlayerState {PlayerId=2}}
            },map,new List<ItemRecipeEntry>());
        }
        private EffectRunReport Run(EffectRegistry registry,string eventType,int player=1)
        {
            var executor=new EffectTreeExecutor(state,registry);
            executor.CreateRoot(EffectSpec.Create("test."+eventType,playerId:player));
            return executor.RunUntilQuiescent();
        }

        [Test] public void DifferentAbilitiesAndPlayersShareTheEntryAcrossActivationsAndArchiveRestore()
        {
            State(); var registry=Registry();
            Assert.That(Run(registry,"MemoryA").Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(Run(registry,"MemoryA").Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(state.EffectRuntime.LuaMemories,Has.Count.EqualTo(1));
            Assert.That(Number(state.EffectRuntime.LuaMemories[0].Value,"count"),Is.EqualTo(2));
            var before=RuleStateDigestService.Compute(state);
            var archive=HostRecoveryService.CreateArchive(state);
            var json=HostRecoveryService.SerializeArchive(archive);
            artifacts.WriteLabeledText("memory正式存档",json);
            var loaded=HostRecoveryService.RecoverJson(json,registry);
            Assert.That(loaded.Succeeded,Is.True,loaded.Diagnostic);
            state=loaded.State;
            Assert.That(RuleStateDigestService.Compute(state),Is.EqualTo(before));
            var resumed=new EffectTreeExecutor(state,registry).RunUntilQuiescent();
            Assert.That(resumed.Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(Number(state.EffectRuntime.LuaMemories[0].Value,"count"),Is.EqualTo(2),"恢复不重放已提交调用");
            Assert.That(Run(registry,"MemoryB",2).Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(2).Score,Is.EqualTo(2));
            Assert.That(Number(state.EffectRuntime.LuaMemories[0].Value,"count"),Is.EqualTo(2),"旧数组返回保留 memory");
        }

        [Test] public void CompilationFailureLeavesPreviouslyCommittedMemoryUntouched()
        {
            const string source=@"return function(ctx)
                if ctx.eventType=='MemoryA' then return {effects={},memory={count=7}} end
                return {effects={{kind='effect_spec',effectTypeId='effect.not.registered'}},memory={count=99}}
            end";
            State();var registry=Registry(source);
            Assert.That(Run(registry,"MemoryA").Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(Run(registry,"MemoryB").Faulted,Is.True);
            Assert.That(Number(state.EffectRuntime.LuaMemories.Single().Value,"count"),Is.EqualTo(7));
            Assert.That(state.EffectRuntime.DispatchReceipts.Count(r=>r.SubscriptionId=="memory.b"),Is.Zero);
        }

        [Test] public void PendingChoiceArchiveAndCompletionAliasUseTheSameMemory()
        {
            const string source=@"return function(ctx)
                if ctx.eventType=='MemoryA' then
                    return {effects={Effect.Choice({player=ctx.playerId,options={'go'}})},memory={count=5}}
                end
                if ctx.payload.effectTypeId=='effect.flow.choice' then
                    local n=ctx.memory.count;return {effects={Effect.GainScore({player=ctx.playerId,amount=n})},memory={count=n+1}}
                end
                return {}
            end";
            State();var registry=new EffectRegistry {ContentPackHash="memory.fixture"};ResourceEffectExecutor.Register(registry);GenericLuaEffectExecutor.Register(registry);
            var dispatcher=new LuaEffectEventDispatcher(registry);
            var completion=new LuaScriptDefinition("content.a","ability.a","done","1.0.0",source,LuaContentHasher.ComputeSha256(source),Entry);
            dispatcher.RegisterContinuation(new LuaHandlerDefinition("memory.done","EffectCompleted","",completion,EffectHandlerRole.Continuation));
            dispatcher.Register(new LuaHandlerDefinition("memory.a","MemoryA","",Definition(source,"a"),EffectHandlerRole.Primary,completionHandlerId:"done"));
            registry.Register("test.MemoryA",c=>EffectStepResult.Completed().AddEvent(new EffectEventRequest {EventType="MemoryA",PlayerId=1,SemanticKey="invoke"}));
            Assert.That(Run(registry,"MemoryA").Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open"),Is.True);
            var json=HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            artifacts.WriteLabeledText("memory选择等待存档",json);
            var loaded=HostRecoveryService.RecoverJson(json,registry);
            Assert.That(loaded.Succeeded,Is.True,loaded.Diagnostic);state=loaded.State;
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
            var engine=new EffectTreeExecutor(state,registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,NormalizedValue.CreateString("go"),out string error),Is.True,error);
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Score,Is.EqualTo(5));
            Assert.That(state.EffectRuntime.LuaMemories,Has.Count.EqualTo(1));
            Assert.That(Number(state.EffectRuntime.LuaMemories.Single().Value,"count"),Is.EqualTo(6));
            Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            Assert.That(state.FindPlayer(1).Score,Is.EqualTo(5));
        }

        [Test] public void LaterHandlerFailureRollsBackEarlierMemoryInTheSameCommit()
        {
            State();var registry=Registry();
            Assert.That(Run(registry,"MemoryA").Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("memory.fail","MemoryA","*","memory.fail",
                _=>throw new InvalidOperationException("fixture_after_memory_failure"),EffectHandlerRole.Observer));
            Assert.That(Run(registry,"MemoryA").Faulted,Is.True);
            Assert.That(Number(state.EffectRuntime.LuaMemories.Single().Value,"count"),Is.EqualTo(1));
        }

        [Test] public void SeparateEntriesGamesAndNamedFunctionsAreIsolatedAndHashChangesAreRejected()
        {
            State();string hash=Definition(Shared).ContentHash;
            LuaEntryMemoryStore.Replace(state.EffectRuntime,Entry,"",hash,ItemTransferEffectExecutor.Object("count",NormalizedValue.CreateInteger(3)));
            Assert.That(Number(LuaEntryMemoryStore.Read(state.EffectRuntime,Entry,"",hash),"count"),Is.EqualTo(3));
            Assert.That(Number(LuaEntryMemoryStore.Read(state.EffectRuntime,"other-entry","",hash),"count"),Is.Zero);
            Assert.That(Number(LuaEntryMemoryStore.Read(state.EffectRuntime,Entry,"other_function",hash),"count"),Is.Zero);
            Assert.That(Number(LuaEntryMemoryStore.Read(new EffectRuntimeState(),Entry,"",hash),"count"),Is.Zero);
            Assert.Throws<InvalidOperationException>(()=>LuaEntryMemoryStore.Read(state.EffectRuntime,Entry,"",new string('f',64)));
            var clone=GameStateCloneService.DeepClone(state);
            var oldDigest=RuleStateDigestService.Compute(state);
            LuaEntryMemoryStore.Replace(clone.EffectRuntime,Entry,"",hash,ItemTransferEffectExecutor.Object());
            Assert.That(RuleStateDigestService.Compute(clone),Is.Not.EqualTo(oldDigest));
            Assert.That(Number(state.EffectRuntime.LuaMemories.Single().Value,"count"),Is.EqualTo(3));
        }

        [Test] public void LegacyMissingFieldsStartEmptyAndCorruptMemoryCannotValidate()
        {
            var old=JObject.FromObject(new EffectRuntimeState());old.Remove("LuaMemories");old.Remove("LuaMemoryProtocolVersion");
            var runtime=old.ToObject<EffectRuntimeState>();
            Assert.That(LuaEntryMemoryStore.TryValidate(runtime,out _),Is.True);
            string hash=Definition(Shared).ContentHash;
            LuaEntryMemoryStore.Replace(runtime,Entry,"",hash,ItemTransferEffectExecutor.Object());
            runtime.LuaMemories.Add(GameStateCloneService.DeepClone(runtime.LuaMemories[0]));
            Assert.That(runtime.TryValidate(out _),Is.False);
            runtime.LuaMemories.RemoveAt(1);runtime.LuaMemories[0].Value=NormalizedValue.CreateString("invalid");
            Assert.That(runtime.TryValidate(out _),Is.False);
            runtime.LuaMemories[0].Value=ItemTransferEffectExecutor.Object();runtime.LuaMemoryProtocolVersion=999;
            Assert.That(runtime.TryValidate(out _),Is.False);
        }

        [Test] public void RegisteredPathIsTheIdentityInsteadOfSourceTextOrAbility()
        {
            const string first="lua/characters/elysium.strategy.lua", second="lua/characters/cannot.tactic.lua";
            var pack=ExternalContentPack.Current.CreateWithOverrides(new Dictionary<string,string> {{first,Shared},{second,Shared}});
            string a=pack.GetScriptEntryId(first), b=pack.GetScriptEntryId(second);
            Assert.That(a,Is.EqualTo(pack.GetScriptEntryId(first)));Assert.That(a,Is.Not.EqualTo(b));
            var firstDefinition=new LuaScriptDefinition("card.a","ability.a","handler.a","1",pack.GetScript(first),LuaContentHasher.ComputeSha256(pack.GetScript(first)),a);
            var secondDefinition=new LuaScriptDefinition("card.b","ability.b","handler.b","2",pack.GetScript(first),LuaContentHasher.ComputeSha256(pack.GetScript(first)),pack.GetScriptEntryId(first));
            Assert.That(firstDefinition.EntryId,Is.EqualTo(secondDefinition.EntryId));
            var runtime=new EffectRuntimeState();
            LuaEntryMemoryStore.Replace(runtime,a,"",firstDefinition.ContentHash,ItemTransferEffectExecutor.Object("count",NormalizedValue.CreateInteger(8)));
            Assert.That(Number(LuaEntryMemoryStore.Read(runtime,secondDefinition.EntryId,"",secondDefinition.ContentHash),"count"),Is.EqualTo(8));
            Assert.That(Number(LuaEntryMemoryStore.Read(runtime,b,"",firstDefinition.ContentHash),"count"),Is.Zero);
        }

        [TestCase("candidatePatches")][TestCase("abilityRuleQuery")][TestCase("influencePlaceAttaching")]
        public void PureQueryReturnContractsCannotWriteMemory(string responseKind)
        {
            var context=Context();context.ResponseKind=responseKind;
            var result=new MoonSharpLuaRuntimeHost().Invoke(Definition("return function(ctx) return {effects={},memory={n=1}} end"),context);
            Assert.That(result.IsSuccess,Is.False);Assert.That(result.Memory,Is.Null);
        }

        [TestCase("EntryId")][TestCase("EntryPoint")][TestCase("ContentHash")][TestCase("Value")][TestCase("nested_value")]
        public void ArchiveCannotSilentlyDefaultMissingMemoryFields(string field)
        {
            State();var registry=Registry();
            Assert.That(Run(registry,"MemoryA").Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            var entry=(JObject)raw["Snapshot"]["State"]["EffectRuntime"]["LuaMemories"][0];
            if(field=="nested_value") ((JObject)entry["Value"]["Properties"][0]["Value"]).Remove("IntegerValue");
            else entry.Remove(field);
            var recovered=HostRecoveryService.RecoverJson(raw.ToString(),registry);
            Assert.That(recovered.Succeeded,Is.False);Assert.That(recovered.Diagnostic,Does.Contain("lua_memory_raw_fields_missing"));
        }

        [Test] public void EntryCostPreviewWithoutExecutionOwnerDoesNotCommitMemory()
        {
            State();var registry=Registry();
            Assert.That(Run(registry,"MemoryA").Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            string before=RuleStateDigestService.Compute(state);
            var definition=new LuaHandlerDefinition("preview","MemoryA","",Definition(Shared,"a"),EffectHandlerRole.Primary);
            var context=new EffectEventHandlerContext(state,new RuleEvent {EventId="preview",EventType="MemoryA",PlayerId=1},new EffectNodeRuntimeState {EffectId="preview",PlayerId=1},null);
            var call=new AbilityActionCallContext {State=state,SourceRef=new AbilitySourceRef(),Action=new AbilityActionDeclaration()};
            typeof(SourceFactsLuaCatalog).GetMethod("BindHostCall",BindingFlags.Static|BindingFlags.NonPublic)
                .Invoke(null,new object[] {context,call,definition,registry});
            typeof(LuaEffectEventDispatcher).GetMethod("Invoke",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(new LuaEffectEventDispatcher(registry),new object[] {definition,context});
            Assert.That(Number(state.EffectRuntime.LuaMemories.Single().Value,"count"),Is.EqualTo(1));
            Assert.That(RuleStateDigestService.Compute(state),Is.EqualTo(before));
        }
    }
}
