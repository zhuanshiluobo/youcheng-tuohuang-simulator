using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    /// <summary>正式选面/立即调用链的隔离回归，不计正常Player或完整对局验收。</summary>
    public sealed class ImmediateEntrySourceFactsR5Tests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        [Test]
        public void RealActionSourceFactsKeepCommitPhasesAcrossCurrentConfirmation()
        {
            using var f = new AgnesContentTestFixture(); f.Formal(); f.Choose(1, "A08", "2");
            var option = f.Action("entrepreneur.A08.tactic.quick");
            var action = (AbilityActionDeclaration)typeof(EffectRegistry).GetMethod("ActionDeclaration", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(f.Registry, new object[] { option.ActionId });
            string root = f.Engine.CreateRoot(AbilityActionExecutor.Create(option, "qy002.source.phase", Object()));
            bool accepted = false, activated = false; int confirmations = 0;
            for (int step = 0; step < 2000 && !activated; step++)
            {
                var invocation = f.State.EffectRuntime.AbilityActionInvocations.SingleOrDefault(i => i.OwnerEffectId == root);
                if (invocation != null && (invocation.Phase == "accepted" || invocation.Phase == "activated"))
                {
                    var call = new AbilityActionCallContext { State = f.State, OwnerNode = f.Engine.GetNode(root), SourceRef = invocation.SourceRef, Action = action };
                    string before = RuleStateDigestService.Compute(f.State);
                    bool cost = f.Registry.TryResolveSourceFactsCall(call, action.EntryCostRef, f.Registry.GetAbilityHandlerHash(action.EntryCostRef), out _, out bool costCallable);
                    bool body = f.Registry.TryResolveSourceFactsCall(call, action.BodyHandlerRef, f.Registry.GetAbilityHandlerHash(action.BodyHandlerRef), out _, out bool bodyCallable);
                    Assert.That(cost, Is.EqualTo(invocation.Phase == "accepted")); Assert.That(costCallable, Is.False);
                    Assert.That(body, Is.EqualTo(invocation.Phase == "activated")); Assert.That(bodyCallable, Is.EqualTo(body));
                    Assert.That(invocation.ActivationCommitted, Is.EqualTo(body)); Assert.That(f.Stored, Is.EqualTo(body ? 0 : 2));
                    Assert.That(RuleStateDigestService.Compute(f.State), Is.EqualTo(before));
                    call.OwnerNode = GameStateCloneService.DeepClone(call.OwnerNode);
                    Assert.That(f.Registry.TryResolveSourceFactsCall(call, cost ? action.EntryCostRef : action.BodyHandlerRef,
                        f.Registry.GetAbilityHandlerHash(cost ? action.EntryCostRef : action.BodyHandlerRef), out _, out _), Is.False);
                    accepted |= cost; activated |= body;
                    f.Artifacts.WriteLabeledText("当前确认链真实行动阶段", JsonConvert.SerializeObject(new { invocation.Phase, invocation.ActivationCommitted, cost, body, f.Stored }));
                    if (activated) break;
                }
                if (f.Engine.Advance()) continue;
                Assert.That(f.State.EffectRuntime.LastFaultMessage, Is.Empty);
                var request = f.State.EffectRuntime.InteractionRequests.Single(q => q.Status == "open");
                Assert.That(InfluenceDepartureProtocol.IsWithin(f.State, request.OwnerEffectId, root), Is.True);
                Assert.That(request.InteractionTypeId, Is.EqualTo("action.decline_effect"));
                Assert.That(request.CandidateIds, Does.Contain("continue"));
                // 只回答当前正式确认；不Pump跨过激活提交点，下一步仍核实际收据阶段。
                Assert.That(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                    NormalizedValue.CreateString("continue"), out string reason), Is.True, reason);
                confirmations++;
            }
            Assert.That(accepted, Is.True); Assert.That(activated, Is.True); Assert.That(confirmations, Is.EqualTo(1));
            f.Pump(); f.ResolveMapRequests();
            Assert.That(f.Stored, Is.Zero); Assert.That(f.OwnMapCount, Is.EqualTo(2));
        }

        [Test]
        public void ActualImmediateCallRejectsForgedAndExpiredAuthority()
        {
            using var f = new PaulContentTestFixture(primaryEntrepreneur: "entrepreneur.ability.M13", setupOnly: true);
            FacilityEntryEffectExecutor.Register(f.Registry);
            const string handlerId = "entrepreneur.M07.immediate.1";
            var bodies = (Dictionary<string, Func<AbilityActionCallContext, IList<EffectSpec>>>)typeof(EffectRegistry)
                .GetField("abilityBodies", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.Registry);
            var original = bodies[handlerId];
            var checkedVariants = new List<string>();
            bodies[handlerId] = call =>
            {
                string hash = f.Registry.GetAbilityHandlerHash(handlerId);
                Assert.That(f.Registry.TryResolveSourceFactsCall(call, handlerId, hash, out var current, out var callable), Is.True);
                Assert.That(current.SourceKey, Is.EqualTo(call.SourceRef.SourceKey));
                Assert.That(callable, Is.False, "立即读取事实不增加主动调用权限");
                var content = f.Pack.Definitions.Single(d => d.DefinitionId == current.DefinitionId);
                var handler = content.AbilityHandlers.Single(h => h.HandlerId == handlerId);
                foreach (string damage in new[] { "copied_owner", "missing_entry", "wrong_entry", "wrong_parent", "wrong_source_key",
                    "wrong_player", "wrong_declaration", "wrong_executor_version", "terminal_entry", "stale_source", "missing_selection_receipt",
                    "wrong_handler", "wrong_hash", "wrong_handler_version", "unbound_payload", "detached_descendant" })
                {
                    var state = GameStateCloneService.DeepClone(call.State);
                    var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == call.OwnerNode.EffectId);
                    var candidate = new AbilityActionCallContext { State = state, OwnerNode = owner,
                        SourceRef = GameStateCloneService.DeepClone(call.SourceRef), Arguments = Object() };
                    var group = state.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups.Single(g => g.RoundNumber == 3);
                    void SetArgument(string key, string value) => owner.NormalizedArguments.Properties.Single(p => p.Name == key).Value = NormalizedValue.CreateString(value);
                    if (damage == "copied_owner") candidate.OwnerNode = GameStateCloneService.DeepClone(owner);
                    if (damage == "missing_entry") group.ImmediateEntries.Clear();
                    if (damage == "wrong_entry") SetArgument("entryId", "forged-entry");
                    if (damage == "wrong_parent") owner.ParentEffectId = "not-the-selected-owner";
                    if (damage == "wrong_source_key") SetArgument("sourceKey", "forged-source-key");
                    if (damage == "wrong_player") owner.PlayerId = 2;
                    if (damage == "wrong_declaration") state.ContentDefinitions.AbilitySources.Single(s => s.AbilityId == current.AbilityId).DeclarationVersion += ".forged";
                    if (damage == "wrong_executor_version") owner.DefinitionVersion += ".forged";
                    if (damage == "terminal_entry") group.ImmediateEntries.Single(e => e.EffectRootId == owner.EffectId).Status = "completed";
                    if (damage == "stale_source") state.ItemZones.Items.Single(i => i.ItemId == current.ItemId).ZoneId = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.box_reserve").ZoneId;
                    if (damage == "missing_selection_receipt") state.ItemZones.Items.Single(i => i.ItemId == current.ItemId).LocalState.Properties.RemoveAll(p => p.Name == "sourceSelectionReceipt");
                    if (damage == "detached_descendant")
                    {
                        var child = GameStateCloneService.DeepClone(owner); child.EffectId = "detached-real-node"; child.ParentEffectId = owner.EffectId;
                        child.EffectTypeId = "effect.fixture.detached"; state.EffectRuntime.EffectNodes.Add(child); candidate.OwnerNode = child;
                    }
                    string id = damage == "wrong_handler" ? "entrepreneur.M08.immediate.1" : handlerId;
                    string scriptHash = damage == "wrong_hash" ? new string('0', 64) : hash;
                    var definition = new LuaHandlerDefinition(id, "AbilityInvoked", "", new LuaScriptDefinition(content.DefinitionId, current.AbilityId, id,
                        damage == "wrong_handler_version" ? "0.0.0" : handler.Version, f.Pack.GetScript(handler.Script), scriptHash),
                        EffectHandlerRole.Primary, contentInstanceId: content.DefinitionId, abilityId: current.AbilityId);
                    var context = (EffectEventHandlerContext)typeof(AbilitySourceLuaCatalog).GetMethod("Context", PrivateStatic)
                        .Invoke(null, new object[] { candidate, definition, f.Registry });
                    if (damage == "unbound_payload") context = new EffectEventHandlerContext(state, context.Event, owner, null);
                    var snapshot = (LuaSnapshotValue)typeof(SourceFactsLuaCatalog).GetMethod("ForInvocation", PrivateStatic)
                        .Invoke(null, new object[] { state, f.Registry, definition, context, 1 });
                    Assert.That(snapshot.Kind, Is.EqualTo(LuaSnapshotValueKind.Null), damage);
                    checkedVariants.Add(damage);
                }
                return original(call); // 正式Lua仍实际读取SourceData并执行，不用探针快照代替。
            };
            try { f.Choose(3, "M07"); }
            finally { bodies[handlerId] = original; }
            Assert.That(checkedVariants.Count, Is.EqualTo(16));
            f.Artifacts.WriteLabeledText("真实立即受信调用拒绝矩阵", JsonConvert.SerializeObject(checkedVariants));
        }

        [TestCase(2390)]
        [TestCase(1124)]
        public void OriginalArchivedSaveRejectsRetiredContentWithoutRewritingEvidence(int revision)
        {
            var artifacts = IsolatedTestArtifacts.BeginCase();
            try
            {
                string directory = Path.Combine(artifacts.InputsDirectory, "原档只读恢复副本"); Directory.CreateDirectory(directory);
                string input = Path.Combine(Directory.GetCurrentDirectory(), "prompt/企业家内容实施规划-20261019/实施记录/QY-002-IMMEDIATE-SOURCE-FACTS-FIX-R5",
                    revision == 2390 ? "故障2390副本.json" : "正常1124副本.json");
                string copy = Path.Combine(directory, "auto-1.json"); File.Copy(input, copy, false);
                string originalHash = IsolatedTestArtifacts.FileHash(copy);
                string sealedHash = revision == 2390 ? "c20870f6d8ca5086c51ed80c4f0770b9abeb2387994979e70637105c58e12c9d" :
                    "655785247ed9e0614c22b947d0c281974f25d93082eb05d1844ba6711f16a755";
                Assert.That(originalHash, Is.EqualTo(sealedHash).IgnoreCase, "须使用历史归档封存的真实原字节");
                var storeType = Type.GetType("YC.Infrastructure.Persistence.MatchSaveStore, Assembly-CSharp", true);
                var store = Activator.CreateInstance(storeType, new object[] { directory });
                var data = (MatchSaveData)storeType.GetMethod("Read").Invoke(store, new object[] { 3 });
                Assert.That(data.ContentHash, Is.EqualTo("b68344b9d026498af200548aca9e51cb23dece71b156adac0da7656b75b8fe29"));
                string archiveBefore = data.ArchiveJson;
                var state = JObject.Parse(archiveBefore)["Snapshot"]["State"];
                var runtime = state["EffectRuntime"];
                Assert.That((string)state["GameId"], Is.EqualTo("8cea6a46d03242b7892f6d1013bfee03"));
                Assert.That((int)runtime["StateRevision"], Is.EqualTo(revision));
                if (revision == 2390)
                {
                    Assert.That((int)runtime["Status"], Is.EqualTo((int)EffectRuntimeStatus.PausedFault));
                    Assert.That((string)runtime["LastFaultCode"], Is.EqualTo("event_dispatch_exception"));
                    Assert.That((string)runtime["LastFaultMessage"], Is.EqualTo("source_facts_unavailable"));
                }
                else
                {
                    Assert.That((int)runtime["Status"], Is.EqualTo((int)EffectRuntimeStatus.Active));
                    Assert.That((string)runtime["LastFaultMessage"], Is.Empty);
                }
                // API-04要求旧内容身份执行前拒绝；不再用当前包强行恢复历史内容。
                var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
                var rejected = Assert.Throws<TargetInvocationException>(() => bootstrapper.GetMethod("Restore", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, new object[] { null, data }));
                Assert.That(rejected.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(rejected.InnerException.Message, Is.EqualTo("content_version_mismatch"));
                Assert.That(data.ArchiveJson, Is.EqualTo(archiveBefore));
                Assert.That(IsolatedTestArtifacts.FileHash(copy), Is.EqualTo(originalHash));
                Assert.That(IsolatedTestArtifacts.FileHash(input), Is.EqualTo(originalHash));
                artifacts.WriteLabeledText("历史原字节与旧内容拒绝", JsonConvert.SerializeObject(new { revision, input, copy, originalHash,
                    data.GameId, data.ContentHash, SavedRuntime = runtime, RejectReason = rejected.InnerException.Message,
                    fileUnmodified = true, archiveUnmodified = true, oldPositiveExpectationReplaced = true,
                    usedForStrategy = false, normalPlayerStarted = false }));
            }
            finally { artifacts.Finish(); }
        }

        [TestCase("M07")]
        [TestCase("M08")]
        public void SelectedImmediateUsesRealSourcesAndResumesPendingTowerBuild(string face)
        {
            using var f = new PaulContentTestFixture(primaryEntrepreneur: "entrepreneur.ability.M13", setupOnly: true);
            FacilityEntryEffectExecutor.Register(f.Registry);
            var tower = f.State.ItemZones.Items.Single(i => i.DefinitionId == "entrepreneur.facility.additional_core_tower" &&
                i.OwnerSubject.PlayerId == 1 && f.State.ItemZones.Zones.Single(z => z.ZoneId == i.ZoneId).TemplateId == "core.zone.player_supply");
            string towerId = tower.ItemId;
            int budget = f.State.FindPlayer(1).RemainingMainActionsThisTurn;
            // 与现有规则隔离夹具一致的阶段起点；选面及全部授权收据由生产请求生成，不编辑来源/选择证明。
            f.State.Round = 3;
            string root = f.Run(ExpansionSetupFlow.RoundPreparationSpec(1));
            bool restored = false;
            string immediateId = "";
            for (int guard = 0; guard < 100; guard++)
            {
                var pending = f.Open.LastOrDefault(q => f.Within(q.OwnerEffectId, root));
                var entry = f.State.EffectRuntime.EffectNodes.SingleOrDefault(n => f.Within(n.EffectId, root) &&
                    n.EffectTypeId == EntrepreneurFlow.EntryTypeId && Text(n.NormalizedArguments, "handlerRef") == "entrepreneur." + face + ".immediate.1");
                if (!restored && entry != null && pending != null && f.Within(pending.OwnerEffectId, entry.EffectId))
                {
                    Assert.That(f.State.EffectRuntime.LastFaultMessage, Is.Empty);
                    Assert.That(entry.FlowStage, Is.EqualTo("body"));
                    immediateId = entry.EffectId;
                    var build = f.State.EffectRuntime.EffectNodes.Single(n => f.Within(n.EffectId, entry.EffectId) && (n.EffectTypeId == FacilityEntryEffectTypeIds.Build && !FacilityBuildContract.IsSelection(n.EffectTypeId, n.NormalizedArguments)));
                    var caller = f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur." + face + ".setup");
                    var construction = f.Registry.QuerySources(f.State, 1).Single(s => s.AbilityId == "entrepreneur.M13.tower_source");
                    Assert.That(Text(entry.NormalizedArguments, "sourceKey"), Is.EqualTo(caller.SourceKey));
                    Assert.That(construction.SourceKey, Is.Not.EqualTo(caller.SourceKey));
                    Assert.That(entry.PlayerId, Is.EqualTo(1)); Assert.That(build.PlayerId, Is.EqualTo(1));
                    Assert.That(Text(build.NormalizedArguments, "sourceKey"), Is.EqualTo(construction.SourceKey));
                    Assert.That(Text(build.NormalizedArguments, "paymentMode"), Is.EqualTo("free"));
                    Assert.That(Flag(build.NormalizedArguments, "consumeMainAction"), Is.False);
                    Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => f.Within(n.EffectId, entry.EffectId) &&
                        (FacilityBuildContract.IsSelection(n.EffectTypeId, n.NormalizedArguments) || n.EffectTypeId == ResourceEffectTypeIds.Pay)), Is.False);
                    f.Artifacts.WriteLabeledText(face + "调用者与查询授权分离", JsonConvert.SerializeObject(new { caller, construction, entry.PlayerId,
                        entryId = entry.EffectId, buildId = build.EffectId }));
                    f.Restore(face + "正式立即塔建设待答"); restored = true;
                    pending = f.Open.Last(q => f.Within(q.OwnerEffectId, root));
                }
                if (f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status == EffectNodeStatus.Completed) break;
                Assert.That(pending, Is.Not.Null, f.State.EffectRuntime.LastFaultMessage);
                string answer = pending.InteractionTypeId == EntrepreneurFlow.FaceInteraction && pending.AnsweringPlayerId == 1
                    ? "entrepreneur.candidate." + face : pending.CandidateIds.Contains("decline") ? "decline" : pending.CandidateIds.First();
                f.Answer(pending, answer);
            }
            Assert.That(restored, Is.True, "必须经过真实立即节点及其待答恢复，不能仅直接创建来源快照");
            var selected = f.State.EntrepreneurSetup.Players.Single(p => p.PlayerId == 1).RoundGroups.Single(g => g.RoundNumber == 3);
            Assert.That(selected.ActiveFaceId, Is.EqualTo(face));
            Assert.That(selected.ImmediateEntries.All(e => e.Status == "completed"), Is.True);
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(f.State.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(budget));
            var built = f.State.ItemZones.Items.Single(i => i.ItemId == towerId);
            Assert.That(f.State.ItemZones.Zones.Single(z => z.ZoneId == built.ZoneId).TemplateId, Is.EqualTo("core.zone.city_facility_cell"));
            f.Artifacts.WriteLabeledText(face + "立即来源及唯一塔实际结果", JsonConvert.SerializeObject(new { selected, towerId, built.ZoneId,
                normalGameCoverage = false, manualSourceFacts = false }));
            f.Restore(face + "立即入口已完整结清后恢复"); f.Pump();
            Assert.That(f.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == immediateId).Status, Is.EqualTo(EffectNodeStatus.Completed));
        }
    }
}
