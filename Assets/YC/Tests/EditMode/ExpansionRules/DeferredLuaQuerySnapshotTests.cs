using System;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离Lua宿主与快照生命周期合同，不计正常界面或对局验收。
    public sealed class DeferredLuaQuerySnapshotTests
    {
        private static void Defer(LuaFacadeSnapshot snapshot,string name,Func<LuaSnapshotValue> build)
            => typeof(LuaFacadeSnapshot).GetMethod("Defer"+name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(snapshot,new object[]{build});
        private static int Pending(LuaFacadeSnapshot snapshot)
            => (int)typeof(LuaFacadeSnapshot).GetProperty("DeferredQueryMask",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(snapshot);
        [TestCase(false)][TestCase(true)]
        public void SuccessfulAndRejectedLuaCallsOnlyBuildRequestedMapAndReleaseEveryFactory(bool rejected)
        {
            var dispatcher=new LuaEffectEventDispatcher(new EffectRegistry());
            for(int attempt=0;attempt<3;attempt++)
            {
                var snapshot=LuaFacadeSnapshot.FromInvocation("p1",0,0,2,"1.0.0");snapshot.StateRevision=3;
                int maps=0,content=0,facilities=0;
                Defer(snapshot,"GlobalMap",()=>{maps++;return LuaSnapshotValue.Object(new[]{
                    new LuaSnapshotEntry("locations",LuaSnapshotValue.Array(Array.Empty<LuaSnapshotValue>())),
                    new LuaSnapshotEntry("mapId",LuaSnapshotValue.String("isolated.map")),
                    new LuaSnapshotEntry("stateRevision",LuaSnapshotValue.Integer(3))});});
                Defer(snapshot,"GlobalContent",()=>{content++;throw new InvalidOperationException("未读取内容不应构建");});
                Defer(snapshot,"GlobalFacilities",()=>{facilities++;throw new InvalidOperationException("未读取设施不应构建");});
                string source="return function(ctx) local m=Global.Map.GetMap(); "+(rejected?"m.changed=true;":"assert(#m.locations==0);")+"return {} end";
                var definition=new LuaScriptDefinition("isolated.lazy","isolated.lazy.ability","isolated.lazy.handler","1.0.0",source,LuaContentHasher.ComputeSha256(source));
                var context=new LuaInvocationContext("event.lazy","EffectCompleted","p1",3,"1.0.0",2,0,0,snapshot);
                var method=typeof(LuaEffectEventDispatcher).GetMethod("InvokeScript",BindingFlags.Instance|BindingFlags.NonPublic);
                var result=(LuaInvocationResult)method.Invoke(dispatcher,new object[]{definition,context});
                Assert.That(result.IsSuccess,Is.EqualTo(!rejected),result.Diagnostic);
                Assert.That(maps,Is.EqualTo(1));Assert.That(content,Is.Zero);Assert.That(facilities,Is.Zero);
                Assert.That(Pending(snapshot),Is.Zero,"成功、失败和下一次调用都不能保留活动状态构造器");
            }
        }
        [Test]
        public void ExplicitSnapshotValuesCancelDeferredBuildersAndKeepNullSemantics()
        {
            var snapshot=new LuaFacadeSnapshot();int calls=0;
            Defer(snapshot,"GlobalMap",()=>{calls++;throw new InvalidOperationException("旧构造器不应被调用");});
            snapshot.GlobalMap=null;
            Assert.That(snapshot.GlobalMap,Is.Null);Assert.That(calls,Is.Zero);Assert.That(Pending(snapshot),Is.Zero);
        }
    }
}
