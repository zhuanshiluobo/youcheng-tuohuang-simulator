using System;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        [ThreadStatic] private static bool fullAbilityCatalogCopies;

        public static IDisposable UseFullAbilityCatalogCopiesForDiagnostics() => new FullCatalogCopyScope();
        private sealed class FullCatalogCopyScope : IDisposable
        {
            private readonly bool previous = fullAbilityCatalogCopies;
            private bool closed;
            internal FullCatalogCopyScope() { fullAbilityCatalogCopies = true; }
            public void Dispose() { if (closed) return; closed = true; fullAbilityCatalogCopies = previous; }
        }

        // 内部谓词仅供领域层已有的纯字段查询；不把冻结声明交给外部回调。
        // 选中后仍返回完整独立副本，公开整目录属性也保留原防御复制合同。
        internal AbilitySourceDeclaration SelectSingleAbilityDeclaration(Func<AbilitySourceDeclaration, bool> predicate)
        {
            using var sample = GameStateCloneService.MeasureDiagnostic("ability.catalog.select");
            return fullAbilityCatalogCopies ? AbilityDeclarations.Single(predicate) :
                GameStateCloneService.DeepClone(abilityDeclarations.Single(predicate));
        }
        internal AbilitySourceDeclaration SelectSingleOrDefaultAbilityDeclaration(Func<AbilitySourceDeclaration, bool> predicate)
        {
            using var sample = GameStateCloneService.MeasureDiagnostic("ability.catalog.select");
            return fullAbilityCatalogCopies ? AbilityDeclarations.SingleOrDefault(predicate) :
                GameStateCloneService.DeepClone(abilityDeclarations.SingleOrDefault(predicate));
        }
        internal AbilitySourceDeclaration SelectFirstOrDefaultAbilityDeclaration(Func<AbilitySourceDeclaration, bool> predicate)
        {
            using var sample = GameStateCloneService.MeasureDiagnostic("ability.catalog.select");
            return fullAbilityCatalogCopies ? AbilityDeclarations.FirstOrDefault(predicate) :
                GameStateCloneService.DeepClone(abilityDeclarations.FirstOrDefault(predicate));
        }
        internal bool ContainsAbilityDeclaration(Func<AbilitySourceDeclaration, bool> predicate)
        {
            using var sample = GameStateCloneService.MeasureDiagnostic("ability.catalog.contains");
            return fullAbilityCatalogCopies ? AbilityDeclarations.Any(predicate) : abilityDeclarations.Any(predicate);
        }

        // 跨程序集只提供具体键查询或标量，不向调用者公开私有声明或通用谓词入口。
        public AbilitySourceDeclaration GetAbilitySourceDeclaration(string abilityId) =>
            SelectSingleAbilityDeclaration(d => d.AbilityId == abilityId);
        public AbilitySourceDeclaration FindAbilitySourceDeclaration(string abilityId, string definitionId) =>
            SelectSingleOrDefaultAbilityDeclaration(d => d.AbilityId == abilityId && d.DefinitionId == definitionId);
        public bool HasAvailabilityControlledEventSource(string eventType) =>
            ContainsAbilityDeclaration(d => d.Kind == "event" && d.ObservedEvents.Contains(eventType) && d.CleanupInjectionPolicy == "when_available");
        public bool IsCurrentAbilitySource(GameState state, AbilitySourceRef source) =>
            AbilitySourceService.IsCurrent(state, fullAbilityCatalogCopies ? AbilityDeclarations : abilityDeclarations, source);
    }
}
