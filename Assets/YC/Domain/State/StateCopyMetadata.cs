using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace YC.Domain.State
{
    // 仅由已核验为纯字段DTO的封闭类型显式实现。空壳始终留在深克隆调用内，
    // 所有持久引用随后仍递归复制；有NonSerialized字段时自动回退原构造路径。
    internal interface IStateCloneShell { object CloneStateShell(); }

    /// <summary>只缓存类型/字段访问计划，不保存任何对局对象；动态代码不可用时仍走原反射路径。</summary>
    internal sealed class StateCopyMetadata
    {
        private static readonly ConcurrentDictionary<Type, Lazy<StateCopyMetadata>> Plans = new ConcurrentDictionary<Type, Lazy<StateCopyMetadata>>();
        internal readonly bool IsScalar;
        internal readonly bool IsList;
        internal readonly bool IsDictionary;
        internal readonly bool IsSerializable;
        internal readonly bool CanCloneShell;
        internal readonly Type ElementType;
        internal readonly bool IsScalarElement;
        internal readonly StateCopyField[] Fields;
        internal readonly StateCopyField[] ReferenceFields;
        internal readonly Func<object, HashSet<object>, bool> PublicationReferences;
        internal readonly Func<object, HashSet<object>, bool> PublicationList;
        private readonly Action<object, object> copyScalars;
        internal readonly Action<object, object, GameStateCloneService.DiagnosticPath, HashSet<object>> CloneFields;
        internal readonly Action<object, object, GameStateCloneService.DiagnosticPath, HashSet<GameStateCloneService.ReferencePair>, HashSet<object>> CopyFields;
        internal readonly Func<object,object,HashSet<GameStateCloneService.ReferencePair>,bool,bool,bool> EquivalentFields;
        private readonly Lazy<Func<object, object, bool>> copyEquivalentFields;
        private readonly Lazy<Func<object, object, bool>> readOnlyEquivalentFields;
        internal Func<object, object, bool> ReadOnlyEquivalentFields => readOnlyEquivalentFields.Value;
        private readonly Func<object> create;
        private readonly Func<int, object> createList;
        private readonly Func<object, object> cloneScalarList;
        private readonly Action<object, object> copyScalarList;
        private readonly Action<object, int> reserveList;
        private readonly Lazy<object> emptyQueryBaselineList;
        internal object EmptyQueryBaselineList => emptyQueryBaselineList.Value;

        internal static StateCopyMetadata For(Type type) => Plans.GetOrAdd(type, value => new Lazy<StateCopyMetadata>(() => new StateCopyMetadata(value))).Value;
        private StateCopyMetadata(Type type)
        {
            IsScalar = IsScalarType(type);
            IsList = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
            emptyQueryBaselineList = IsList ? new Lazy<object>(() => CreateList(0)) : null;
            IsDictionary = typeof(System.Collections.IDictionary).IsAssignableFrom(type) || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);
            IsSerializable = Attribute.IsDefined(type, typeof(SerializableAttribute), true);
            ElementType = type.IsArray ? type.GetElementType() : IsList ? type.GetGenericArguments()[0] : null;
            IsScalarElement = ElementType != null && IsScalarType(ElementType);
            Fields = IsScalar || IsList || type.IsArray || IsDictionary ? Array.Empty<StateCopyField>() : BuildFields(type);
            ReferenceFields = Array.FindAll(Fields, field => !field.IsScalar);
            // 唯一引用检查仍遍历每个持久引用，仅把反射取字段/非泛型枚举换成类型计划。
            // 动态代码不可用、派生容器等情形继续原递归路径；不保存任何对局值。
            try
            {
                var value = Expression.Parameter(typeof(object), "value");
                var seen = Expression.Parameter(typeof(HashSet<object>), "seen");
                const BindingFlags binding = BindingFlags.Static | BindingFlags.NonPublic;
                if (IsList)
                {
                    var visitList = typeof(GameStateCloneService).GetMethod(
                        CanUseKnownDto(ElementType) ? "VisitKnownPublicationList" : "VisitPublicationList", binding).MakeGenericMethod(ElementType);
                    PublicationList = Expression.Lambda<Func<object, HashSet<object>, bool>>(
                        Expression.Call(visitList, Expression.Convert(value, type), seen), value, seen).Compile();
                }
                else if (!IsScalar && !IsDictionary && !type.IsArray && !type.IsValueType && IsSerializable)
                {
                    var typed = Expression.Variable(type, "typed");
                    Expression valid = Expression.Constant(true);
                    var visit = typeof(GameStateCloneService).GetMethod("VisitPublicationShape", binding);
                    foreach (var field in ReferenceFields)
                    {
                        var child = Expression.Field(typed, field.Info);
                        Expression call = Expression.Call(visit, Expression.Convert(child, typeof(object)), seen);
                        if (CanUseKnownDto(field.FieldType))
                        {
                            var known = typeof(GameStateCloneService).GetMethod("VisitKnownPublicationDto", binding).MakeGenericMethod(field.FieldType);
                            call = Expression.Call(known, child, seen);
                        }
                        valid = Expression.AndAlso(valid, call);
                    }
                    PublicationReferences = Expression.Lambda<Func<object, HashSet<object>, bool>>(
                        Expression.Block(new[] { typed }, Expression.Assign(typed, Expression.Convert(value, type)), valid), value, seen).Compile();
                }
            }
            catch (Exception exception) when (exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException)
            { PublicationReferences = null; PublicationList = null; }
            copyEquivalentFields = new Lazy<Func<object, object, bool>>(() => BuildCopyEquivalence(type));
            readOnlyEquivalentFields = new Lazy<Func<object, object, bool>>(() => BuildReadOnlyEquivalence(type));
            CanCloneShell = type.IsSealed && typeof(IStateCloneShell).IsAssignableFrom(type) &&
                HasOnlySerializedFields(type);
            if (!type.IsValueType && Fields.Length > 0 && !Array.Exists(Fields, field => field.IsScalar && field.Info.IsInitOnly))
            {
                try
                {
                    var target = Expression.Parameter(typeof(object), "target");
                    var source = Expression.Parameter(typeof(object), "source");
                    var assignments = new List<Expression>();
                    foreach (var field in Fields)
                        if (field.IsScalar) assignments.Add(Expression.Assign(
                            Expression.Field(Expression.Convert(target, field.Info.DeclaringType), field.Info),
                            Expression.Field(Expression.Convert(source, field.Info.DeclaringType), field.Info)));
                    assignments.Add(Expression.Empty());
                    copyScalars = Expression.Lambda<Action<object, object>>(Expression.Block(assignments), target, source).Compile();
                }
                catch (Exception exception) when (exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException) { }
            }
            // 容器不走 DTO 构造路径，避免为永不使用的默认构造再编译一份委托。
            create = IsScalar || IsList || type.IsArray || IsDictionary ? null : BuildConstructor(type);
            // 与旧逐字段Equals保持相同顺序和标量语义；只缓存访问计划，不缓存对局值。
            if (!type.IsValueType && Fields.Length > 0)
            {
                try
                {
                    var left=Expression.Parameter(typeof(object),"left");var right=Expression.Parameter(typeof(object),"right");
                    var visited=Expression.Parameter(typeof(HashSet<GameStateCloneService.ReferencePair>),"visited");
                    var ignore=Expression.Parameter(typeof(bool),"ignoreRuntime");var root=Expression.Parameter(typeof(bool),"root");
                    var a=Expression.Variable(type,"a");var b=Expression.Variable(type,"b");
                    var method=typeof(GameStateCloneService).GetMethod("AreEquivalentValue",BindingFlags.Static|BindingFlags.NonPublic);
                    Expression equal=Expression.Constant(true);
                    foreach(var field in Fields)
                    {
                        var l=Expression.Field(a,field.Info);var r=Expression.Field(b,field.Info);Expression current;
                        if(field.IsScalar)
                        {
                            var comparer=typeof(EqualityComparer<>).MakeGenericType(field.FieldType);
                            current=Expression.Call(Expression.Property(null,comparer.GetProperty("Default")),comparer.GetMethod("Equals",new[]{field.FieldType,field.FieldType}),l,r);
                        }
                        else current=Expression.Call(method,Expression.Convert(l,typeof(object)),Expression.Convert(r,typeof(object)),Expression.Constant(field.FieldType,typeof(Type)),visited,ignore,Expression.Constant(false));
                        if(field.Name=="EffectRuntime")current=Expression.OrElse(Expression.AndAlso(root,ignore),current);
                        equal=Expression.AndAlso(equal,current);
                    }
                    EquivalentFields=Expression.Lambda<Func<object,object,HashSet<GameStateCloneService.ReferencePair>,bool,bool,bool>>(
                        Expression.Block(new[]{a,b},Expression.Assign(a,Expression.Convert(left,type)),Expression.Assign(b,Expression.Convert(right,type)),equal),left,right,visited,ignore,root).Compile();
                }
                catch(Exception exception) when(exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException){ }
            }
            // 每种DTO编译一次完整字段访问计划。引用值仍调用原递归入口，不跳过隔离/类型/循环检查；
            // 新持久字段自动进入同一元数据计划，AOT/只读字段/值类型保留原实现。
            if (!type.IsValueType && Fields.Length > 0 && !Array.Exists(Fields, field => field.Info.IsInitOnly))
            {
                try
                {
                    var target = Expression.Parameter(typeof(object), "target");
                    var source = Expression.Parameter(typeof(object), "source");
                    var path = Expression.Parameter(typeof(GameStateCloneService.DiagnosticPath), "path");
                    var references = Expression.Parameter(typeof(HashSet<object>), "references");
                    var visited = Expression.Parameter(typeof(HashSet<GameStateCloneService.ReferencePair>), "visited");
                    var typedTarget = Expression.Variable(type, "typedTarget");
                    var typedSource = Expression.Variable(type, "typedSource");
                    var cloneExpressions = new List<Expression> { Expression.Assign(typedTarget, Expression.Convert(target, type)), Expression.Assign(typedSource, Expression.Convert(source, type)) };
                    var copyExpressions = new List<Expression>(cloneExpressions);
                    var binding = BindingFlags.Static | BindingFlags.NonPublic;
                    var cloneMethod = typeof(GameStateCloneService).GetMethod("CloneValue", binding);
                    var copyMethod = typeof(GameStateCloneService).GetMethod("CopyValueInto", binding);
                    var fieldPathMethod = typeof(GameStateCloneService.DiagnosticPath).GetMethod("AtField", BindingFlags.Instance | BindingFlags.NonPublic);
                    foreach (var field in Fields)
                    {
                        var to = Expression.Field(typedTarget, field.Info);
                        var from = Expression.Field(typedSource, field.Info);
                        if (field.IsScalar)
                        { if (!CanCloneShell) cloneExpressions.Add(Expression.Assign(to, from)); copyExpressions.Add(Expression.Assign(to, from)); }
                        else
                        {
                            var fieldPath = Expression.Call(path, fieldPathMethod, Expression.Constant(field.Name));
                            var declared = Expression.Constant(field.FieldType, typeof(Type));
                            Expression cloneValue=Expression.Convert(Expression.Call(cloneMethod,
                                Expression.Convert(from, typeof(object)), declared, fieldPath, references), field.FieldType);
                            Expression copyValue=Expression.Convert(Expression.Call(copyMethod,
                                Expression.Convert(to, typeof(object)), Expression.Convert(from, typeof(object)), declared, fieldPath, visited, references), field.FieldType);
                            if (CanUseKnownDto(field.FieldType))
                            {
                                var typedClone=typeof(GameStateCloneService).GetMethod("CloneKnownDto",binding).MakeGenericMethod(field.FieldType);
                                var typedCopy=typeof(GameStateCloneService).GetMethod("CopyKnownDto",binding).MakeGenericMethod(field.FieldType);
                                cloneValue=Expression.Call(typedClone,from,fieldPath,references);
                                copyValue=Expression.Call(typedCopy,to,from,fieldPath,visited,references);
                            }
                            else if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition()==typeof(List<>))
                            {
                                var element=field.FieldType.GetGenericArguments()[0];
                                if (CanUseKnownDto(element))
                                {
                                    // 派生列表可能有附加持久字段，必须继续走原动态入口。
                                    var exact=Expression.Equal(Expression.Call(from,typeof(object).GetMethod(nameof(GetType))),Expression.Constant(field.FieldType,typeof(Type)));
                                    var typedClone=typeof(GameStateCloneService).GetMethod("CloneKnownList",binding).MakeGenericMethod(element);
                                    var typedCopy=typeof(GameStateCloneService).GetMethod("CopyKnownList",binding).MakeGenericMethod(element);
                                    cloneValue=Expression.Condition(exact,Expression.Call(typedClone,from,fieldPath,references),cloneValue);
                                    copyValue=Expression.Condition(exact,Expression.Call(typedCopy,to,from,fieldPath,visited,references),copyValue);
                                }
                            }
                            // null引用不可能产生路径错误或回边；避免先写诊断路径再进入递归。
                            if (!field.FieldType.IsValueType)
                            {
                                var missing=Expression.Constant(null,field.FieldType);
                                var isNull=Expression.Equal(from,missing);
                                cloneValue=Expression.Condition(isNull,missing,cloneValue);
                                copyValue=Expression.Condition(isNull,missing,copyValue);
                            }
                            cloneExpressions.Add(Expression.Assign(to,cloneValue));
                            copyExpressions.Add(Expression.Assign(to,copyValue));
                        }
                    }
                    cloneExpressions.Add(Expression.Empty()); copyExpressions.Add(Expression.Empty());
                    CloneFields = Expression.Lambda<Action<object, object, GameStateCloneService.DiagnosticPath, HashSet<object>>>(
                        Expression.Block(new[] { typedTarget, typedSource }, cloneExpressions), target, source, path, references).Compile();
                    CopyFields = Expression.Lambda<Action<object, object, GameStateCloneService.DiagnosticPath, HashSet<GameStateCloneService.ReferencePair>, HashSet<object>>>(
                        Expression.Block(new[] { typedTarget, typedSource }, copyExpressions), target, source, path, visited, references).Compile();
                }
                catch (Exception exception) when (exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException)
                { CloneFields = null; CopyFields = null; }
            }
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
        private static bool CanUseKnownDto(Type type) => type.IsSealed && !type.IsValueType &&
            type != typeof(NormalizedValue) && type != typeof(NormalizedValueEntry) &&
            Attribute.IsDefined(type, typeof(SerializableAttribute), true) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
        private static bool HasOnlySerializedFields(Type type)
        {
            for(var owner=type;owner!=null && owner!=typeof(object);owner=owner.BaseType)
                foreach(var field in owner.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
                    if(field.IsNotSerialized)return false;
            return true;
        }
        internal object Create() => create();
        internal bool HasIdenticalCopyFields(object left, object right) => copyEquivalentFields.Value?.Invoke(left, right) == true;
        private Func<object, object, bool> BuildCopyEquivalence(Type type)
        {
            if (!type.IsSealed || type.IsValueType || !IsSerializable || Fields.Length == 0) return null;
            try
            {
                var left = Expression.Parameter(typeof(object), "left"); var right = Expression.Parameter(typeof(object), "right");
                var a = Expression.Variable(type, "a"); var b = Expression.Variable(type, "b");
                var compare = typeof(GameStateCloneService).GetMethod("HasIdenticalCopyValue", BindingFlags.Static | BindingFlags.NonPublic);
                Expression equal = Expression.Constant(true);
                foreach (var field in Fields)
                {
                    var l = Expression.Field(a, field.Info); var r = Expression.Field(b, field.Info); Expression current;
                    // 相同字符串值但不同实例仍按原CopyTo赋源引用；浮点和decimal须保留每一位。
                    if (field.FieldType == typeof(string)) current = Expression.ReferenceEqual(l, r);
                    else if (field.IsScalar && field.FieldType != typeof(float) && field.FieldType != typeof(double) && field.FieldType != typeof(decimal)) current = Expression.Equal(l, r);
                    else current = Expression.Call(compare, Expression.Convert(l, typeof(object)), Expression.Convert(r, typeof(object)), Expression.Constant(field.FieldType, typeof(Type)));
                    equal = Expression.AndAlso(equal, current);
                }
                return Expression.Lambda<Func<object, object, bool>>(Expression.Block(new[] { a, b },
                    Expression.Assign(a, Expression.Convert(left, type)), Expression.Assign(b, Expression.Convert(right, type)), equal), left, right).Compile();
            }
            catch (Exception exception) when (exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException) { return null; }
        }
        private Func<object, object, bool> BuildReadOnlyEquivalence(Type type)
        {
            if (IsScalar || IsList || type.IsArray || IsDictionary || !type.IsValueType && !IsSerializable) return null;
            try
            {
                var left=Expression.Parameter(typeof(object),"baseline"); var right=Expression.Parameter(typeof(object),"current");
                var a=Expression.Variable(type,"a"); var b=Expression.Variable(type,"b");
                var compare=typeof(GameStateCloneService).GetMethod("HaveIdenticalReadOnlyFields",BindingFlags.Static|BindingFlags.NonPublic);
                Expression equal=Expression.Constant(true);
                foreach(var field in Fields)
                {
                    var l=Expression.Field(a,field.Info); var r=Expression.Field(b,field.Info); Expression current;
                    if(field.FieldType==typeof(string))current=Expression.ReferenceEqual(l,r);
                    else if(field.IsScalar && field.FieldType!=typeof(float) && field.FieldType!=typeof(double) && field.FieldType!=typeof(decimal))current=Expression.Equal(l,r);
                    else current=Expression.Call(compare,Expression.Convert(l,typeof(object)),Expression.Convert(r,typeof(object)));
                    equal=Expression.AndAlso(equal,current);
                }
                return Expression.Lambda<Func<object,object,bool>>(Expression.Block(new[]{a,b},
                    Expression.Assign(a,Expression.Convert(left,type)),Expression.Assign(b,Expression.Convert(right,type)),equal),left,right).Compile();
            }
            catch(Exception exception) when(exception is NotSupportedException || exception is MemberAccessException || exception is ArgumentException){return null;}
        }

        // 只批量复制不可变标量；引用字段仍走完整递归、别名隔离和循环检查。
        internal bool TryCopyScalars(object target, object source)
        {
            if (copyScalars == null) return false;
            copyScalars(target, source); return true;
        }
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
        internal readonly FieldInfo Info;
        internal readonly string Name;
        internal readonly Type FieldType;
        internal readonly bool IsScalar;
        private readonly Func<object, object> get;
        private readonly Action<object, object> set;
        private readonly Action<object, object> copyScalar;
        private readonly Func<object, object, bool> equivalentScalar;
        internal StateCopyField(FieldInfo field)
        {
            Info = field;
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
