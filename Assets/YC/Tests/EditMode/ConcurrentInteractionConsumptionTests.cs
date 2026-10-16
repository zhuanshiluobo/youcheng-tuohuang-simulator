using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class ConcurrentInteractionConsumptionTests
    {
        private static InteractionRequestProjection Request(int revision = 10) => new InteractionRequestProjection {
            VisibleToViewer = true, Status = "open", InteractionId = "本人企业选择", OwnerEffectId = "本人任务",
            SourceNodeId = "本人任务", AnsweringPlayerId = 2, InteractionTypeId = "setup.enterprise.choose",
            PromptKey = "setup.enterprise.choose", Visibility = "owner", CandidateSetId = "本人候选",
            CandidateSetVersion = 1, CandidateIds = new List<string> { "甲", "乙" },
            MinSelections = 1, MaxSelections = 1, AnswerSchema = "candidate_id", StateRevision = revision
        };

        [Test, Category("多人并发客户端")]
        public void FormalResultsKeepSubmittedGameAndPlayerIdentityAcrossInitialAndOtherConfirmations()
        {
            var registry = new SubmittedInteractionCommands(2);
            var command = EffectInteractionCommands.Answer(Request(), 2, new[] { "乙" });
            var foreignRequest = Request(); foreignRequest.AnsweringPlayerId = 1;
            var foreign = EffectInteractionCommands.Answer(foreignRequest, 1, new[] { "甲" });
            registry.Record("旧局", foreign);
            Assert.That(registry.TryResolve(foreign.CommandId, 1, "旧局", out _), Is.False, "联机默认配置不跟踪他人提交");
            registry.Record("旧局", command);
            Assert.That(registry.TryResolve(string.Empty, 2, "旧局", out _), Is.False, "Initial无命令身份");
            Assert.That(registry.TryResolve("他人命令", 1, "旧局", out _), Is.False);
            Assert.That(registry.TryResolve(command.CommandId, 1, "旧局", out _), Is.False);
            Assert.That(registry.TryResolve(command.CommandId, 2, "新局", out _), Is.False);
            Assert.That(registry.TryResolve(command.CommandId, 2, null, out var resolvedGame), Is.True);
            Assert.That(resolvedGame, Is.EqualTo("旧局"), "Rejected没有GameId时使用实际提交绑定");
            Assert.That(registry.TryResolve(command.CommandId, 2, "旧局", out _), Is.False, "重复结果不得再次消费");
        }

        [Test, Category("热座回执")]
        public void HotseatResultsUseEachSubmittedIdentityAndRejectForeignGamesPlayersAndDuplicates()
        {
            var registry = new SubmittedInteractionCommands(1, controlsCurrentPlayerLocally: true);
            var firstRequest = Request(); firstRequest.AnsweringPlayerId = 1;
            var first = EffectInteractionCommands.Answer(firstRequest, 1, new[] { "甲" });
            var second = EffectInteractionCommands.Answer(Request(), 2, new[] { "乙" });
            registry.Record("热座局", first); registry.Record("热座局", second);
            first.PlayerId = 3; second.PlayerId = 1;
            registry.Record("覆盖局", second);
            Assert.That(registry.TryResolve(string.Empty, 2, "热座局", out _), Is.False);
            Assert.That(registry.TryResolve(second.CommandId, 1, "热座局", out _), Is.False);
            Assert.That(registry.TryResolve(second.CommandId, 2, "覆盖局", out _), Is.False);
            Assert.That(registry.TryResolve(second.CommandId, 2, "热座局", out var secondGame), Is.True);
            Assert.That(secondGame, Is.EqualTo("热座局"));
            Assert.That(registry.TryResolve(second.CommandId, 2, null, out _), Is.False);
            Assert.That(registry.TryResolve(first.CommandId, 3, "热座局", out _), Is.False);
            Assert.That(registry.TryResolve(first.CommandId, 1, null, out var firstGame), Is.True);
            Assert.That(firstGame, Is.EqualTo("热座局"));
        }

        [Test, Category("多人并发客户端")]
        public void UnknownPendingNeverRetriesAndExactStaleRejectionRebuildsAtMostThreeFreshTokens()
        {
            var request = Request();
            var command = EffectInteractionCommands.Answer(request, 2, new[] { "乙" });
            var intent = new InteractionCommandIntent();
            Assert.That(intent.Begin("局", request, command, true, "原费用"), Is.True);
            Assert.That(intent.TryRebuild("局", Request(11), "原费用", out _), Is.False);
            Assert.That(intent.HasPending, Is.True, "更高revision不代表本人命令成功");
            Assert.That(intent.Resolve("局", "他人命令", true, CommandErrorCode.None), Is.False);
            Assert.That(intent.Resolve("他局", command.CommandId, false, CommandErrorCode.StateRevisionMismatch), Is.False);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.That(intent.Resolve("局", command.CommandId, false, CommandErrorCode.StateRevisionMismatch), Is.True);
                Assert.That(intent.TryRebuild("局", Request(10 + attempt), "原费用", out _), Is.False, "相同token不得循环");
                Assert.That(intent.TryRebuild("局", Request(11 + attempt), "原费用", out var rebuilt), Is.True);
                Assert.That(rebuilt.CommandId, Is.Not.EqualTo(command.CommandId));
                Assert.That(rebuilt.OptionIds, Is.EqualTo(new[] { "乙" }));
                Assert.That(rebuilt.Parameters["expectedRevision"], Is.EqualTo((11 + attempt).ToString()));
                command = rebuilt;
            }
            Assert.That(intent.Resolve("局", command.CommandId, false, CommandErrorCode.StateRevisionMismatch), Is.True);
            Assert.That(intent.TryRebuild("局", Request(14), "原费用", out _), Is.False);
            Assert.That(intent.IsBusy, Is.False);
            command = EffectInteractionCommands.Answer(Request(), 2, new[] { "乙" });
            Assert.That(intent.Begin("局", Request(), command, true), Is.True);
            Assert.That(intent.Resolve("局", command.CommandId, false, CommandErrorCode.InvalidTarget), Is.True);
            Assert.That(intent.TryRebuild("局", Request(11), string.Empty, out _), Is.False, "普通拒绝不重试");
        }

        [Test, Category("多人并发客户端")]
        public void ChangedOwnerCandidatesAnswerConstraintsOrCostsStopConfirmedIntentRebuild()
        {
            var changes = new Action<InteractionRequestProjection>[] {
                value => value.OwnerEffectId = "其他任务", value => value.CandidateSetId = "替换候选",
                value => value.CandidateSetVersion++, value => value.AnsweringPlayerId = 1,
                value => value.CandidateIds.Remove("乙"), value => value.MaxSelections = 2,
                value => value.AnswerSchema = "candidate_ids", value => value.Status = "answered",
                value => value.PromptParameters = NormalizedValue.CreateString("事实变化") };
            foreach (var change in changes)
            {
                var original = Request(); var next = Request(11); change(next);
                var command = EffectInteractionCommands.Answer(original, 2, new[] { "乙" });
                var intent = new InteractionCommandIntent(); intent.Begin("局", original, command, true, "原费用");
                intent.Resolve("局", command.CommandId, false, CommandErrorCode.StateRevisionMismatch);
                Assert.That(intent.TryRebuild("局", next, "原费用", out _), Is.False);
                Assert.That(intent.IsBusy, Is.False);
            }
            var legal = new InteractionCommandIntent(); var old = Request();
            var submitted = EffectInteractionCommands.Answer(old, 2, new[] { "乙" });
            legal.Begin("局", old, submitted, true, "原费用");
            legal.Resolve("局", submitted.CommandId, false, CommandErrorCode.StateRevisionMismatch);
            Assert.That(legal.TryRebuild("局", Request(11), "新费用", out _), Is.False);
            var route = Request(); route.InteractionTypeId = "route_resource.selection";
            route.PromptParameters = NormalizedValue.CreateObject(new List<NormalizedValueEntry> {
                new NormalizedValueEntry { Name = "requestRevision", Value = NormalizedValue.CreateInteger(10) },
                new NormalizedValueEntry { Name = "scopeKey", Value = NormalizedValue.CreateString("原scope") } });
            var nextRoute = Request(11); nextRoute.InteractionTypeId = route.InteractionTypeId;
            nextRoute.PromptParameters = route.PromptParameters.Clone();
            nextRoute.PromptParameters.Properties.Single(value => value.Name == "requestRevision").Value = NormalizedValue.CreateInteger(11);
            Assert.That(InteractionCommandIntent.SameContract("局", route, "局", nextRoute), Is.True);
            nextRoute.PromptParameters.Properties.Single(value => value.Name == "scopeKey").Value = NormalizedValue.CreateString("新scope");
            Assert.That(InteractionCommandIntent.SameContract("局", route, "局", nextRoute), Is.False);
            var extra = new InteractionCommandIntent(); extra.Begin("局", old, submitted, false);
            extra.Resolve("局", submitted.CommandId, false, CommandErrorCode.StateRevisionMismatch);
            Assert.That(extra.TryRebuild("局", Request(11), string.Empty, out _), Is.False, "未经批准用途不自动重建");
        }

        [Test, Category("多人并发客户端")]
        public void EnterpriseRefreshKeepsLegalSelectionAndUnknownPendingUntilAccurateCompletion()
        {
            EnterpriseSelectionProjection Projection(int revision, string game = "局") => new EnterpriseSelectionProjection {
                GameId = game, ViewerPlayerId = 2, RequestId = "企业选择", OwnerEffectId = "任务",
                CandidateSetId = "候选", CandidateSetVersion = 1, RequestContract = "合同", Revision = revision,
                SubmissionAvailable = true, Boards = new[] { new EnterpriseBoardProjection { Id = "板" } },
                Targets = new[] { new EnterpriseSelectionTarget { Id = "乙", BoardId = "板", CompanyId = "板", Available = true } }
            };
            var draft = new EnterpriseSelectionDraft(); draft.Refresh(Projection(10)); draft.Select("乙");
            Assert.That(draft.TryBeginSubmit(out var submitted), Is.True);
            Assert.That(draft.Refresh(Projection(11)), Is.True);
            Assert.That(draft.SelectedIds, Is.EqualTo(new[] { "乙" }));
            Assert.That(draft.IsPending, Is.True);
            Assert.That(draft.TryBeginSubmit(out _), Is.False);
            Assert.That(draft.Complete(submitted), Is.True, "准确意图结果可跨观察revision解除等待");
            Assert.That(draft.CanConfirm, Is.True);
            Assert.That(EnterpriseSelectionDraft.SameContract(Projection(11), Projection(12)), Is.True);
            draft.Refresh(Projection(12, "新局"));
            Assert.That(draft.SelectedIds, Is.Empty);
            Assert.That(draft.Complete(submitted), Is.False);
        }

        [Test, Category("多人并发客户端")]
        public void NormalCollectionBindingRefreshKeepsRoutesRecipientsDeselectionsAndStartBudget()
        {
            var map = new GameMapDefinition { MapId = "草稿地图", MinPlayers = 2, MaxPlayers = 3,
                Locations = new List<MapLocationDefinition> {
                    new MapLocationDefinition { LocationId = "S", InfluenceSlotCount = 2, CanDockCity = true },
                    new MapLocationDefinition { LocationId = "T", InfluenceSlotCount = 2, CanDockCity = true } },
                Routes = new List<MapRouteDefinition> { new MapRouteDefinition { RouteId = "路", FromLocationId = "S", ToLocationId = "T",
                    CoveredLocationIds = new List<string> { "S", "T" }, InfluenceSlotCount = 2, RegionId = "区域" } } };
            var state = new GameState { GameId = "局", MapId = map.MapId, Round = 1, Phase = GamePhase.ResourceCollection, CurrentPlayerId = -1,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, CityLocationId = "S",
                    Resources = new ResourceSet { GoldVoucher = 20 }, ResourceCollectionStartGoldVoucher = 20 }, new PlayerState { PlayerId = 2 } } };
            var owner = new EffectNodeRuntimeState { EffectId = "本人采集", PlayerId = 1, EffectTypeId = RouteResourceSelectionEffectExecutor.TypeId };
            state.EffectRuntime.EffectNodes.Add(owner);
            var mapQuery = new MapQueryService(map); var authority = new RouteSelectionSessionAuthority(mapQuery);
            var options = new RouteSelectionOptions(RouteSelectionPurpose.NormalHarvest, 1, "S", new[] { "S", "T" }, 0, 2);
            Assert.That(authority.TryOpen(state, owner, options, out var session).IsValid, Is.True);
            Assert.That(authority.TryPrepareRequest(state, owner, session, options.SelectableLocationUpperBound, out var binding, out var candidateIds).IsValid, Is.True);
            var presenter = new RouteResourceSelectionPresenter(() => state, new RouteResourceSelectionService(mapQuery));
            presenter.Begin(session, binding, options.SelectableLocationUpperBound);
            Assert.That(presenter.SelectRoute("路"), Is.Not.Null);
            Assert.That(presenter.ConfirmRecipient("路", "bank"), Is.True);
            Assert.That(presenter.Draft.SelectedLocationIds, Does.Contain("T"));
            Assert.That(presenter.ToggleLocation("T"), Is.True);
            var before = JsonConvert.SerializeObject(presenter.Draft); var version = presenter.Draft.LocalDraftVersion;
            var record = owner.ContinuationData.Properties.Single(value => value.Name == "routeSelection").Value;
            record.Properties.Single(value => value.Name == "requestRevision").Value = NormalizedValue.CreateInteger(binding.RequestStateRevision + 1);
            state.EffectRuntime.StateRevision = binding.RequestStateRevision + 1;
            Assert.That(authority.TryRestore(state, owner, out var fresh, out var refreshedBinding).IsValid, Is.True);
            Assert.That(presenter.TryRefreshOpenBinding(fresh, refreshedBinding, options.SelectableLocationUpperBound, out _), Is.True);
            Assert.That(JsonConvert.SerializeObject(presenter.Draft), Is.EqualTo(before));
            Assert.That(presenter.Draft.LocalDraftVersion, Is.EqualTo(version));
            Assert.That(presenter.Draft.SelectedLocationIds, Does.Not.Contain("T"));
            Assert.That(presenter.Draft.RecipientChoices.Single().ReceiverSubjectKey, Is.EqualTo("bank"));
            Assert.That(presenter.Session.StartGoldVoucherAmount, Is.EqualTo(20));
            Assert.That(presenter.RequestBinding.RequestStateRevision, Is.EqualTo(state.EffectRuntime.StateRevision));
            Assert.That(presenter.TryValidateConfirmedIntent(out var hostPlanDigest), Is.True);
            var projectedRequest = new InteractionRequest {
                InteractionId = "本人采集请求", RequestId = "本人采集请求", SourceNodeId = owner.EffectId,
                OwnerEffectId = owner.EffectId, AnsweringPlayerId = 1, InteractionTypeId = "route_resource.selection",
                Visibility = "owner", PromptKey = "route_resource.selection", PromptParameters = record.Clone(),
                CandidateSetId = refreshedBinding.CandidateSetId, CandidateSetVersion = refreshedBinding.CandidateSetVersion,
                CandidateIds = candidateIds.ToList(), MinSelections = 1, MaxSelections = 4,
                AnswerSchema = "candidate_ids", StateRevision = state.EffectRuntime.StateRevision
            };
            state.EffectRuntime.InteractionRequests.Add(projectedRequest);
            var playerView = GameStateViewProjector.ProjectForPlayer(state, 1);
            var visibleRequest = playerView.Interactions.Single(value => value.InteractionId == projectedRequest.InteractionId);
            Assert.That(authority.TryRead(playerView, visibleRequest, out var memberSession, out var memberBinding).IsValid, Is.True);
            var member = GameStateViewProjector.ToClientState(playerView);
            Assert.That(member.EffectRuntime, Is.Null, "成员状态必须来自正式投影，不补造Host运行态");
            var memberPresenter = new RouteResourceSelectionPresenter(() => member, new RouteResourceSelectionService(mapQuery));
            memberPresenter.Begin(memberSession, memberBinding, options.SelectableLocationUpperBound);
            Assert.That(memberPresenter.SelectRoute("路"), Is.Not.Null);
            Assert.That(memberPresenter.ConfirmRecipient("路", "bank"), Is.True);
            Assert.That(memberPresenter.ToggleLocation("T"), Is.True);
            Assert.That(JsonConvert.SerializeObject(memberPresenter.Draft), Is.EqualTo(before));
            var memberBefore = JsonConvert.SerializeObject(member);
            Assert.That(memberPresenter.TryValidateConfirmedIntent(out var memberPlanDigest), Is.True);
            Assert.That(memberPlanDigest, Is.EqualTo(hostPlanDigest));
            Assert.That(JsonConvert.SerializeObject(member), Is.EqualTo(memberBefore), "确认意图复核只能读成员状态");
            member.FindPlayer(1).HasCollectedResourcesThisRound = true;
            var changedFactsBefore = JsonConvert.SerializeObject(member);
            Assert.That(memberPresenter.TryValidateConfirmedIntent(out var changedFactsPlan), Is.False);
            Assert.That(changedFactsPlan, Is.Null);
            Assert.That(JsonConvert.SerializeObject(member), Is.EqualTo(changedFactsBefore), "事实变化拒绝也不能写状态");
            member.FindPlayer(1).HasCollectedResourcesThisRound = false;
            Assert.That(memberPresenter.ToggleLocation("T"), Is.True);
            Assert.That(memberPresenter.TryValidateConfirmedIntent(out _), Is.True);
            member.FindPlayer(1).Resources.GoldVoucher = 0;
            var insufficientBudgetBefore = JsonConvert.SerializeObject(member);
            Assert.That(memberPresenter.TryValidateConfirmedIntent(out var insufficientBudgetPlan), Is.False);
            Assert.That(insufficientBudgetPlan, Is.Null);
            Assert.That(JsonConvert.SerializeObject(member), Is.EqualTo(insufficientBudgetBefore), "预算不足拒绝也不能写状态");
            record.Properties.Single(value => value.Name == "scopeKey").Value = NormalizedValue.CreateString(new string('a', 64));
            Assert.That(authority.TryRestore(state, owner, out fresh, out refreshedBinding).IsValid, Is.True);
            Assert.That(presenter.TryRefreshOpenBinding(fresh, refreshedBinding, options.SelectableLocationUpperBound, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(presenter.Draft), Is.EqualTo(before));
        }

        [Test, Category("多人并发客户端")]
        public void ParallelNormalCoverUsesOwnOpenRequestAndHandIntersectionWithoutGrantingActionTurn()
        {
            CharacterCardDatabase.Initialize(ExternalContentPack.Current.CreateCharacters());
            var configuration = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.ThreePlayerMapId };
            var catalog = ActiveContentCatalogBuilder.Build(ExternalContentPack.Current, configuration);
            configuration.ContentPackHash = catalog.ContentPackHash;
            var state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, new List<PlayerSeat> {
                new PlayerSeat { PlayerId = 1, Color = PlayerColor.Red }, new PlayerSeat { PlayerId = 2, Color = PlayerColor.Blue },
                new PlayerSeat { PlayerId = 3, Color = PlayerColor.Green } }, configuration, 71,
                catalog.SetupRecipe, catalog.ContentDefinitions);
            var effects = new EffectRegistry(); LuaContentCatalog.Register(effects, catalog);
            Assert.That(new RoundExecutionService(effects).CreateRound(state, 1, 1).IsValid, Is.True);
            Assert.That(state.CurrentPlayerId, Is.EqualTo(-1));
            var visible = GameStateViewProjector.ProjectForPlayer(state, 2);
            var member = GameStateViewProjector.ToClientState(visible);
            var presenter = new CharacterCardPanelPresenter(); var model = presenter.BuildProjectedView(member, 2, visible);
            Assert.That(model.CurrentCoverSelection, Is.Not.Null);
            Assert.That(model.CanCover, Is.True);
            Assert.That(model.HandCards.Where(card => card.CanCover).All(card => model.CurrentCoverSelection.CandidateIds.Contains(card.CardId)), Is.True);
            Assert.That(presenter.BuildProjectedView(member, 1, visible).CanCover, Is.False);
            member.Phase = GamePhase.ActionRound1; member.CurrentPlayerId = 1;
            var action = presenter.BuildProjectedView(member, 2, visible);
            Assert.That(action.CanCover, Is.False);
            Assert.That(action.CanUse, Is.False);
        }
    }
}
