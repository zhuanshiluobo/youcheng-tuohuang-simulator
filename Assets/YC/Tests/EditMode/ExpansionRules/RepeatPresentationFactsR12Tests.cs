using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 隔离通用展示合同；合成子请求仅复现候选规模，不实现卡面规则或代替正常UI验收。
    public sealed class RepeatPresentationFactsR12Tests
    {
        private const string Script = "lua/presentation/interaction.choose_build.lua";
        private const string ChildType = "qy008.ordinary.presentation.child";
        private const string ChildPrompt = "core:interaction.map.influence.move";        [TestCase(128)][TestCase(288)]
        public void OrdinaryLargeCandidatesKeepTheirOwnPromptAndFullSetAcrossRecovery(int candidateCount)
        {
            using var f = new Fixture(candidateCount);
            string root = f.Start(); var request = f.Open;
            Assert.That(request.CandidateIds, Is.EqualTo(f.Candidates));
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo(ChildPrompt));
            Assert.That(request.Presentation.Progress, Is.Null); Assert.That(request.Presentation.Options, Is.Empty);
            var before = JsonConvert.SerializeObject(request.Presentation);
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(f.State, f.State.ContentPackHash)), f.Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); f.State = recovered.State; f.Pump();
            Assert.That(JsonConvert.SerializeObject(f.Open.Presentation), Is.EqualTo(before));
            Assert.That(f.Open.CandidateIds, Is.EqualTo(f.Candidates));
            f.Answer(f.Candidates.Last());
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.Contexts, Is.Empty, "未绑定脚本的普通请求不运行已退役的父级进度脚本");
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(q => q.Status == "open"), Is.False);
            f.AssertNoPresentationFailure();
        }        [TestCase(0)][TestCase(2)]
        public void IndependentEffectsWithoutInputDoNotInventPresentationOrChoice(int count)
        {
            using var f = new Fixture(0);
            for (int i = 0; i < count; i++)
            {
                string root = f.Start();
                Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            }
            Assert.That(f.State.EffectRuntime.InteractionRequests, Is.Empty); Assert.That(f.Contexts, Is.Empty);
            Assert.That(f.State.EffectRuntime.EffectNodes.Count, Is.EqualTo(count)); f.AssertNoPresentationFailure();
        }


        [TestCase("empty")]
        [TestCase("partial_progress")]
        [TestCase("other_candidates")]
        public void FormalBuildScriptAcceptsNoApplicableDisplayWithoutInventingFacts(string shape)
        {
            using var f = new Fixture(0);
            var facts = shape == "empty" ? Object() : shape == "partial_progress"
                ? Object("completedCount", NormalizedValue.CreateInteger(0))
                : Object("candidateIds", Strings(new[] { "first", "second" }),
                    "minSelections", NormalizedValue.CreateInteger(1), "maxSelections", NormalizedValue.CreateInteger(1));
            string before = JsonConvert.SerializeObject(facts);
            Assert.That(f.Pack.TryPresent(Script, new GameplayPresentationContext { Purpose = "interaction", Facts = facts },
                out var result, out var code, out string reason), Is.True, reason);
            Assert.That(code, Is.EqualTo(LuaFailureCode.None));
            Assert.That(result.Interaction, Is.Null, "无适用显示时不生成空交互对象");
            Assert.That(result.Resolution, Is.Null); Assert.That(result.Details, Is.Empty);
            Assert.That(JsonConvert.SerializeObject(facts), Is.EqualTo(before));
        }

        [Test]
        public void OverBudgetFactsRemainRejectedInsteadOfRelaxingTheNormalizer()
        {
            using var f = new Fixture(288);
            var facts = Object("candidateIds", Strings(f.Candidates), "minSelections", NormalizedValue.CreateInteger(1),
                "maxSelections", NormalizedValue.CreateInteger(1), "completedCount", NormalizedValue.CreateInteger(0),
                "totalCount", NormalizedValue.CreateInteger(1));
            Assert.That(facts.IsValid(), Is.False);
            Assert.That(f.Pack.TryPresent(Script, new GameplayPresentationContext { Purpose = "interaction", Facts = facts },
                out var result, out var code, out string reason), Is.False);
            Assert.That(code, Is.EqualTo(LuaFailureCode.InvalidReturnShape));
            Assert.That(reason, Does.Contain("显示参数无效")); Assert.That(result, Is.Null);
        }







        private sealed class Fixture : IDisposable
        {
            internal readonly IsolatedTestArtifacts Artifacts = IsolatedTestArtifacts.BeginCase();
            internal readonly ExternalContentPack Pack;
            internal readonly EffectRegistry Registry = new EffectRegistry();
            internal readonly List<GameplayPresentationContext> Contexts = new List<GameplayPresentationContext>();
            internal readonly List<string> Candidates;
            internal GameState State;
            private EffectTreeExecutor Engine => new EffectTreeExecutor(State, Registry);
            internal InteractionRequest Open => State.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");

            internal Fixture(int candidateCount)
            {
                string input = Artifacts.CopyContent(Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core"), "R12正式展示只读输入");
                var configuration = new GameRuleConfiguration { PlayerCount = 3, EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
                var installed = ExternalContentPack.Load(input);
                var active = ActiveContentCatalogBuilder.Build(installed, configuration); configuration.ContentPackHash = active.ContentPackHash;
                State = active.SetupRecipe.Assemble(new GameState { GameId = "qy008.ordinary.presentation." + Guid.NewGuid().ToString("N"),
                    MapId = configuration.MapId, ContentPackHash = active.ContentPackHash, ContentDefinitions = active.ContentDefinitions,
                    RuleConfiguration = configuration, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                    Phase = GamePhase.ActionRound1, LaunchPlayerOrder = new List<int> { 1, 2, 3 },
                    Players = Enumerable.Range(1, 3).Select(id => new PlayerState { PlayerId = id }).ToList() },
                    StaticMapDefinitions.CreateThreePlayerMap(), 17);
                GenericLuaEffectExecutor.Register(Registry); ResourceEffectExecutor.Register(Registry); active.Register(Registry);
                Pack = installed.CreateActiveSnapshot(configuration); Artifacts.RecordContent("R12实际装载展示输入", input, Pack.ContentHash);
                var present = Registry.GameplayPresenter;
                Registry.GameplayPresenter = (string path, GameplayPresentationContext context,
                    out GameplayPresentationResult result, out string diagnostic) => {
                    Contexts.Add(GameplayPresentationNormalizer.Clone(context));
                    return present(path, context, out result, out diagnostic);
                };
                Candidates = Enumerable.Range(0, candidateCount).Select(n => "candidate." + n.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).ToList();
                Registry.Register(ChildType, context => {
                    var answer = context.GetLatestInteractionAnswer();
                    if (answer != null || Candidates.Count == 0) return EffectStepResult.Completed(answer);
                    var request = new EffectInteractionSpec { InteractionTypeId = ChildType, AnsweringPlayerId = 1,
                        Visibility = "owner", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1,
                        Presentation = new GameplayInteractionPresentation { Prompt = new GameplayTextRef(ChildPrompt) } };
                    request.CandidateIds.AddRange(Candidates);
                    return EffectStepResult.Continue("waiting").AddInteraction(request);
                }, EffectExecutorKind.IntrinsicFlow);
                Artifacts.WriteLabeledText("R12隔离边界", "普通独立效果和正式展示Lua；合成子请求仅复现128/288候选规模，不使用旧存档、卡面特例或正常UI覆盖。");
            }

            internal string Start()
            {
                string root = Engine.CreateRoot(EffectSpec.Create(ChildType, playerId: 1)); Pump(); return root;
            }

            internal void Pump()
            {
                var report = Engine.RunUntilQuiescent();
                Assert.That(report.Faulted, Is.False, State.EffectRuntime.LastFaultMessage);
            }
            internal void Answer(string answer)
            {
                var request = Open;
                Assert.That(Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                    NormalizedValue.CreateString(answer), out string reason), Is.True, reason); Pump();
            }
            internal void AssertNoPresentationFailure() => Assert.That(State.EffectRuntime.Diagnostics.Any(d => d.Code == "presentation_failed"),
                Is.False, JsonConvert.SerializeObject(State.EffectRuntime.Diagnostics));
            public void Dispose()
            {
                Artifacts.WriteLabeledText("R12最终状态与展示上下文", JsonConvert.SerializeObject(new { State, Contexts }));
                Artifacts.Finish();
            }
        }
    }
}
