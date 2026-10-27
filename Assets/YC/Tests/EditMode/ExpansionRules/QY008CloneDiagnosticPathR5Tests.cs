using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class QY008CloneDiagnosticPathR5Tests
    {
        [Serializable] public sealed class Root { public List<object> A; public object Z; }
        [Serializable] public sealed class Branch { public object[] Values; }
        public sealed class Unsupported { }

        [Test]
        public void RejectedPathsKeepNestedFieldsIndicesAndSiblingDepth()
        {
            var root = new Root { A = new List<object> { new Branch { Values = new object[] { "ok", null } } } };
            var branch = (Branch)root.A[0];
            branch.Values[1] = new Dictionary<string, int>();
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(root)).Message,
                Is.EqualTo("持久化状态禁止 Dictionary：Root.A[0].Values[1]。请改用有序 List DTO。"));
            branch.Values[1] = new Unsupported();
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(root)).Message,
                Is.EqualTo("状态字段必须是 [Serializable] DTO：Root.A[0].Values[1] (" + typeof(Unsupported).FullName + ")。"));
            branch.Values[1] = root;
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(root)).Message,
                Is.EqualTo("状态 DTO 不能包含循环引用：Root.A[0].Values[1]。"));
            branch.Values[1] = null;
            root.Z = new Dictionary<string, int>();
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(root)).Message,
                Is.EqualTo("持久化状态禁止 Dictionary：Root.Z。请改用有序 List DTO。"));
            root.Z = root;
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(root)).Message,
                Is.EqualTo("状态 DTO 不能包含循环引用：Root.Z。"));
        }

        [Test]
        public void SuccessfulSiblingAliasesAndCallsRemainIndependentAfterFailure()
        {
            var shared = new Branch { Values = new object[] { new List<string> { "before" } } };
            var root = new Root { A = new List<object> { shared, shared }, Z = shared };
            var clone = GameStateCloneService.DeepClone(root);
            Assert.That(clone.A[0], Is.Not.SameAs(clone.A[1]));
            Assert.That(clone.Z, Is.Not.SameAs(clone.A[0]));
            ((List<string>)((Branch)clone.A[0]).Values[0])[0] = "changed";
            Assert.That(((List<string>)shared.Values[0])[0], Is.EqualTo("before"));
            root.Z = new Unsupported();
            Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(root));
            root.Z = shared;
            var next = GameStateCloneService.DeepClone(root);
            Assert.That(((List<string>)((Branch)next.Z).Values[0])[0], Is.EqualTo("before"));
            Assert.That(next.Z, Is.Not.SameAs(clone.Z));
        }
    }
}
