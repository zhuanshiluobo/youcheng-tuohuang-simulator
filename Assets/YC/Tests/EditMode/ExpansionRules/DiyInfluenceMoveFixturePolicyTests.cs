using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 检查测试驱动如何从真实页面候选继续；不把隔离夹具算作正常局通过。
    public sealed class DiyInfluenceMoveFixturePolicyTests
    {
        private static string Choose(IEnumerable<string> preferred, IEnumerable<string> visible, bool mandatory)
        {
            var driver = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
            return (string)driver.GetMethod("ChooseVisibleMoveTarget", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { preferred, visible, mandatory });
        }

        private static bool CanFallback(string type, bool visible = true, bool owned = true, bool required = true,
            bool allowDecline = false, bool canEnd = false, bool current = true, bool revisionMatches = true)
        {
            var driver = Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true);
            return (bool)driver.GetMethod("CanUseRequiredInfluenceTarget", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { type, visible, owned, required, allowDecline, canEnd, current, revisionMatches });
        }

        [Test]
        public void SelectsPreferredOnlyWhenVisibleAndFallsBackOnlyForMandatoryCurrentRequest()
        {
            Assert.That(Choose(new[] { "not-visible", "visible-b" }, new[] { "visible-a", "visible-b" }, true), Is.EqualTo("visible-b"));
            Assert.That(Choose(new[] { "not-visible" }, new[] { "visible-b", "visible-a" }, true), Is.EqualTo("visible-a"));
            Assert.That(Choose(Array.Empty<string>(), new[] { "visible-a" }, false), Is.Null);
            Assert.That(Choose(new[] { "not-visible" }, Array.Empty<string>(), true), Is.Null);
        }

        [Test]
        public void RequiredFallbackRejectsOtherPlayersStaleRequestsAndOptionalActions()
        {
            Assert.That(CanFallback("effect.influence.place.target"), Is.True);
            Assert.That(CanFallback("effect.influence.move.target"), Is.True);
            Assert.That(CanFallback("effect.influence.remove.target"), Is.False);
            Assert.That(CanFallback("effect.influence.move.target", visible: false), Is.False);
            Assert.That(CanFallback("effect.influence.move.target", owned: false), Is.False);
            Assert.That(CanFallback("effect.influence.move.target", required: false), Is.False);
            Assert.That(CanFallback("effect.influence.move.target", allowDecline: true), Is.False);
            Assert.That(CanFallback("effect.influence.move.target", canEnd: true), Is.False);
            Assert.That(CanFallback("effect.influence.move.target", current: false), Is.False);
            Assert.That(CanFallback("effect.influence.move.target", revisionMatches: false), Is.False);
        }

        [Test]
        public void RecordedRequiredTargetHasSixVisibleLegalFallbacks()
        {
            string fixture = Environment.GetCommandLineArgs().FirstOrDefault(value =>
                value.StartsWith("--yc-influence-target-fixture=", StringComparison.Ordinal))?.Substring("--yc-influence-target-fixture=".Length);
            if (fixture == null) Assert.Ignore("仅显式提供只读原页面夹具时运行。");
            string results = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            fixture = Path.GetFullPath(fixture);
            Assert.That(fixture.StartsWith(results, StringComparison.OrdinalIgnoreCase), Is.True);
            var manifest = JObject.Parse(File.ReadAllText(fixture));
            string Verified(string name)
            {
                string path = Path.GetFullPath((string)manifest["files"][name]["path"]);
                Assert.That(path.StartsWith(results, StringComparison.OrdinalIgnoreCase), Is.True);
                using var stream = File.OpenRead(path); using var sha = SHA256.Create();
                Assert.That(string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2"))),
                    Is.EqualTo((string)manifest["files"][name]["sha256"]));
                return path;
            }
            var first = JObject.Parse(File.ReadAllText(Verified("difference")));
            string policy = Verified("policy"); Verified("failure");
            var state = first["Current"]; var request = state["CurrentRequest"];
            Assert.That((string)state["GameId"], Is.EqualTo((string)manifest["gameId"]));
            Assert.That((int)state["Revision"], Is.EqualTo((int)manifest["revision"]));
            Assert.That((string)request["InteractionId"], Is.EqualTo((string)manifest["requestId"]));
            Assert.That(CanFallback((string)request["InteractionTypeId"], (bool)request["VisibleToViewer"],
                (int)request["AnsweringPlayerId"] == (int)state["LocalPlayerId"], (int)request["MinSelections"] > 0,
                (bool)request["AllowDecline"], (bool)state["CanEnd"], (string)request["Status"] == "open",
                (int)request["StateRevision"] == (int)state["Revision"]), Is.True);
            var candidates = ((JArray)request["CandidateIds"]).Values<string>().ToArray();
            var visible = ((JArray)state["ActualHighlights"]).Where(value => (string)value["Kind"] == "InfluenceSlot" &&
                (bool)value["Visible"] && (bool)value["Active"] && (bool)value["IsInteractive"])
                .Select(value => (string)value["TargetId"]).Intersect(candidates).ToArray();
            Assert.That(visible.Length, Is.EqualTo(6));
            var row = File.ReadLines(policy).Select(JObject.Parse).Single(value => (int)value["Frame"] == (int)manifest["frame"]);
            Assert.That(((JArray)row["Candidates"]).Count, Is.Zero);
            Assert.That(visible, Does.Contain(Choose(Array.Empty<string>(), visible, true)));
        }

        [Test]
        public void RecordedMandatoryMoveHasVisibleLegalFallbackAfterOriginalSourceClick()
        {
            string fixture = Environment.GetCommandLineArgs().FirstOrDefault(value =>
                value.StartsWith("--yc-influence-move-fixture=", StringComparison.Ordinal))?.Substring("--yc-influence-move-fixture=".Length);
            if (fixture == null) Assert.Ignore("仅显式提供只读原页面夹具时运行。");
            string results = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            fixture = Path.GetFullPath(fixture);
            Assert.That(fixture.StartsWith(results, StringComparison.OrdinalIgnoreCase), Is.True);
            var manifest = JObject.Parse(File.ReadAllText(fixture));
            string Verified(string name)
            {
                string path = Path.GetFullPath((string)manifest["files"][name]["path"]);
                Assert.That(path.StartsWith(results, StringComparison.OrdinalIgnoreCase), Is.True);
                using var stream = File.OpenRead(path); using var sha = SHA256.Create();
                string actual = string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
                Assert.That(actual, Is.EqualTo((string)manifest["files"][name]["sha256"]));
                return path;
            }
            string difference = Verified("difference"), policy = Verified("policy"), controls = Verified("controls");
            Verified("failure");
            var first = JObject.Parse(File.ReadAllText(difference));
            var current = first["Current"]; var request = current["CurrentRequest"];
            Assert.That((string)current["GameId"], Is.EqualTo((string)manifest["gameId"]));
            Assert.That((int)current["Revision"], Is.EqualTo((int)manifest["revision"]));
            Assert.That((string)request["InteractionId"], Is.EqualTo((string)manifest["requestId"]));
            Assert.That((string)request["InteractionTypeId"], Is.EqualTo("character.ability.choice.move"));
            Assert.That((bool)request["VisibleToViewer"], Is.True);
            Assert.That((int)request["AnsweringPlayerId"], Is.EqualTo((int)current["LocalPlayerId"]));
            Assert.That((int)request["MinSelections"], Is.GreaterThan(0));
            Assert.That((bool)request["AllowDecline"], Is.False);
            var pairs = ((JArray)request["CandidateIds"]).Values<string>().Select(value => value.Split('|')).ToArray();
            Assert.That(pairs.All(value => value.Length == 3 && value[0] == "move"), Is.True);
            Assert.That(pairs.Select(value => value[1]).Distinct().Count(), Is.EqualTo(1));
            var visible = ((JArray)current["ActualHighlights"]).Where(value => (string)value["Kind"] == "InfluenceSlot" &&
                (bool)value["Visible"] && (bool)value["Active"] && (bool)value["IsInteractive"]).Select(value => (string)value["TargetId"]).ToArray();
            var legalVisible = visible.Intersect(pairs.Select(value => value[2])).ToArray();
            Assert.That(legalVisible.Length, Is.EqualTo(8));
            var row = File.ReadLines(policy).Select(JObject.Parse).Single(value => (int)value["Frame"] == (int)manifest["frame"]);
            Assert.That(((JArray)row["Candidates"]).Count, Is.Zero, "原策略确实未选出候选。");
            var presses = File.ReadLines(controls).Select(JObject.Parse).Where(value => (string)value["Stage"] == (string)first["Stage"]).ToArray();
            Assert.That(presses.Any(value => (string)value["Description"] == "角色移动来源原首击"), Is.True);
            string selected = Choose(Array.Empty<string>(), legalVisible, true);
            Assert.That(selected, Is.Not.Null);
            Assert.That(legalVisible, Does.Contain(selected));
            Assert.That(pairs.Count(value => value[2] == selected), Is.EqualTo(1));
            Assert.That(Choose(Array.Empty<string>(), legalVisible, false), Is.Null);
        }
    }
}
