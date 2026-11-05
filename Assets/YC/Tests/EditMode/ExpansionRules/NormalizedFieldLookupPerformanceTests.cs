using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class NormalizedFieldLookupPerformanceTests
    {
        [Test]
        public void Utf8LengthValidationKeepsExactBoundariesAndFallback()
        {
            foreach(var text in new[]{null,"",new string('a',1024),new string('a',1025),new string('雪',341),new string('雪',342),
                string.Concat(System.Linq.Enumerable.Repeat("😀",256)),string.Concat(System.Linq.Enumerable.Repeat("😀",257)),
                new string((char)0xd800,341),new string((char)0xd800,342),new string('a',128),new string('a',129),
                new string('雪',42),new string('雪',43),string.Concat(System.Linq.Enumerable.Repeat("😀",32)),string.Concat(System.Linq.Enumerable.Repeat("😀",33))})
            {
                bool valid=text!=null && Encoding.UTF8.GetByteCount(text)<=NormalizedValue.MaxStringBytes;
                Assert.That(new NormalizedValue{Kind=NormalizedValueKind.String,StringValue=text}.IsValid(),Is.EqualTo(valid));
                bool property=!string.IsNullOrEmpty(text) && Encoding.UTF8.GetByteCount(text)<=128;
                Assert.That(new NormalizedValue{Kind=NormalizedValueKind.Object,Properties=new List<NormalizedValueEntry>{
                    new NormalizedValueEntry{Name=text,Value=NormalizedValue.CreateNull()}}}.IsValid(),Is.EqualTo(property));
            }
        }

        [Test]
        public void EmptyNormalizedChildrenKeepIndependentListsAndTargetReferences()
        {
            var source=new NormalizedValue{Kind=NormalizedValueKind.Integer,IntegerValue=12,StringValue="保留闲置字段"};
            var clone=GameStateCloneService.DeepClone(source);
            Assert.That(clone.Items,Is.Not.SameAs(source.Items));
            Assert.That(clone.Properties,Is.Not.SameAs(source.Properties));
            Assert.That(clone.StringValue,Is.EqualTo(source.StringValue));
            var target=new NormalizedValue();var items=target.Items;var properties=target.Properties;
            var targetState=new GameState();var sourceState=new GameState();
            targetState.EffectRuntime.Journal.Add(new RuleJournalEntry{Payload=target});
            sourceState.EffectRuntime.Journal.Add(new RuleJournalEntry{Payload=source});
            items.Add(NormalizedValue.CreateNull());properties.Add(new NormalizedValueEntry{Name="旧",Value=NormalizedValue.CreateNull()});
            GameStateCloneService.CopyTo(targetState,sourceState);
            Assert.That(target.Items,Is.SameAs(items).And.Empty);Assert.That(target.Properties,Is.SameAs(properties).And.Empty);
            items.Add(NormalizedValue.CreateNull());source.Items=null;
            GameStateCloneService.CopyTo(targetState,sourceState);
            Assert.That(target.Items,Is.Null);Assert.That(items.Count,Is.EqualTo(1),"移除引用不应清空外部仍持有的旧列表");
        }
        [Test]
        public void EmptyListFastCopyKeepsRepeatedReferencePairSemantics()
        {
            var source=new GameState();var target=new GameState();
            var empty=new List<NormalizedValue>();var shared=new List<NormalizedValue>{NormalizedValue.CreateNull()};
            for(int i=0;i<3;i++)
            {
                source.EffectRuntime.Journal.Add(new RuleJournalEntry{Payload=new NormalizedValue{
                    Items=i==1 ? new List<NormalizedValue>{NormalizedValue.CreateString("中间写入")} : empty}});
                target.EffectRuntime.Journal.Add(new RuleJournalEntry{Payload=new NormalizedValue{Items=shared}});
            }
            GameStateCloneService.CopyTo(target,source);
            Assert.That(shared.Count,Is.EqualTo(1));Assert.That(shared[0].StringValue,Is.EqualTo("中间写入"));
            Assert.That(target.EffectRuntime.Journal[2].Payload.Items,Is.SameAs(shared));
        }

        [Test]
        public void LookupKeepsFirstMatchNullAndFallbackSemantics()
        {
            var first=NormalizedValue.CreateString("first");
            var value=new NormalizedValue {Properties=new List<NormalizedValueEntry>{
                new NormalizedValueEntry{Name="key",Value=first},new NormalizedValueEntry{Name="key",Value=NormalizedValue.CreateString("second")},
                new NormalizedValueEntry{Name=null,Value=NormalizedValue.CreateInteger(8)},new NormalizedValueEntry{Name="null",Value=null}}};
            Assert.That(ItemTransferEffectExecutor.Value(value,"key"),Is.SameAs(first));
            Assert.That(ItemTransferEffectExecutor.Text(value,"key"),Is.EqualTo("first"));
            Assert.That(ItemTransferEffectExecutor.Value(value,null).IntegerValue,Is.EqualTo(8));
            Assert.That(ItemTransferEffectExecutor.Text(value,"null","fallback"),Is.EqualTo("fallback"));
            Assert.That(ItemTransferEffectExecutor.Value(null,"key"),Is.Null);
            Assert.That(ItemTransferEffectExecutor.Number(value,"missing",17),Is.EqualTo(17));
            Assert.That(ItemTransferEffectExecutor.Flag(value,"key"),Is.False);
            value.Properties.Insert(0,null);
            Assert.Throws<NullReferenceException>(()=>ItemTransferEffectExecutor.Value(value,"key"));
        }
        [Test]
        public void NoSelectionReceiptStillRejectsAuthorizationAndMalformedLocalFields()
        {
            var state=new GameState();var item=new ItemInstanceState();
            Assert.That(AbilitySourceSelection.ValidateLocalState(state,item,out _),Is.True);
            item.LocalState.Properties.Add(new NormalizedValueEntry{Name="activationAuthorizationRef",Value=NormalizedValue.CreateString("unproven")});
            Assert.That(AbilitySourceSelection.ValidateLocalState(state,item,out _),Is.False);
            item.LocalState.Properties.Clear();
            item.LocalState.Properties.Add(new NormalizedValueEntry{Name="activeFaceId",Value=NormalizedValue.CreateInteger(3)});
            Assert.That(AbilitySourceSelection.ValidateLocalState(state,item,out _),Is.False);
            item.LocalState.Properties.Clear();
            item.LocalState.Properties.Add(new NormalizedValueEntry{Name="sourceSelectionReceipt",Value=NormalizedValue.CreateString("fake")});
            Assert.That(AbilitySourceSelection.ValidateLocalState(state,item,out _),Is.False);
        }
    }
}
