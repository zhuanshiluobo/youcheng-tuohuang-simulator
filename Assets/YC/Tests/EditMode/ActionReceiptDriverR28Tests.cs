using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Events;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // 驱动记账单位边界；私有最小状态只表达回执/节点状态，不模拟或签署生产库存规则。
    // 本轮017不运行；根汇合002后用准确过滤器执行。
    public sealed class ActionReceiptDriverR28Tests
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static Type T(string n)=>Type.GetType("YC.Presentation."+n+", Assembly-CSharp",true);
        static object Read(object o,string n)=>o.GetType().GetField(n,Flags)?.GetValue(o)??o.GetType().GetProperty(n,Flags)?.GetValue(o);
        static void Set(object o,string n,object v){var f=o.GetType().GetField(n,Flags);if(f!=null)f.SetValue(o,v);else o.GetType().GetProperty(n,Flags).SetValue(o,v);}
        static object Call(object o,string n,params object[] args){try{return o.GetType().GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}catch(TargetInvocationException e){ExceptionDispatchInfo.Capture(e.InnerException??e).Throw();throw;}}
        GameObject driverRoot,mobileRoot;object driver;GameState state;List<object> commands;IsolatedTestArtifacts artifacts;string output;
        WorkflowSubmissionResult Accepted()=>new WorkflowSubmissionResult(CommandResult.SuccessResult(new List<GameEvent>(),"隔离受理"),true);
        object Begin(GameCommandKind kind=GameCommandKind.DeployInfluence,string key="action:deploy",InteractionRequestProjection request=null)=>
            Call(driver,"BeginObservedAction",kind,1,"",kind==GameCommandKind.DeployInfluence?"location:A-01:1":"",key,request);
        GameCommand Command(GameCommandKind kind=GameCommandKind.DeployInfluence)=>new GameCommand{CommandId=Guid.NewGuid().ToString("N"),Kind=kind,PlayerId=1,TargetId=kind==GameCommandKind.DeployInfluence?"location:A-01:1":""};
        int Coverage(string key)=>((Dictionary<string,int>)Read(Read(driver,"campaignVersion"),"Completed")).TryGetValue(key,out int count)?count:0;
        void Record(GameCommand command,WorkflowSubmissionResult result,int before,int after)=>commands.Add(new{Command=command,Result=result,BeforeRevision=before,AfterRevision=after});
        [SetUp] public void Prepare()
        {
            artifacts=IsolatedTestArtifacts.BeginCase();string root=Environment.GetEnvironmentVariable("QY017_R28_OUTPUT");
            Assert.That(Path.IsPathRooted(root??""),Is.True,"联合运行须设置本轮全新绝对输出目录");output=Path.Combine(root,TestContext.CurrentContext.Test.MethodName);Assert.That(Directory.Exists(output),Is.False);Directory.CreateDirectory(output);
            state=new GameState{GameId="r28.receipt.isolated",ContentPackHash="r28.private",MapId=StaticMapDefinitions.FourPlayerMapId,Round=1,Phase=GamePhase.ActionRound1,CurrentPlayerId=1,ItemZones=null};
            state.Players.Add(new PlayerState{PlayerId=1,CityLocationId="A-03",RemainingMainActionsThisTurn=1});
            mobileRoot=new GameObject("R28未启动控制器");mobileRoot.SetActive(false);var mobile=mobileRoot.AddComponent(T("MobileCityInteractionController"));Set(mobile,"localPlayerId",1);
            driverRoot=new GameObject("R28记账单位边界");driverRoot.SetActive(false);driver=driverRoot.AddComponent(T("Validation.HotseatUiValidationDriver"));
            Set(driver,"mobile",mobile);Set(driver,"session",new GameSession(state));Set(driver,"output",output);Set(driver,"selectedCase","full-game");
            foreach(string name in new[]{"campaignGame","campaignVersion"})Set(driver,name,Activator.CreateInstance(driver.GetType().GetField(name,Flags).FieldType,true));
            commands=(List<object>)Read(driver,"commands");
        }
        [TearDown] public void Release()
        {
            if(driver!=null)foreach(var writer in ((IDictionary)Read(driver,"appendLogs")).Values.Cast<StreamWriter>())writer.Dispose();
            if(driverRoot!=null)UnityEngine.Object.DestroyImmediate(driverRoot);if(mobileRoot!=null)UnityEngine.Object.DestroyImmediate(mobileRoot);artifacts?.Finish();
        }
        [Test] public void AccurateRejectionStopsBeforeCompletionAndBlocksSameRevisionRetry()
        {
            var action=Begin();var command=Command();var rejected=new WorkflowSubmissionResult(CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InsufficientInfluence,"insufficientsupply")),false);
            Record(command,rejected,0,0);
            var error=Assert.Throws<InvalidOperationException>(()=>Call(driver,"FinishObservedAction",action));Assert.That(error.Message,Does.Contain("insufficientsupply"));
            Assert.That(Coverage("action:deploy"),Is.Zero);Assert.That(Read(Read(driver,"campaignGame"),"Completed"),Is.Empty);
            Assert.Throws<InvalidOperationException>(()=>Begin());Assert.That(commands.Count,Is.EqualTo(1));Assert.That(state.EffectRuntime.StateRevision,Is.Zero);
            var proof=File.ReadAllText(Path.Combine(output,"主行动首个拒绝-"+command.CommandId+".json"));Assert.That(proof,Does.Contain(command.CommandId).And.Contain("insufficientsupply"));
            foreach(var invalid in new[]{new WorkflowSubmissionResult(CommandResult.SuccessResult(new List<GameEvent>(),"未Applied"),false),rejected})
                Assert.That((bool)driver.GetType().GetMethod("AccurateActionReceipt",Flags).Invoke(null,new object[]{command,invalid,command.Kind,1,"",command.TargetId}),Is.False);
            Assert.That((bool)driver.GetType().GetMethod("AccurateActionReceipt",Flags).Invoke(null,new object[]{command,Accepted(),GameCommandKind.DispatchInfluence,1,"",command.TargetId}),Is.False);
            Assert.That((bool)driver.GetType().GetMethod("AccurateActionReceipt",Flags).Invoke(null,new object[]{command,Accepted(),command.Kind,2,"",command.TargetId}),Is.False);
            Assert.That((bool)driver.GetType().GetMethod("AccurateActionReceipt",Flags).Invoke(null,new object[]{command,Accepted(),command.Kind,1,"","location:A-02:0"}),Is.False);
            artifacts.WriteLabeledText("准确拒绝不计不重试",JsonConvert.SerializeObject(new{command,rejected,error.Message,SameRevisionRetryBlocked=true,Completed=Coverage("action:deploy"),ProductionSupplyRuleRetested=false}));
        }
        [Test] public void SuccessfulAdmissionWaitsForEffectAndActualPlacementThenCountsOnce()
        {
            var action=Begin();var command=Command();var receipt=Accepted();
            var node=new EffectNodeRuntimeState{EffectId="r28.deploy.effect",ParentEffectId="existing.main.window",PlayerId=1,Status=EffectNodeStatus.Blocked,OperationContext=new OperationCommandContext{SourceCommandId=command.CommandId,ActingPlayerId=1}};
            state.EffectRuntime.EffectNodes.Add(node);state.Map.Influences.Add(new InfluencePlacement{InfluenceId="r28.real-in-private-fixture",PlayerId=1,SlotId=command.TargetId});state.EffectRuntime.StateRevision=1;
            Record(command,receipt,0,1);Call(driver,"RecordPlayCommand",command,receipt);
            var progress=Call(driver,"PlayProgress",1);Assert.That(Read(progress,"Deployments"),Is.EqualTo(0));
            Assert.That(Call(driver,"FinishObservedAction",action),Is.EqualTo(false));Assert.That(Coverage("action:deploy"),Is.Zero);Assert.That(Read(progress,"Deployments"),Is.EqualTo(0));
            node.Status=EffectNodeStatus.Completed;state.EffectRuntime.StateRevision=2;Call(driver,"ObserveActionReceipts");
            Assert.That(Coverage("action:deploy"),Is.EqualTo(1));Assert.That(Read(progress,"Deployments"),Is.EqualTo(1));Call(driver,"ObserveActionReceipts");Assert.That(Coverage("action:deploy"),Is.EqualTo(1));Assert.That(Read(progress,"Deployments"),Is.EqualTo(1));
            artifacts.WriteLabeledText("受理与真实完成分开",JsonConvert.SerializeObject(new{command,receipt,node,Completed=Coverage("action:deploy"),Progress=progress,IsolatedAccountingOnly=true}));
        }
        [Test] public void AnsweredEntryDoesNotClaimExplorationUntilItsOwnerSettles()
        {
            var request=new InteractionRequest{InteractionId="r28.route.request",OwnerEffectId="r28.route.owner",AnsweringPlayerId=1,StateRevision=0,Status="open",Visibility="owner"};
            var node=new EffectNodeRuntimeState{EffectId=request.OwnerEffectId,PlayerId=1,Status=EffectNodeStatus.Blocked};state.EffectRuntime.InteractionRequests.Add(request);state.EffectRuntime.EffectNodes.Add(node);
            var projection=new InteractionRequestProjection{InteractionId=request.InteractionId,OwnerEffectId=request.OwnerEffectId,AnsweringPlayerId=1,StateRevision=0,Status="open",VisibleToViewer=true};
            var action=Begin(GameCommandKind.AnswerInteraction,"action:explore",projection);var command=Command(GameCommandKind.AnswerInteraction);command.Parameters["interactionId"]=request.InteractionId;command.Parameters["expectedRevision"]="0";
            request.Status="answered";state.EffectRuntime.StateRevision=1;Record(command,Accepted(),0,1);
            Assert.That(Call(driver,"FinishObservedAction",action),Is.EqualTo(false));Assert.That(Coverage("action:explore"),Is.Zero);
            node.Status=EffectNodeStatus.Completed;state.EffectRuntime.StateRevision=2;Call(driver,"ObserveActionReceipts");Assert.That(Coverage("action:explore"),Is.EqualTo(1));
            artifacts.WriteLabeledText("回答入口后继未提前完成",JsonConvert.SerializeObject(new{command,request,node,Completed=Coverage("action:explore"),NormalExplorationOrKernelRuleRetested=false}));
        }
    }

    public sealed class HarvestRoundBoundaryFixtureTests
    {
        [Test]
        public void OpenSpecialChoiceUsesItsVisibleCandidateInsteadOfRecheckingTheBlockedEntry()
        {
            var method = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true)
                .GetMethod("SharedSpecialChoiceMatches", BindingFlags.Static | BindingFlags.NonPublic);
            var prior = new GameStateView { GameId = "game", ContentPackHash = "hash", ViewerPlayerId = 2, StateRevision = 5139 };
            var current = new GameStateView { GameId = "game", ContentPackHash = "hash", ViewerPlayerId = 2, StateRevision = 5145 };
            var option = new AbilityActionOption { OptionId = "special", Enabled = true,
                SourceRef = new AbilitySourceRef { AuthorizedPlayerId = 2 } };
            var request = new InteractionRequestProjection { VisibleToViewer = true, Status = "open", InteractionId = "request",
                OwnerEffectId = "choice", AnsweringPlayerId = 2, StateRevision = 5145, InteractionTypeId = "effect.choice.execute",
                AnswerSchema = "candidate_id", MinSelections = 0, MaxSelections = 1, CandidateIds = new List<string> { "special" } };
            bool Match() => (bool)method.Invoke(null, new object[] { prior, current, request, option });
            Assert.That(Match(), Is.True);
            request.CandidateIds.Clear(); Assert.That(Match(), Is.False); request.CandidateIds.Add("special");
            request.StateRevision--; Assert.That(Match(), Is.False); request.StateRevision++;
            request.VisibleToViewer = false; Assert.That(Match(), Is.False); request.VisibleToViewer = true;
            current.ViewerPlayerId = 3; Assert.That(Match(), Is.False); current.ViewerPlayerId = 2;
            current.ContentPackHash = "other"; Assert.That(Match(), Is.False);
        }

        [Test]
        public void SpecialChoiceReceiptAndEffectMustBelongToTheSameRequest()
        {
            var type = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
            var receiptMethod = type.GetMethod("AccurateSharedSpecialAnswer", BindingFlags.Static | BindingFlags.NonPublic);
            var treeMethod = type.GetMethod("SharedSpecialEffectBelongsToRequest", BindingFlags.Static | BindingFlags.NonPublic);
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = 2,
                OptionIds = new List<string> { "special" } };
            command.Parameters["interactionId"] = "request"; command.Parameters["expectedRevision"] = "5145";
            var accepted = new WorkflowSubmissionResult(CommandResult.SuccessResult(new List<GameEvent>(), "受理"), true);
            bool Match(WorkflowSubmissionResult result, bool network = false, int submitted = -1, string contract = null) =>
                (bool)receiptMethod.Invoke(null, new object[] { "request", "contract", 5145, 2, "special", command, result,
                    network ? 5147 : 5145, network, submitted, contract });
            Assert.That(Match(accepted), Is.True);
            command.OptionIds[0] = "other"; Assert.That(Match(accepted), Is.False); command.OptionIds[0] = "special";
            Assert.That(Match(new WorkflowSubmissionResult(accepted.CommandResult, false)), Is.False);
            command.Parameters["expectedRevision"] = "5147";
            Assert.That(Match(accepted, true, 5147, "contract"), Is.True);
            Assert.That(Match(accepted, true, 5147, "other"), Is.False);
            var state = new GameState();
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "choice" });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "child", ParentEffectId = "choice" });
            state.EffectRuntime.EffectNodes.Add(new EffectNodeRuntimeState { EffectId = "old", ParentEffectId = "other-choice" });
            bool Belongs(string effect) => (bool)treeMethod.Invoke(null, new object[] { state, effect, "choice" });
            Assert.That(Belongs("child"), Is.True);
            Assert.That(Belongs("old"), Is.False);
            Assert.That((bool)treeMethod.Invoke(null, new object[] { state, "missing", "missing" }), Is.False);
        }

        [Test]
        public void ConcurrentReceiptAllowsOnlyTheSameRequestAtItsActualSubmissionRevision()
        {
            var method = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true)
                .GetMethod("SameObservedRequest", BindingFlags.Static | BindingFlags.NonPublic);
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            command.Parameters["interactionId"] = "original"; command.Parameters["expectedRevision"] = "875";
            bool Match(int before, bool network, int submitted, string contract) => (bool)method.Invoke(null,
                new object[] { "original", "request-contract", 871, command, before, network, submitted, contract });
            Assert.That(Match(875, true, 875, "request-contract"), Is.True);
            Assert.That(Match(875, true, 875, "different-contract"), Is.False);
            Assert.That(Match(876, true, 875, "request-contract"), Is.False);
            Assert.That(Match(870, true, 870, "request-contract"), Is.False);
            Assert.That(Match(875, false, -1, null), Is.False);
            command.Parameters["interactionId"] = "other";
            Assert.That(Match(875, true, 875, "request-contract"), Is.False);
        }

        [Test]
        public void CompletedCollectionRemainsProvableAfterRoundFlagsReset()
        {
            var state = new GameState { Round = 2 };
            var result = NormalizedValue.CreateObject(new List<NormalizedValueEntry> {
                new NormalizedValueEntry { Name = "changed", Value = NormalizedValue.CreateBoolean(true) },
                new NormalizedValueEntry { Name = "collectedLocationIds", Value = NormalizedValue.CreateArray(
                    new List<NormalizedValue> { NormalizedValue.CreateString("B-03") }) } });
            var node = new EffectNodeRuntimeState { EffectId = "collection", EffectTypeId = RouteResourceSelectionEffectExecutor.TypeId,
                Status = EffectNodeStatus.Completed, PlayerId = 1, NormalizedResult = result,
                ContinuationOperationContext = new OperationCommandContext { SourceCommandId = "answer" } };
            state.EffectRuntime.EffectNodes.Add(node);
            var method = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true)
                .GetMethod("CompletedHarvestBeforeRoundAdvance", BindingFlags.Static | BindingFlags.NonPublic);
            bool Confirm(string owner, string command, int actor, int previousRound) =>
                (bool)method.Invoke(null, new object[] { state, owner, command, actor, previousRound });
            Assert.That(Confirm("collection", "answer", 1, 1), Is.True);
            Assert.That(Confirm("collection", "answer", 1, 2), Is.False);
            Assert.That(Confirm("collection", "other", 1, 1), Is.False);
            Assert.That(Confirm("collection", "answer", 2, 1), Is.False);
            node.Status = EffectNodeStatus.Blocked;
            Assert.That(Confirm("collection", "answer", 1, 1), Is.False);
            node.Status = EffectNodeStatus.Completed;
            ItemTransferEffectExecutor.Value(result, "collectedLocationIds").Items.Clear();
            Assert.That(Confirm("collection", "answer", 1, 1), Is.False);
        }
    }
}
