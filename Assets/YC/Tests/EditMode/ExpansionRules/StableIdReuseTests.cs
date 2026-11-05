using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class StableIdReuseTests
    {
        private static IDisposable Scope() => (IDisposable)typeof(StableIdFactory).GetMethod("BeginSynchronousReuseScope", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        private static string Reference(string kind, string[] parts)
        {
            using var sha = SHA256.Create();
            return kind + "_" + string.Concat(sha.ComputeHash(StableIdFactory.EncodeCanonicalInput(kind, parts)).Select(b => b.ToString("x2")));
        }

        [Test]
        public void ReuseKeepsOriginalProtocolNullUnicodeAndMutableCallerArrays()
        {
            using var scope = Scope();
            foreach (var parts in new[] { null, Array.Empty<string>(), new string[] { null }, new[] { "" }, new[] { "雪😀", "a\0b", "x|y" }, new[] { new string((char)0xd800, 1), "a" } })
                for (int repeat = 0; repeat < 3; repeat++)
                    Assert.That(StableIdFactory.Create("规则", parts), Is.EqualTo(Reference("规则", parts)));
            var caller = new[] { "原值", "第二项" };
            string original = StableIdFactory.Create("node", caller);
            caller[0] = "修改";
            Assert.That(StableIdFactory.Create("node", caller), Is.EqualTo(Reference("node", caller)).And.Not.EqualTo(original));
            caller[0] = "原值";
            Assert.That(StableIdFactory.Create("node", caller), Is.EqualTo(original));
            Assert.Throws<ArgumentException>(() => StableIdFactory.Create("非法 类型", caller));
            for (int i = 0; i < 4100; i++) Assert.That(StableIdFactory.Create("limit", i.ToString()), Is.EqualTo(Reference("limit", new[] { i.ToString() })));
        }

        [Test]
        public void SharedReuseCopiesCallerKeysAndBypassesOversizedInputs()
        {
            string nonce = Guid.NewGuid().ToString("N"); var parts = new[] { nonce, "原值" };
            string original = StableIdFactory.Create("shared", parts);
            parts[1] = "修改";
            Assert.That(StableIdFactory.Create("shared", parts), Is.EqualTo(Reference("shared", parts)).And.Not.EqualTo(original));
            parts[1] = "原值"; Assert.That(StableIdFactory.Create("shared", parts), Is.EqualTo(original));
            int computes = 0, hits = 0;
            using var timing = GameStateCloneService.BeginTimingScope((phase, ticks) => { if (phase == "stable_id.compute") computes++; if (phase == "stable_id.reuse") hits++; });
            var large = new[] { nonce, new string('雪', 1025) };
            for (int i = 0; i < 2; i++) Assert.That(StableIdFactory.Create("oversized", large), Is.EqualTo(Reference("oversized", large)));
            Assert.That(computes, Is.EqualTo(2)); Assert.That(hits, Is.Zero);
        }

        [Test]
        public void ConcurrentSharedInputsKeepOriginalBytesWhileCacheRotates()
        {
            string nonce = Guid.NewGuid().ToString("N");
            var jobs = Enumerable.Range(0, 4).Select(worker => System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 1100; i++)
                {
                    var parts = new[] { nonce, worker.ToString(), i.ToString(), null };
                    if (StableIdFactory.Create("rotation", parts) != Reference("rotation", parts)) return false;
                }
                return true;
            })).ToArray();
            System.Threading.Tasks.Task.WaitAll(jobs);
            Assert.That(jobs.All(job => job.Result), Is.True);
            Assert.That(StableIdFactory.Create("rotation", nonce, "end"), Is.EqualTo(Reference("rotation", new[] { nonce, "end" })));
        }

        [Test]
        public void NestedAndExceptionScopesReleaseValuesAndKeepOuterScope()
        {
            string component = Guid.NewGuid().ToString("N");
            var samples = new Dictionary<string, int>();
            using var timing = GameStateCloneService.BeginTimingScope((phase, ticks) => samples[phase] = samples.TryGetValue(phase, out int n) ? n + 1 : 1);
            using (Scope())
            {
                StableIdFactory.Create("scope", component);
                try { using var nested = Scope(); StableIdFactory.Create("scope", component); StableIdFactory.Create("scope", component); throw new InvalidOperationException(); }
                catch (InvalidOperationException) { }
                StableIdFactory.Create("scope", component);
            }
            StableIdFactory.Create("scope", component);
            Assert.That(samples["stable_id.compute"], Is.EqualTo(3));
            Assert.That(samples["stable_id.reuse"], Is.EqualTo(2));
        }
    }
}
