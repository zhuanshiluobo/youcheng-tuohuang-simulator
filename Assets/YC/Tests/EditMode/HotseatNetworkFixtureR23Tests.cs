using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace YC.Tests.EditMode
{
    // 仅夹具政策/消费者隔离检查，不是正常局、Mirror运输或Steam验收。
    public sealed class HotseatNetworkFixtureR23Tests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type Policy => Type.GetType("YC.Presentation.Validation.HotseatUiValidationContinuationPolicy, Assembly-CSharp", true);
        private static Type Driver => Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
        private static object Call(object value, string name, params object[] args) =>
            value.GetType().GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(value, args);
        private static void Set(object value, string name, object data) => value.GetType().GetField(name, Flags).SetValue(value, data);
        private static object Get(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value) ?? value.GetType().GetProperty(name, Flags).GetValue(value);

        [Test]
        public void FirstIssueAndBoundedRereadsRequireActualProgress()
        {
            var policy = Activator.CreateInstance(Policy, true);
            Assert.IsTrue((bool)Call(policy, "FirstIssue", "original-error"));
            Assert.IsFalse((bool)Call(policy, "FirstIssue", "original-error"));
            for (int i = 0; i < 3; i++) Assert.IsTrue((bool)Call(policy, "TryReread", "game:epoch:revision:sequence:request", true, true, true));
            Assert.IsFalse((bool)Call(policy, "TryReread", "game:epoch:revision:sequence:request", true, true, true));
            Assert.IsTrue((bool)Call(policy, "TryReread", "game:epoch:next-revision:sequence:request", true, true, true));
        }

        [Test]
        public void IdentityProtocolOrRuntimeFailureCannotBeDeferred()
        {
            var policy = Activator.CreateInstance(Policy, true);
            Assert.IsFalse((bool)Call(policy, "TryReread", "same", false, true, true));
            Assert.IsFalse((bool)Call(policy, "TryReread", "same", true, false, true));
            Assert.IsFalse((bool)Call(policy, "TryReread", "same", true, true, false));
            Assert.IsFalse((bool)Call(policy, "TryReread", "", true, true, true));
            var host = new GameObject("R23协议失败保护");
            try
            {
                var driver = host.AddComponent(Driver);
                Set(driver, "selectedCase", "network-full-game"); Set(driver, "deferContentDifferencesRequested", true);
                Set(driver, "networkFault", "isolated-protocol-fault");
                var error = Assert.Throws<TargetInvocationException>(() => Call(driver, "NetworkCheck"));
                Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
                Assert.IsFalse((bool)Call(driver, "TryDeferDriverStep", error.InnerException,
                    new System.Collections.Generic.Stack<System.Collections.IEnumerator>()));
                Assert.AreEqual("isolated-protocol-fault", Get(driver, "networkFault"));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void BusinessRejectionDoesNotHidePermissionOrProtocolFailure()
        {
            var method = Policy.GetMethod("CanDeferBusinessRejection", Flags);
            bool Can(string code, bool exact = true, bool healthy = true) => (bool)method.Invoke(null, new object[] { code, exact, healthy });
            foreach (string code in new[] { "InsufficientResource", "InsufficientInfluence", "OccupiedSlot", "ClosedLocation", "NoRoute" })
                Assert.IsTrue(Can(code), code);
            foreach (string code in new[] { "WrongPhase", "NotCurrentPlayer", "InvalidPlayer", "InvalidSource", "UnknownCommand", "StateRevisionMismatch", "SettlementWindowMismatch", "PendingChoiceRequired" })
                Assert.IsFalse(Can(code), code);
            Assert.IsFalse(Can("InsufficientResource", false));
            Assert.IsFalse(Can("InsufficientResource", true, false));
        }

        [Test]
        public void ContentDifferenceIsRetainedWithoutEndingDriverOrOverwritingFirstEvidence()
        {
            var host = new GameObject("R23 isolated fixture policy");
            string output = Path.Combine(Path.GetDirectoryName(UnityEngine.Application.dataPath), "Logs", "FixtureR23", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            object driver = host.AddComponent(Driver);
            try
            {
                Set(driver, "selectedCase", "network-full-game"); Set(driver, "output", output);
                Set(driver, "deferContentDifferencesRequested", true);
                Assert.DoesNotThrow(() => Call(driver, "CheckContent", false, "isolated-content-mismatch"));
                string first = Path.Combine(output, "延后差异首次-0001.json");
                string original = File.ReadAllText(first);
                Assert.DoesNotThrow(() => Call(driver, "CheckContent", false, "isolated-content-mismatch"));
                Assert.AreEqual(original, File.ReadAllText(first));
                Assert.AreEqual(1, Get(driver, "deferredDifferenceCount"));
                Assert.IsFalse((bool)Get(driver, "finished"));
                Assert.IsTrue(original.Contains("\"Passed\": false"));
            }
            finally
            {
                foreach (IDisposable writer in ((System.Collections.IDictionary)Get(driver, "appendLogs")).Values) writer.Dispose();
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void ExplicitModeKeepsLegacyLocalCountsAndFourIndependentNetworkPlayers()
        {
            var mode = Driver.GetMethod("RequestedEntrepreneurMode", Flags);
            Assert.IsTrue((bool)mode.Invoke(null, new object[] { "entrepreneur", null }));
            Assert.IsTrue((bool)mode.Invoke(null, new object[] { "Entrepreneur", null }));
            Assert.IsTrue((bool)mode.Invoke(null, new object[] { null, "true" }));
            Assert.IsFalse((bool)mode.Invoke(null, new object[] { "standard", "true" }));
            var count = Driver.GetMethod("RequestedLocalPlayerCount", Flags);
            foreach (int n in new[] { 2, 3, 4 }) Assert.AreEqual(n, count.Invoke(null, new object[] { true, n.ToString() }));
            Assert.AreEqual(0, count.Invoke(null, new object[] { true, "5" }));
            var host = new GameObject("R23 network count");
            try
            {
                var driver = host.AddComponent(Driver); Set(driver, "selectedCase", "network-full-game");
                Assert.AreEqual(4, Get(driver, "ExpectedLocalPlayerCount"));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }
    }
}
