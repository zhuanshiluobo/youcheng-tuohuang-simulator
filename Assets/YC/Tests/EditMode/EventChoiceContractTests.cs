using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.Cards;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class EventChoiceContractTests
    {
        private const string Source = "event/fixture.json";
        private static JObject Data(int count)
        {
            var choices = new JArray();
            for (int i = 0; i < count; i++)
                choices.Add(new JObject
                {
                    ["choiceId"] = "choice_" + i,
                    ["description"] = "选项 " + i,
                    ["button"] = new JObject { ["topPercent"] = i * 100.0 / count, ["bottomPercent"] = (i + 1) * 100.0 / count },
                    ["script"] = "lua/shared.lua", ["version"] = "1.0.0"
                });
            var data = new JObject { ["description"] = "夹具描述", ["color"] = 0, ["resourceType"] = 0, ["resourceAmount"] = 1,
                ["representativeResourceType"] = 0, ["representativeResourceAmount"] = 1,
                ["eventChoiceSchemaVersion"] = 1, ["choices"] = choices };
            if (count == 0) data["immediateResolve"] = new JObject { ["script"] = "lua/immediate.lua", ["version"] = "1.0.0" };
            return data;
        }

        private static EventChoiceContract Parse(JObject value) => EventChoiceContract.Parse(value, Source,
            _ => "error('不应在加载时运行')\nreturn function(ctx) return {} end", new[] { "fixture.completed" });

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(6)] [TestCase(16)]
        public void ParsesSupportedCountsWithoutExecutingScripts(int count)
        {
            var parsed = Parse(Data(count));
            Assert.That(parsed.Choices, Has.Count.EqualTo(count));
            Assert.That(parsed.ImmediateResolve != null, Is.EqualTo(count == 0));
            for (int i = 0; i < count; i++) Assert.That(parsed.Choices[i].ChoiceId, Is.EqualTo("choice_" + i));
        }

        [Test]
        public void RejectsTooManyDuplicateIdsAndOverlappingBounds()
        {
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(Data(17))).Message, Does.Contain("choices"));
            var duplicate = Data(2);
            duplicate["choices"][1]["choiceId"] = duplicate["choices"][0]["choiceId"].DeepClone();
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(duplicate)).Message, Does.Contain("choiceId"));
            var overlap = Data(2);
            overlap["choices"][1]["button"]["topPercent"] = 49.5;
            var message = Assert.Throws<InvalidDataException>(() => Parse(overlap)).Message;
            Assert.That(message, Does.Contain("choice_0").And.Contain("choice_1"));
        }

        [Test]
        public void RejectsInvalidBoundsAndOldOrUnknownFields()
        {
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, -1d, 101d })
            {
                var data = Data(1); data["choices"][0]["button"]["topPercent"] = value;
                Assert.That(Assert.Throws<InvalidDataException>(() => Parse(data)).Message, Does.Contain("button"));
            }
            var mixed = Data(1); mixed["choiceRewards"] = new JArray();
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(mixed)).Message, Does.Contain("choiceRewards"));
            var version = Data(1); version["eventChoiceSchemaVersion"] = long.MaxValue;
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(version)).Message, Does.Contain("eventChoiceSchemaVersion"));
            var unknown = Data(1); unknown["choices"][0]["button"]["height"] = 2;
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(unknown)).Message, Does.Contain("height"));
        }

        [Test]
        public void RequiresExactlyOneAppropriateResolutionPath()
        {
            var missing = Data(0); missing.Remove("immediateResolve");
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(missing)).Message, Does.Contain("immediateResolve"));
            var both = Data(1); both["immediateResolve"] = new JObject { ["script"] = "lua/other.lua", ["version"] = "1.0.0" };
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(both)).Message, Does.Contain("immediateResolve"));
        }

        [Test]
        public void RejectsInvalidResourceFieldsBeforeCatalogCreation()
        {
            var wrongColor = Data(1); wrongColor["color"] = 999;
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(wrongColor)).Message, Does.Contain("枚举"));
            var missingAmount = Data(1); missingAmount.Remove("resourceAmount");
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(missingAmount)).Message, Does.Contain("resourceAmount"));
            var zero = Data(1); zero["representativeResourceAmount"] = 0;
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(zero)).Message, Does.Contain("正整数"));
            var wrongName = Data(1); wrongName["name"] = new JObject();
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(wrongName)).Message, Does.Contain("name"));
            var wrongId = Data(1); wrongId["cardId"] = new JObject();
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(wrongId)).Message, Does.Contain("cardId"));
        }

        [TestCase("../outside.lua")] [TestCase("C:/outside.lua")] [TestCase("/outside.lua")] [TestCase("lua//bad.lua")]
        public void RejectsUnsafeScriptPaths(string path)
        {
            var data = Data(1); data["choices"][0]["script"] = path;
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(data)).Message, Does.Contain("script"));
        }

        [Test]
        public void ChecksScriptSyntaxAndCompletionRegistration()
        {
            var data = Data(1); data["choices"][0]["completionHandlerId"] = "fixture.completed";
            Assert.That(Parse(data).Choices[0].CompletionHandlerId, Is.EqualTo("fixture.completed"));
            data["choices"][0]["completionHandlerId"] = "fixture.missing";
            Assert.That(Assert.Throws<InvalidDataException>(() => Parse(data)).Message, Does.Contain("completionHandlerId"));
            data["choices"][0]["completionHandlerId"] = "fixture.completed";
            Assert.That(Assert.Throws<InvalidDataException>(() => EventChoiceContract.Parse(data, Source,
                _ => "return function(", new[] { "fixture.completed" })).Message, Does.Contain("Lua 编译失败"));
            Assert.That(Assert.Throws<InvalidDataException>(() => EventChoiceContract.Parse(data, Source,
                _ => throw new FileNotFoundException("缺失"), new[] { "fixture.completed" })).Message, Does.Contain("lua/shared.lua"));
        }

        [Test]
        public void SharedScriptUsesOneNormalizedPackagePath()
        {
            var data = Data(2); data["choices"][0]["script"] = "lua\\shared.lua";
            var reads = new List<string>();
            var parsed = EventChoiceContract.Parse(data, Source, path => { reads.Add(path); return "return function(ctx) return {} end"; }, null);
            Assert.That(reads, Is.EqualTo(new[] { "lua/shared.lua" }));
            Assert.That(parsed.Choices[0].Script, Is.EqualTo(parsed.Choices[1].Script));
        }

        [Test]
        public void ChoiceCandidateIdentitySurvivesReorderingAndRejectsOtherCards()
        {
            var candidate = EventCardOptionIdFactory.CreateChoice("demo.event", "trade");
            Assert.That(EventCardOptionIdFactory.TryReadChoice(candidate, "demo.event", new[] { "collect", "trade" }, out var first), Is.True);
            Assert.That(EventCardOptionIdFactory.TryReadChoice(candidate, "demo.event", new[] { "trade", "collect" }, out var reordered), Is.True);
            Assert.That(first, Is.EqualTo("trade"));
            Assert.That(reordered, Is.EqualTo(first));
            Assert.That(EventCardOptionIdFactory.TryReadChoice(candidate, "other.event", new[] { "trade" }, out _), Is.False);
            Assert.That(EventCardOptionIdFactory.TryReadChoice(candidate, "demo.event", new[] { "trade", "trade" }, out _), Is.False);
            Assert.That(EventCardOptionIdFactory.CreateChoice("a:b", "c"), Is.Not.EqualTo(EventCardOptionIdFactory.CreateChoice("a", "b:c")));
        }

        [TestCase("event:option:01")] [TestCase("event:option:+1")] [TestCase("event:option: 1")]
        [TestCase("other:option:1")] [TestCase("event:option:2")]
        public void LegacyCandidateReaderRejectsNonCanonicalAndForeignIds(string candidate)
        {
            Assert.That(EventCardOptionIdFactory.TryRead(candidate, "event", 2, out var index), Is.False);
            Assert.That(index, Is.EqualTo(-1));
            Assert.That(EventCardOptionIdFactory.TryRead("event:option:1", "event", 2, out index), Is.True);
            Assert.That(index, Is.EqualTo(1));
        }

        [TestCase("eventChoiceSchemaVersion")] [TestCase("choices")] [TestCase("choiceRewards")]
        public void LoaderRejectsMissingContractAndLegacyFields(string field)
        {
            var root = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core");
            var pack = ExternalContentPack.Load(root);
            const string path = "event/event_green_01.json";
            var definition = JObject.Parse(File.ReadAllText(Path.Combine(root, path)));
            if (field == "choiceRewards") definition["data"][field] = new JArray();
            else ((JObject)definition["data"]).Remove(field);
            var error = Assert.Throws<InvalidDataException>(() => pack.CreateWithOverrides(new Dictionary<string, string> { [path] = definition.ToString() }));
            Assert.That(error.Message, Does.Contain(field).And.Contain(path));
        }

        [Test]
        public void LoaderChecksChoiceAndCompletionScriptPathsAndSyntax()
        {
            var root = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core");
            var pack = ExternalContentPack.Load(root);
            const string path = "event/event_red_01.json";
            var original = JObject.Parse(File.ReadAllText(Path.Combine(root, path)));
            var malformedChoice = (JObject)original.DeepClone();
            malformedChoice["data"]["choices"][0]["script"] = "fixture/invalid.lua";
            var overrides = new Dictionary<string, string> { [path] = malformedChoice.ToString(), ["fixture/invalid.lua"] = "return function(" };
            Assert.That(Assert.Throws<InvalidDataException>(() => pack.CreateWithOverrides(overrides)).Message,
                Does.Contain("fixture/invalid.lua").And.Contain("编译失败"));
            var unsafeCompletion = (JObject)original.DeepClone();
            unsafeCompletion["abilityHandlers"][0]["script"] = "../escape.lua";
            Assert.That(Assert.Throws<InvalidDataException>(() => pack.CreateWithOverrides(new Dictionary<string, string> { [path] = unsafeCompletion.ToString() })).Message,
                Does.Contain("abilityHandlers").And.Contain("script"));
        }

        [Test]
        public void ChoiceDefinitionsSurviveCloningAndDetectChangedBinding()
        {
            var card = new EventCardDefinition { CardId = "fixture.event", Name = "夹具事件", Description = "夹具",
                Color = EventColor.Green, ResourceType = ResourceType.Originium, RepresentativeResourceType = ResourceType.Originium,
                EventChoiceSchemaVersion = 1, Choices = new List<EventCardChoice> {
                    new EventCardChoice { ChoiceId = "first", Description = "第一项", Script = "lua/first.lua", Version = "1.0.0",
                        Button = new EventCardChoiceButton { TopPercent = 10, BottomPercent = 40 } },
                    new EventCardChoice { ChoiceId = "second", Description = "第二项", Script = "lua/second.lua", Version = "1.0.0",
                        Button = new EventCardChoiceButton { TopPercent = 40, BottomPercent = 90 } } } };
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var validate = typeof(EventCardDatabase).GetMethod("ValidateDefinition", flags);
            var clone = typeof(EventCardDatabase).GetMethod("CloneDefinition", flags);
            var equal = typeof(EventCardDatabase).GetMethod("DefinitionsEqual", flags);
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { card }));
            var copy = (EventCardDefinition)clone.Invoke(null, new object[] { card });
            Assert.That((bool)equal.Invoke(null, new object[] { card, copy }), Is.True);
            copy.Choices[0].Script = "lua/changed.lua";
            copy.Choices[0].Button.TopPercent = 20;
            Assert.That(card.Choices[0].Script, Is.EqualTo("lua/first.lua"));
            Assert.That(card.Choices[0].Button.TopPercent, Is.EqualTo(10));
            Assert.That((bool)equal.Invoke(null, new object[] { card, copy }), Is.False);
            var restored = GameStateCloneService.DeepClone(card);
            Assert.That((bool)equal.Invoke(null, new object[] { card, restored }), Is.True);
        }
    }
}
