using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class PaymentPreparationCancellationTests
    {
        private PaymentTestFixture f;
        [SetUp] public void SetUp() => f = new PaymentTestFixture();
        [TearDown] public void TearDown() => f?.Dispose();
        private string Prepare(out string target, out string originalRequest, bool anotherCandidate = false)
        {
            f.Run(RefillSpec(1, FindZone(f.State, "facility_supply_0").ZoneId));
            if (anotherCandidate) f.Run(RefillSpec(1, FindZone(f.State, "facility_supply_1").ZoneId));
            target = new ItemZoneQuery().GetItemIds(f.State, FindZone(f.State, "facility_supply_0").ZoneId).Single();
            f.Run(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply"), "allowDecline", NormalizedValue.CreateBoolean(true)), 1));
            var q = f.Open(); originalRequest = q.GetStableInteractionId();
            Assert.That(f.Engine.TryPrepareEffectBuildPayment(1, q.OwnerEffectId, originalRequest, q.CandidateSetId, q.CandidateSetVersion, target,
                Object("expectedSourceZoneId", NormalizedValue.CreateString(FindZone(f.State, "facility_supply_0").ZoneId), "paymentMode", NormalizedValue.CreateString("gold")),
                f.State.EffectRuntime.StateRevision, "007合法设施付款准备", out var reason), Is.True, reason);
            return f.Open().OwnerEffectId;
        }
        [TestCase(false)] [TestCase(true)] public void ChangedSourceCanCancelAndRefreshWithoutPaymentOrFallback(bool anotherCandidate)
        {
            string owner = Prepare(out var target, out var original, anotherCandidate); int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            f.Run(MoveSpec(1, new[] { target }, FindZone(f.State, "facility_supply_0").ZoneId, FindZone(f.State, "retired").ZoneId, "unordered", 1));
            Assert.That(ResourcePaymentPreparationService.CanConfirm(f.State, f.Engine.GetNode(owner), f.Registry), Is.False);
            f.Artifacts.WriteLabeledText("G6目标已真实移走首次现场", JsonConvert.SerializeObject(f.State));
            f.Restore("G6未付目标失效恢复");
            Assert.That(f.Engine.TryCancelPreparedEffectBuildPayment(1, owner, f.State.EffectRuntime.StateRevision, "007取消失效目标", out var reason), Is.True, reason);
            if (anotherCandidate)
            {
                Assert.That(f.Open().GetStableInteractionId(), Is.Not.EqualTo(original)); Assert.That(f.Open().CandidateIds, Does.Not.Contain(target));
            }
            else
            {
                Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
                var selection = f.Engine.GetNode(f.Engine.GetNode(owner).ParentEffectId);
                Assert.That(selection.Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(selection.NormalizedResult.StringValue, Is.EqualTo("no_legal_target"));
            }
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(f.State.Map.Facilities, Is.Empty);
            f.Restore("G6已取消父选择刷新恢复"); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(f.State.Map.Facilities, Is.Empty);
        }
        [Test] public void WrongPlayerAndOldRevisionCannotCancelAndSameCommandIsIdempotent()
        {
            string owner = Prepare(out _, out _); int revision = f.State.EffectRuntime.StateRevision; string before = JsonConvert.SerializeObject(f.State);
            Assert.That(f.Engine.TryCancelPreparedEffectBuildPayment(2, owner, revision, "007越权取消", out _), Is.False);
            Assert.That(f.Engine.TryCancelPreparedEffectBuildPayment(1, owner, revision - 1, "007过期取消", out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            Assert.That(f.Engine.TryCancelPreparedEffectBuildPayment(1, owner, revision, "007唯一取消", out var reason), Is.True, reason);
            string after = JsonConvert.SerializeObject(f.State);
            Assert.That(f.Engine.TryCancelPreparedEffectBuildPayment(1, owner, revision, "007唯一取消", out reason), Is.True, reason);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(after));
            Assert.That(f.Engine.TryCancelPreparedEffectBuildPayment(1, owner, revision + 1, "007唯一取消", out reason), Is.False); Assert.That(reason, Is.EqualTo("command_id_payload_mismatch"));
        }
        [Test] public void CompletedRealPaymentCannotBeCancelledAsUnpaidPreparation()
        {
            var n = f.Run(ResourceEffectSpecFactory.Pay(1, new ResourceSet { GoldVoucher = 4 }));
            Assert.That(n.Payment.IsCommitted, Is.True); int balance = f.State.FindPlayer(1).Resources.GoldVoucher;
            Assert.That(f.Engine.TryCancelPreparedEffectBuildPayment(1, n.EffectId, f.State.EffectRuntime.StateRevision, "007已付禁止撤销", out _), Is.False);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(balance));
        }
        [Test] public void RawPaymentV2MissingAllocationOrReceiptIsNeverDefaultedByRecovery()
        {
            var n = f.Run(ResourceEffectSpecFactory.Pay(1, new ResourceSet { GoldVoucher = 4 }));
            var raw = JObject.FromObject(n.Payment); raw.Remove("SelectedAllocation");
            Assert.That(ResourcePaymentRecovery.HasRawFields(raw, out _), Is.False);
            raw = JObject.FromObject(n.Payment); raw["FinalReceipt"] = null;
            Assert.That(ResourcePaymentRecovery.HasRawFields(raw, out _), Is.False);
            var saved = n.Payment.FinalReceipt; n.Payment.FinalReceipt = null;
            Assert.That(ResourcePaymentRecovery.TryValidateNode(f.State, f.Registry, n, out _), Is.False); n.Payment.FinalReceipt = saved;
        }

        private static System.Collections.Generic.IEnumerable<TestCaseData> Qy007B2PreparationCorruptions()
        {
            foreach (bool checkpoint in new[] { false, true })
            foreach (string field in new[] { "unknown_kind", "fake_main_action", "no_preparation", "owner", "source_selection", "interaction",
                "candidate_version", "operation_window", "target", "payment_mode" })
                yield return new TestCaseData(field, checkpoint);
        }
        [TestCaseSource(nameof(Qy007B2PreparationCorruptions))]
        public void Qy007B2RealPreparationWithoutInternalMarkerRestoresButForgedIdentityDoesNot(string field, bool checkpoint)
        {
            var baseline = HostRecoveryService.CreateArchive(f.State);
            string owner = Prepare(out _, out _); var node = f.Engine.GetNode(owner);
            Assert.That(node.Payment.PaymentProtocolVersion, Is.EqualTo(2));
            Assert.That(Value(node.NormalizedArguments, "paymentProtocolVersion"), Is.Null);
            Assert.That(node.Payment.Preparation.AuthorizationKind, Is.EqualTo("effect_build"));
            Assert.That(node.Payment.IsCommitted, Is.False);
            if (checkpoint) HostRecoveryService.AppendJournal(baseline, f.State);
            string json = HostRecoveryService.SerializeArchive(checkpoint ? baseline : HostRecoveryService.CreateArchive(f.State));
            f.Artifacts.WriteLabeledText("真实准备无新标记合法归档" + field + checkpoint, json);
            var control = HostRecoveryService.RecoverJson(json, f.Registry);
            Assert.That(control.Succeeded, Is.True, control.Diagnostic);
            Assert.That(control.State.EffectRuntime.EffectNodes.Single(n => n.EffectId == owner).Payment.PaymentId, Is.EqualTo(owner));
            Assert.That(control.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
            var bad = JObject.Parse(json);
            var raw = checkpoint ? (JObject)((JArray)bad["Journal"]["Entries"]).Last(e => e["StateAfter"] is JObject)["StateAfter"] : (JObject)bad["Snapshot"]["State"];
            var rawNode = ((JArray)raw["EffectRuntime"]["EffectNodes"]).OfType<JObject>().Single(n => (string)n["EffectId"] == owner);
            var preparation = (JObject)rawNode["Payment"]["Preparation"];
            switch (field)
            {
                case "unknown_kind": preparation["AuthorizationKind"] = "forged"; break;
                case "fake_main_action": preparation["AuthorizationKind"] = "main_action"; break;
                case "no_preparation": rawNode["Payment"]["Preparation"] = null; break;
                case "owner": preparation["PreparationOwnerEffectId"] = "effect.not-the-payment"; break;
                case "source_selection": preparation["SelectionOwnerEffectId"] = "effect.unrelated-selection"; break;
                case "interaction": preparation["SelectionInteractionId"] = "interaction.not-committed"; break;
                case "candidate_version": preparation["SelectionCandidateSetVersion"] = preparation["SelectionCandidateSetVersion"].Value<int>() + 1; break;
                case "operation_window": preparation["OperationWindowId"] = "window.unproven"; break;
                case "target": preparation["TargetId"] = "item.not-the-original-target"; break;
                case "payment_mode": ((JArray)preparation["ActionArguments"]["Properties"]).OfType<JObject>().Single(p => (string)p["Name"] == "paymentMode")["Value"]["StringValue"] = "resources"; break;
                default: Assert.Fail(field); break;
            }
            string before = JsonConvert.SerializeObject(f.State);
            string corrupted = bad.ToString(); f.Artifacts.WriteLabeledText("伪准备身份原始坏档" + field + checkpoint, corrupted);
            var rejected = HostRecoveryService.RecoverJson(corrupted, f.Registry);
            f.Artifacts.WriteLabeledText("伪准备身份拒绝结果" + field + checkpoint, JsonConvert.SerializeObject(new { rejected.Succeeded, rejected.Diagnostic }));
            Assert.That(rejected.Succeeded, Is.False, "缺少真实准备授权不能绕过内部v2标记约束：" + field);
            Assert.That(rejected.Diagnostic, Is.Not.Empty);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
        }
        [Test] public void Qy007B2LegacyUnmarkedInternalPaymentExecutesOriginalProtocolAndDoesNotUpgradeOnRestore()
        {
            // 原内部付款段的隔离入口，真实执行扣费；不伪造已付节点，不计原Build/正常页面验收。
            var definition = f.State.ContentDefinitions.Facilities.Single(d => d.FacilityId == "building_001");
            int before = f.State.FindPlayer(1).Resources.GoldVoucher;
            var node = f.Run(EffectSpec.Create(FacilityBuildEffectExecutor.PaymentTypeId,
                Object("facilityId", NormalizedValue.CreateString(definition.FacilityId), "paymentMode", NormalizedValue.CreateString("gold")), 1));
            Assert.That(Value(node.NormalizedArguments, "paymentProtocolVersion"), Is.Null);
            Assert.That(node.Payment.PaymentProtocolVersion, Is.EqualTo(1));
            Assert.That(node.Payment.IsCommitted, Is.True);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before - definition.GoldVoucherCost));
            string id = node.EffectId; f.Restore("旧无标记内部付款1真实扣费后恢复");
            Assert.That(f.Engine.GetNode(id).Payment.PaymentProtocolVersion, Is.EqualTo(1));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before - definition.GoldVoucherCost));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.Payment != null), Is.EqualTo(1));
        }
    }
}
