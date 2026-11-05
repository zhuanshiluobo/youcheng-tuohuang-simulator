using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class RuleStateSha256CompatibilityTests
    {
        private static SHA256 Create() => (SHA256)typeof(RuleStateDigestService).Assembly
            .GetType("YC.Domain.State.RuleStateSha256",true).GetMethod("Create",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);

        [Test]
        public void ProviderKeepsEmptyOffsetFragmentedAndReusedHashBytes()
        {
            var input=Enumerable.Range(0,131257).Select(i=>(byte)(i*37)).ToArray();
            var unchanged=(byte[])input.Clone();
            using var actual=Create();
            if(Environment.OSVersion.Platform==PlatformID.Win32NT)Assert.That(actual.GetType().Name,Is.EqualTo("WindowsSha256"));
            foreach(int size in new[]{0,1,63,64,65,4095,4096,65535,65536,131219})
            {
                using var reference=SHA256.Create();var expected=reference.ComputeHash(input,17,size);
                Assert.That(actual.ComputeHash(input,17,size),Is.EqualTo(expected),"偏移/长度="+size);
                using var streamed=Create();
                using var stream=new CryptoStream(Stream.Null,streamed,CryptoStreamMode.Write,true);
                int first=Math.Min(13,size);stream.Write(input,17,first);stream.Write(input,17+first,size-first);stream.FlushFinalBlock();
                Assert.That(streamed.Hash,Is.EqualTo(expected),"分段="+size);
            }
            Assert.That(input,Is.EqualTo(unchanged));
            actual.Dispose();actual.Dispose();
            Assert.Throws<ObjectDisposedException>(()=>actual.ComputeHash(input));
        }
    }
}
