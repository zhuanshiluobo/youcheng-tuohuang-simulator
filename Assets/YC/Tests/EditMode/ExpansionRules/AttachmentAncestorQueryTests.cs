using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class AttachmentAncestorQueryTests
    {
        private static readonly MethodInfo Select=typeof(GameState).Assembly.GetType("YC.Domain.Effects.EffectDescendantQuery",true).GetMethod("SelectFromExistingIndex",BindingFlags.Static|BindingFlags.NonPublic);
        private static List<EffectNodeRuntimeState> Run(List<EffectNodeRuntimeState> candidates,Dictionary<string,EffectNodeRuntimeState> nodes,string root,bool full)
        {
            using var diagnostics=full?FinalScoringSessionService.UseFullDescendantTraversalForDiagnostics():FinalScoringSessionService.VerifyDescendantTraversalForDiagnostics();
            return (List<EffectNodeRuntimeState>)Select.Invoke(null,new object[]{candidates,nodes,root});
        }
        [TestCase("root")][TestCase("missing")][TestCase("cycle.2")][TestCase("")][TestCase(null)]
        public void CapturedFirstMatchCandidatesKeepOrderDuplicatesMissingParentsAndCycles(string root)
        {
            var all=new List<EffectNodeRuntimeState>{new EffectNodeRuntimeState{EffectId="first",ParentEffectId="branch"},new EffectNodeRuntimeState{EffectId="root"},new EffectNodeRuntimeState{EffectId="branch",ParentEffectId="root"},
                new EffectNodeRuntimeState{EffectId="first",ParentEffectId="missing"},new EffectNodeRuntimeState{EffectId="orphan",ParentEffectId="missing"},new EffectNodeRuntimeState{EffectId="cycle.1",ParentEffectId="cycle.2"},new EffectNodeRuntimeState{EffectId="cycle.2",ParentEffectId="cycle.1"},new EffectNodeRuntimeState{EffectId=null}};
            var index=new Dictionary<string,EffectNodeRuntimeState>(StringComparer.Ordinal);
            foreach(var node in all)if(node.EffectId!=null && !index.ContainsKey(node.EffectId))index.Add(node.EffectId,node);
            var expected=Run(all,index,root,true);var actual=Run(all,index,root,false);
            Assert.That(actual.Count,Is.EqualTo(expected.Count));
            for(int i=0;i<actual.Count;i++)Assert.That(actual[i],Is.SameAs(expected[i]));
        }
        [Test]
        public void ANewFilterSeesChangedParentsInTheCapturedIndexWithoutAddingNewCandidates()
        {
            var root=new EffectNodeRuntimeState{EffectId="root"};var child=new EffectNodeRuntimeState{EffectId="child",ParentEffectId="root"};
            var candidates=new List<EffectNodeRuntimeState>{child};var index=new Dictionary<string,EffectNodeRuntimeState>{{root.EffectId,root},{child.EffectId,child}};
            Assert.That(Run(candidates,index,"root",false),Is.EqualTo(new[]{child}));
            child.ParentEffectId="outside";index.Add("new",new EffectNodeRuntimeState{EffectId="new",ParentEffectId="root"});
            Assert.That(Run(candidates,index,"root",false),Is.Empty);
            child.ParentEffectId="root";Assert.That(Run(candidates,index,"root",false),Is.EqualTo(new[]{child}));
        }
    }
}
