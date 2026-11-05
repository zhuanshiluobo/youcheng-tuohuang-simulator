using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Items
{
    public static partial class ItemMoveInputNormalization
    {
        [ThreadStatic] private static ValidationMemo currentValidationMemo;

        internal static IDisposable BeginSynchronousValidationScope()
        {
            var memo = new ValidationMemo(currentValidationMemo);
            currentValidationMemo = memo;
            return memo;
        }

        // 只复用旧输入归一化这项纯局部校验。每次先与独立私有副本逐字段比较，
        // 不能仅信摘要字符串、对象身份或revision；其他节点/跨节点/收据校验仍照常执行。
        private sealed class ValidationMemo : IDisposable
        {
            private const int MaximumEntries = 4096;
            private sealed class Entry
            {
                internal string Type;
                internal NormalizedValue Arguments;
                internal ItemMoveInputNormalizationV1 Input;
            }
            private readonly ValidationMemo previous;
            private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            private bool disposed;
            internal ValidationMemo(ValidationMemo previous) { this.previous = previous; }
            internal bool Matches(string type, NormalizedValue args, ItemMoveInputNormalizationV1 input)
            {
                return input.CanonicalArgumentsDigest != null && entries.TryGetValue(input.CanonicalArgumentsDigest, out var entry) &&
                    entry.Type == type &&
                    GameStateCloneService.AreEquivalentPersistentValues(input, entry.Input, typeof(ItemMoveInputNormalizationV1)) &&
                    GameStateCloneService.AreEquivalentPersistentValues(args, entry.Arguments, typeof(NormalizedValue));
            }
            internal void Remember(string type, NormalizedValue args, ItemMoveInputNormalizationV1 input)
            {
                if (input.CanonicalArgumentsDigest == null || entries.Count >= MaximumEntries) return;
                try
                {
                    var entry = new Entry { Type = type, Arguments = GameStateCloneService.DeepClone(args), Input = GameStateCloneService.DeepClone(input) };
                    entries[input.CanonicalArgumentsDigest] = entry;
                }
                // 校验允许但通用复制不支持的附加字段/容器不获得凭据，保持原校验语义。
                catch (InvalidOperationException) { }
                catch (NotSupportedException) { }
                catch (ArgumentException) { }
                catch (MissingMethodException) { }
                catch (MemberAccessException) { }
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                entries.Clear();
                if (!ReferenceEquals(currentValidationMemo, this)) return;
                var restored = previous;
                while (restored != null && restored.disposed) restored = restored.previous;
                currentValidationMemo = restored;
            }
        }
    }
}
