using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class LuaSnapshotCollectionAllocationTests
    {
        [Test]
        public void ScalarCollectionsRemainNonNullIndependentStableAndSerializable()
        {
            foreach (var value in new[] { LuaSnapshotValue.Boolean(true), LuaSnapshotValue.Integer(7), LuaSnapshotValue.String("雪😀") })
            {
                var other = LuaSnapshotValue.String("other");
                Assert.That(value.Items, Is.Not.Null.And.Empty); Assert.That(value.Properties, Is.Not.Null.And.Empty);
                Assert.That(value.Items, Is.SameAs(value.Items).And.Not.SameAs(other.Items));
                Assert.That(value.Properties, Is.SameAs(value.Properties).And.Not.SameAs(other.Properties));
                value.Properties.Add(new LuaSnapshotEntry("附加", LuaSnapshotValue.Integer(12)));
                Assert.That(value.Get("附加").IntegerValue, Is.EqualTo(12));
                Assert.That(other.Get("附加"), Is.SameAs(LuaSnapshotValue.Null));
                Assert.That(JsonConvert.SerializeObject(other), Does.Contain("\"Items\":[]").And.Contain("\"Properties\":[]"));
            }
        }
        [Test]
        public void ArraysObjectsAndConcurrentFirstReadsKeepOriginalPublicValues()
        {
            var value = LuaSnapshotValue.Integer(1);
            var reads = Enumerable.Range(0, 16).Select(_ => Task.Run(() => value.Items)).ToArray();
            Task.WaitAll(reads);
            foreach (var read in reads) Assert.That(read.Result, Is.SameAs(value.Items));
            var array = LuaSnapshotValue.Array(new[] { value, null });
            Assert.That(array.Items[0], Is.SameAs(value)); Assert.That(array.Items[1], Is.SameAs(LuaSnapshotValue.Null));
            Assert.That(array.Properties, Is.Empty);
            var obj = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("z", value), null, new LuaSnapshotEntry("a", array) });
            Assert.That(obj.Properties.Select(p => p.Name), Is.EqualTo(new[] { "a", "z" }));
            Assert.That(obj.Get("z"), Is.SameAs(value)); Assert.That(obj.Get("missing"), Is.SameAs(LuaSnapshotValue.Null));
        }
    }
}
