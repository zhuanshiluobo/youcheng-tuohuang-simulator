using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    /// <summary>API-38正式命令专用四字段编码；其它schema不接受此通道。</summary>
    public static class AllocationSequenceAnswerJson
    {
        public const string Parameter="allocationSequenceV1";
        public static string Encode(NormalizedValue answer) => new JObject {
            ["sequence"]=new JArray(ItemTransferEffectExecutor.Value(answer,"sequence").Items.Select(v=>v.StringValue)),
            ["submitKind"]=AllocationEffects.S(answer,"submitKind"),["candidateSetId"]=AllocationEffects.S(answer,"candidateSetId"),
            ["candidateSetVersion"]=ItemTransferEffectExecutor.Value(answer,"candidateSetVersion").IntegerValue }.ToString(Formatting.None);
        public static bool TryDecode(string json,out NormalizedValue answer)
        {
            answer=null;if(string.IsNullOrEmpty(json) || json.Length>8192)return false;
            try
            {
                using var reader=new JsonTextReader(new StringReader(json)){MaxDepth=8,DateParseHandling=DateParseHandling.None};
                var obj=JObject.Load(reader,new JsonLoadSettings{DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error});
                if(reader.Read() || obj.Count!=4 || obj.Properties().Any(p=>!new[]{"sequence","submitKind","candidateSetId","candidateSetVersion"}.Contains(p.Name)) ||
                    obj["sequence"] is not JArray sequence || sequence.Count>99 || sequence.Any(v=>v.Type!=JTokenType.String || v.Value<string>().Length<1 || v.Value<string>().Length>64) ||
                    obj["submitKind"]?.Type!=JTokenType.String || !new[]{"confirm","end_action"}.Contains(obj.Value<string>("submitKind")) || obj["candidateSetId"]?.Type!=JTokenType.String ||
                    obj.Value<string>("candidateSetId").Length<1 || obj.Value<string>("candidateSetId").Length>1024 || obj["candidateSetVersion"]?.Type!=JTokenType.Integer || obj.Value<long>("candidateSetVersion")<1 || obj.Value<long>("candidateSetVersion")>int.MaxValue)return false;
                answer=ItemTransferEffectExecutor.Object("sequence",ItemTransferEffectExecutor.Strings(sequence.Values<string>()),"submitKind",NormalizedValue.CreateString(obj.Value<string>("submitKind")),"candidateSetId",NormalizedValue.CreateString(obj.Value<string>("candidateSetId")),"candidateSetVersion",NormalizedValue.CreateInteger(obj.Value<long>("candidateSetVersion")));
                return answer.IsValid();
            }
            catch(Exception e)when(e is JsonException || e is ArgumentException || e is OverflowException){return false;}
        }
    }
}
