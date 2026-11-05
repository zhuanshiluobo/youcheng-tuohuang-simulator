using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq.Expressions;
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
        // 仅缓存类型描述字节；绝不缓存可变字段值、摘要或对局对象。
        private static readonly ConcurrentDictionary<Type, byte[]> TypeRecords = new ConcurrentDictionary<Type, byte[]>();
        private static readonly ConcurrentDictionary<Type, byte[]> ElementRecords = new ConcurrentDictionary<Type, byte[]>();
        private static byte[] TypeRecord(Type type) => TypeRecords.GetOrAdd(type, t => Record(TypeName(t)));
        private static byte[] ElementRecord(Type type) => ElementRecords.GetOrAdd(type,
            t => TypeRecord(t.IsArray ? t.GetElementType() : t.GetGenericArguments()[0]));

        public static string Compute(GameState state)
        {
            using var diagnostic = GameStateCloneService.MeasureDiagnostic("rule.digest");
            if (state == null) throw new ArgumentNullException(nameof(state));
            using (var hash = RuleStateSha256.Create())
            {
                byte[] hashBytes;
                using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
                using (var buffer = new BufferedStream(stream, 65536))
                {
                    using (var writer = new DigestWriter(buffer))
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
                writer.Write(TypeRecord(type));
                WriteValue(writer, Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture), active, false, false);
                return;
            }
            if (WriteScalar(writer, value, type)) return;
            // 精确空List没有子引用或回边；仍写入完全相同的容器头和元素类型。
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && ((IList)value).Count == 0)
            {
                writer.Write((byte)31); writer.Write(ElementRecord(type)); writer.Write(0);
                return;
            }
            if (!active.Add(value)) throw new InvalidOperationException("规则摘要不支持循环持久化引用：" + TypeName(type));
            try
            {
                if (type.IsArray)
                {
                    var array = (Array)value;
                    writer.Write((byte)30);
                    writer.Write(ElementRecord(type));
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
                    writer.Write(ElementRecord(type));
                    writer.Write(list.Count);
                    for (int i = 0; i < list.Count; i++) WriteValue(writer, list[i], active, false, gameLogs);
                    return;
                }
                bool legacyValues = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LegacyPositionList<>);
                if (typeof(IEnumerable).IsAssignableFrom(type) && !legacyValues)
                    throw new InvalidOperationException("规则摘要不支持持久化集合：" + TypeName(type));
                TypePlan plan = Plans.GetOrAdd(type, valueType => new TypePlan(valueType));
                WriteObjectFields(writer,value,plan,active,root,gameLogs);
            }
            finally { active.Remove(value); }
        }

        private static void WriteObjectFields(BinaryWriter writer,object value,TypePlan plan,HashSet<object> active,bool root,bool gameLogs)
        {
            writer.Write((byte)32);writer.Write(plan.TypeRecord);
            writer.Write(plan.Fields.Length-(gameLogs?plan.LogMessageCount:0));
            if(plan.WriteFields!=null){plan.WriteFields(writer,value,active,root,gameLogs);return;}
            foreach(var field in plan.Fields)
            {
                if(gameLogs && field.LogMessage)continue;
                writer.Write(field.Record);
                if(field.WriteScalar!=null)field.WriteScalar(writer,value);
                else WriteValue(writer,field.Get(value),active,false,root && field.RootLogs);
            }
        }

        private static class KnownDto<T> where T:class
        { internal static readonly TypePlan Plan=Plans.GetOrAdd(typeof(T),type=>new TypePlan(type)); }

        // 封闭DTO由字段计划直接分派，仍逐字段写入、检测环和重复分支；不缓存字段值。
        private static void WriteKnownDto<T>(BinaryWriter writer,T value,HashSet<object> active,bool gameLogs) where T:class
        {
            if(value==null){writer.Write((byte)0);return;}
            if(!active.Add(value))throw new InvalidOperationException("规则摘要不支持循环持久化引用："+TypeName(typeof(T)));
            try{WriteObjectFields(writer,value,KnownDto<T>.Plan,active,false,gameLogs);}
            finally{active.Remove(value);}
        }

        private static void WriteKnownList<T>(BinaryWriter writer,List<T> value,HashSet<object> active,bool gameLogs) where T:class
        {
            if(value==null){writer.Write((byte)0);return;}
            if(value.GetType()!=typeof(List<T>)){WriteValue(writer,value,active,false,gameLogs);return;}
            if(value.Count==0){writer.Write((byte)31);writer.Write(ElementRecord(typeof(List<T>)));writer.Write(0);return;}
            if(!active.Add(value))throw new InvalidOperationException("规则摘要不支持循环持久化引用："+TypeName(value.GetType()));
            try
            {
                writer.Write((byte)31);writer.Write(ElementRecord(typeof(List<T>)));writer.Write(value.Count);
                for(int i=0;i<value.Count;i++)WriteKnownDto(writer,value[i],active,gameLogs);
            }
            finally{active.Remove(value);}
        }

        private static bool IsKnownDto(Type type) => type.IsSealed && !type.IsValueType &&
            Attribute.IsDefined(type,typeof(SerializableAttribute),true) && !typeof(IEnumerable).IsAssignableFrom(type);

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
            if (writer is DigestWriter digest) { digest.WriteText(value); return; }
            byte[] bytes = Utf8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private sealed class DigestWriter : BinaryWriter
        {
            private readonly byte[] textBuffer = new byte[4096];
            // 同一次摘要中的重复短字符串只编码一次；字典随writer释放，不缓存对局。
            private readonly Dictionary<string,byte[]> shortTexts=new Dictionary<string,byte[]>(StringComparer.Ordinal);
            internal DigestWriter(Stream stream) : base(stream, Utf8, true) { }
            internal void WriteText(string value)
            {
                if(value.Length==0){Write(0);return;}
                if(value.Length<=128)
                {
                    if(shortTexts.TryGetValue(value,out var record)){Write(record.Length);Write(record);return;}
                    if(shortTexts.Count<4096)
                    {
                        record=Utf8.GetBytes(value);shortTexts.Add(value,record);Write(record.Length);Write(record);return;
                    }
                }
                Write(Utf8.GetByteCount(value));
                for (int offset = 0; offset < value.Length;)
                {
                    int count = Math.Min(1024, value.Length - offset);
                    if (offset + count < value.Length && char.IsHighSurrogate(value[offset + count - 1])) count--;
                    int bytes = Utf8.GetBytes(value, offset, count, textBuffer, 0);
                    Write(textBuffer, 0, bytes); offset += count;
                }
            }
        }

        private static Expression ScalarExpression(Expression writer, Expression value, Type type)
        {
            Expression Byte(byte tag) => Expression.Call(writer, nameof(BinaryWriter.Write), null, Expression.Constant(tag));
            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                return Expression.Block(Byte(20), Expression.Call(writer, nameof(BinaryWriter.Write), null,
                    Expression.Constant(TypeRecord(type))), ScalarExpression(writer, Expression.Convert(value, underlying), underlying));
            }
            if (type == typeof(string))
                return Expression.IfThenElse(Expression.Equal(value, Expression.Constant(null, typeof(string))), Byte(0),
                    Expression.Block(Byte(1), Expression.Call(typeof(RuleStateDigestService).GetMethod(nameof(WriteText), BindingFlags.NonPublic | BindingFlags.Static), writer, value)));
            byte tag = type == typeof(bool) ? (byte)2 : type == typeof(byte) ? (byte)3 : type == typeof(sbyte) ? (byte)4 :
                type == typeof(short) ? (byte)5 : type == typeof(ushort) ? (byte)6 : type == typeof(int) ? (byte)7 :
                type == typeof(uint) ? (byte)8 : type == typeof(long) ? (byte)9 : type == typeof(ulong) ? (byte)10 :
                type == typeof(char) ? (byte)11 : type == typeof(float) ? (byte)12 : type == typeof(double) ? (byte)13 : (byte)0;
            if (tag == 0) return null;
            if (type == typeof(sbyte)) value = Expression.Convert(value, typeof(byte));
            if (type == typeof(char)) value = Expression.Convert(value, typeof(ushort));
            return Expression.Block(Byte(tag), Expression.Call(writer, nameof(BinaryWriter.Write), null, value));
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
            internal readonly int LogMessageCount;
            internal readonly Action<BinaryWriter,object,HashSet<object>,bool,bool> WriteFields;
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
                foreach(var field in Fields)if(field.LogMessage)LogMessageCount++;
                try
                {
                    var writer=Expression.Parameter(typeof(BinaryWriter),"writer");
                    var source=Expression.Parameter(typeof(object),"source");
                    var active=Expression.Parameter(typeof(HashSet<object>),"active");
                    var root=Expression.Parameter(typeof(bool),"root");
                    var logs=Expression.Parameter(typeof(bool),"logs");
                    var operations=new List<Expression>();
                    var writeValue=typeof(RuleStateDigestService).GetMethod(nameof(WriteValue),BindingFlags.Static|BindingFlags.NonPublic);
                    foreach(var field in Fields)
                    {
                        var value=Expression.Field(Expression.Convert(source,field.Field.DeclaringType),field.Field);
                        Type fieldType=field.Field.FieldType;
                        Expression nextLogs=Expression.AndAlso(root,Expression.Constant(field.RootLogs));
                        Expression write=ScalarExpression(writer,value,fieldType);
                        if(write==null)
                        {
                            MethodInfo known=null;Type argument=fieldType;
                            if(IsKnownDto(fieldType))known=typeof(RuleStateDigestService).GetMethod(nameof(WriteKnownDto),BindingFlags.Static|BindingFlags.NonPublic);
                            else if(fieldType.IsGenericType && fieldType.GetGenericTypeDefinition()==typeof(List<>) && IsKnownDto(fieldType.GetGenericArguments()[0]))
                            {argument=fieldType.GetGenericArguments()[0];known=typeof(RuleStateDigestService).GetMethod(nameof(WriteKnownList),BindingFlags.Static|BindingFlags.NonPublic);}
                            write=known!=null ? Expression.Call(known.MakeGenericMethod(argument),writer,value,active,nextLogs) :
                                Expression.Call(writeValue,writer,Expression.Convert(value,typeof(object)),active,Expression.Constant(false),nextLogs);
                        }
                        var operation=Expression.Block(Expression.Call(writer,nameof(BinaryWriter.Write),null,Expression.Constant(field.Record)),write);
                        operations.Add(field.LogMessage ? Expression.IfThen(Expression.Not(logs),operation) : (Expression)operation);
                    }
                    operations.Add(Expression.Empty());
                    WriteFields=Expression.Lambda<Action<BinaryWriter,object,HashSet<object>,bool,bool>>(
                        Expression.Block(operations),writer,source,active,root,logs).Compile();
                }
                catch(Exception exception) when(exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException)
                { WriteFields=null; }
            }
        }

        private sealed class FieldPlan
        {
            internal readonly FieldInfo Field;
            internal readonly Func<object, object> Get;
            internal readonly Action<BinaryWriter, object> WriteScalar;
            internal readonly byte[] Record;
            internal readonly bool RootLogs, LogMessage;
            internal FieldPlan(FieldInfo field)
            {
                Field = field;
                Get = field.GetValue;
                try
                {
                    var target = Expression.Parameter(typeof(object), "target");
                    var value = Expression.Field(Expression.Convert(target, field.DeclaringType), field);
                    Get = Expression.Lambda<Func<object, object>>(Expression.Convert(value, typeof(object)), target).Compile();
                    var writer = Expression.Parameter(typeof(BinaryWriter), "writer");
                    var scalar = ScalarExpression(writer, value, field.FieldType);
                    if (scalar != null) WriteScalar = Expression.Lambda<Action<BinaryWriter, object>>(scalar, writer, target).Compile();
                }
                catch (Exception exception) when (exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException) { }
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
