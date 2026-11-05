using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CanonicalTransferDigestPerformanceTests
    {
        [Serializable] public class PrivateBase { private long stamp=73; public long ReadStamp()=>stamp; }
        [Serializable] public struct Nested { public int Stamp; public List<string> Values; }
        [Serializable] public sealed class Planned : PrivateBase { public double NaN=double.NaN; public Nested Nested; public object Polymorphic; }
        [Serializable] public sealed class DerivedJournalList : List<RuleJournalEntry> { public object Extra; }
        [Serializable] public sealed class KnownEntry : PrivateBase
        {
            public List<string> Values = new List<string>();
            [NonSerialized] public List<int> Runtime = new List<int> { 42 };
        }
        [Serializable] public sealed class KnownHolder { public KnownEntry Direct; public List<KnownEntry> Entries; }
        [Serializable] public sealed class DerivedKnownList : List<KnownEntry> { public object Extra; }

        [Test]
        public void SealedDtoFieldAndListPlansKeepConstructorsPrivateBaseFieldsAndDerivedListDiagnostics()
        {
            var entry = new KnownEntry { Values = new List<string> { "原始" }, Runtime = new List<int> { 99 } };
            var source = new KnownHolder { Direct = entry, Entries = new List<KnownEntry> { entry, null, entry } };
            var clone = GameStateCloneService.DeepClone(source);
            Assert.That(clone.Direct.ReadStamp(), Is.EqualTo(73));
            Assert.That(clone.Direct.Runtime, Is.EqualTo(new[] { 42 }), "非持久字段仍由原构造函数初始化");
            Assert.That(clone.Direct, Is.Not.SameAs(clone.Entries[0]));
            Assert.That(clone.Entries[0], Is.Not.SameAs(clone.Entries[2]));
            Assert.That(clone.Entries[1], Is.Null);
            clone.Direct.Values[0] = "修改";
            Assert.That(entry.Values[0], Is.EqualTo("原始"));
            var derived = new DerivedKnownList { entry }; derived.Extra = new Dictionary<string, string>(); source.Entries = derived;
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateCloneService.DeepClone(source)).Message,
                Does.Contain("KnownHolder.Entries.Extra"));
        }

        [Test]
        public void TypedStateListsKeepAliasIsolationDerivedFieldsAndCycleDiagnostics()
        {
            var state=new GameState();var shared=new RuleJournalEntry{Detail="原始",Payload=NormalizedValue.CreateString("payload")};
            state.EffectRuntime.Journal.Add(shared);state.EffectRuntime.Journal.Add(shared);state.EffectRuntime.Journal.Add(null);
            var clone=GameStateCloneService.DeepClone(state);
            Assert.That(clone.EffectRuntime.Journal[0],Is.Not.SameAs(clone.EffectRuntime.Journal[1]));
            clone.EffectRuntime.Journal[0].Payload.StringValue="修改";
            Assert.That(shared.Payload.StringValue,Is.EqualTo("payload"));Assert.That(clone.EffectRuntime.Journal[2],Is.Null);
            var derived=new DerivedJournalList{shared};derived.Extra=new List<string>{"派生字段"};state.EffectRuntime.Journal=derived;
            clone=GameStateCloneService.DeepClone(state);
            Assert.That(clone.EffectRuntime.Journal,Is.TypeOf<DerivedJournalList>());
            Assert.That(((DerivedJournalList)clone.EffectRuntime.Journal).Extra,Is.EqualTo(derived.Extra).And.Not.SameAs(derived.Extra));
            derived.Extra=new Dictionary<string,int>();
            Assert.That(Assert.Throws<InvalidOperationException>(()=>GameStateCloneService.DeepClone(state)).Message,Does.Contain("持久化状态禁止 Dictionary"));
            state.EffectRuntime.Journal=new List<RuleJournalEntry>();
            var node=new EffectNodeRuntimeState();var nested=new EffectSpecRuntimeState();node.NestedEffects.Add(nested);nested.NestedEffects.Add(nested);state.EffectRuntime.EffectNodes.Add(node);
            Assert.That(Assert.Throws<InvalidOperationException>(()=>GameStateCloneService.DeepClone(state)).Message,
                Is.EqualTo("状态 DTO 不能包含循环引用：GameState.EffectRuntime.EffectNodes[0].NestedEffects[0].NestedEffects[0]。"));
        }
        private static JToken OldCanonical(JToken value) => value is JObject obj
            ? new JObject(obj.Properties().OrderBy(p => p.Name,StringComparer.Ordinal).Select(p=>new JProperty(p.Name,OldCanonical(p.Value))))
            : value is JArray array ? new JArray(array.Select(OldCanonical)) : value.DeepClone();
        private static string OldDigest(object value)
        {
            var token=value==null?JValue.CreateNull():JToken.FromObject(value);
            using var sha=SHA256.Create();
            return ItemMovementV4.ContractVersion+":sha256:"+string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(OldCanonical(token).ToString(Formatting.None))).Select(b=>b.ToString("x2")));
        }
        private static string OldLocalDigest(ItemInstanceState item)
        {
            var token = JToken.FromObject(item.LocalState);
            using var sha = SHA256.Create();
            return "yc.item-local-state.sha256.v1:" + string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(OldCanonical(token).ToString(Formatting.None))).Select(b => b.ToString("x2")));
        }
        private static string LocalOutcome(Func<string> action)
        {
            try { return action(); }
            catch (Exception error) { return error.GetType().FullName + "|" + error.Message; }
        }
        [Test]
        public void LocalStateDirectDigestKeepsAllFieldsCustomSettingsAndOriginalFailures()
        {
            foreach (var value in new[] { NormalizedValue.CreateObject(Array.Empty<NormalizedValueEntry>()), NormalizedValue.CreateInteger(8),
                NormalizedValue.CreateObject(new[] { new NormalizedValueEntry { Name = "中文", Value = NormalizedValue.CreateString("雪😀\n") } }) })
            {
                var item = new ItemInstanceState { LocalState = value };
                Assert.That(ItemTransferService.LocalStateDigest(item), Is.EqualTo(OldLocalDigest(item)));
                value.StringValue = "闲置字段"; value.BooleanValue = true;
                Assert.That(ItemTransferService.LocalStateDigest(item), Is.EqualTo(OldLocalDigest(item)));
                var derived = new DerivedNormalizedList { NormalizedValue.CreateString("派生列表") };
                value.Items = derived;
                Assert.That(LocalOutcome(() => ItemTransferService.LocalStateDigest(item)), Is.EqualTo(LocalOutcome(() => OldLocalDigest(item))));
            }
            var cycle = NormalizedValue.CreateNull(); cycle.Items.Add(cycle);
            foreach (var item in new[] { null, new ItemInstanceState { LocalState = null }, new ItemInstanceState { LocalState = cycle } })
                Assert.That(LocalOutcome(() => ItemTransferService.LocalStateDigest(item)), Is.EqualTo(LocalOutcome(() => OldLocalDigest(item))));
            var prior = JsonConvert.DefaultSettings;
            try
            {
                JsonConvert.DefaultSettings = () => new JsonSerializerSettings { Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() } };
                var item = new ItemInstanceState { LocalState = NormalizedValue.CreateInteger(9) };
                Assert.That(ItemTransferService.LocalStateDigest(item), Is.EqualTo(OldLocalDigest(item)));
            }
            finally { JsonConvert.DefaultSettings = prior; }
        }
        [Serializable] private sealed class DerivedNormalizedList : List<NormalizedValue> { }
        [Test]
        public void DigestKeepsExistingBytesAcrossNestedValuesCulturesAndInputMutation()
        {
            var original=CultureInfo.CurrentCulture;
            try
            {
                foreach(string culture in new[]{"en-US","fr-FR","zh-CN"})
                {
                    CultureInfo.CurrentCulture=new CultureInfo(culture);
                    var normalized=NormalizedValue.CreateObject(new List<NormalizedValueEntry>{
                        new NormalizedValueEntry{Name="中文\n引号\"",Value=NormalizedValue.CreateString("\\雪😀\t")},
                        new NormalizedValueEntry{Name="array",Value=NormalizedValue.CreateArray(new[]{NormalizedValue.CreateInteger(-5),NormalizedValue.CreateInteger(9)})}});
                    var token=new JObject { ["z"]=new JArray(new JObject{["b"]=2,["a"]=1},null,normalized==null?null:JToken.FromObject(normalized)),
                        ["decimal"]=0.100m,["double"]=-0.0d,["nan"]=double.NaN,["date"]=new DateTime(2026,11,3,1,2,3,DateTimeKind.Utc),["bool"]=true };
                    foreach(object value in new object[]{null,normalized,token,new byte[]{0,255,1},new JConstructor("Date",123),"literal",double.PositiveInfinity})
                        Assert.That(ItemTransferService.MovementV4Digest(value),Is.EqualTo(OldDigest(value)),culture);
                    string before=ItemTransferService.MovementV4Digest(normalized);
                    normalized.Properties[0].Value=NormalizedValue.CreateBoolean(false);
                    Assert.That(ItemTransferService.MovementV4Digest(normalized),Is.EqualTo(OldDigest(normalized)));
                    Assert.That(ItemTransferService.MovementV4Digest(normalized),Is.Not.EqualTo(before),"不能把可变参数的旧摘要当成缓存命中");
                }
            }
            finally {CultureInfo.CurrentCulture=original;}
        }
        [Test]
        public void EmptyCloneListsRemainIndependentWhilePublishedEmptyTargetsAreRetained()
        {
            var original=new GameState();
            var clone=GameStateCloneService.DeepClone(original);
            Assert.That(clone.Players,Is.Not.SameAs(original.Players));
            clone.Players.Add(new PlayerState{PlayerId=1});
            Assert.That(original.Players,Is.Empty);
            var target=new GameState();var players=target.Players;
            GameStateCloneService.CopyTo(target,original);
            Assert.That(target.Players,Is.SameAs(players));
        }
        [Test]
        public void CompiledFieldPlanKeepsPrivateBaseFieldsBoxedStructsAndReferenceIsolation()
        {
            var source=new Planned {Nested=new Nested{Stamp=9,Values=new List<string>{"before"}},Polymorphic=new List<object>{new List<int>{3}}};
            var clone=GameStateCloneService.DeepClone(source);
            Assert.That(clone.ReadStamp(),Is.EqualTo(73));Assert.That(double.IsNaN(clone.NaN),Is.True);
            source.Nested.Values[0]="source";Assert.That(clone.Nested.Stamp,Is.EqualTo(9));Assert.That(clone.Nested.Values[0],Is.EqualTo("before"));
            ((List<int>)((List<object>)clone.Polymorphic)[0])[0]=7;
            Assert.That(((List<int>)((List<object>)source.Polymorphic)[0])[0],Is.EqualTo(3));
        }
        [Test]
        public void CustomGlobalSerializerSettingsStillFollowOriginalDigestContract()
        {
            var prior=JsonConvert.DefaultSettings;
            try
            {
                JsonConvert.DefaultSettings=()=>new JsonSerializerSettings{Converters={new Newtonsoft.Json.Converters.StringEnumConverter()}};
                var value=NormalizedValue.CreateObject(new[]{new NormalizedValueEntry{Name="A",Value=NormalizedValue.CreateInteger(8)}});
                Assert.That(ItemTransferService.MovementV4Digest(value),Is.EqualTo(OldDigest(value)));
            }
            finally {JsonConvert.DefaultSettings=prior;}
        }
        [Test]
        public void EveryNormalizedFieldIsHashedIncludingDormantFieldsAndFailuresDoNotPoisonNextCall()
        {
            foreach(NormalizedValueKind kind in Enum.GetValues(typeof(NormalizedValueKind)))
            {
                var value=new NormalizedValue{Kind=kind,BooleanValue=true,IntegerValue=long.MinValue,StringValue=null,
                    ReferenceId="引用",ReferenceType=null,Items=new List<NormalizedValue>{null,NormalizedValue.CreateString("x")},
                    Properties=new List<NormalizedValueEntry>{null,new NormalizedValueEntry{Name=null,Value=NormalizedValue.CreateBoolean(false)}}};
                Assert.That(ItemTransferService.MovementV4Digest(value),Is.EqualTo(OldDigest(value)),kind.ToString());
            }
            var cycle=NormalizedValue.CreateNull();cycle.Items.Add(cycle);
            Assert.Throws<JsonSerializationException>(()=>ItemTransferService.MovementV4Digest(cycle));
            var next=NormalizedValue.CreateString("after failed graph");
            Assert.That(ItemTransferService.MovementV4Digest(next),Is.EqualTo(OldDigest(next)));
        }
    }
}
