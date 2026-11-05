using System;
using System.Collections.Generic;
using NUnit.Framework;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class CanonicalValueEqualityTests
    {
        private static string Outcome(Func<bool> action)
        {try{return action()?"true":"false";}catch(Exception error){return error.GetType().FullName+":"+error.Message;}}
        private static void Same(NormalizedValue left,NormalizedValue right)
        {
            Assert.That(Outcome(()=>left.HasSameCanonicalValue(right)),Is.EqualTo(Outcome(()=>left.ToDeterministicString()==right.ToDeterministicString())));
        }
        private static List<NormalizedValue> Values()
        {
            var values=new List<NormalizedValue>{null,NormalizedValue.CreateNull(),NormalizedValue.CreateBoolean(false),NormalizedValue.CreateBoolean(true),NormalizedValue.CreateInteger(0),NormalizedValue.CreateInteger(NormalizedValue.MinInteger),NormalizedValue.CreateInteger(NormalizedValue.MaxInteger)};
            foreach(string s in new[]{"","N","中英:O2{A]","\0\r\n","\ud800","\udfff","\ud83d\ude03",new string('a',1024),new string('中',341)})values.Add(NormalizedValue.CreateString(s));
            values.Add(NormalizedValue.CreateStableReference("类型:","对象;"));
            values.Add(NormalizedValue.CreateArray(new List<NormalizedValue>{NormalizedValue.CreateInteger(1),NormalizedValue.CreateInteger(2)}));
            values.Add(NormalizedValue.CreateObject(new List<NormalizedValueEntry>{new NormalizedValueEntry{Name="a",Value=NormalizedValue.CreateString("前")},new NormalizedValueEntry{Name="b",Value=NormalizedValue.CreateBoolean(false)}}));
            values.Add(new NormalizedValue{Kind=NormalizedValueKind.Integer,IntegerValue=NormalizedValue.MaxInteger+1});
            values.Add(new NormalizedValue{Kind=NormalizedValueKind.String,StringValue=null});
            values.Add(new NormalizedValue{Kind=NormalizedValueKind.String,StringValue=new string('中',342)});
            values.Add(new NormalizedValue{Kind=NormalizedValueKind.StableReference,ReferenceType="",ReferenceId="x"});
            values.Add(new NormalizedValue{Kind=(NormalizedValueKind)999});
            values.Add(new NormalizedValue{Kind=NormalizedValueKind.Array,Items=new List<NormalizedValue>{null}});
            values.Add(new NormalizedValue{Kind=NormalizedValueKind.Array,Items=new List<NormalizedValue>{NormalizedValue.CreateInteger(1),NormalizedValue.CreateString("2")}});
            values.Add(new NormalizedValue{Kind=NormalizedValueKind.Object,Properties=new List<NormalizedValueEntry>{new NormalizedValueEntry{Name="b",Value=NormalizedValue.CreateNull()},new NormalizedValueEntry{Name="a",Value=NormalizedValue.CreateNull()}}});
            values.Add(new NormalizedValue{Kind=NormalizedValueKind.Object,Properties=new List<NormalizedValueEntry>{new NormalizedValueEntry{Name=new string('中',43),Value=NormalizedValue.CreateNull()}}});
            var cycle=new NormalizedValue{Kind=NormalizedValueKind.Array};cycle.Items.Add(cycle);values.Add(cycle);
            var oversized=new NormalizedValue{Kind=NormalizedValueKind.Array};for(int i=0;i<129;i++)oversized.Items.Add(NormalizedValue.CreateNull());values.Add(oversized);
            var deep=NormalizedValue.CreateNull();for(int i=0;i<10;i++)deep=new NormalizedValue{Kind=NormalizedValueKind.Array,Items=new List<NormalizedValue>{deep}};values.Add(deep);
            return values;
        }
        [Test]
        public void BothResultsAndFirstFailureMatchTheOriginalCanonicalStrings()
        {
            var values=Values();using var verification=NormalizedValue.VerifyCanonicalEqualityForDiagnostics();
            foreach(var left in values)foreach(var right in values)Same(left,right);
        }
        [Test]
        public void InactiveFieldsAreIgnoredAndOrdinalDifferencesRemainDistinct()
        {
            var left=NormalizedValue.CreateString("相同");var right=NormalizedValue.CreateString("相同");
            left.IntegerValue=999;left.ReferenceId=null;left.Items.Add(left);
            Assert.That(left.HasSameCanonicalValue(right),Is.True);Same(left,right);
            right.StringValue="不同";Assert.That(left.HasSameCanonicalValue(right),Is.False);Same(left,right);
            Same(NormalizedValue.CreateString("\u00e9"),NormalizedValue.CreateString("e\u0301"));
            Assert.That(NormalizedValue.CreateString("I").HasSameCanonicalValue(NormalizedValue.CreateString("i")),Is.False);
        }
        [Test]
        public void ContainersAndNestedAliasesKeepTheOriginalElementCountingAndOrder()
        {
            var leaf=NormalizedValue.CreateInteger(3);var a=NormalizedValue.CreateArray(new List<NormalizedValue>{leaf,leaf});
            var b=NormalizedValue.CreateArray(new List<NormalizedValue>{NormalizedValue.CreateInteger(3),NormalizedValue.CreateInteger(3)});
            Same(a,b);Assert.That(a.HasSameCanonicalValue(b),Is.True);
            b.Items.Reverse();Same(a,b);b.Items[1].IntegerValue=4;Same(a,b);Assert.That(a.HasSameCanonicalValue(b),Is.False);
            using(NormalizedValue.UseFullCanonicalEqualityForDiagnostics())Same(a,b);
        }
    }
}
