using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Scoring;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class EffectDescendantQueryTests
    {
        private static GameState State()
        {
            var state = new GameState();
            var nodes = state.EffectRuntime.EffectNodes;
            for (int i = 127; i >= 0; i--)
                nodes.Add(new EffectNodeRuntimeState { EffectId = "history." + i, ParentEffectId = i == 0 ? "missing" : "history." + (i - 1), Status = EffectNodeStatus.Completed });
            nodes.Add(new EffectNodeRuntimeState { EffectId = "leaf", ParentEffectId = "branch", Status = EffectNodeStatus.Completed });
            nodes.Add(new EffectNodeRuntimeState { EffectId = "root", Status = EffectNodeStatus.Completed });
            nodes.Add(new EffectNodeRuntimeState { EffectId = "branch", ParentEffectId = "root", Status = EffectNodeStatus.Running });
            nodes.Add(new EffectNodeRuntimeState { EffectId = "cycle.1", ParentEffectId = "cycle.2", Status = EffectNodeStatus.Completed });
            nodes.Add(new EffectNodeRuntimeState { EffectId = "cycle.2", ParentEffectId = "cycle.1", Status = EffectNodeStatus.Failed });
            return state;
        }
        private static List<EffectNodeRuntimeState> Select(GameState state, string ancestor, bool completed, bool exclude, bool single)
        {
            var type = typeof(GameState).Assembly.GetType("YC.Domain.Effects.EffectDescendantQuery", true);
            try { return (List<EffectNodeRuntimeState>)type.GetMethod("Select", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { state, ancestor, completed, exclude, single }); }
            catch (TargetInvocationException e) { throw e.InnerException; }
        }
        private static string Outcome(GameState state, string ancestor, bool completed, bool exclude, bool single, bool original)
        {
            using var mode = original ? FinalScoringSessionService.UseFullDescendantTraversalForDiagnostics() : FinalScoringSessionService.VerifyDescendantTraversalForDiagnostics();
            try { return JsonConvert.SerializeObject(Select(state, ancestor, completed, exclude, single).ConvertAll(n => n.EffectId)); }
            catch (Exception e) { return e.GetType().FullName + "|" + e.Message; }
        }
        [TestCase(true, false, false)]
        [TestCase(false, true, true)]
        public void IndexedSelectionKeepsNodeReferencesOrderCyclesAndMissingParents(bool completed, bool exclude, bool single)
        {
            var state = State(); string before = JsonConvert.SerializeObject(state); int hits = 0;
            using (GameStateCloneService.BeginTimingScope((phase, ticks) => { if (phase == "ancestry.filter.index") hits++; }))
            using (FinalScoringSessionService.VerifyDescendantTraversalForDiagnostics())
            {
                var selected = Select(state, "root", completed, exclude, single);
                Assert.That(selected.ConvertAll(n => n.EffectId), Is.EqualTo(completed ? new[] { "leaf", "root" } : new[] { "leaf", "branch" }));
                Assert.That(selected[0], Is.SameAs(state.EffectRuntime.EffectNodes[128]));
            }
            Assert.That(hits, Is.EqualTo(1));
            foreach (string ancestor in new[] { "root", "cycle.2", "missing", "gone", "", null })
                Assert.That(Outcome(state, ancestor, completed, exclude, single, false), Is.EqualTo(Outcome(state, ancestor, completed, exclude, single, true)));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }
        [Test]
        public void SameRevisionAndCountsDoNotReuseOldParentsOrStatuses()
        {
            var state = State();
            Assert.That(Outcome(state, "root", true, false, false, false), Is.EqualTo(Outcome(state, "root", true, false, false, true)));
            state.EffectRuntime.EffectNodes[0].ParentEffectId = "root";
            state.EffectRuntime.EffectNodes[128].Status = EffectNodeStatus.Running;
            Assert.That(Outcome(state, "root", true, false, false, false), Is.EqualTo(Outcome(state, "root", true, false, false, true)));
        }
        [TestCase("duplicate", false)] [TestCase("duplicate", true)]
        [TestCase("null", false)] [TestCase("null", true)]
        [TestCase("empty", false)] [TestCase("empty", true)]
        public void MalformedGraphsKeepEachCallersOriginalFirstOrSingleBehavior(string kind, bool single)
        {
            var state = State(); var nodes = state.EffectRuntime.EffectNodes;
            if (kind == "duplicate") nodes.Insert(0, new EffectNodeRuntimeState { EffectId = "branch", ParentEffectId = "gone" });
            if (kind == "null") nodes.Insert(0, null);
            if (kind == "empty") nodes.Insert(0, new EffectNodeRuntimeState { EffectId = "" });
            Assert.That(Outcome(state, "root", false, false, single, false), Is.EqualTo(Outcome(state, "root", false, false, single, true)));
        }
    }
}
