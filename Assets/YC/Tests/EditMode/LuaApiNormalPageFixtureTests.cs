using System;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.State;
using YC.Domain.Presentation;

namespace YC.Tests.EditMode
{
    // API-06/07：夹具识别与候选政策隔离检查，不计正常页面覆盖。
    public sealed class LuaApiNormalPageFixtureTests
    {
        private static object Call(string name, params object[] args) =>
            Type.GetType("YC.Presentation.Validation.HotseatUiValidationDriver, Assembly-CSharp", true)
                .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);

        [Test]
        public void CollapsedViewportPausesContinuationUntilStableRestorationWithoutSubmittingInput()
        {
            var sizes = new[] { new UnityEngine.Vector2Int(144, 1), new UnityEngine.Vector2Int(144, 1),
                new UnityEngine.Vector2Int(1600, 900), new UnityEngine.Vector2Int(144, 1),
                new UnityEngine.Vector2Int(1600, 900), new UnityEngine.Vector2Int(1600, 900) };
            int read = 0, submitted = 0;
            var observations = new System.Collections.Generic.List<bool>();
            var wait = (System.Collections.IEnumerator)Call("AwaitUsableViewport",
                (Func<UnityEngine.Vector2Int>)(() => sizes[read++]),
                (Action<bool, UnityEngine.Vector2Int>)((paused, size) => observations.Add(paused)));
            for (int i = 0; i < sizes.Length - 1; i++)
            {
                Assert.That(wait.MoveNext(), Is.True, "折叠或尚未稳定的一帧不得恢复原续接");
                Assert.That(submitted, Is.Zero);
                Assert.That(observations, Is.EqualTo(new[] { true }));
            }
            Assert.That(wait.MoveNext(), Is.False);
            submitted++; // 调用方只有在等待结束后才可进入原步骤；门禁本身不提交输入。
            Assert.That(submitted, Is.EqualTo(1));
            Assert.That(observations, Is.EqualTo(new[] { true, false }));
            Assert.That(read, Is.EqualTo(sizes.Length));
        }

        [Test]
        public void AvailableViewportContinuesImmediatelyWithoutPauseOrResize()
        {
            int observations = 0;
            var wait = (System.Collections.IEnumerator)Call("AwaitUsableViewport",
                (Func<UnityEngine.Vector2Int>)(() => new UnityEngine.Vector2Int(1280, 720)),
                (Action<bool, UnityEngine.Vector2Int>)((paused, size) => observations++));
            Assert.That(wait.MoveNext(), Is.False);
            Assert.That(observations, Is.Zero);
        }

        [TestCase("typed_p2", true)]
        [TestCase("explicit_player", true)]
        [TestCase("decimal_alias", true)]
        [TestCase("untyped_p2", false)]
        [TestCase("wrong_reference", false)]
        [TestCase("other_game", false)]
        [TestCase("stale_revision", false)]
        [TestCase("other_candidate", false)]
        [TestCase("other_viewer", false)]
        [TestCase("config_revision", false)]
        [TestCase("ineligible", false)]
        [TestCase("duplicate_selection", false)]
        [TestCase("missing_selection", false)]
        public void PlayerAnswerUsesBoundTypedProjectionAndPreservesFormalIdentity(string variant, bool expected)
        {
            string id = variant == "explicit_player" ? "player:2" : variant == "decimal_alias" ? "2" : "p2";
            var routed = new InteractionRequestProjection { InteractionId = "actual-request", SourceNodeId = "actual-owner",
                OwnerEffectId = "actual-owner", InteractionTypeId = "lua.choice", AnsweringPlayerId = 1,
                Visibility = "owner", VisibleToViewer = true, Status = "open", StateRevision = 12,
                AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1,
                CandidateIds = new System.Collections.Generic.List<string> { id } };
            var rendered = GameStateCloneService.DeepClone(routed);
            rendered.Presentation = new GameplayInteractionPresentation { Options = new System.Collections.Generic.List<GameplayOptionPresentation> {
                new GameplayOptionPresentation { CandidateId = id, Label = new GameplayTextRef("core:entrepreneur.M.M13.opponent.player",
                    new GameplayTextArgument { Name = "who", Kind = "player", Value = NormalizedValue.CreateStableReference("player", variant == "wrong_reference" ? "p3" : "p2") }) } } };
            // 重现Router基础投影无typed标签、原页面完整投影有标签的正式边界。
            Assert.That(routed.Presentation, Is.Null);
            if (variant == "untyped_p2") rendered.Presentation = null;
            if (variant == "stale_revision") rendered.StateRevision--;
            if (variant == "other_candidate") rendered.CandidateIds[0] = "p3";
            var type = Type.GetType("YC.Presentation.PlayerSelectionConfig, Assembly-CSharp", true);
            var config = Activator.CreateInstance(type);
            type.GetField("RequestId").SetValue(config, routed.InteractionId);
            type.GetField("Revision").SetValue(config, variant == "config_revision" ? 11 : 12);
            var optionType = Type.GetType("YC.Presentation.PlayerSelectionOption, Assembly-CSharp", true);
            var player = Activator.CreateInstance(optionType); optionType.GetField("Id").SetValue(player, 2);
            optionType.GetField("Eligible").SetValue(player, variant != "ineligible");
            var players = Array.CreateInstance(optionType, 1); players.SetValue(player, 0); type.GetField("Players").SetValue(config, players);
            var selected = variant == "missing_selection" ? Array.Empty<int>() : variant == "duplicate_selection" ? new[] { 2, 2 } : new[] { 2 };
            var answer = Call("PlayerSelectionFormalAnswers", "actual-game", variant == "other_game" ? "old-game" : "actual-game",
                variant == "other_viewer" ? 2 : 1, routed, rendered, config, selected) as string[];
            if (expected) CollectionAssert.AreEqual(new[] { id }, answer);
            else Assert.That(answer, Is.Null, "无法证明原页面与正式请求为同一来源时不得猜测p2或提交旧答案。");
        }

        [Test]
        public void SaleUsesFormalSemanticIndependentlyOfPrompt()
        {
            var request = new InteractionRequestProjection { PromptKey = "effect.resource.choose_type",
                PromptParameters = NormalizedValue.CreateObject(new System.Collections.Generic.List<NormalizedValueEntry> {
                    new NormalizedValueEntry { Name = "interactionSemantic", Value = NormalizedValue.CreateString(ContentPrimitiveEffectExecutor.ResourceSaleSemantic) } }) };
            Assert.IsTrue((bool)Call("LuaApiSaleSemantic", request));
            request.PromptParameters = null; request.PromptKey = "effect.resource.sell";
            Assert.IsFalse((bool)Call("LuaApiSaleSemantic", request), "普通选择不能凭提示文字伪装正式出售语义");
        }

        [Test]
        public void TiagoMustBeBothFormallyOfferedAndRendered()
        {
            const string tiago = "entrepreneur.ability.D09", other = "entrepreneur.ability.A09";
            Assert.AreEqual(tiago, Call("LuaApiEntrepreneurCandidate", new[] { other, tiago }, new[] { tiago, other }));
            Assert.AreEqual(other, Call("LuaApiEntrepreneurCandidate", new[] { other }, new[] { other, tiago }));
            Assert.AreEqual(other, Call("LuaApiEntrepreneurCandidate", new[] { tiago, other }, new[] { other }));
            Assert.IsNull(Call("LuaApiEntrepreneurCandidate", new[] { tiago }, new[] { other }));
        }

        [Test]
        public void SalePointPriorityNeverCreatesAnUnofferedOrHiddenFour()
        {
            var request = new InteractionRequestProjection { InteractionTypeId = "effect.choice.execute",
                PromptKey = "core:entrepreneur.D.points.prompt", VisibleToViewer = true, Status = "open",
                CandidateIds = new System.Collections.Generic.List<string> { "2", "4" } };
            Assert.IsTrue((bool)Call("LuaApiPreferSalePoint", request, "4"));
            Assert.IsFalse((bool)Call("LuaApiPreferSalePoint", request, "2"));
            request.CandidateIds.Remove("4");
            Assert.IsFalse((bool)Call("LuaApiPreferSalePoint", request, "4"));
            request.CandidateIds.Add("4"); request.VisibleToViewer = false;
            Assert.IsFalse((bool)Call("LuaApiPreferSalePoint", request, "4"));
            request.VisibleToViewer = true; request.Status = "closed";
            Assert.IsFalse((bool)Call("LuaApiPreferSalePoint", request, "4"));
            request.Status = "open"; request.PromptKey = "another.choice";
            Assert.IsFalse((bool)Call("LuaApiPreferSalePoint", request, "4"));
        }

        [TestCase("eligible", true)]
        [TestCase("covered", false)]
        [TestCase("unaffordable", false)]
        [TestCase("other_source", false)]
        [TestCase("other_prompt", false)]
        [TestCase("other_kind", false)]
        [TestCase("other_interaction", false)]
        [TestCase("hidden", false)]
        [TestCase("closed", false)]
        [TestCase("no_continue", false)]
        public void InvestmentCoveragePriorityRequiresActualLegalUncoveredTiago(string variant, bool expected)
        {
            var request = new InteractionRequestProjection { InteractionTypeId = "action.decline_effect",
                VisibleToViewer = true, Status = "open",
                CandidateIds = new System.Collections.Generic.List<string> { "continue", "decline" },
                Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef("core:entrepreneur.D.investment.condition") } };
            if (variant == "other_prompt") request.Presentation.Prompt = new GameplayTextRef("core:entrepreneur.D.exchange.condition");
            if (variant == "other_interaction") request.InteractionTypeId = "effect.resource.payment_confirmation";
            if (variant == "hidden") request.VisibleToViewer = false;
            if (variant == "closed") request.Status = "closed";
            if (variant == "no_continue") request.CandidateIds.Remove("continue");
            Assert.AreEqual(expected, Call("LuaApiPreferInvestmentExecution", request,
                variant == "other_kind" ? "payment" : "condition", variant != "other_source", variant != "unaffordable", variant == "covered"));
        }

        [TestCase("entrepreneur.ability.D09", "", true)]
        [TestCase("entrepreneur.round.D.3", "", true)]
        [TestCase("", "entrepreneur.D07.quick", true)]
        [TestCase("entrepreneur.ability.A09", "entrepreneur.A09.special", false)]
        [TestCase("", "entrepreneur.Different", false)]
        public void SaleSourceRequiresActualTiagoIdentity(string definition, string ability, bool expected) =>
            Assert.AreEqual(expected, Call("LuaApiTiagoSource", definition, ability));
    }
}
