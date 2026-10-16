using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace YC.Validation
{
    public sealed class ArchiveEvidence
    {
        public Hashtable Snapshot;
        public Hashtable State;
        public long JournalEntryCount;
        public long? MaximumCommitSequence;
    }
    public sealed class JsonLineEvidence
    {
        public object Value;
        public long Offset;
        public int Length;
        public long LineNumber;
    }
    public sealed class TransactionLocation
    {
        public long Offset;
        public int Length;
    }
    public sealed class TransactionEvidenceIndex
    {
        public long Count;
        public readonly Dictionary<string, List<TransactionLocation>> ByNode = new Dictionary<string, List<TransactionLocation>>(StringComparer.InvariantCultureIgnoreCase);
        public readonly Dictionary<string, List<TransactionLocation>> ByCommand = new Dictionary<string, List<TransactionLocation>>(StringComparer.InvariantCultureIgnoreCase);
    }
    // 读取助手只改变工作内存与遍历方式；业务判断仍由原PowerShell谓词执行。
    public static class HostArchiveEvidenceReader
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static JsonTextReader Reader(TextReader input)
        {
            return new JsonTextReader(input) { DateParseHandling = DateParseHandling.DateTime, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 100, CloseInput = false };
        }
        static bool Next(JsonTextReader reader)
        {
            while (reader.Read()) if (reader.TokenType != JsonToken.Comment) return true;
            return false;
        }
        static void RequireNext(JsonTextReader reader)
        {
            if (!Next(reader)) throw new JsonReaderException("JSON意外截断。");
        }
        static object Value(JsonTextReader reader, bool retain)
        {
            if (reader.TokenType == JsonToken.StartObject)
            {
                Hashtable result = retain ? new Hashtable(StringComparer.Ordinal) : null;
                while (true)
                {
                    RequireNext(reader);
                    if (reader.TokenType == JsonToken.EndObject) return result;
                    if (reader.TokenType != JsonToken.PropertyName) throw new JsonReaderException("JSON对象缺少字段名。");
                    string key = (string)reader.Value; RequireNext(reader);
                    object value = Value(reader, retain); if (retain) result[key] = value;
                }
            }
            if (reader.TokenType == JsonToken.StartArray)
            {
                List<object> result = retain ? new List<object>() : null;
                while (true)
                {
                    RequireNext(reader);
                    if (reader.TokenType == JsonToken.EndArray) return retain ? result.ToArray() : null;
                    object value = Value(reader, retain); if (retain) result.Add(value);
                }
            }
            switch (reader.TokenType)
            {
                case JsonToken.Integer: case JsonToken.Float: case JsonToken.String:
                case JsonToken.Boolean: case JsonToken.Null: case JsonToken.Date:
                    return retain ? reader.Value : null;
                default: throw new JsonReaderException("JSON值结构无效：" + reader.TokenType);
            }
        }
        static object Complete(JsonTextReader reader)
        {
            RequireNext(reader); object result = Value(reader, true);
            if (Next(reader)) throw new JsonReaderException("JSON根值后存在额外内容。");
            return result;
        }
        public static object ReadJson(string path)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var input = new StreamReader(stream, Utf8, true, 65536))
            using (var reader = Reader(input)) return Complete(reader);
        }
        // 首遍完整检查所有JSON（包括未选StateAfter），仅保留元数据；次遍只物化旧审计选择的最大序号状态。
        public static ArchiveEvidence ReadArchive(string path)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = new ArchiveEvidence();
                long selected = -1, maximum = long.MinValue;
                int snapshotOccurrence = 0, journalOccurrence = 0, entriesOccurrence = 0;
                using (var input = new StreamReader(stream, Utf8, true, 65536, true))
                using (var reader = Reader(input))
                {
                    RequireNext(reader); if (reader.TokenType != JsonToken.StartObject) throw new JsonReaderException("归档根必须为对象。");
                    while (true)
                    {
                        RequireNext(reader); if (reader.TokenType == JsonToken.EndObject) break;
                        if (reader.TokenType != JsonToken.PropertyName) throw new JsonReaderException("归档字段结构无效。");
                        string name = (string)reader.Value; RequireNext(reader);
                        if (name == "Snapshot" && reader.TokenType == JsonToken.StartObject)
                        {
                            snapshotOccurrence++; result.Snapshot = new Hashtable(StringComparer.Ordinal);
                            while (true)
                            {
                                RequireNext(reader); if (reader.TokenType == JsonToken.EndObject) break;
                                if (reader.TokenType != JsonToken.PropertyName) throw new JsonReaderException("Snapshot字段结构无效。");
                                string field = (string)reader.Value; RequireNext(reader);
                                object value = Value(reader, field != "State"); if (field != "State") result.Snapshot[field] = value;
                            }
                        }
                        else if (name == "Journal" && reader.TokenType == JsonToken.StartObject)
                        {
                            journalOccurrence++; entriesOccurrence = 0; result.JournalEntryCount = 0; selected = -1; maximum = long.MinValue;
                            while (true)
                            {
                                RequireNext(reader); if (reader.TokenType == JsonToken.EndObject) break;
                                if (reader.TokenType != JsonToken.PropertyName) throw new JsonReaderException("Journal字段结构无效。");
                                string field = (string)reader.Value; RequireNext(reader);
                                if (field != "Entries") { Value(reader, false); continue; }
                                entriesOccurrence++;
                                if (reader.TokenType != JsonToken.StartArray) throw new JsonReaderException("Journal.Entries必须为数组。");
                                result.JournalEntryCount = 0; selected = -1; maximum = long.MinValue;
                                while (true)
                                {
                                    RequireNext(reader); if (reader.TokenType == JsonToken.EndArray) break;
                                    long sequence = long.MinValue;
                                    if (reader.TokenType == JsonToken.StartObject)
                                    {
                                        while (true)
                                        {
                                            RequireNext(reader); if (reader.TokenType == JsonToken.EndObject) break;
                                            if (reader.TokenType != JsonToken.PropertyName) throw new JsonReaderException("Journal条目字段无效。");
                                            string entryField = (string)reader.Value; RequireNext(reader);
                                            if (entryField == "CommitSequence") { object value = Value(reader, true); sequence = value == null ? long.MinValue : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
                                            else Value(reader, false);
                                        }
                                    }
                                    else Value(reader, false);
                                    if (selected < 0 || sequence >= maximum) { maximum = sequence; selected = result.JournalEntryCount; }
                                    result.JournalEntryCount++;
                                }
                            }
                        }
                        else Value(reader, false);
                    }
                    if (Next(reader)) throw new JsonReaderException("归档根值后存在额外内容。");
                }
                result.MaximumCommitSequence = selected >= 0 && maximum != long.MinValue ? (long?)maximum : null;
                stream.Position = 0;
                using (var input = new StreamReader(stream, Utf8, true, 65536, true))
                using (var reader = Reader(input))
                {
                    RequireNext(reader); int snapshotsSeen = 0, journalsSeen = 0;
                    while (true)
                    {
                        RequireNext(reader); if (reader.TokenType == JsonToken.EndObject) break;
                        string name = (string)reader.Value; RequireNext(reader);
                        if (name == "Snapshot")
                        {
                            snapshotsSeen++;
                            if (selected < 0 && snapshotsSeen == snapshotOccurrence) { var snapshot = (Hashtable)Value(reader, true); result.State = snapshot?["State"] as Hashtable; return result; }
                            Value(reader, false);
                        }
                        else if (name == "Journal")
                        {
                            journalsSeen++;
                            if (selected < 0 || journalsSeen != journalOccurrence) { Value(reader, false); continue; }
                            int entriesSeen = 0;
                            while (true)
                            {
                                RequireNext(reader); if (reader.TokenType == JsonToken.EndObject) break;
                                string field = (string)reader.Value; RequireNext(reader);
                                if (field != "Entries") { Value(reader, false); continue; }
                                if (++entriesSeen != entriesOccurrence) { Value(reader, false); continue; }
                                long ordinal = 0;
                                while (true)
                                {
                                    RequireNext(reader); if (reader.TokenType == JsonToken.EndArray) break;
                                    if (ordinal++ == selected) { var entry = Value(reader, true) as Hashtable; result.State = entry?["StateAfter"] as Hashtable; return result; }
                                    Value(reader, false);
                                }
                            }
                        }
                        else Value(reader, false);
                    }
                }
                return result;
            }
        }
        static object ParseLine(byte[] bytes, out bool blank)
        {
            string line = Utf8.GetString(bytes).TrimStart('\uFEFF');
            blank = string.IsNullOrWhiteSpace(line);
            if (blank) return null;
            using (var input = new StringReader(line)) using (var reader = Reader(input)) return Complete(reader);
        }
        public static IEnumerable<JsonLineEvidence> ReadLines(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            {
                long lineNumber = 0;
                while (stream.Position < stream.Length)
                {
                    long offset = stream.Position;
                    using (var line = new MemoryStream())
                    {
                        int value; while ((value = stream.ReadByte()) >= 0 && value != 10) line.WriteByte((byte)value);
                        object parsed; bool blank;
                        try { parsed = ParseLine(line.ToArray(), out blank); }
                        catch (Exception error) { throw new JsonReaderException("JSONL坏记录：" + path + "，行" + (lineNumber + 1), error); }
                        lineNumber++;
                        if (!blank) yield return new JsonLineEvidence { Value = parsed, Offset = offset, Length = checked((int)(stream.Position - offset)), LineNumber = lineNumber };
                    }
                }
            }
        }
        public static object ReadLineAt(string path, long offset, int length)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                stream.Position = offset; byte[] bytes = new byte[length]; int read = 0;
                while (read < length) { int count = stream.Read(bytes, read, length - read); if (count == 0) throw new EndOfStreamException("索引所指JSONL记录截断。"); read += count; }
                return ParseLine(bytes, out _);
            }
        }
        static object Get(object value, params string[] path)
        {
            foreach (string field in path) { var map = value as IDictionary; if (map == null || !map.Contains(field)) return null; value = map[field]; }
            return value;
        }
        static IEnumerable<object> Items(object value) { var items = value as IEnumerable; if (items != null && !(value is string)) foreach (object item in items) yield return item; }
        static bool Completed(object node) { string status = Convert.ToString(Get(node, "Status"), CultureInfo.InvariantCulture); return status == "4" || status == "Completed"; }
        static void Add(Dictionary<string, List<TransactionLocation>> index, string key, TransactionLocation location)
        {
            if (!index.TryGetValue(key, out var list)) { list = new List<TransactionLocation>(); index.Add(key, list); }
            list.Add(location);
        }
        public static TransactionEvidenceIndex IndexTransactions(string path)
        {
            var index = new TransactionEvidenceIndex();
            foreach (var record in ReadLines(path))
            {
                index.Count++; var location = new TransactionLocation { Offset = record.Offset, Length = record.Length };
                Add(index.ByCommand, Convert.ToString(Get(record.Value, "CommandId"), CultureInfo.InvariantCulture) ?? "", location);
                object before = Get(record.Value, "Before"), after = Get(record.Value, "After");
                if (before == null || after == null) continue;
                long beforeSequence = Convert.ToInt64(Get(before, "LastCommitSequence"), CultureInfo.InvariantCulture);
                long afterSequence = Convert.ToInt64(Get(after, "LastCommitSequence"), CultureInfo.InvariantCulture);
                var previouslyCompleted = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                foreach (object node in Items(Get(before, "State", "EffectRuntime", "EffectNodes"))) if (Completed(node)) previouslyCompleted.Add(Convert.ToString(Get(node, "EffectId"), CultureInfo.InvariantCulture) ?? "");
                var candidates = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                foreach (object node in Items(Get(after, "State", "EffectRuntime", "EffectNodes")))
                {
                    string id = Convert.ToString(Get(node, "EffectId"), CultureInfo.InvariantCulture) ?? "";
                    long sequence = Convert.ToInt64(Get(node, "LastCommitSequence"), CultureInfo.InvariantCulture);
                    if (Completed(node) && sequence > beforeSequence && sequence <= afterSequence && !previouslyCompleted.Contains(id)) candidates.Add(id);
                }
                foreach (string id in candidates) Add(index.ByNode, id, location);
            }
            return index;
        }
    }
}
