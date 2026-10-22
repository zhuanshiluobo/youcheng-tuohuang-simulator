using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Items;

namespace YC.Domain.State
{
    /// <summary>完整持久化字段图的只读规范摘要；不是 archive 字节摘要或玩家视图摘要。</summary>
    public static class RuleStateDigestService
    {
        public const string DigestVersion = "yc.rule-state.sha256.v1";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly ConcurrentDictionary<Type, TypePlan> Plans = new ConcurrentDictionary<Type, TypePlan>();

        public static string Compute(GameState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            using (var hash = SHA256.Create())
            {
                byte[] hashBytes;
                using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
                using (var buffer = new BufferedStream(stream, 65536))
                {
                    using (var writer = new BinaryWriter(buffer, Utf8, true))
                    {
                        WriteText(writer, "YC.RULE.STATE");
                        WriteText(writer, DigestVersion);
                        WriteValue(writer, state, new HashSet<object>(ReferenceComparer.Instance), true, false);
                        writer.Flush();
                    }
                    stream.FlushFinalBlock();
                    hashBytes = hash.Hash;
                }
                const string digits = "0123456789abcdef";
                var result = new char[hashBytes.Length * 2];
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    result[i * 2] = digits[hashBytes[i] >> 4];
                    result[i * 2 + 1] = digits[hashBytes[i] & 15];
                }
                return new string(result);
            }
        }

        private static void WriteValue(BinaryWriter writer, object value, HashSet<object> active, bool root, bool gameLogs)
        {
            if (value == null) { writer.Write((byte)0); return; }
            Type type = value.GetType();
            if (type.IsEnum)
            {
                writer.Write((byte)20);
                WriteText(writer, TypeName(type));
                WriteValue(writer, Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture), active, false, false);
                return;
            }
            if (WriteScalar(writer, value, type)) return;
            if (!active.Add(value)) throw new InvalidOperationException("规则摘要不支持循环持久化引用：" + TypeName(type));
            try
            {
                if (type.IsArray)
                {
                    var array = (Array)value;
                    writer.Write((byte)30);
                    WriteText(writer, TypeName(type.GetElementType()));
                    writer.Write(array.Rank);
                    for (int dimension = 0; dimension < array.Rank; dimension++)
                    {
                        writer.Write(array.GetLowerBound(dimension));
                        writer.Write(array.GetLength(dimension));
                    }
                    foreach (object item in array) WriteValue(writer, item, active, false, gameLogs);
                    return;
                }
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var list = (IList)value;
                    writer.Write((byte)31);
                    WriteText(writer, TypeName(type.GetGenericArguments()[0]));
                    writer.Write(list.Count);
                    for (int i = 0; i < list.Count; i++) WriteValue(writer, list[i], active, false, gameLogs);
                    return;
                }
                bool legacyValues = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LegacyPositionList<>);
                if (typeof(IEnumerable).IsAssignableFrom(type) && !legacyValues)
                    throw new InvalidOperationException("规则摘要不支持持久化集合：" + TypeName(type));
                TypePlan plan = Plans.GetOrAdd(type, valueType => new TypePlan(valueType));
                writer.Write((byte)32);
                writer.Write(plan.TypeRecord);
                int count = plan.Fields.Length;
                if (gameLogs)
                    foreach (FieldPlan field in plan.Fields) if (field.LogMessage) count--;
                writer.Write(count);
                foreach (FieldPlan field in plan.Fields)
                {
                    if (gameLogs && field.LogMessage) continue;
                    writer.Write(field.Record);
                    WriteValue(writer, field.Field.GetValue(value), active, false, root && field.RootLogs);
                }
            }
            finally { active.Remove(value); }
        }

        private static bool WriteScalar(BinaryWriter writer, object value, Type type)
        {
            if (type == typeof(string)) { writer.Write((byte)1); WriteText(writer, (string)value); }
            else if (type == typeof(bool)) { writer.Write((byte)2); writer.Write((bool)value); }
            else if (type == typeof(byte)) { writer.Write((byte)3); writer.Write((byte)value); }
            else if (type == typeof(sbyte)) { writer.Write((byte)4); writer.Write(unchecked((byte)(sbyte)value)); }
            else if (type == typeof(short)) { writer.Write((byte)5); writer.Write((short)value); }
            else if (type == typeof(ushort)) { writer.Write((byte)6); writer.Write((ushort)value); }
            else if (type == typeof(int)) { writer.Write((byte)7); writer.Write((int)value); }
            else if (type == typeof(uint)) { writer.Write((byte)8); writer.Write((uint)value); }
            else if (type == typeof(long)) { writer.Write((byte)9); writer.Write((long)value); }
            else if (type == typeof(ulong)) { writer.Write((byte)10); writer.Write((ulong)value); }
            else if (type == typeof(char)) { writer.Write((byte)11); writer.Write((ushort)(char)value); }
            else if (type == typeof(float)) { writer.Write((byte)12); writer.Write((float)value); }
            else if (type == typeof(double)) { writer.Write((byte)13); writer.Write((double)value); }
            else if (type == typeof(decimal))
            {
                writer.Write((byte)14);
                foreach (int word in decimal.GetBits((decimal)value)) writer.Write(word);
            }
            else if (type == typeof(Guid)) { writer.Write((byte)15); writer.Write(((Guid)value).ToByteArray()); }
            else if (type == typeof(DateTime)) { writer.Write((byte)16); writer.Write(((DateTime)value).Ticks); writer.Write((byte)((DateTime)value).Kind); }
            else if (type == typeof(TimeSpan)) { writer.Write((byte)17); writer.Write(((TimeSpan)value).Ticks); }
            else if (type == typeof(DateTimeOffset)) { writer.Write((byte)18); writer.Write(((DateTimeOffset)value).Ticks); writer.Write(((DateTimeOffset)value).Offset.Ticks); }
            else return false;
            return true;
        }

        private static void WriteText(BinaryWriter writer, string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string TypeName(Type type)
        {
            if (type.IsArray) return TypeName(type.GetElementType()) + "[" + type.GetArrayRank().ToString(CultureInfo.InvariantCulture) + "]";
            if (!type.IsGenericType) return type.FullName;
            var name = new StringBuilder(type.GetGenericTypeDefinition().FullName).Append('<');
            Type[] arguments = type.GetGenericArguments();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0) name.Append(',');
                name.Append(TypeName(arguments[i]));
            }
            return name.Append('>').ToString();
        }

        private static byte[] Record(params string[] values)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                foreach (string value in values) WriteText(writer, value);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private sealed class TypePlan
        {
            internal readonly byte[] TypeRecord;
            internal readonly FieldPlan[] Fields;
            internal TypePlan(Type type)
            {
                if (type.IsPrimitive || !Attribute.IsDefined(type, typeof(SerializableAttribute), true))
                    throw new InvalidOperationException("规则摘要要求可序列化 DTO：" + TypeName(type));
                TypeRecord = Record(TypeName(type));
                var fields = new List<FieldInfo>();
                for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
                    foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (!field.IsStatic && !field.IsNotSerialized) fields.Add(field);
                fields.Sort((left, right) =>
                {
                    int owner = StringComparer.Ordinal.Compare(TypeName(left.DeclaringType), TypeName(right.DeclaringType));
                    return owner != 0 ? owner : StringComparer.Ordinal.Compare(left.Name, right.Name);
                });
                Fields = fields.ConvertAll(field => new FieldPlan(field)).ToArray();
            }
        }

        private sealed class FieldPlan
        {
            internal readonly FieldInfo Field;
            internal readonly byte[] Record;
            internal readonly bool RootLogs, LogMessage;
            internal FieldPlan(FieldInfo field)
            {
                Field = field;
                Record = RuleStateDigestService.Record(TypeName(field.DeclaringType), field.Name, TypeName(field.FieldType));
                RootLogs = field.DeclaringType == typeof(GameState) && field.Name == nameof(GameState.Logs);
                LogMessage = field.DeclaringType == typeof(GameLogEntry) && field.Name == nameof(GameLogEntry.Message);
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
