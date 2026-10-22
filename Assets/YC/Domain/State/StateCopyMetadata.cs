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
        internal readonly StateCopyField[] Fields;
        private readonly Func<object> create;

        internal static StateCopyMetadata For(Type type) => Plans.GetOrAdd(type, value => new Lazy<StateCopyMetadata>(() => new StateCopyMetadata(value))).Value;
        private StateCopyMetadata(Type type)
        {
            IsScalar = type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal);
            IsList = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
            IsDictionary = typeof(System.Collections.IDictionary).IsAssignableFrom(type) || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);
            IsSerializable = Attribute.IsDefined(type, typeof(SerializableAttribute), true);
            ElementType = type.IsArray ? type.GetElementType() : IsList ? type.GetGenericArguments()[0] : null;
            Fields = IsScalar || IsList || type.IsArray || IsDictionary ? Array.Empty<StateCopyField>() : BuildFields(type);
            create = BuildConstructor(type);
        }
        internal object Create() => create();
        private static Func<object> BuildConstructor(Type type)
        {
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
                set = Expression.Lambda<Action<object, object>>(Expression.Assign(member, Expression.Convert(value, field.FieldType)), target, value).Compile();
            }
            catch (Exception exception) when (exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException)
            { get = field.GetValue; set = field.SetValue; }
        }
        internal object GetValue(object target) => get(target);
        internal void SetValue(object target, object value) => set(target, value);
    }
}
