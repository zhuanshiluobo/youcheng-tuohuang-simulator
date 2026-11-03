using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;
using YC.Domain.Effects;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public sealed partial class MoonSharpLuaRuntimeHost
    {
        private static bool DenseAllocationArray(DynValue value, int min, int max)
        {
            if (value.Type!=DataType.Table) return false;
            var table=SnapshotValues(value.Table); var pairs=table.Pairs.ToList();
            return table.Length>=min && table.Length<=max && pairs.Count==table.Length && pairs.All(p=>p.Key.Type==DataType.Number && p.Key.Number==Math.Truncate(p.Key.Number) && p.Key.Number>=1 && p.Key.Number<=table.Length);
        }
        private static bool TryNormalizeAllocation(Table table,LuaInvocationContext context,out LuaEffectSpec effect,out LuaFailureCode code,out string diagnostic)
        {
            effect=null;code=LuaFailureCode.InvalidEffectSpec;diagnostic="allocation_declaration_invalid";
            try
            {
                var rawOptions=table.Get("options");
                if (!DenseAllocationArray(rawOptions,1,16)) return false;
                var options=new List<AllocationOptionV1>();var groups=new List<IReadOnlyList<LuaEffectSpec>>();
                for (int i=1;i<=rawOptions.Table.Length;i++)
                {
                    var raw=rawOptions.Table.Get(i);if(raw.Type!=DataType.Table)return false;
                    var item=SnapshotValues(raw.Table);
                    if(item.Pairs.Count()!=4 || item.Pairs.Any(p=>p.Key.Type!=DataType.String || !new[]{"id","maxExecutions","text","effects"}.Contains(p.Key.String)))return false;
                    var id=item.Get("id");var max=item.Get("maxExecutions");
                    if(id.Type!=DataType.String || max.Type!=DataType.Number || max.Number!=Math.Truncate(max.Number) || max.Number<0 || max.Number>99 || !DenseAllocationArray(item.Get("effects"),1,16))return false;
                    var text=GameplayPresentationNormalizer.FromNormalized<GameplayTextRef>(ReadPresentationValue(item.Get("text"),0));
                    if(!TryReadNestedEffectArray(item.Get("effects"),context,"effects",out var roots,out code,out diagnostic))return false;
                    if(roots.Any(ContainsAllocation)) { diagnostic="allocation_nested_effect_invalid";return false; }
                    options.Add(new AllocationOptionV1{Id=id.String,MaxExecutions=(int)max.Number,Text=text});groups.Add(roots);
                }
                var args=new List<NormalizedValueEntry>();
                foreach(string name in new[]{"apiVersion","player","totalExecutions","allowEndAction"})
                {
                    var value=ToNormalizedValue(table.Get(name),0,out code,out diagnostic);if(value==null)return false;
                    args.Add(new NormalizedValueEntry{Name=name,Value=value});
                }
                var arguments=NormalizedValue.CreateObject(args);NormalizeContractPlayerFields(arguments,"player");
                diagnostic=AllocationEffects.ValidateArguments(arguments,options,groups.Count);
                if(diagnostic.Length>0 || AllocationEffects.S(arguments,"player")!=ParsePlayerKey(context.PlayerId).ToString(System.Globalization.CultureInfo.InvariantCulture)) { code=LuaFailureCode.InvalidEffectSpec;return false; }
                effect=LuaEffectSpec.FromEffectGroups(AllocationEffects.TypeId,arguments,groups.ToArray());effect.AllocationOptions=options;
                code=LuaFailureCode.None;return true;
            }
            catch(ArgumentException error){diagnostic=error.Message;code=LuaFailureCode.InvalidEffectSpec;return false;}
        }
        private static bool ContainsAllocation(LuaEffectSpec spec) => spec.EffectTypeId==AllocationEffects.TypeId || spec.NestedEffectGroups!=null && spec.NestedEffectGroups.Any(g=>g!=null && g.Any(ContainsAllocation));
    }
}
