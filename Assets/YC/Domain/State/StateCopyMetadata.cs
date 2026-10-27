using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace YC.Domain.State
{
    /// <summary>只缓存类型/字段访问计划，不保存任何对局对象；动态代码不可用时仍走原反射路径。</summary>
    internal sealed class StateCopyMetadata
    {
        private static readonly ConcurrentDictionary<Type, Lazy<StateCopyMetadata>> Plans = new ConcurrentDictionary<Type, Lazy<StateCopyMetadata>>();
        internal readonly bool IsScalar;
        internal readonly bool IsList;
        internal readonly bool IsDictionary;
        internal readonly bool IsSerializable;
        internal readonly Type ElementType;
        internal readonly bool IsScalarElement;
        internal readonly StateCopyField[] Fields;
        private readonly Func<object> create;
        private readonly Func<int, object> createList;
        private readonly Func<object, object> cloneScalarList;
        private readonly Action<object, object> copyScalarList;
        private readonly Action<object, int> reserveList;

        internal static StateCopyMetadata For(Type type) => Plans.GetOrAdd(type, value => new Lazy<StateCopyMetadata>(() => new StateCopyMetadata(value))).Value;
        private StateCopyMetadata(Type type)
        {
            IsScalar = IsScalarType(type);
            IsList = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
            IsDictionary = typeof(System.Collections.IDictionary).IsAssignableFrom(type) || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);
            IsSerializable = Attribute.IsDefined(type, typeof(SerializableAttribute), true);
            ElementType = type.IsArray ? type.GetElementType() : IsList ? type.GetGenericArguments()[0] : null;
            IsScalarElement = ElementType != null && IsScalarType(ElementType);
            Fields = IsScalar || IsList || type.IsArray || IsDictionary ? Array.Empty<StateCopyField>() : BuildFields(type);
            // 容器不走 DTO 构造路径，避免为永不使用的默认构造再编译一份委托。
            create = IsScalar || IsList || type.IsArray || IsDictionary ? null : BuildConstructor(type);
            if (IsList)
            {
                // 动态代码不可用时保留原无参数构造路径，避免逐列表的反射参数数组及 binder 开销。
                createList = count => Activator.CreateInstance(type);
                var capacity = type.GetProperty("Capacity");
                reserveList = (list, count) => { if ((int)capacity.GetValue(list) < count) capacity.SetValue(list, count); };
                try
                {
                    var count = Expression.Parameter(typeof(int), "count");
                    var list = Expression.Parameter(typeof(object), "list");
                    if (IsScalarElement)
                    {
                        var source = Expression.Parameter(typeof(object), "source");
                        var sequenceType = typeof(IEnumerable<>).MakeGenericType(ElementType);
                        cloneScalarList = Expression.Lambda<Func<object, object>>(Expression.Convert(
                            Expression.New(type.GetConstructor(new[] { sequenceType }), Expression.Convert(source, sequenceType)), typeof(object)), source).Compile();
                        copyScalarList = Expression.Lambda<Action<object, object>>(Expression.Block(
                            Expression.Call(Expression.Convert(list, type), type.GetMethod("Clear")),
                            Expression.Call(Expression.Convert(list, type), type.GetMethod("AddRange"), Expression.Convert(source, sequenceType))), list, source).Compile();
                    }
                    else
                    {
                        createList = Expression.Lambda<Func<int, object>>(Expression.Convert(
                            Expression.New(type.GetConstructor(new[] { typeof(int) }), count), typeof(object)), count).Compile();
                        var member = Expression.Property(Expression.Convert(list, type), capacity);
                        reserveList = Expression.Lambda<Action<object, int>>(Expression.IfThen(
                            Expression.LessThan(member, count), Expression.Block(Expression.Assign(member, count), Expression.Empty())), list, count).Compile();
                    }
                }
                catch (Exception exception) when (exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException) { }
            }
        }
        private static bool IsScalarType(Type type) => type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal);
        internal object Create() => create();
        internal object CreateList(int count) => createList(count);
        internal void ReserveList(object list, int count) => reserveList(list, count);
        internal bool TryCloneScalarList(object source, out object clone)
        {
            // 仅 List<不可变标量> 可批量复制，List<object> 和含引用的 struct 继续逐值递归。
            clone = cloneScalarList == null ? null : cloneScalarList(source);
            return cloneScalarList != null;
        }
        internal bool TryCopyScalarList(object target, object source)
        {
            if (copyScalarList == null) return false;
            if (!ReferenceEquals(target, source) && (((System.Collections.IList)target).Count != 0 || ((System.Collections.IList)source).Count != 0))
                copyScalarList(target, source);
            return true;
        }
        private static Func<object> BuildConstructor(Type type)
        {
            // 这两种归一化DTO的内部空壳只省默认分配；仍由CloneObjectFields复制每个字段。
            if (type == typeof(NormalizedValue)) return () => NormalizedValue.CreateCloneShell();
            if (type == typeof(NormalizedValueEntry)) return () => NormalizedValueEntry.CreateCloneShell();
            if (!type.IsValueType && type.GetConstructor(Type.EmptyTypes) != null)
            {
                try { return Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(type), typeof(object))).Compile(); }
                catch (Exception exception) when (exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException) { }
            }
            return () => Activator.CreateInstance(type);
        }
        private static StateCopyField[] BuildFields(Type type)
        {
            var fields = new List<FieldInfo>();
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!field.IsStatic && !field.IsNotSerialized) fields.Add(field);
            fields.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            return fields.ConvertAll(field => new StateCopyField(field)).ToArray();
        }
    }
    internal sealed class StateCopyField
    {
        internal readonly string Name;
        internal readonly Type FieldType;
        internal readonly bool IsScalar;
        private readonly Func<object, object> get;
        private readonly Action<object, object> set;
        private readonly Action<object, object> copyScalar;
        private readonly Func<object, object, bool> equivalentScalar;
        internal StateCopyField(FieldInfo field)
        {
            Name = field.Name; FieldType = field.FieldType;
            // 仅按声明类型确认不可变标量；object/Nullable/包含引用的struct仍走完整递归。
            IsScalar = FieldType == typeof(string) || FieldType.IsPrimitive || FieldType.IsEnum || FieldType == typeof(decimal);
            get = field.GetValue; set = field.SetValue;
            // 对boxed struct保留FieldInfo.SetValue，避免对转换后的值副本赋值；readonly字段也保留原语义。
            if (field.DeclaringType.IsValueType || field.IsInitOnly) return;
            try
            {
                var target = Expression.Parameter(typeof(object), "target");
                var value = Expression.Parameter(typeof(object), "value");
                var member = Expression.Field(Expression.Convert(target, field.DeclaringType), field);
                get = Expression.Lambda<Func<object, object>>(Expression.Convert(member, typeof(object)), target).Compile();
                if (IsScalar)
                {
                    var source = Expression.Parameter(typeof(object), "source");
                    copyScalar = Expression.Lambda<Action<object, object>>(Expression.Assign(member,
                        Expression.Field(Expression.Convert(source, field.DeclaringType), field)), target, source).Compile();
                    // 使用与装箱值.Equals相同的标量相等语义，避免逐字段创建装箱对象。
                    var comparerType = typeof(EqualityComparer<>).MakeGenericType(FieldType);
                    equivalentScalar = Expression.Lambda<Func<object, object, bool>>(Expression.Call(
                        Expression.Property(null, comparerType.GetProperty("Default")),
                        comparerType.GetMethod("Equals", new[] { FieldType, FieldType }), member,
                        Expression.Field(Expression.Convert(source, field.DeclaringType), field)), target, source).Compile();
                }
                else
                    set = Expression.Lambda<Action<object, object>>(Expression.Assign(member, Expression.Convert(value, field.FieldType)), target, value).Compile();
            }
            catch (Exception exception) when (exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException)
            { get = field.GetValue; set = field.SetValue; }
        }
        internal object GetValue(object target) => get(target);
        internal void SetValue(object target, object value) => set(target, value);
        internal bool AreEquivalentScalar(object left, object right)
        {
            // AOT、boxed struct和只读字段保留原反射/装箱回退，不缓存状态对象。
            return equivalentScalar != null ? equivalentScalar(left, right) :
                object.Equals(get(left), get(right));
        }
        internal void CopyScalar(object target, object source)
        {
            if (copyScalar != null) copyScalar(target, source);
            else set(target, get(source));
        }
    }
}
