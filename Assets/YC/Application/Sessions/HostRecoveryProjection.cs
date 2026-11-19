using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YC.Application.Sessions
{
    public static partial class HostRecoveryService
    {
        // 正式原始字段检查不读取 EffectRuntime 的 Journal/RuleEvents；语法和重复键仍完整扫描，
        // 随后的 DTO 反序列化、恢复校验仍读取并验证原始完整历史。新增原始检查时必须同步本投影。
        private enum RecoveryShape { Full, Discard, Root, Snapshot, State, Runtime, ArchiveJournal, Entries, Entry, History }

        private static JObject ReadRecoveryValidationProjection(string json, out bool projected)
        {
            var settings = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
            projected = false;
            try
            {
                using var text = new StringReader(json);
                using var reader = new JsonTextReader(text);
                if (!ReadRecoveryContent(reader) || reader.TokenType != JsonToken.StartObject)
                    throw new JsonReaderException("需要完整的 Host JSON 对象。");
                var result = (JObject)ReadRecoveryValue(reader, RecoveryShape.Root, settings);
                while (reader.Read()) { } // 保留原来的尾部语法及多内容检查。
                projected = true;
                return result;
            }
            catch (JsonReaderException)
            {
                // 异常或宽松语法交给原解析器，保持错误类型和兼容行为。
                return JObject.Parse(json, settings);
            }
        }

        private static bool ReadRecoveryContent(JsonReader reader)
        {
            while (reader.Read()) if (reader.TokenType != JsonToken.Comment) return true;
            return false;
        }

        private static JToken ReadRecoveryValue(JsonReader reader, RecoveryShape shape, JsonLoadSettings settings)
        {
            if (shape == RecoveryShape.Full) return JToken.ReadFrom(reader, settings);
            if (shape == RecoveryShape.History)
            {
                if (reader.TokenType != JsonToken.StartArray) return JToken.ReadFrom(reader, settings);
                ReadRecoveryValue(reader, RecoveryShape.Discard, settings);
                return new JArray();
            }
            if (reader.TokenType == JsonToken.StartObject && shape != RecoveryShape.Entries)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                var result = shape == RecoveryShape.Discard ? null : new JObject();
                while (ReadRecoveryContent(reader))
                {
                    if (reader.TokenType == JsonToken.EndObject) return result;
                    if (reader.TokenType != JsonToken.PropertyName) throw new JsonReaderException("对象属性结构不完整。");
                    string name = (string)reader.Value;
                    if (!names.Add(name)) throw new JsonReaderException("对象含重复属性。");
                    var next = SelectRecoveryShape(shape, name);
                    if (!ReadRecoveryContent(reader)) throw new JsonReaderException("属性值缺失。");
                    var value = ReadRecoveryValue(reader, next, settings);
                    if (result != null) result.Add(name, value);
                }
                throw new JsonReaderException("对象未结束。");
            }
            if (reader.TokenType == JsonToken.StartArray && (shape == RecoveryShape.Entries || shape == RecoveryShape.Discard))
            {
                var result = shape == RecoveryShape.Entries ? new JArray() : null;
                while (ReadRecoveryContent(reader))
                {
                    if (reader.TokenType == JsonToken.EndArray) return result;
                    var value = ReadRecoveryValue(reader, shape == RecoveryShape.Entries ? RecoveryShape.Entry : RecoveryShape.Discard, settings);
                    if (result != null) result.Add(value);
                }
                throw new JsonReaderException("数组未结束。");
            }
            // 未预期的保留字段形状交给原来的字段检查；丢弃字段也要完整扫描重复键。
            if (shape != RecoveryShape.Discard) return JToken.ReadFrom(reader, settings);
            if (reader.TokenType == JsonToken.StartConstructor)
            {
                while (ReadRecoveryContent(reader))
                {
                    if (reader.TokenType == JsonToken.EndConstructor) return null;
                    ReadRecoveryValue(reader, RecoveryShape.Discard, settings);
                }
                throw new JsonReaderException("构造值未结束。");
            }
            switch (reader.TokenType)
            {
                case JsonToken.Integer: case JsonToken.Float: case JsonToken.String: case JsonToken.Boolean:
                case JsonToken.Null: case JsonToken.Undefined: case JsonToken.Date: case JsonToken.Bytes:
                    return null;
                default: throw new JsonReaderException("JSON 值结构不完整。");
            }
        }

        private static RecoveryShape SelectRecoveryShape(RecoveryShape parent, string name)
        {
            if (parent == RecoveryShape.Discard) return RecoveryShape.Discard;
            if (parent == RecoveryShape.Root && name == "Snapshot") return RecoveryShape.Snapshot;
            if (parent == RecoveryShape.Root && name == "Journal") return RecoveryShape.ArchiveJournal;
            if (parent == RecoveryShape.Snapshot && name == "State") return RecoveryShape.State;
            if (parent == RecoveryShape.ArchiveJournal && name == "Entries") return RecoveryShape.Entries;
            if (parent == RecoveryShape.Entry && name == "StateAfter") return RecoveryShape.State;
            if (parent == RecoveryShape.State && name == "EffectRuntime") return RecoveryShape.Runtime;
            if (parent == RecoveryShape.Runtime && (name == "Journal" || name == "RuleEvents")) return RecoveryShape.History;
            return RecoveryShape.Full;
        }
    }
}
