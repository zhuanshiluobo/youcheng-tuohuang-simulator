using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class OperationWindowCloneIsolationTests
    {
        [Test]
        public void CausalContextCopiesEveryFieldAndDetachesBothPlayerLists()
        {
            var context = new OperationCommandContext { OperationWindowId="window",WindowGeneration=7,WindowKind="kind",
                SourceMainNodeId="main",SourceEffectId="effect",ActingPlayerId=3,SourceCommandId="command",
                OriginMainOperationWindowId="origin",MainWindowPlayerIdAtCreation=2,
                WindowFacts=new OperationWindowState {OperationWindowId="window",WindowGeneration=7,WindowKind="kind",
                    SourceMainNodeId="main",SourceEffectId="effect",SourceSchedulingGroupId="group",ParentOperationWindowId="parent",
                    AuthorizedPlayerIds=new List<int>{2,3},AvailablePlayerIds=new List<int>{3},Lifecycle=OperationWindowLifecycle.Closed,
                    OpenedOrdinal=18,OpenedAtStateRevision=21,ClosedAtStateRevision=25} };
            var clone=GameStateCloneService.DeepClone(context);
            Assert.That(JsonConvert.SerializeObject(clone),Is.EqualTo(JsonConvert.SerializeObject(context)));
            Assert.That(context.GetType().GetInterface("YC.Domain.State.IStateCloneShell"),Is.Not.Null);
            Assert.That(context.WindowFacts.GetType().GetInterface("YC.Domain.State.IStateCloneShell"),Is.Not.Null);
            clone.WindowFacts.AuthorizedPlayerIds.Clear();clone.WindowFacts.AvailablePlayerIds.Add(4);
            clone.WindowFacts.WindowGeneration++;clone.ActingPlayerId=4;
            Assert.That(context.WindowFacts.AuthorizedPlayerIds,Is.EqualTo(new[]{2,3}));
            Assert.That(context.WindowFacts.AvailablePlayerIds,Is.EqualTo(new[]{3}));
            Assert.That(context.WindowFacts.WindowGeneration,Is.EqualTo(7));Assert.That(context.ActingPlayerId,Is.EqualTo(3));
        }
    }
}
