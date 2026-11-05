using System.Collections.Generic;
using YC.Domain.Rules;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        // ItemZoneQuery.TryValidateCore及其PlayerAreaPolicy、PlayerMarkerTrackPolicy、
        // FacilityLayerQuery和AbilitySourceSelection调用链的完整只读输入。
        // 新增实体校验依赖时必须同步此集合；诊断模式仍对每次命中执行原完整校验。
        private static readonly HashSet<string> EntityWorldInputFields=new HashSet<string>(System.StringComparer.Ordinal)
        {
            nameof(GameState.GameId),nameof(GameState.MapId),nameof(GameState.EntitySchemaVersion),
            nameof(GameState.ItemZones),nameof(GameState.Players),nameof(GameState.Map),nameof(GameState.Decks),
            nameof(GameState.GameBox),nameof(GameState.SetupRecipe),nameof(GameState.ContentDefinitions)
        };
        private static GameContentDefinitions CloneEntityDefinitions(GameContentDefinitions source)
        {
            if(source==null)return null;
            return new GameContentDefinitions
            {
                Awards=DeepClone(source.Awards),
                UsageTracks=DeepClone(source.UsageTracks)
            };
        }
        private static bool SameEntityDefinitions(GameContentDefinitions baseline,GameContentDefinitions current)
        {
            if(baseline==null || current==null)return ReferenceEquals(baseline,current);
            return HaveIdenticalReadOnlyFields(baseline.Awards,current.Awards) && HaveIdenticalReadOnlyFields(baseline.UsageTracks,current.UsageTracks);
        }
        // 私有基线不执行绑定，所选输入全部独立复制，其他字段保持独立默认值，不借用原世界。
        internal static GameState CloneEntityWorld(GameState source)
        {
            var metadata=StateCopyMetadata.For(typeof(GameState));
            var result=(GameState)metadata.Create();result.EffectRuntime=null;
            var active=new HashSet<object>(ReferenceEqualityComparer.Instance){source};
            var path=new DiagnosticPath(nameof(GameState));
            foreach(var field in metadata.Fields)
            {
                if(!EntityWorldInputFields.Contains(field.Name))continue;
                if(field.Name==nameof(GameState.ContentDefinitions))result.ContentDefinitions=CloneEntityDefinitions(source.ContentDefinitions);
                else if(field.IsScalar)field.CopyScalar(result,source);
                else field.SetValue(result,CloneValue(field.GetValue(source),field.FieldType,path.AtField(field.Name),active));
            }
            return result;
        }
        internal static bool HaveIdenticalEntityWorld(GameState baseline,GameState current)
        {
            foreach(var field in StateCopyMetadata.For(typeof(GameState)).Fields)
            {
                if(!EntityWorldInputFields.Contains(field.Name))continue;
                bool same=field.Name==nameof(GameState.ContentDefinitions) ? SameEntityDefinitions(baseline.ContentDefinitions,current.ContentDefinitions) :
                    HaveIdenticalReadOnlyFields(field.GetValue(baseline),field.GetValue(current));
                if(!same){using var changed=MeasureDiagnostic("entity.validation.different_world",field.Name);return false;}
            }
            return true;
        }
        // 来源选择收据只读取这些请求字段；revision、展示、别的请求不参与实体授权证明。
        internal static bool HaveIdenticalEntitySelectionRequest(InteractionRequest baseline,InteractionRequest current) =>
            ReferenceEquals(baseline.InteractionId,current.InteractionId) && ReferenceEquals(baseline.RequestId,current.RequestId) &&
            ReferenceEquals(baseline.OwnerEffectId,current.OwnerEffectId) && baseline.AnsweringPlayerId==current.AnsweringPlayerId &&
            ReferenceEquals(baseline.Status,current.Status) && HaveIdenticalReadOnlyFields(baseline.NormalizedAnswer,current.NormalizedAnswer) &&
            HaveIdenticalReadOnlyFields(baseline.CandidateIds,current.CandidateIds) && HaveIdenticalReadOnlyFields(baseline.PromptParameters,current.PromptParameters);
    }
}
