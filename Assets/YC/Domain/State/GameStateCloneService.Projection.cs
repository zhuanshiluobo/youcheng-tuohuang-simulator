using System.Collections.Generic;
using YC.Domain.Items;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        // 只供没有原始状态回调的同步展示使用；可变世界完整复制，规则历史只读借用。
        // 结果不得发布为规则状态或交给处理器。公开DeepClone／CopyTo合同不变。
        internal static GameState CloneWorldForReadOnlyProjection(GameState source)
        {
            using var sample=MeasureDiagnostic("projection.world_snapshot");
            var metadata=StateCopyMetadata.For(typeof(GameState));
            var result=(GameState)metadata.Create();
            var active=new HashSet<object>(ReferenceEqualityComparer.Instance);
            active.Add(source);
            var path=new DiagnosticPath(nameof(GameState));
            foreach(var field in metadata.Fields)
            {
                if(field.Name==nameof(GameState.EffectRuntime))field.SetValue(result,source.EffectRuntime);
                else if(field.IsScalar)field.CopyScalar(result,source);
                else field.SetValue(result,CloneValue(field.GetValue(source),field.FieldType,path.AtField(field.Name),active));
            }
            ItemZoneLegacyProjection.Bind(result);
            return result;
        }
    }
}
