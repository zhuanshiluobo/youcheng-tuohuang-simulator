using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Application.Sessions;
using YC.Domain.Interactions;
using YC.Presentation.Workflows;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>API-08规则隔离：真实Lua、基础操作、请求与冷恢复；不计正常页面验收。</summary>
    public sealed class InfluenceOperationSelectionV3Tests
    {
        private const string Hash = "api08.influence.selection.v3.isolated";
        private IsolatedTestArtifacts artifacts;
        private GameState state;
        private GameMapDefinition map;
        private EffectRegistry registry;

        [SetUp] public void Begin()
        {
            artifacts = IsolatedTestArtifacts.BeginCase();
            map = StaticMapDefinitions.CreateThreePlayerMap();
            registry = new EffectRegistry { ContentPackHash = Hash };
            ResourceEffectExecutor.Register(registry);
            GenericLuaEffectExecutor.Register(registry);
            ItemTransferEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, new InfluenceService(new MapQueryService(map)));
            var initial = new GameState {
                GameId = "api08.influence.selection.v3.isolated", ContentPackHash = Hash, MapId = map.MapId,
                Round = 1, ActionRound = 1, Phase = GamePhase.ActionRound1, StartPlayerId = 1, CurrentPlayerId = 1,
                LaunchPlayerOrder = new List<int> { 1, 2 },
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, ContentPackHash = Hash, PlayerCount = 2 },
                Players = new List<PlayerState> {
                    new PlayerState { PlayerId = 1, Resources = new ResourceSet { GoldVoucher = 20 } },
                    new PlayerState { PlayerId = 2, Resources = new ResourceSet { GoldVoucher = 20 } } }
            };
            var recipe = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", PerPlayer = true, Quantity = 6, InitialZoneRole = "supply" }
            };
            foreach (var location in map.Locations.Where(l => !l.IsRedZone))
                recipe.Add(new ItemRecipeEntry { EntryId = "resource_" + location.LocationId, DefinitionId = "core.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + location.LocationId });
            initial.ContentDefinitions = new GameContentDefinitions { Components = new List<ComponentContentDefinition> {
                new ComponentContentDefinition { DefinitionId="infected.component.company_marker",TypeId="infected.company_marker",QuantityPolicy="finite",
                    CubeAppearance=new CubeAppearanceDeclaration {Color=new CubeColorDeclaration {Mode="fixed",Name="Orange",Rgb="D97000"}} } } };
            recipe.Add(new ItemRecipeEntry {EntryId="company",DefinitionId="infected.component.company_marker",TypeId="infected.company_marker",Quantity=3,InitialZoneRole="company_markers"});
            state = new ItemZoneStateFactory().CreateForSetup(initial, map, recipe);
            state.Map.OpenLocationIds.AddRange(map.Locations.Where(l => !l.IsRedZone).Select(l => l.LocationId));
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            artifacts.WriteLabeledText("隔离边界", "人工初始化物品，仅经实际Lua、编译器、正式Condition请求和真实影响力操作定位通用合同；不是正式P05/P06或正常游戏验收。");
        }
        [TearDown] public void End()
        {
            if (state != null) artifacts.WriteLabeledText("最终真实状态", JsonConvert.SerializeObject(state));
            artifacts.Finish();
        }
        private EffectTreeExecutor Engine() => new EffectTreeExecutor(state, registry);
        private void Pump()
        {
            var result = Engine().RunUntilQuiescent();
            if (result.Faulted) artifacts.WriteLabeledText("首次故障状态", JsonConvert.SerializeObject(state));
            Assert.That(result.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private EffectNodeRuntimeState Run(EffectSpec spec)
        {
            string id = Engine().CreateRoot(spec); Pump();
            return state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id);
        }
        private string Place(int ordinal)
        {
            var slots = map.Locations.Where(l => !l.IsRedZone).SelectMany(l => Enumerable.Range(0, l.InfluenceSlotCount)
                .Select(i => InfluenceService.GetLocationSlotId(l.LocationId, i))).ToList();
            return Value(Run(InfluenceEffectSpecFactory.PlaceInfluence(1, slots[ordinal])).NormalizedResult, "influenceId").ReferenceId;
        }
        private void Restore(string label)
        {
            artifacts.WriteLabeledText(label + "恢复前原始状态", JsonConvert.SerializeObject(state));
            var result = artifacts.Recover(state, label, registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic);
            state = result.State;
        }
        private void Answer(string value)
        {
            var request = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId,
                request.StateRevision, NormalizedValue.CreateString(value), out string reason), Is.True, reason);
            Pump();
        }
        private string DomainSignature() => JsonConvert.SerializeObject(new {
            items = state.ItemZones.Items.OrderBy(i => i.ItemId).Select(i => new { i.ItemId, i.TypeId, i.ZoneId, i.SlotId, i.OriginalSupplyZoneId }),
            zones = state.ItemZones.Zones, orders = state.ItemZones.Orders,
            influences = state.Map.Influences.Select(i => new { i.InfluenceId, i.PlayerId, i.SlotId, i.LocationId, i.RouteId }),
            players = state.Players.Select(p => new { p.PlayerId, p.Score, p.Resources })
        });
        private int[] DepartureCounts() => new[] {
            state.EffectRuntime.RuleEvents.Count(e => e.EventType == InfluenceEventTypeIds.InfluenceRemoved),
            state.EffectRuntime.InfluenceDepartures.Count, state.EffectRuntime.InfluenceDepartureBatches.Count,
            state.EffectRuntime.DeferredResponses.Count
        };
        private LuaInvocationResult InvokeV3(string operation, string fields="")
        {
            string code="return function(ctx) return {Effect."+operation+"({apiVersion=3,targetMode='select',executingPlayer=ctx.playerId"+fields+"})} end";
            artifacts.WriteLabeledText("实际Lua声明",code);
            return new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Hash,"api08.selection","api08.selection","1.0.0",code,LuaContentHasher.ComputeSha256(code)),
                new LuaInvocationContext("api08.input","Api08Isolated","p1",0,"1.0.0",2,0,0));
        }
        private EffectSpec CompileV3(string operation,string fields="")
        {
            var invocation=InvokeV3(operation,fields);Assert.That(invocation.IsSuccess,Is.True,invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation,new LuaEffectCompilationContext{PlayerId=1},out var specs,out var failure),Is.True,failure?.Diagnostic);
            return specs.Single();
        }
        [TestCase("RemoveInfluence")][TestCase("MoveInfluence")][TestCase("ReplaceInfluence")]
        public void BasicOperationOwnsItsChoiceAndRestoresWithoutSelectAction(string operation)
        {
            Place(0);var node=Run(CompileV3(operation,",subjectFilter='self',allowDecline=false"));string id=node.EffectId;
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId),Is.False);
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
            Assert.That(request.OwnerEffectId,Is.EqualTo(id));Assert.That(Text(request.PromptParameters,"interactionSemantic"),Is.EqualTo(InfluenceSelectionFlow.InteractionSemantic));
            Restore("基础操作待选"+operation);request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");Answer(request.CandidateIds.First());
            Assert.That(Engine().GetNode(id).Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(Number(Engine().GetNode(id).NormalizedResult,"completedCount"),Is.EqualTo(1));
            Assert.That(state.Map.Influences.Count,Is.EqualTo(operation=="RemoveInfluence"?0:1));Restore("基础操作完成"+operation);
        }
        [TestCase(",count=0")][TestCase(",count=17")][TestCase(",count=1.5")][TestCase(",count=1,requiredCount=2")]
        [TestCase(",ownerFilter='self'")][TestCase(",player=ctx.playerId")][TestCase(",candidateScope='routes'")]
        [TestCase(",distinctPolicy='owner'")][TestCase(",targetInfluence='forged'")][TestCase(",targetMode='explicit'")]
        [TestCase(",allowDecline='true'")][TestCase(",excludedSubjectKeys={'player:0'}")]
        [TestCase(",kind='effect_spec'")][TestCase(",effectTypeId='effect.flow.choice'")]
        public void AmbiguousOrInvalidSelectionFieldsFailBeforeAnyEffects(string fields)
        {
            string before=DomainSignature();Assert.That(InvokeV3("RemoveInfluence",fields).IsSuccess,Is.False);Assert.That(DomainSignature(),Is.EqualTo(before));
        }
        [Test] public void StrictSelectionRequiresEnoughDistinctSubjectsWithoutRemovingAnything()
        {
            Place(0);Place(1);string before=DomainSignature();var node=Run(CompileV3("RemoveInfluence",",count=2,requiredCount=2,requireCompleteSelection=true,distinctPolicy='subject'"));
            Assert.That(node.Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(Number(node.NormalizedResult,"actualRemovedCount"),Is.Zero);
            Assert.That(DomainSignature(),Is.EqualTo(before));Assert.That(state.EffectRuntime.InteractionRequests.Where(r=>r.Status=="open"),Is.Empty);Restore("主体不足零移除");
        }
        [Test] public void StrictSelectionPreservesExclusionsThroughChildAndRecovery()
        {
            string first=Place(0),second=Place(1);var spec=CompileV3("RemoveInfluence",",count=2,requiredCount=2,requireCompleteSelection=true,subjectFilter='self',excludedSubjectKeys={'player:2'}");
            string id=Run(spec).EffectId;Restore("严格两枚待选");var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,Strings(new[]{first,second}),out string reason),Is.True,reason);Pump();
            var child=state.EffectRuntime.EffectNodes.Single(n=>n.ParentEffectId==id && Value(n.NormalizedArguments,"targets")!=null);
            Assert.That(Text(child.NormalizedArguments,"subjectFilter"),Is.EqualTo("self"));Assert.That(Value(child.NormalizedArguments,"excludedSubjectKeys").Items.Select(v=>v.StringValue),Is.EqualTo(new[]{"player:2"}));
            Assert.That(Number(Engine().GetNode(id).NormalizedResult,"actualRemovedCount"),Is.EqualTo(2));Assert.That(state.Map.Influences,Is.Empty);Restore("严格两枚完成");
        }
        [Test] public void MoveCannotRequestCompleteSelectionOrAnUnsupportedInteractionType()
        {
            Assert.That(InvokeV3("MoveInfluence",",requireCompleteSelection=true,count=2,requiredCount=2").IsSuccess,Is.False);
            Assert.That(InvokeV3("MoveInfluence",",interactionType='unimplemented.custom'").IsSuccess,Is.False);
        }
        private string PlaceFor(int player,bool company=false,bool route=false)
        {
            var subject=company?ItemSubjectRef.Company("stonehoove_security"):ItemSubjectRef.Player(player);
            string slot=registry.InfluenceService.GetLegalPlacementSlotIds(state,subject,company?"infected.company_marker":"core.player_marker")
                .First(id=>route?id.StartsWith("route:",StringComparison.Ordinal):id.StartsWith("location:",StringComparison.Ordinal));
            var owner=company?new RuleSubjectReference{SubjectType="company",DefinitionId="stonehoove_security"}:null;
            return Value(Run(InfluenceEffectSpecFactory.PlaceInfluence(player,slot,ownerSubject:owner)).NormalizedResult,"influenceId").ReferenceId;
        }
        [Test] public void MoveRefreshesInstanceCandidatesAndContinuesOnlyAfterItsChild()
        {
            string first=Place(0),second=Place(1);string root=Run(CompileV3("MoveInfluence",",count=2,requiredCount=2,distinctPolicy='instance'")).EffectId;
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");Answer(request.CandidateIds.First(id=>id.Split('|')[1]==first));
            Restore("两次移动之间");request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
            Assert.That(request.CandidateIds.All(id=>id.Split('|')[1]==second),Is.True);Answer(request.CandidateIds.First());
            Assert.That(Number(Engine().GetNode(root).NormalizedResult,"completedCount"),Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType==InfluenceEventTypeIds.InfluenceMoved),Is.EqualTo(2));Restore("两次移动完成不重放");
        }
        [Test] public void OpponentFilterIncludesTheRealCompanyWhilePlayerExclusionRemovesOnlyThatPlayer()
        {
            string own=PlaceFor(1),other=PlaceFor(2),company=PlaceFor(1,company:true);
            Run(CompileV3("RemoveInfluence",",subjectFilter='opponent',excludedSubjectKeys={'player:2'}"));
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");
            Assert.That(request.CandidateIds,Is.EqualTo(new[]{company}));Answer(company);Restore("公司真实移除返供");
            Assert.That(state.Map.Influences.Select(i=>i.InfluenceId),Is.EquivalentTo(new[]{own,other}));
            var item=state.ItemZones.Items.Single(i=>i.ItemId==company);Assert.That(item.ZoneId,Is.EqualTo(item.OriginalSupplyZoneId));Assert.That(item.OwnerSubject.Kind,Is.EqualTo(ItemSubjectKind.Company));
        }
        [Test] public void FullSetRejectsTwoInstancesOfOneSubjectBeforeAcceptingAValidMixedSet()
        {
            string one=Place(0),two=Place(1),other=PlaceFor(2);string root=Run(CompileV3("RemoveInfluence",",subjectFilter='any',count=2,requiredCount=2,requireCompleteSelection=true,distinctPolicy='subject'")).EffectId;
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");string before=DomainSignature();
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,Strings(new[]{one,two}),out string reason),Is.False);
            Assert.That(reason,Is.EqualTo("influence_selection_subject_not_distinct"));Assert.That(DomainSignature(),Is.EqualTo(before));
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,Strings(new[]{one,other}),out reason),Is.True,reason);Pump();
            Assert.That(Engine().GetNode(root).Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(state.Map.Influences.Single().InfluenceId,Is.EqualTo(two));Restore("合法不同主体整组完成");
        }
        [TestCase("resource_points")][TestCase("routes")]
        public void SelectionScopeUsesTheActualSourceInsteadOfThePrompt(string scope)
        {
            string location=PlaceFor(1),route=PlaceFor(1,route:true);Run(CompileV3("RemoveInfluence",",selectionScope='"+scope+"',promptKey='custom.api08.scope'"));
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");Assert.That(request.CandidateIds,Is.EqualTo(new[]{scope=="routes"?route:location}));Answer(request.CandidateIds.Single());Restore("来源范围"+scope);
        }
        [TestCase(false)][TestCase(true)]
        public void OptionalStartKeepsTheExistingContinueDeclineContract(bool accept)
        {
            string target=Place(0);string root=Run(CompileV3("RemoveInfluence",",allowDecline=true")).EffectId;
            Assert.That(state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open").InteractionTypeId,Is.EqualTo("action.decline_effect"));Restore("可选开始确认");Answer(accept?"continue":"decline");
            if(accept)Answer(target);
            Assert.That(state.Map.Influences.Count,Is.EqualTo(accept?0:1));Assert.That(Engine().GetNode(root).Status,Is.EqualTo(accept?EffectNodeStatus.Completed:EffectNodeStatus.Failed));Restore("可选开始结果");
        }
        [Test] public void PreciseSingleCandidateKeepsOriginalPositionFactsAndRequiredOutcome()
        {
            string one=Place(0);Place(1);string slot=state.Map.Influences.Single(i=>i.InfluenceId==one).SlotId;
            var spec=CompileV3("RemoveInfluence",",candidateIds={'"+one+"'},count=1,requiredCount=1,requireCompleteSelection=true");spec.RequiredOutcome=new RequiredEffectOutcome{ResultField="completedCount",ExpectedValue=1};string root=Run(spec).EffectId;
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");Assert.That(request.CandidateIds,Is.EqualTo(new[]{one}));Answer(one);
            var facts=Value(Engine().GetNode(root).NormalizedResult,"removedInfluences").Items.Single();Assert.That(Text(facts,"influenceId"),Is.EqualTo(one));Assert.That(Text(Value(facts,"originalPosition"),"slotId"),Is.EqualTo(slot));Restore("精确候选必做收据");
        }
        [TestCase(false)][TestCase(true)]
        public void RestoreRejectsForgedSelectionProgressAndChildConstraintsWithoutEditingOriginal(bool journal)
        {
            string one=Place(0),two=Place(1);var archive=HostRecoveryService.CreateArchive(state);string root=Run(CompileV3("RemoveInfluence",",count=2,requiredCount=2,requireCompleteSelection=true,excludedSubjectKeys={'player:2'}")).EffectId;
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,Strings(new[]{one,two}),out string reason),Is.True,reason);
            for(int step=0;step<100 && !state.EffectRuntime.EffectNodes.Any(n=>n.ParentEffectId==root);step++)Assert.That(Engine().Advance(),Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Count(n=>n.ParentEffectId==root),Is.EqualTo(1));
            if(journal)HostRecoveryService.AppendJournal(archive,state,Hash);else archive=HostRecoveryService.CreateArchive(state);
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(archive));string original=raw.ToString();
            Assert.That(HostRecoveryService.RecoverJson(original,registry).Succeeded,Is.True);
            foreach(string mutation in new[]{"progress","protocol","scope","childFilter","answer","control","requestCount"})
            {
                var bad=(JObject)raw.DeepClone();var saved=journal?bad["Journal"]["Entries"].Last["StateAfter"]:bad["Snapshot"]["State"];var nodes=saved["EffectRuntime"]["EffectNodes"];
                var parent=nodes.Single(n=>(string)n["EffectId"]==root);var child=nodes.Single(n=>(string)n["ParentEffectId"]==root);
                JToken Arg(JToken n,string key)=>n["NormalizedArguments"]["Properties"].Single(x=>(string)x["Name"]==key)["Value"];
                JToken Data(string key)=>parent["ContinuationData"]["Properties"].Single(x=>(string)x["Name"]==key);
                if(mutation=="progress")Data("nextIndex")["Value"]["IntegerValue"]=2;
                if(mutation=="protocol")Data("selectionProtocolVersion").Remove();
                if(mutation=="scope")((JArray)parent["NormalizedArguments"]["Properties"]).Add(JObject.FromObject(new NormalizedValueEntry{Name="selectionScope",Value=NormalizedValue.CreateString("routes")}));
                if(mutation=="childFilter")Arg(child,"subjectFilter")["StringValue"]="any";
                if(mutation=="answer")((JArray)saved["EffectRuntime"]["InteractionRequests"]).Single(r=>(string)r["OwnerEffectId"]==root)["NormalizedAnswer"]["Items"][0]["StringValue"]=two;
                if(mutation=="control")parent["AllowDecline"]=true;
                if(mutation=="requestCount")saved["EffectRuntime"]["InteractionRequests"].Single(r=>(string)r["OwnerEffectId"]==root)["MaxSelections"]=3;
                artifacts.WriteLabeledText("选择恢复反例"+mutation,bad.ToString());Assert.That(HostRecoveryService.RecoverJson(bad.ToString(),registry).Succeeded,Is.False,mutation);
            }
            Assert.That(raw.ToString(),Is.EqualTo(original));Pump();Assert.That(state.Map.Influences,Is.Empty);Restore("原始未修改选择合法完成");
        }
        [Test] public void SelectedSourceThatActuallyMovesOutsideScopeCannotBePartlyRemoved()
        {
            string one=Place(0),two=Place(1);string root=Run(CompileV3("RemoveInfluence",",selectionScope='resource_points',count=2,requiredCount=2,requireCompleteSelection=true")).EffectId;
            var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,Strings(new[]{one,two}),out string reason),Is.True,reason);
            // 使用真实新根操作抢先移动；不暂停、重排或修改已有生产节点来制造窗口。
            string slot=registry.InfluenceService.GetLegalPlacementSlotIds(state,1).First(id=>id.StartsWith("route:",StringComparison.Ordinal));
            string competing=Engine().CreateRoot(InfluenceEffectSpecFactory.MoveInfluence(1,two,slot));
            for(int step=0;step<200 && Engine().GetNode(competing).Status!=EffectNodeStatus.Completed;step++)Assert.That(Engine().Advance(),Is.True);
            Assert.That(Engine().GetNode(competing).Status,Is.EqualTo(EffectNodeStatus.Completed));string before=DomainSignature();Restore("目标真实移出原范围");Pump();
            Assert.That(Engine().GetNode(root).Status,Is.EqualTo(EffectNodeStatus.Failed));Assert.That(Number(Engine().GetNode(root).NormalizedResult,"actualRemovedCount"),Is.Zero);Assert.That(DomainSignature(),Is.EqualTo(before));
        }
        [TestCase("RemoveInfluence","apiVersion=2,targets={},requiredCount=1,selectionScope='routes'")]
        [TestCase("RemoveInfluence","executingPlayer=ctx.playerId,targetInfluence='x',count=2")]
        [TestCase("MoveInfluence","targetInfluence='x',targetSlotId='route:x:0',count=2")]
        [TestCase("ReplaceInfluence","targetInfluence='x',replacementSource='x',replacementOwner=ctx.playerId,placementFailurePolicy='keep_removal',distinctPolicy='subject'")]
        public void SelectionFieldsCannotBeSilentlyLostInAnOlderExplicitContract(string operation,string fields)
        {
            string source="return function(ctx) return {Effect."+operation+"({executingPlayer=ctx.playerId,"+fields+"})} end";
            var result=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Hash,"api08.explicit","api08.explicit","1.0.0",source,LuaContentHasher.ComputeSha256(source)),new LuaInvocationContext("api08.explicit","Api08Isolated","p1",0,"1.0.0",2,0,0));
            Assert.That(result.IsSuccess,Is.False);
        }
        [Test] public void ARequiredObserverKeepsItsOwnScopeAndRestoresAfterSelectionTerminal()
        {
            string target=PlaceFor(1),extra=PlaceFor(1,route:true);
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("api08.after-selection","EffectCompleted","","api08.after-selection",e=>
            {
                if(!InfluenceSelectionFlow.IsSelection(e.OwnerNode.EffectTypeId,e.OwnerNode.NormalizedArguments))return Array.Empty<EffectSpec>();
                return new[]{EffectSpec.Create(LuaDomainEffectTypeIds.Choice,Object("player",NormalizedValue.CreateString("1"),"options",Strings(new[]{"response_done"}),"interactionType",NormalizedValue.CreateString("api08.response")),1),InfluenceEffectSpecFactory.RemoveInfluence(1,extra)};
            },EffectHandlerRole.Observer));
            string root=Run(CompileV3("RemoveInfluence",",selectionScope='resource_points'")).EffectId;Answer(target);
            Assert.That(state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open").InteractionTypeId,Is.EqualTo("api08.response"));Restore("选择后必要观察者待答");Answer("response_done");
            Assert.That(Engine().GetNode(root).Status,Is.EqualTo(EffectNodeStatus.Completed));Assert.That(Number(Engine().GetNode(root).NormalizedResult,"completedCount"),Is.EqualTo(1));Assert.That(state.Map.Influences,Is.Empty);Restore("观察者作用域独立完成");
        }
        [TestCase("count",4294967297L)][TestCase("requiredCount",4294967296L)][TestCase("apiVersion",4294967299L)]
        public void RawLongIntegersCannotWrapIntoAValidSelectionBoundary(string field,long value)
        {
            var spec=CompileV3("RemoveInfluence");
            var entry=spec.NormalizedArguments.Properties.SingleOrDefault(p=>p.Name==field);
            var raw=new NormalizedValue {Kind=NormalizedValueKind.Integer,IntegerValue=value};
            if(entry==null)spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry{Name=field,Value=raw});else entry.Value=raw;
            Assert.That(InfluenceSelectionFlow.ValidateArguments(spec.EffectTypeId,spec.NormalizedArguments),Is.Not.Empty);
        }
        [TestCase("RemoveInfluence")][TestCase("ReplaceInfluence")]
        public void CustomPromptRoutesOnlyFromFormalOperationRequestSemantics(string operation)
        {
            Place(0);Run(CompileV3(operation,",interactionType='character.ability.target',promptKey='custom.api08.target'"));
            var request=InteractionRequestProjector.ProjectForPlayer(state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open"),1);
            var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("YC.Presentation.EffectDialogSelectionSpec",false)).Single(t=>t!=null);
            var method=type.GetMethods(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Single(m=>m.Name=="IsInfluenceMapTarget" && m.GetParameters().Length==1 && m.GetParameters()[0].ParameterType==typeof(InteractionRequestProjection));
            Assert.That((bool)method.Invoke(null,new object[]{request}),Is.True);
            request.PromptParameters=Object();Assert.That((bool)method.Invoke(null,new object[]{request}),Is.False);
        }
        [TestCase(",destination='player_supply'")][TestCase(",causeKind='direct'")][TestCase(",reasonId=7")]
        public void StrictExplicitModeRejectsFieldsThatTheRuntimeDoesNotImplement(string extra)
        {
            string source="return function(ctx) return {Effect.RemoveInfluence({apiVersion=2,executingPlayer=ctx.playerId,targets={},requiredCount=1"+extra+"})} end";
            var result=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Hash,"api08.strict.fields","api08.strict.fields","1.0.0",source,LuaContentHasher.ComputeSha256(source)),new LuaInvocationContext("api08.strict.fields","Api08Isolated","p1",0,"1.0.0",2,0,0));Assert.That(result.IsSuccess,Is.False);
        }
        [TestCase("nested")][TestCase("settlement")][TestCase("deferred")][TestCase("departure_offer")][TestCase("place_mapping")]
        public void PendingSelectionSpecsCannotLoseTheirExecutorOrControlContract(string carrier)
        {
            Place(0);Assert.That(ContentPrimitiveEffectExecutor.TryValidateContentContinuations(state,out string reason),Is.True,reason);
            var declared=CompileV3("RemoveInfluence");var saved=new EffectSpecRuntimeState{EffectTypeId=declared.EffectTypeId,PlayerId=declared.PlayerId,NormalizedArguments=declared.NormalizedArguments.Clone(),AllowDecline=true};
            if(carrier=="nested")state.EffectRuntime.EffectNodes[0].NestedEffects.Add(saved);
            if(carrier=="settlement")state.EffectRuntime.EffectNodes[0].SettlementWindow=new SettlementWindowRuntimeState{Entries=new List<SettlementEntryRuntimeState>{new SettlementEntryRuntimeState{Effects=new List<EffectSpecRuntimeState>{saved}}}};
            if(carrier=="deferred")state.EffectRuntime.DeferredResponses.Add(new DeferredResponseState{PendingBody=new List<EffectSpecRuntimeState>{saved}});
            if(carrier=="departure_offer")state.EffectRuntime.InfluenceDepartureBatches.Add(new InfluenceDepartureBatch{Offers=new List<DepartureBatchOffer>{new DepartureBatchOffer{Body=saved}}});
            if(carrier=="place_mapping")state.EffectRuntime.InfluencePlaceMappings.Add(new InfluencePlaceMapping{OriginalSpec=saved});
            Assert.That(ContentPrimitiveEffectExecutor.TryValidateContentContinuations(state,out reason),Is.False);Assert.That(reason,Is.EqualTo("influence_selection_executor_mismatch"));
        }
        [TestCase("AllowDecline",false)][TestCase("AllowDecline",true)]
        [TestCase("DecisionPlayerId",false)][TestCase("FlowStage",true)]
        [TestCase("progressInteger",false)][TestCase("progressInteger",true)]
        [TestCase("requestFlag",false)][TestCase("requestFlag",true)]
        public void RawMissingSelectionFieldsCannotBeReconstructedFromDefaults(string field,bool journal)
        {
            Place(0);var archive=HostRecoveryService.CreateArchive(state);string root=Run(CompileV3("RemoveInfluence")).EffectId;
            if(journal)HostRecoveryService.AppendJournal(archive,state,Hash);else archive=HostRecoveryService.CreateArchive(state);
            var raw=JObject.Parse(HostRecoveryService.SerializeArchive(archive));string original=raw.ToString();Assert.That(HostRecoveryService.RecoverJson(original,registry).Succeeded,Is.True);
            var saved=journal?raw["Journal"]["Entries"].Last["StateAfter"]:raw["Snapshot"]["State"];var node=(JObject)saved["EffectRuntime"]["EffectNodes"].Single(n=>(string)n["EffectId"]==root);
            if(field=="progressInteger")((JObject)node["ContinuationData"]["Properties"].Single(p=>(string)p["Name"]=="nextIndex")["Value"]).Remove("IntegerValue");
            else if(field=="requestFlag")((JObject)saved["EffectRuntime"]["InteractionRequests"].Single(r=>(string)r["OwnerEffectId"]==root)).Remove("AllowDecline");
            else node.Remove(field);
            var result=HostRecoveryService.RecoverJson(raw.ToString(),registry);Assert.That(result.Succeeded,Is.False);Assert.That(result.Diagnostic,Does.Contain("influence_selection_"));
            artifacts.WriteLabeledText("原始选择字段缺失"+field,raw.ToString());
        }
        [TestCase("RemoveInfluence","effect.resource.choose_type","api08.custom.map")]
        [TestCase("ReplaceInfluence","effect.resource.choose_type","api08.custom.map")]
        [TestCase("MoveInfluence","effect.resource.choose_type","")]
        [TestCase("RemoveInfluence","effect.resource.sell","api08.custom.map")]
        [TestCase("RemoveInfluence","effect.resource.choose_type",ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId)]
        public void FormalSelectionWinsOverLegacyHintRoutingInTheActualRenderer(string operation,string prompt,string interactionType)
        {
            Place(0);Run(CompileV3(operation,",promptKey='"+prompt+"'"+(interactionType.Length==0?"":",interactionType='"+interactionType+"'")));
            UnityEngine.GameObject host=null;IInteractionRequestRenderer renderer=null;
            try
            {
                host=new UnityEngine.GameObject("API08隔离地图请求消费者",typeof(UnityEngine.RectTransform));
                var registryType=Type.GetType("YC.Presentation.GameplayDialogRegistry, Assembly-CSharp",true);
                var prefab=UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
                var uiRegistry=prefab.GetComponentInChildren(registryType,true);
                var dialogType=Type.GetType("YC.Presentation.CharacterCardEffectChoiceDialog, Assembly-CSharp",true);
                var dialog=Activator.CreateInstance(dialogType,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic,null,new object[]{uiRegistry,(UnityEngine.RectTransform)host.transform},null);
                var commands=new List<YC.Domain.Commands.GameCommand>();var highlights=new List<YC.Presentation.Workflows.WorkflowHighlight>();
                renderer=(IInteractionRequestRenderer)Activator.CreateInstance(Type.GetType("YC.Presentation.CharacterAbilityInteractionUiCoordinator, Assembly-CSharp",true),new object[]{
                    new Func<GameState>(()=>state),new Func<int>(()=>1),dialog,new Action<IReadOnlyList<YC.Presentation.Workflows.WorkflowHighlight>>(x=>{highlights.Clear();highlights.AddRange(x);}),
                    new Action(()=>highlights.Clear()),new Action<YC.Domain.Commands.GameCommand>(commands.Add),new Action<string>(_=>{})});
                var request=state.EffectRuntime.InteractionRequests.Single(r=>r.Status=="open");var projection=InteractionRequestProjector.ProjectForPlayer(request,1);
                Assert.That(renderer.CanRender(projection),Is.True);Assert.That(renderer.CanRender(InteractionRequestProjector.ProjectForPlayer(request,2)),Is.False);renderer.Render(projection);
                Assert.That(highlights.Count,Is.GreaterThan(0));Assert.That((bool)dialogType.GetProperty("IsShowing",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).GetValue(dialog),Is.False);
                Assert.That(commands,Is.Empty,"显示地图候选不得发送业务命令。");
            }
            finally { if(renderer!=null)renderer.Clear();if(host!=null)UnityEngine.Object.DestroyImmediate(host); }
        }
        [Test] public void ImmediateDispatchOwnsTwoActualMovesAcrossColdRecovery()
        {
            Place(0);
            string root=Run(EffectSpec.Create(LuaDomainEffectTypeIds.ExecuteMainAction,Object(
                "player",NormalizedValue.CreateString("1"),"executionMode",NormalizedValue.CreateString("immediate"),
                "allowedActionTypes",NormalizedValue.CreateArray(new[]{NormalizedValue.CreateString("dispatch")})),1)).EffectId;
            Answer("dispatch");
            var selection=state.EffectRuntime.EffectNodes.Single(n=>n.ParentEffectId==root && n.EffectTypeId==InfluenceEffectTypeIds.MoveInfluence);
            Assert.That(InfluenceSelectionFlow.IsSelection(selection.EffectTypeId,selection.NormalizedArguments),Is.True);
            Assert.That(Number(selection.NormalizedArguments,"count"),Is.EqualTo(2));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n=>n.EffectTypeId==ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId),Is.False);
            Restore("API09即时分派第一次待答");
            for(int i=0;i<2;i++)
            {
                Answer(state.EffectRuntime.InteractionRequests.Single(q=>q.Status=="open").CandidateIds.First());
                if(i==0)Restore("API09即时分派第二次待答");
            }
            Assert.That(Engine().GetNode(root).Status,Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Number(Engine().GetNode(selection.EffectId).NormalizedResult,"completedCount"),Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e=>e.EventType==InfluenceEventTypeIds.InfluenceMoved),Is.EqualTo(2));
            Restore("API09即时分派完成");
        }
        [Test] public void RetiredSelectorPendingAnswerIsRejectedBeforeAnyMutation()
        {
            Place(0); var node = Run(CompileV3("RemoveInfluence"));
            var request = state.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
            // 只损坏隔离副本，模拟仍在内存的旧待答，不改写或续算真实旧存档。
            node.EffectTypeId = ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionTypeId;
            node.NormalizedArguments = Object("player", NormalizedValue.CreateString("1"), "operation", NormalizedValue.CreateString("remove"), "count", NormalizedValue.CreateInteger(1));
            request.InteractionTypeId = "lua.choice"; request.PromptParameters = Object();
            string before = JsonConvert.SerializeObject(state);
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString(request.CandidateIds.First()), out string reason), Is.False);
            Assert.That(reason, Is.EqualTo(ContentPrimitiveEffectExecutor.RetiredInfluenceSelectionReason));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            artifacts.WriteLabeledText("API10旧待答拒绝原始副本", before);
        }
        [Test] public void RawV3DeclarationUsesTheSameGuardedBasicOperation()
        {
            string target=Place(0);const string source="return function(ctx) return {{kind='effect_spec',effectTypeId='effect.influence.remove',apiVersion=3,targetMode='select',executingPlayer=ctx.playerId}} end";
            var invocation=new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition(Hash,"api08.raw","api08.raw","1.0.0",source,LuaContentHasher.ComputeSha256(source)),new LuaInvocationContext("api08.raw","Api08Isolated","p1",0,"1.0.0",2,0,0));
            Assert.That(invocation.IsSuccess,Is.True,invocation.Diagnostic);Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation,new LuaEffectCompilationContext{PlayerId=1},out var specs,out var failure),Is.True,failure?.Diagnostic);
            string id=Run(specs.Single()).EffectId;Answer(target);Assert.That(Engine().GetNode(id).Status,Is.EqualTo(EffectNodeStatus.Completed));Restore("原始v3规格合法完成");
        }
    }
}
