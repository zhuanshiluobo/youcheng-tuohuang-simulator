using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using YC.Application.Presentation;
using YC.Domain.Presentation;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class GameplayTextResolverTests
    {
        [Test] public void ParametersAndNamesRemainLiteralAndInheritFormatting()
        {
            var catalog = Catalog(); catalog.Add("core:test", "[b]{name}[/b]：{text}，{amount}。", "name", "player", "text", "text", "amount", "integer");
            var context = Context(catalog); context.PlayerNames = new Dictionary<string, string> { { "p1", "[color:#ffffff]策略77</b>" } };
            var doc = new GameplayTextResolver(catalog).Resolve(new GameplayTextRef("core:test", Arg("name", "player", "p1"), Arg("text", "text", "{amount}[icon:core:score]"), Arg("amount", "integer", 3)), context);
            Assert.That(doc.ToPlainText(), Is.EqualTo("[color:#ffffff]策略77</b>：{amount}[icon:core:score]，3。"));
            Assert.That(doc.Runs.OfType<NumberRun>().Single().Value, Is.EqualTo(3));
            Assert.That(doc.Runs[0].Style.WeightMode, Is.EqualTo("Bold"));
            Assert.That(doc.Runs.OfType<IconRun>(), Is.Empty);
        }
        [Test] public void NestedColorAndNormalRestoreOuterStyle()
        {
            var catalog = Catalog(); catalog.Add("core:test", "[color:#D95745][b]甲[normal]乙[/normal]丙[/b]丁[/color]戊");
            var doc = new GameplayTextResolver(catalog).Resolve(new GameplayTextRef("core:test"), Context(catalog));
            Assert.That(doc.ToPlainText(), Is.EqualTo("甲乙丙丁戊"));
            Assert.That(doc.Runs.Select(r => r.Style.WeightMode), Is.EqualTo(new[] { "Bold", "Normal", "Bold", "Inherit", "Inherit" }));
            Assert.That(doc.Runs.Take(4).All(r => r.Style.ColorRgba == 0xD95745FFu), Is.True);
            Assert.That(doc.Runs.Last().Style.ColorRgba, Is.Null);
        }
        [Test] public void SemanticKeywordsUseLongestContextMatchAndDoNotRescanParameters()
        {
            var catalog = Catalog(); catalog.Add("core:test", "策略计划策略计谋{text}", "text", "text");
            var context = Context(catalog); context.SemanticKeywords = new[] { new GameplayTextSemanticKeyword("策略计划"), new GameplayTextSemanticKeyword("策略", "Regular"), new GameplayTextSemanticKeyword("计谋") };
            var doc = new GameplayTextResolver(catalog).Resolve(new GameplayTextRef("core:test", Arg("text", "text", "策略计划9")), context);
            Assert.That(doc.Runs[0].ToPlainText(), Is.EqualTo("策略计划"));
            Assert.That(doc.Runs[0].Style.SemanticRole, Is.EqualTo("SpecialWord"));
            Assert.That(doc.Runs.Last().ToPlainText(), Is.EqualTo("策略计划9"));
            Assert.That(doc.Runs.Last().Style.SemanticRole, Is.EqualTo("Regular"));
        }
        [TestCase(0)] [TestCase(-12)] [TestCase(1234)] public void ScoreValueIsInsideOneIconRunAndPlainTextKeepsValue(int amount)
        {
            var catalog = Catalog(); catalog.Add("core:test", "[color:#E0B85A][b]获得[icon:core:score|value={amount}][/b][/color]", "amount", "integer");
            var doc = new GameplayTextResolver(catalog).Resolve(new GameplayTextRef("core:test", Arg("amount", "integer", amount)), Context(catalog));
            var icon = doc.Runs.OfType<IconRun>().Single();
            Assert.That(icon.NumberValue, Is.EqualTo(amount)); Assert.That(icon.OverlayTextStyle.WeightMode, Is.EqualTo("Bold"));
            Assert.That(icon.OverlayTextStyle.ColorRgba, Is.EqualTo(0xE0B85AFFu));
            Assert.That(doc.Runs.OfType<NumberRun>(), Is.Empty); Assert.That(doc.ToPlainText(), Is.EqualTo("获得" + amount + " 分"));
        }
        [Test] public void BareScoreAndCrossedTagsFailDuringPrecompile()
        {
            var catalog = Catalog(); catalog.Add("core:bare", "[icon:core:score]"); catalog.Add("core:cross", "[b][color:#ffffff]甲[/b][/color]");
            var resolver = new GameplayTextResolver(catalog);
            Assert.Throws<GameplayTextFormatException>(() => resolver.Precompile(new[] { "core:bare" }));
            var error = Assert.Throws<GameplayTextFormatException>(() => resolver.Precompile(new[] { "core:cross" }));
            Assert.That(error.TextKey, Is.EqualTo("core:cross")); Assert.That(error.Position, Is.GreaterThan(0));
        }
        [Test] public void EscapesUnknownTagsLeadingZerosAndHardBreakRemainReadable()
        {
            var catalog = Catalog(); catalog.Add("core:test", "{{amount}} [[b]] 001 <b>文字</b>\r\n计谋");
            var doc = new GameplayTextResolver(catalog).Resolve(new GameplayTextRef("core:test"), Context(catalog));
            Assert.That(doc.ToPlainText(), Is.EqualTo("{amount} [b] 001 <b>文字</b>\n计谋"));
            Assert.That(doc.Runs.OfType<LineBreak>().Count(), Is.EqualTo(1));
            Assert.That(doc.Runs.OfType<TextRun>().Any(r => r.Text == "001" && r.Style.SemanticRole == "EffectNumber"), Is.True);
        }
        [Test] public void CacheSeparatesViewerNamesAndVisibilityScopeAtSameRevision()
        {
            var catalog = Catalog(); catalog.Add("core:test", "{card}", "card", "item"); var resolver = new GameplayTextResolver(catalog);
            var reference = new GameplayTextRef("core:test", Arg("card", "item", "i1"));
            var owner = Context(catalog); owner.ItemNames = new Dictionary<string, string> { { "i1", "私密卡" } }; owner.VisibilityScopeId = "session-a";
            var a = resolver.Resolve(reference, owner); Assert.That(resolver.Resolve(reference, owner), Is.SameAs(a));
            var other = Context(catalog); other.ViewerPlayerId = 2; other.VisibilityScopeId = "session-a";
            var b = resolver.Resolve(reference, other); Assert.That(b.ToPlainText(), Is.EqualTo("未知对象")); Assert.That(b.Identity, Is.Not.EqualTo(a.Identity));
            owner.VisibilityScopeId = "session-b"; Assert.That(resolver.Resolve(reference, owner).Identity, Is.Not.EqualTo(a.Identity));
            owner.ItemNames = new Dictionary<string, string> { { "i1", "新名字" } }; Assert.That(resolver.Resolve(reference, owner).ToPlainText(), Is.EqualTo("新名字"));
        }
        [Test] public void ImmutableDocumentSurvivesArgumentMutationAndRepeatedResolveIsCached()
        {
            var catalog = Catalog(); catalog.Add("core:test", "{text}", "text", "text"); var resolver = new GameplayTextResolver(catalog);
            var arg = Arg("text", "text", "原文"); var reference = new GameplayTextRef("core:test", arg); var context = Context(catalog);
            var old = resolver.Resolve(reference, context); arg.Value.StringValue = "新文";
            var changed = resolver.Resolve(reference, context); Assert.That(old.ToPlainText(), Is.EqualTo("原文")); Assert.That(changed.ToPlainText(), Is.EqualTo("新文"));
            Assert.That(resolver.Resolve(reference, context), Is.SameAs(changed)); Assert.That(resolver.CompiledTemplateCount, Is.EqualTo(1));
        }
        [Test] public void WrongArgumentAndMissingKeyUseDeclaredFallbackAndDeduplicateDiagnostics()
        {
            var catalog = Catalog(); catalog.Add("core:test", "{amount}", "amount", "integer"); var resolver = new GameplayTextResolver(catalog); int errors = 0; resolver.Diagnostic += _ => errors++;
            var bad = new GameplayTextRef("core:test", Arg("amount", "text", "秘密内容"));
            Assert.That(resolver.Resolve(bad, Context(catalog)).ToPlainText(), Is.EqualTo("此项说明暂不可用"));
            resolver.Resolve(bad, Context(catalog)); Assert.That(errors, Is.EqualTo(1));
            Assert.That(resolver.Resolve(new GameplayTextRef("core:missing"), Context(catalog)).ToPlainText(), Is.EqualTo("此项说明暂不可用"));
        }
        [Test] public void ResourceUsesExplicitRegisteredMappingRatherThanEnglishGuess()
        {
            var catalog = Catalog(); catalog.Add("core:test", "支付{resource}", "resource", "resource");
            var context = Context(catalog); context.ResourceIconKeys = new Dictionary<string, string> { { "originium", "core:rock" } };
            var doc = new GameplayTextResolver(catalog).Resolve(new GameplayTextRef("core:test", Arg("resource", "resource", "originium")), context);
            Assert.That(doc.ToPlainText(), Is.EqualTo("支付源岩")); Assert.That(doc.Runs.OfType<IconRun>().Single().IconKey, Is.EqualTo("core:rock"));
        }
        [Test] public void ForeignContentOrLocaleCannotReuseCatalog()
        {
            var catalog = Catalog(); var context = Context(catalog); context.ContentHash = "other";
            Assert.Throws<InvalidOperationException>(() => new GameplayTextResolver(catalog).Resolve(new GameplayTextRef("core:presentation.unavailable"), context));
            context.ContentHash = catalog.ContentHash; context.Locale = "en-US";
            Assert.Throws<InvalidOperationException>(() => new GameplayTextResolver(catalog).Resolve(new GameplayTextRef("core:presentation.unavailable"), context));
        }
        [Test] public void BoundedCacheEvictsOldDocumentsWithoutGrowingHistory()
        {
            var catalog = Catalog(); catalog.Add("core:test", "{amount}", "amount", "integer"); var resolver = new GameplayTextResolver(catalog, cacheCapacity: 2);
            var context = Context(catalog); var a = resolver.Resolve(new GameplayTextRef("core:test", Arg("amount", "integer", 1)), context);
            resolver.Resolve(new GameplayTextRef("core:test", Arg("amount", "integer", 2)), context); resolver.Resolve(new GameplayTextRef("core:test", Arg("amount", "integer", 3)), context);
            Assert.That(resolver.Resolve(new GameplayTextRef("core:test", Arg("amount", "integer", 1)), context), Is.Not.SameAs(a));
        }
        [Test] public void SnapshotPrecompileUsesFrozenEnumerationAndLegacyInputKindIsExplicit()
        {
            var catalog = Catalog(); var resolver = new GameplayTextResolver(catalog); resolver.Precompile();
            Assert.That(resolver.CompiledTemplateCount, Is.EqualTo(catalog.Texts.Count));
            var adapter = new LegacyGameplayTextAdapter(resolver); var context = Context(catalog);
            Assert.That(adapter.Resolve("core:presentation.unavailable", LegacyGameplayTextInputKind.Literal, context).ToPlainText(), Is.EqualTo("core:presentation.unavailable"));
            Assert.That(adapter.Resolve("core:presentation.unavailable", LegacyGameplayTextInputKind.TextKey, context).ToPlainText(), Is.EqualTo("此项说明暂不可用"));
            Assert.That(adapter.Resolve("[b]策略77[/b]", LegacyGameplayTextInputKind.Literal, context).Runs.OfType<NumberRun>(), Is.Empty);
        }
        [Test] public void FullCatalogPrecompileFailsWithTemplateIdentityBeforeUiCanConsumeIt()
        {
            var catalog = Catalog(); catalog.Add("core:last.invalid", "正文[b]漏闭合");
            var resolver = new GameplayTextResolver(catalog); int runtimeDiagnostics = 0;
            resolver.Diagnostic += _ => runtimeDiagnostics++;
            var error = Assert.Throws<GameplayTextFormatException>(() => resolver.Precompile());
            Assert.That(error.TextKey, Is.EqualTo("core:last.invalid")); Assert.That(error.Position, Is.GreaterThanOrEqualTo(0));
            Assert.That(runtimeDiagnostics, Is.EqualTo(0)); Assert.That(resolver.ResolvedDocumentCount, Is.EqualTo(0));
        }

        private static GameplayTextContext Context(TestCatalog catalog) => new GameplayTextContext { Catalog = catalog, ContentHash = catalog.ContentHash, Locale = catalog.Locale, ViewerPlayerId = 1, StateRevision = 3 };
        private static GameplayTextArgument Arg(string name, string kind, object value) => new GameplayTextArgument { Name = name, Kind = kind, Value = kind == "integer" ? NormalizedValue.CreateInteger(Convert.ToInt64(value)) : kind == "text" ? NormalizedValue.CreateString((string)value) : NormalizedValue.CreateStableReference(kind, (string)value) };
        private static TestCatalog Catalog()
        {
            var catalog = new TestCatalog(); catalog.Add("core:presentation.unavailable", "此项说明暂不可用"); catalog.Add("core:presentation.unknown_entity", "未知对象");
            catalog.Add("core:score.alt", "分数"); catalog.Add("core:score.value", "{value} 分", "value", "integer"); catalog.Add("core:rock.alt", "源岩");
            catalog.Icons.Add("core:score", new GameplayIconDefinition { Id = "core:score", Usage = "inline_rule", RenderMode = "number_overlay", ValueRequired = true, ValueAltTextKey = "core:score.value", AltText = new GameplayTextRef("core:score.alt") });
            catalog.Icons.Add("core:rock", new GameplayIconDefinition { Id = "core:rock", Usage = "inline_rule", AltText = new GameplayTextRef("core:rock.alt") }); return catalog;
        }
        public sealed class TestCatalog : IGameplayTextCatalogSnapshot
        {
            public string ContentHash => "test-content"; public string Locale => "zh-CN";
            public readonly Dictionary<string, GameplayTextTemplateDefinition> Templates = new Dictionary<string, GameplayTextTemplateDefinition>();
            public readonly Dictionary<string, GameplayIconDefinition> Icons = new Dictionary<string, GameplayIconDefinition>();
            public IReadOnlyList<GameplayTextTemplateDefinition> Texts => Templates.Values.ToList().AsReadOnly();
            public bool TryGetTemplate(string key, out GameplayTextTemplateDefinition definition) => Templates.TryGetValue(key, out definition);
            public bool TryGetIcon(string key, out GameplayIconDefinition definition) => Icons.TryGetValue(key, out definition);
            public void Add(string key, string template, params string[] parameters)
            { var definition = new GameplayTextTemplateDefinition { Key = key, Template = template }; for (int i = 0; i < parameters.Length; i += 2) definition.Parameters.Add(new GameplayTextParameterDefinition { Name = parameters[i], Kind = parameters[i + 1] }); Templates.Add(key, definition); }
        }
    }
}
