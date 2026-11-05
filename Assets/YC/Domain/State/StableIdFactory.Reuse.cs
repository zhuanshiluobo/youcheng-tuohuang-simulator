using System;
using System.Collections.Generic;

namespace YC.Domain.State
{
    public static partial class StableIdFactory
    {
        [ThreadStatic] private static IdReuseScope currentReuseScope;
        private static readonly object sharedReuseGate = new object();
        private static readonly IdReuseScope sharedReuse = new IdReuseScope(null, true);

        // 界面查询及保存校验也调用同一纯函数。共享缓存只保留有限长度的不可变字符串，
        // 不保留对局对象；最多4096条，满后清空，超长键不缓存。规则Run仍用独占局部表。
        private static bool CanReuseInput(string kind, string[] parts)
        {
            if (kind.Length > 64 || (parts?.Length ?? 0) > 32) return false;
            int remaining = 1024 - kind.Length;
            if (parts != null)
                foreach (string part in parts)
                    if (part != null)
                    {
                        if (part.Length > remaining) return false;
                        remaining -= part.Length;
                    }
            return true;
        }
        private static bool TryReuse(IdReuseScope scope, string kind, string[] parts, out string value)
        {
            if (scope != null) return scope.TryGet(kind, parts, out value);
            lock (sharedReuseGate) return sharedReuse.TryGet(kind, parts, out value);
        }
        private static void Remember(IdReuseScope scope, string kind, string[] parts, string value)
        {
            if (scope != null) { scope.Remember(kind, parts, value); return; }
            lock (sharedReuseGate) sharedReuse.Remember(kind, parts, value);
        }

        // 仅一次同步规则推进内复用纯函数值。键只含不可变字符串，复制调用者的数组；
        // 不保存GameState、不按revision缓存，退出或异常后释放全部条目。
        internal static IDisposable BeginSynchronousReuseScope()
        {
            var scope = new IdReuseScope(currentReuseScope);
            currentReuseScope = scope;
            return scope;
        }

        private readonly struct IdInput : IEquatable<IdInput>
        {
            private readonly string kind;
            private readonly string[] components;
            internal IdInput(string kind, string[] components) { this.kind = kind; this.components = components; }
            public bool Equals(IdInput other)
            {
                if (!string.Equals(kind, other.kind, StringComparison.Ordinal)) return false;
                int count = components?.Length ?? 0;
                if (count != (other.components?.Length ?? 0)) return false;
                for (int i = 0; i < count; i++)
                    if (!string.Equals(components[i], other.components[i], StringComparison.Ordinal)) return false;
                return true;
            }
            public override bool Equals(object value) => value is IdInput input && Equals(input);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = StringComparer.Ordinal.GetHashCode(kind);
                    if (components != null)
                        foreach (string part in components) hash = hash * 31 + (part == null ? 0 : StringComparer.Ordinal.GetHashCode(part));
                    return hash;
                }
            }
        }

        private sealed class IdReuseScope : IDisposable
        {
            private const int MaximumEntries = 4096;
            private readonly IdReuseScope previous;
            private readonly bool resetWhenFull;
            private readonly Dictionary<IdInput, string> values = new Dictionary<IdInput, string>();
            private bool disposed;
            internal IdReuseScope(IdReuseScope previous, bool resetWhenFull = false) { this.previous = previous; this.resetWhenFull = resetWhenFull; }
            internal bool TryGet(string kind, string[] components, out string value) => values.TryGetValue(new IdInput(kind, components), out value);
            internal void Remember(string kind, string[] components, string value)
            {
                if (values.Count >= MaximumEntries && resetWhenFull) values.Clear();
                if (values.Count < MaximumEntries)
                    values[new IdInput(kind, components == null ? null : (string[])components.Clone())] = value;
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                values.Clear();
                if (!ReferenceEquals(currentReuseScope, this)) return;
                var restored = previous;
                while (restored != null && restored.disposed) restored = restored.previous;
                currentReuseScope = restored;
            }
        }
    }
}
