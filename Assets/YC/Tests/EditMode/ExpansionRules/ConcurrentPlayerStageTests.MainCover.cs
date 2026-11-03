using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed partial class ConcurrentPlayerStageTests
    {
        // 隔离规则/恢复验证；不计作菜单、建房及正常独立程序验收。
        [Test]
        public void MainCoverBusinessOwnsSelectionAndExplicitBodyOwnsReceiptAcrossRestore()
        {
            var fixture = Create(registerExtra: registry => Hold(registry, "CharacterCardCovered"));
            StartCover(fixture);
            var task = Node(fixture.State, CharacterCoverEffectExecutor.EffectTypeId, 1);
            var body = MainCoverAuthority.Body(fixture.State, task);
            Assert.That(body, Is.Null);
            var request = fixture.Request(CharacterCoverEffectExecutor.InteractionTypeId, 1);
            Assert.That(request.OwnerEffectId, Is.EqualTo(task.EffectId));Assert.That(task.ChildEffectIds,Is.Empty);
            Assert.That(task.PositionChanges, Is.Empty);
            string selected = request.CandidateIds.Last();
            fixture.Restore();
            fixture.Submit(fixture.Request(CharacterCoverEffectExecutor.InteractionTypeId, 1), new[] { selected });
            body = MainCoverAuthority.Body(fixture.State,Node(fixture.State,CharacterCoverEffectExecutor.EffectTypeId,1));
            Assert.That(body,Is.Not.Null);Assert.That(fixture.State.EffectRuntime.InteractionRequests.Where(r=>r.OwnerEffectId==body.EffectId),Is.Empty);
            Assert.That(body.CharacterCoverMovementV4.Receipt.Result.MovedItemIds, Is.EqualTo(new[] { selected }));
            Assert.That(body.Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Assert.That(Node(fixture.State, CharacterCoverEffectExecutor.EffectTypeId, 1).Status, Is.EqualTo(EffectNodeStatus.Blocked));
            var covered = fixture.State.EffectRuntime.RuleEvents.Single(e => e.EventType == "CharacterCardCovered");
            Assert.That(covered.OwnerNodeId, Is.EqualTo(body.EffectId));
            Assert.That(Field(covered.Payload, "cardInstanceRef").ReferenceId, Is.EqualTo("hidden"));
            Assert.That(Field(covered.HostOnlyPayload, "cardInstanceRef").ReferenceId, Is.EqualTo(selected));
            Assert.That(body.CharacterCoverMovementV4.Receipt.EventIds, Is.EqualTo(new[] { covered.EventId }));
            string receipt = body.CharacterCoverMovementV4.Receipt.ReceiptId;
            fixture.Restore();
            fixture.Submit(fixture.Request(HoldType, 1));
            body = fixture.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == body.EffectId);
            Assert.That(body.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(body.CharacterCoverMovementV4.Receipt.ReceiptId, Is.EqualTo(receipt));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "CharacterCardCovered"), Is.EqualTo(1));
            fixture.Restore();
        }

        [TestCase(false)][TestCase(true)]
        public void MainCover_ExistingCoveredCardCompletesWithZeroReceiptWithoutDuplicateEvent(bool journal)
        {
            var fixture = Create();
            // 隔离准备上一轮真实盖牌；不计正常流程验收。
            var executor = new EffectTreeExecutor(fixture.State, fixture.Registry);
            executor.CreateRoot(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1));
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            var request = fixture.Request(CharacterCoverEffectExecutor.InteractionTypeId, 1);
            string selected = request.CandidateIds[0];
            Assert.That(executor.TrySubmitInteraction(request.InteractionId, 1, request.StateRevision,
                NormalizedValue.CreateString(selected), out var reason), Is.True, reason);
            Assert.That(executor.RunUntilQuiescent().Faulted, Is.False);
            int events = fixture.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "CharacterCardCovered");
            var archive = HostRecoveryService.CreateArchive(fixture.State);
            StartCover(fixture);
            var task = fixture.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == CharacterCoverEffectExecutor.EffectTypeId && n.PlayerId == 1 && n.FlowStage == "main_unchanged" && n.ParentEffectId.Length > 0);
            var body = MainCoverAuthority.Body(fixture.State, task);
            Assert.That(body,Is.Null);Assert.That(task.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(task.MainCoverFlowV1.NoChangeReceipt.Result.Changed, Is.False);
            Assert.That(task.MainCoverFlowV1.NoChangeReceipt.Items, Is.Empty);
            Assert.That(task.MainCoverFlowV1.NoChangeReceipt.EventIds, Is.Empty);
            Assert.That(fixture.State.FindPlayer(1).CoveredCharacterCardId, Is.EqualTo(selected));
            Assert.That(fixture.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "CharacterCardCovered"), Is.EqualTo(events));
            if (journal) HostRecoveryService.AppendJournal(archive, fixture.State, fixture.State.ContentPackHash);
            else archive = HostRecoveryService.CreateArchive(fixture.State);
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), fixture.Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(recovered.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == task.EffectId).MainCoverFlowV1.NoChangeReceipt.ReceiptId,
                Is.EqualTo(task.MainCoverFlowV1.NoChangeReceipt.ReceiptId));
        }

        [TestCase("task")][TestCase("flow")][TestCase("slot")][TestCase("event")][TestCase("old_wrapper")][TestCase("old_body")]
        public void MainCover_RecoveryRejectsAlteredCommonFactsWithoutRewritingArchive(string fault)
        {
            var fixture = Create(registerExtra: registry => Hold(registry, "CharacterCardCovered"));
            StartCover(fixture);
            fixture.Submit(fixture.Request(CharacterCoverEffectExecutor.InteractionTypeId, 1));
            var task = Node(fixture.State, CharacterCoverEffectExecutor.EffectTypeId, 1);
            var body = MainCoverAuthority.Body(fixture.State, task);
            string clean = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(fixture.State));
            var raw = JObject.Parse(clean);
            var runtime = raw["Snapshot"]["State"]["EffectRuntime"];
            var saved = runtime["EffectNodes"].Single(n => (string)n["EffectId"] == body.EffectId);
            var savedTask = runtime["EffectNodes"].Single(n => (string)n["EffectId"] == task.EffectId);
            if (fault == "task") saved["CharacterCoverMovementV4"]["MainTaskEffectId"] = "foreign";
            if (fault == "flow") ((JObject)savedTask).Property("MainCoverFlowV1").Remove();
            if (fault == "slot") saved["CharacterCoverMovementV4"]["SlotAfter"]["Reusable"] = true;
            if (fault == "event") runtime["RuleEvents"].Single(e => (string)e["EventType"] == "CharacterCardCovered")["HostOnlyPayload"] = null;
            if (fault == "old_wrapper") savedTask["DefinitionVersion"] = "2.0.0";
            if (fault == "old_body") saved["DefinitionVersion"] = "4.6.0";
            string bad = raw.ToString();
            Assert.That(HostRecoveryService.RecoverJson(bad, fixture.Registry).Succeeded, Is.False, fault);
            Assert.That(raw.ToString(), Is.EqualTo(bad));
            Assert.That(HostRecoveryService.RecoverJson(clean, fixture.Registry).Succeeded, Is.True);
        }

        [TestCase(false)][TestCase(true)]
        public void MainCover_PublicManualSpecCannotBorrowHostTaskAuthorityWithPermissiveCatalog(bool nested)
        {
            var leaf = LuaEffectSpec.FromArguments(CharacterCoverEffectExecutor.EffectTypeId,
                Object("playerId", NormalizedValue.CreateInteger(1)));
            var spec = nested ? LuaEffectSpec.FromEffectGroups(EffectTypeIds.Condition, Object(),
                new IReadOnlyList<LuaEffectSpec>[] { new[] { leaf }, Array.Empty<LuaEffectSpec>() }) : leaf;
            var success = typeof(LuaInvocationResult).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(method => method.Name == "Success" && method.GetParameters().Length == 2);
            var script = new LuaScriptDefinition("test.main", "test", "test", "1.0.0", "return function(ctx) return {} end", "test");
            var invocation = (LuaInvocationResult)success.Invoke(null, new object[] { script, new[] { spec } });
            var catalog = new LuaEffectTypeCatalog();
            var entries = (Dictionary<string, LuaEffectTypeRegistration>)typeof(LuaEffectTypeCatalog)
                .GetField("registrations", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(catalog);
            entries[CharacterCoverEffectExecutor.EffectTypeId] = new LuaEffectTypeRegistration(CharacterCoverEffectExecutor.EffectTypeId, null, item => item.RawArguments.Clone());
            Assert.That(new LuaEffectSpecCompiler(catalog).TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1 }, out _, out var fault), Is.False);
            Assert.That(fault.Diagnostic, Does.Contain(CharacterCoverEffectExecutor.PublicNormalCoverReason));
        }
    }
}
