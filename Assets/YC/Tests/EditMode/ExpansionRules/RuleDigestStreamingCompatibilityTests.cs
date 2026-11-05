using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class RuleDigestStreamingCompatibilityTests
    {
        private enum Small : sbyte { Negative = -12 }
        [Serializable] private sealed class Mixed
        {
            private int privateValue = -23;
            public string Text = null;
            public Small Enum = Small.Negative;
            public bool Boolean = true;
            public byte Byte = 255;
            public sbyte SignedByte = -127;
            public short Short = short.MinValue;
            public ushort UnsignedShort = ushort.MaxValue;
            public int Integer = int.MinValue;
            public uint UnsignedInteger = uint.MaxValue;
            public long Long = long.MinValue;
            public ulong UnsignedLong = ulong.MaxValue;
            public char Character = '\u96ea';
            public float Float = float.NaN;
            public double Double = double.NegativeInfinity;
            public decimal Decimal = 0.100m;
            public DateTime Date = new DateTime(2026,11,3,1,2,3,DateTimeKind.Utc);
            public DateTimeOffset Offset = new DateTimeOffset(2026,11,3,9,2,3,TimeSpan.FromHours(8));
            public Guid Guid = System.Guid.Parse("f841c154-04e4-4600-b665-0123456789ab");
            public TimeSpan Span = TimeSpan.FromTicks(-31);
            public List<object> Values = new List<object>{null, "混合😀", -3, new[]{"雪",null}, NormalizedValue.CreateInteger(-7)};
            public object Array = System.Array.CreateInstance(typeof(int),new[]{2,2},new[]{-1,3});
            [NonSerialized] public string Ignored = "不入摘要";
        }
        private static byte[] Bytes(Type service, object value)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream,new UTF8Encoding(false,true),true);
            service.GetMethod("WriteValue",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,
                new object[]{writer,value,new HashSet<object>(),false,false});
            writer.Flush();return stream.ToArray();
        }
        [Test]
        public void FieldWritersKeepEveryExistingScalarEnumPrivateFieldAndContainerByte()
        {
            var prior = CultureInfo.CurrentCulture;
            try
            {
                foreach(string culture in new[]{"zh-CN","fr-FR","en-US"})
                {
                    CultureInfo.CurrentCulture = new CultureInfo(culture);
                    foreach(object value in new object[]{new Mixed(),null,new Mixed{Text="斜线\\引号\"\n😀"}})
                        Assert.That(Bytes(typeof(RuleStateDigestService),value),Is.EqualTo(Bytes(typeof(LegacyRuleStateDigestReference),value)),culture);
                }
            }
            finally { CultureInfo.CurrentCulture = prior; }
        }
        [Test]
        public void StreamingUtf8KeepsLongSurrogateBoundariesNullsAndInvalidInputBehavior()
        {
            var state = new GameState {GameId = new string('雪',1023)+"😀"+new string('x',4096), ContentPackHash=null};
            state.Logs.Add(new GameLogEntry{Message="本地日志正文应继续排除"});
            Assert.That(RuleStateDigestService.Compute(state),Is.EqualTo(LegacyRuleStateDigestReference.Compute(state)));
            state.GameId="invalid\ud800";
            Assert.Throws<EncoderFallbackException>(()=>RuleStateDigestService.Compute(state));
            Assert.Throws<EncoderFallbackException>(()=>LegacyRuleStateDigestReference.Compute(state));
            state.GameId="after invalid text";
            Assert.That(RuleStateDigestService.Compute(state),Is.EqualTo(LegacyRuleStateDigestReference.Compute(state)));
        }

        [Test]
        public void RepeatedShortTextsPastWorkingSetCapacityKeepBytesAndLaterInputChanges()
        {
            var state=new GameState{GameId="isolated.digest.short-texts",ContentPackHash=new string('雪',129)};
            for(int i=0;i<5000;i++)
            {
                string text=i.ToString("D6",CultureInfo.InvariantCulture)+new string('雪',120+i%3);
                state.Decks.CharacterDeck.Values.Add(text);state.Decks.CharacterDiscard.Values.Add(text);
            }
            var before=RuleStateDigestService.Compute(state);
            Assert.That(before,Is.EqualTo(LegacyRuleStateDigestReference.Compute(state)));
            state.Decks.CharacterDiscard.Values[0]="changed";
            Assert.That(RuleStateDigestService.Compute(state),Is.Not.EqualTo(before));
            Assert.That(RuleStateDigestService.Compute(state),Is.EqualTo(LegacyRuleStateDigestReference.Compute(state)));
        }
    }
}
