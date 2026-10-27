using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Rules;
using YC.Domain.Interactions;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class QY008DeferredCleanupEntryR7Tests
    {
        [TestCase("source")]
        [TestCase("optional")]
        [TestCase("dispatch")]
        [TestCase("version")]
        [TestCase("completion")]
        public void RecreatedArchiveRejectsForgedDeferredEntryEvidence(string mutation)
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "2");
            f.Invoke("entrepreneur.A08.tactic.quick"); f.ResolveMapRequests(); f.Cleanup();
            if (mutation == "completion") { f.StartCleanupObligation(); f.ResolveMapRequests(); }
            var archive = HostRecoveryService.CreateArchive(f.State);
            var state = archive.Snapshot.State;
            var duty = state.EffectRuntime.CleanupObligations.Single();
            var entry = state.EffectRuntime.EffectNodes.Where(n => n.SettlementWindow != null)
                .SelectMany(n => n.SettlementWindow.Entries).Single(e => e.LocalId == CleanupObligation.SettlementLocalId);
            if (mutation == "source") entry.SourceEffectId = "forged-schedule";
            else if (mutation == "optional") entry.WholeEffectPolicy = SettlementWholeEffectPolicy.Optional;
            else if (mutation == "dispatch") state.EffectRuntime.DispatchReceipts.RemoveAll(r => r.HandlerId == "cleanup.obligation.due");
            else if (mutation == "version") duty.HandlerHash += ".forged";
            else duty.CompletionReceiptId += ".forged";
            // 从合法归档副本篡改内部证据，直接覆盖实际反序列化与恢复门禁。
            string json = HostRecoveryService.SerializeArchive(archive);
            f.Artifacts.WriteLabeledText("R7伪造义务证据输入-" + mutation, json);
            var recovered = HostRecoveryService.RecoverJson(json, f.Registry);
            f.Artifacts.WriteLabeledText("R7伪造义务证据结果-" + mutation,
                JsonConvert.SerializeObject(new { recovered.Succeeded, recovered.Diagnostic }));
            Assert.That(recovered.Succeeded, Is.False);
            if (mutation == "source") Assert.That(recovered.Diagnostic, Is.EqualTo("cleanup_obligation_settlement_origin_invalid"));
        }

        [Test]
        public void FormalCleanupCatalogRetainsSevenOptionalSourcesAndNoStandingDeferredSource()
        {
            using var f = new AgnesContentTestFixture(); f.Formal();
            string[] expected = { "entrepreneur.T09.cleanup", "entrepreneur.D09.investment", "entrepreneur.D01.exchange", "entrepreneur.D02.exchange",
                "entrepreneur.A05.cleanup", "entrepreneur.P03.cleanup", "entrepreneur.P04.cleanup" };
            var declarations = f.Registry.AbilityDeclarations.Where(d => d.Kind == "cleanup" && d.AbilityId.StartsWith("entrepreneur.", StringComparison.Ordinal)).ToArray();
            Assert.That(declarations.Select(d => d.AbilityId).OrderBy(id => id), Is.EqualTo(expected.OrderBy(id => id)));
            foreach (var declaration in declarations)
            {
                Assert.That(f.Registry.TryGetSettlementSourceContract(declaration.AbilityId, out var contract), Is.True);
                Assert.That(contract.EntryKind, Is.EqualTo(SettlementEntryService.CleanupOrder));
                Assert.That(contract.WholeEffectPolicy, Is.EqualTo(SettlementWholeEffectPolicy.Optional));
                bool repeat = new[] { "entrepreneur.T09.cleanup", "entrepreneur.D01.exchange", "entrepreneur.D02.exchange" }.Contains(declaration.AbilityId);
                Assert.That(declaration.RepeatPolicy, Is.EqualTo(repeat ? "unbounded" : "once"));
                Assert.That(contract.AvailabilityHandlerId.Length > 0, Is.True);
            }
            Assert.That(f.Registry.TryGetSettlementSourceContract(CleanupObligation.InvokeType, out var deferred), Is.True);
            Assert.That(deferred.WholeEffectPolicy, Is.EqualTo(SettlementWholeEffectPolicy.Required));
            Assert.That(f.Registry.AbilityDeclarations.Any(d => d.AbilityId == CleanupObligation.InvokeType), Is.False);
            Assert.That(f.Registry.QuerySources(f.State, 1).Any(s => s.AbilityId == CleanupObligation.InvokeType), Is.False);
            f.Artifacts.WriteLabeledText("R7七原来源与独立必做合同", JsonConvert.SerializeObject(new { declarations, deferred }));
        }

        private static GameSession Session(AgnesContentTestFixture f)
        {
            var session = new GameSession(f.State);
            session.RegisterHandler(new InvokeSettlementOptionCommandHandler(f.Registry));
            session.RegisterHandler(new AnswerInteractionCommandHandler(f.Registry));
            session.RegisterHandler(new EndActionCommandHandler(new RoundAdvanceService(), null, f.Registry));
            return session;
        }
        private static GameCommand Invoke(string option, int player, int revision)
        {
            var command = new GameCommand { Kind = GameCommandKind.InvokeSettlementOption, PlayerId = player };
            command.Parameters["optionId"] = option; command.Parameters["useId"] = "";
            command.Parameters["expectedRevision"] = revision.ToString(CultureInfo.InvariantCulture);
            return command;
        }

        [Test]
        public void RealA08RegistersRequiredOptionAndOnlyItsCurrentCommandStartsRemoval()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "2");
            f.Invoke("entrepreneur.A08.tactic.quick"); f.ResolveMapRequests();
            Assert.That(f.OwnMapCount, Is.EqualTo(2)); f.Cleanup();
            var duty = f.State.EffectRuntime.CleanupObligations.Single();
            var option = f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.LocalId == CleanupObligation.SettlementLocalId);
            var window = SettlementHostProtocol.FindActiveWindow(f.State);
            var target = f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == option.TargetEffectId);
            Assert.That(window.Context.Kind, Is.EqualTo(SettlementEntryService.PlayerCleanup));
            Assert.That(option.WindowId, Is.EqualTo(window.Context.Id));
            Assert.That(target.ParentEffectId, Is.EqualTo(window.Context.OwnerEffectId));
            Assert.That(ItemTransferEffectExecutor.Text(target.NormalizedArguments, "obligationId"), Is.EqualTo(duty.ObligationId));
            Assert.That(target.Status, Is.EqualTo(EffectNodeStatus.Created)); Assert.That(duty.Status, Is.EqualTo("pending"));
            Assert.That(option.IsEnabled, Is.True, option.DisabledReasonCode); Assert.That(option.CanDeclineWholeEffect, Is.False);
            Assert.That(f.OwnMapCount, Is.EqualTo(2), "登记不能自动执行义务正文");
            f.Artifacts.WriteLabeledText("R7真实必做条目及原窗口", JsonConvert.SerializeObject(new { option, window, duty, state = f.State }));

            var session = Session(f); string before = JsonConvert.SerializeObject(f.State);
            Assert.That(session.Submit(Invoke(option.OptionId, 2, option.StateRevision)).Succeeded, Is.False);
            Assert.That(session.Submit(Invoke(option.OptionId, 1, option.StateRevision - 1)).Succeeded, Is.False);
            var close = new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = 1 };
            close.Parameters["windowId"] = option.WindowId; close.Parameters["expectedRevision"] = option.StateRevision.ToString(CultureInfo.InvariantCulture);
            Assert.That(session.Submit(close).Succeeded, Is.False);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            f.Restore("R7必做条目尚未选择恢复");
            option = f.Registry.GetSettlementOptions(f.State, 1).Single(o => o.LocalId == CleanupObligation.SettlementLocalId);
            session = Session(f); var command = Invoke(option.OptionId, 1, option.StateRevision);
            var result = session.Submit(command); Assert.That(result.Succeeded, Is.True, result.Validation?.Reason); f.Pump();
            var request = f.Open; Assert.That(request.AllowDecline, Is.False);
            Assert.That(f.OwnMapCount, Is.EqualTo(2));
            before = JsonConvert.SerializeObject(f.State);
            string inventoryBeforeRetry = JsonConvert.SerializeObject(f.State.ItemZones), playersBeforeRetry = JsonConvert.SerializeObject(f.State.Players);
            string obligationBeforeRetry = JsonConvert.SerializeObject(f.State.EffectRuntime.CleanupObligations);
            f.Artifacts.WriteLabeledText("R7重复请求前原状态", before);
            Assert.That(session.Submit(command).Succeeded, Is.True, "相同命令ID只复用受理结果");
            f.Artifacts.WriteLabeledText("R7重复请求后原状态", JsonConvert.SerializeObject(f.State));
            // 旧通用结算重复受理仍会刷新命令上下文/记无动作日志；不能把它误称整份state零提交。
            Assert.That(JsonConvert.SerializeObject(f.State.ItemZones), Is.EqualTo(inventoryBeforeRetry));
            Assert.That(JsonConvert.SerializeObject(f.State.Players), Is.EqualTo(playersBeforeRetry));
            Assert.That(JsonConvert.SerializeObject(f.State.EffectRuntime.CleanupObligations), Is.EqualTo(obligationBeforeRetry));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == CleanupObligation.InvokeType), Is.EqualTo(1));
            var answer = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { request.CandidateIds.First() });
            f.Restore("R7义务待答保存恢复"); session = Session(f);
            Assert.That(session.Submit(answer).Succeeded, Is.True); f.Pump();
            Assert.That(f.OwnMapCount, Is.EqualTo(1)); Assert.That(f.State.EffectRuntime.CleanupObligations.Single().Status, Is.EqualTo("completed"));
            f.Restore("R7完成收据恢复"); session = Session(f); before = JsonConvert.SerializeObject(f.State);
            var stale = EffectInteractionCommands.Answer(InteractionRequestProjector.ProjectForPlayer(request, 1), 1, new[] { request.CandidateIds.First() });
            Assert.That(session.Submit(stale).Succeeded, Is.False);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == CleanupObligation.InvokeType), Is.EqualTo(1));
            f.Artifacts.WriteLabeledText("R7正式结算命令与完成收据", JsonConvert.SerializeObject(new { command, result, answer, state = f.State }));
        }
    }
}
