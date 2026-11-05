using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class AbilityCatalogReadQueryTests
    {
        private static EffectRegistry Registry(int count, out List<AbilitySourceDeclaration> input)
        {
            var registry = new EffectRegistry();
            registry.RegisterAbilityHandler("body", "body-v1", _ => Array.Empty<EffectSpec>());
            input = Enumerable.Range(0, count).Select(i => new AbilitySourceDeclaration {
                AbilityId = "a" + i, DefinitionId = "definition", Kind = i == 1 ? "event" : "quick", HandlerRef = "body",
                PresentationRef = "definition", DeclarationVersion = "v1", CleanupInjectionPolicy = "when_available",
                EffectiveZones = new List<string> { "hand" }, ObservedEvents = new List<string> { "E" },
                Actions = new List<AbilityActionDeclaration> { new AbilityActionDeclaration {
                    ActionId = "action" + i, EntryKind = "quick", BodyHandlerRef = "body", PresentationRef = "definition" } }
            }).ToList();
            registry.FreezeAbilityCatalog(input); return registry;
        }
        private static object Internal(EffectRegistry registry, string method, Func<AbilitySourceDeclaration, bool> predicate)
        {
            try { return typeof(EffectRegistry).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(registry, new object[] { predicate }); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private static string Outcome(Func<object> action)
        {
            try { return JsonConvert.SerializeObject(action()); }
            catch (Exception error) { return error.GetType().FullName + "|" + error.Message; }
        }
        [Test]
        public void SelectedAndScalarQueriesKeepWholeCatalogResultsAndOriginalFailureSemantics()
        {
            var registry = Registry(3, out _);
            foreach (Func<AbilitySourceDeclaration, bool> predicate in new Func<AbilitySourceDeclaration, bool>[] {
                d => d.AbilityId == "a1", d => d.AbilityId == "missing", d => d.DefinitionId == "definition", null })
            {
                Assert.That(Outcome(() => Internal(registry, "SelectSingleAbilityDeclaration", predicate)),
                    Is.EqualTo(Outcome(() => registry.AbilityDeclarations.Single(predicate))));
                Assert.That(Outcome(() => Internal(registry, "SelectSingleOrDefaultAbilityDeclaration", predicate)),
                    Is.EqualTo(Outcome(() => registry.AbilityDeclarations.SingleOrDefault(predicate))));
                Assert.That(Outcome(() => Internal(registry, "SelectFirstOrDefaultAbilityDeclaration", predicate)),
                    Is.EqualTo(Outcome(() => registry.AbilityDeclarations.FirstOrDefault(predicate))));
                Assert.That(Outcome(() => Internal(registry, "ContainsAbilityDeclaration", predicate)),
                    Is.EqualTo(Outcome(() => registry.AbilityDeclarations.Any(predicate))));
            }
            Assert.That(registry.HasAvailabilityControlledEventSource("E"), Is.True);
            Assert.That(registry.HasAvailabilityControlledEventSource("missing"), Is.False);
            Assert.That(registry.FindAbilitySourceDeclaration("a1", "wrong"), Is.Null);
            using (EffectRegistry.UseFullAbilityCatalogCopiesForDiagnostics())
                Assert.That(registry.GetAbilitySourceDeclaration("a1").AbilityId, Is.EqualTo("a1"));
        }
        [Test]
        public void SelectedDeclarationsAndNestedCollectionsStayDetachedFromFrozenInputsAndOtherReads()
        {
            var registry = Registry(3, out var input);
            var selected = registry.GetAbilitySourceDeclaration("a1");
            input[1].ObservedEvents.Clear(); input[1].Actions[0].ActionId = "changed-input";
            selected.ObservedEvents.Clear(); selected.Actions[0].ActionId = "changed-copy";
            var next = registry.FindAbilitySourceDeclaration("a1", "definition");
            Assert.That(next.ObservedEvents, Is.EqualTo(new[] { "E" }));
            Assert.That(next.Actions[0].ActionId, Is.EqualTo("action1"));
            Assert.That(next, Is.Not.SameAs(selected));
            Assert.That(next.Actions, Is.Not.SameAs(selected.Actions));
            var all = registry.AbilityDeclarations; all[1].Actions.Clear();
            Assert.That(registry.GetAbilitySourceDeclaration("a1").Actions.Count, Is.EqualTo(1));
        }
        [Test]
        public void RepeatedPointQueriesHaveTheSameValuesAsIndependentWholeCatalogCopies()
        {
            var registry = Registry(300, out _); long checksum = 0;
            double Measure(bool full)
            {
                using var scope = full ? EffectRegistry.UseFullAbilityCatalogCopiesForDiagnostics() : null;
                var clock = Stopwatch.StartNew();
                for (int i = 0; i < 120; i++) checksum += registry.GetAbilitySourceDeclaration("a" + i).Actions.Count;
                return clock.Elapsed.TotalSeconds;
            }
            double reference = Measure(true), optimized = Measure(false);
            Assert.That(checksum, Is.EqualTo(240));
            const string prefix = "--yc-catalog-report=";
            string report = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
            if (report != null) File.WriteAllText(report.Substring(prefix.Length), JsonConvert.SerializeObject(new {
                Scope = "冻结目录单条查询隔离定位，不计正常UI验收", Declarations = 300, Queries = 120,
                WholeCatalogSeconds = reference, SelectedCopySeconds = optimized }, Formatting.Indented));
        }
    }
}
