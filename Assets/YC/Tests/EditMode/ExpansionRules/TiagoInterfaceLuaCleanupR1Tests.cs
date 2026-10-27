using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 本轮独占源码。构树输入/指定骰值仅用于隔离规则定位，不计正常游戏或随机验收。
    public sealed class TiagoInterfaceLuaCleanupR1Tests
    {
        private IsolatedTestArtifacts artifacts;
        private string code, sourceHash, handlerVersion;
        private readonly Dictionary<string, JObject> faceDefinitions = new Dictionary<string, JObject>();
        [SetUp] public void CaptureOneFrozenInputSet()
        {
            artifacts = IsolatedTestArtifacts.BeginCase(); faceDefinitions.Clear();
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            var captured = new List<object>();
            var documents = new Dictionary<string, JObject>();
            var paths = new[] { "lua/entrepreneurs/tiago.lua", "entrepreneur/entrepreneur.ability.D09.json",
                "entrepreneur/entrepreneur.round.D.1.json", "entrepreneur/entrepreneur.round.D.3.json",
                "entrepreneur/entrepreneur.round.D.5.json", "entrepreneur/entrepreneur.round.D.7.json" };
            foreach (string relative in paths)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(installed, relative));
                string text = new UTF8Encoding(false, true).GetString(bytes);
                using var sha = SHA256.Create();
                captured.Add(new { relative, sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(),
                    bytesBase64 = Convert.ToBase64String(bytes) });
                if (relative.EndsWith(".lua", StringComparison.Ordinal)) code = text;
                else documents.Add(relative, JObject.Parse(text));
            }
            sourceHash = LuaContentHasher.ComputeSha256(code);
            handlerVersion = (string)documents[paths[1]]["abilityHandlers"].Single(h => (string)h["handlerId"] == "entrepreneur.D09.investment")["version"];
            artifacts.WriteLabeledText("014规则单元完整冻结输入", JsonConvert.SerializeObject(new { captured, handlerSourceSha256 = sourceHash,
                limitation = "仅六份规则单元实际输入，逐文件原字节已封存；不复制整套贴图，不装配正式pack，不计Host或正常局" }));
            foreach (int round in new[] { 1, 3, 5, 7 })
            {
                var definition = documents["entrepreneur/entrepreneur.round.D." + round + ".json"];
                foreach (JObject face in definition["data"]["faces"])
                {
                    string rule = (string)face["ruleRefs"][0];
                    faceDefinitions.Add((string)face["faceId"], new JObject {
                        ["definitionId"] = definition["definitionId"], ["face"] = face.DeepClone(),
                        ["declarationVersion"] = definition["abilitySources"].Single(x => (string)x["abilityId"] == rule)["declarationVersion"] });
                }
            }
        }
        [TearDown] public void Finish() => artifacts?.Finish();
        private static LuaSnapshotValue Snapshot(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return LuaSnapshotValue.Null;
            if (token is JObject obj) return LuaSnapshotValue.Object(obj.Properties().Select(p => new LuaSnapshotEntry(p.Name, Snapshot(p.Value))).ToList());
            if (token is JArray array) return LuaSnapshotValue.Array(array.Select(Snapshot).ToList());
            if (token.Type == JTokenType.Integer) return LuaSnapshotValue.Integer(token.Value<long>());
            if (token.Type == JTokenType.Boolean) return LuaSnapshotValue.Boolean(token.Value<bool>());
            return LuaSnapshotValue.String(token.Value<string>());
        }
        private static LuaSnapshotValue Snapshot(NormalizedValue value)
        {
            if (value == null || value.Kind == NormalizedValueKind.Null) return LuaSnapshotValue.Null;
            if (value.Kind == NormalizedValueKind.Array) return LuaSnapshotValue.Array(value.Items.Select(Snapshot).ToList());
            if (value.Kind == NormalizedValueKind.Object) return LuaSnapshotValue.Object(value.Properties.Select(p => new LuaSnapshotEntry(p.Name, Snapshot(p.Value))).ToList());
            if (value.Kind == NormalizedValueKind.Integer) return LuaSnapshotValue.Integer(value.IntegerValue);
            if (value.Kind == NormalizedValueKind.Boolean) return LuaSnapshotValue.Boolean(value.BooleanValue);
            return LuaSnapshotValue.String(value.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value.StringValue);
        }
        private JObject Selected(string face)
        {
            var definition = faceDefinitions[face]; string item = "selected." + face;
            return new JObject { ["sourceKey"] = "opaque." + face, ["itemId"] = item,
                ["definitionId"] = definition["definitionId"], ["abilityId"] = definition["face"]["ruleRefs"][0],
                ["activeFaceId"] = face, ["declarationVersion"] = definition["declarationVersion"], ["handlerHash"] = sourceHash,
                ["authorizedPlayerId"] = "p1", ["authorizationRef"] = "selection:choice." + face,
                ["effectiveZoneId"] = "active", ["ruleOwnerItemId"] = item,
                ["abilityOwnerItemId"] = "carrier", ["abilityOwnerDefinitionId"] = "entrepreneur.ability.D09",
                ["selectionReceipt"] = new JObject { ["protocolVersion"] = 1, ["requestId"] = "request." + face,
                    ["ownerEffectId"] = "choice." + face, ["candidateId"] = definition["face"]["candidateId"], ["faceId"] = face },
                ["grantingSourceRefs"] = new JArray() };
        }
        private LuaInvocationResult Invoke(string[] faces, string ability = "entrepreneur.D09.investment", NormalizedValue saved = null,
            NormalizedValue answer = null, Action<JArray, JObject> alter = null, string player = "p1", string outcome = "completed", JArray tokenInventory = null)
        {
            var sources = new JArray();
            // 目标来源在第二页以后，避免只测第一页。无关来源不能凭D面同名生效。
            for (int i = 0; i < 5; i++) sources.Add(new JObject { ["sourceKey"] = "unrelated." + i, ["activeFaceId"] = "D05",
                ["definitionId"] = "diy.other", ["authorizedPlayerId"] = "p1", ["selectionReceipt"] = null });
            foreach (string face in faces) sources.Add(Selected(face));
            var carrier = new JObject { ["id"] = "carrier", ["definitionId"] = "entrepreneur.ability.D09", ["controller"] = "p1",
                ["owner"] = "p1", ["zoneId"] = "active", ["roundChoices"] = new JArray() };
            alter?.Invoke(sources, carrier);
            var instances = new JArray(carrier);
            foreach (string face in faces) instances.Add(new JObject { ["id"] = "selected." + face,
                ["definitionId"] = faceDefinitions[face]["definitionId"], ["owner"] = "p1", ["controller"] = "p1",
                ["zoneId"] = "active", ["entrepreneurAbilityItemId"] = "carrier" });
            for (int i = 1; i <= 3; i++) instances.Add(new JObject { ["id"] = "die." + i, ["definitionId"] = "entrepreneur.component.die",
                ["typeId"] = "core.die", ["owner"] = "p1", ["controller"] = "p1", ["zoneId"] = "supply" });
            var content = new JObject { ["instances"] = instances, ["definitions"] = new JArray(), ["zones"] = new JArray(
                new JObject { ["id"] = "active", ["templateId"] = "core.zone.entrepreneur_active" },
                new JObject { ["id"] = "reputation", ["parentId"] = "carrier", ["localZoneId"] = "reputation" },
                new JObject { ["id"] = "supply", ["owner"] = "p1", ["localZoneId"] = "supply" },
                new JObject { ["id"] = "retired", ["templateId"] = "core.zone.box_retired" }) };
            var sourceFacts = new JObject { ["protocolVersion"] = 1, ["stateRevision"] = 0, ["contentHash"] = "qy014.cleanup-r1.isolated",
                ["authorizedPlayerId"] = player, ["ruleOwnerSourceKey"] = "opaque.owner", ["ruleOwnerItemId"] = "carrier",
                ["sources"] = sources, ["grants"] = new JArray(), ["callableActions"] = new JArray(),
                ["transferableTokens"] = tokenInventory ?? TiagoInvestmentTestFixture.IsolatedTokens(8), ["transferableTokenCount"] = (tokenInventory ?? TiagoInvestmentTestFixture.IsolatedTokens(8)).Count };
            var snapshot = LuaFacadeSnapshot.FromInvocation(player, 0, 100, 2, handlerVersion); snapshot.GlobalContent = Snapshot(content);
            snapshot.EventPayload = saved == null ? Snapshot(new JObject { ["abilityId"] = ability, ["sourceItemId"] = "carrier" }) :
                LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("continuationContext", Snapshot(saved)),
                    new LuaSnapshotEntry("normalizedResult", Snapshot(answer)), new LuaSnapshotEntry("outcome", LuaSnapshotValue.String(outcome)) });
            artifacts.WriteLabeledText("014本轮规则定位调用输入", JsonConvert.SerializeObject(new { faces, ability, sourceFacts, content, snapshot.EventPayload,
                limitation = "手写来源快照和骰值只验证正式Lua；不代替SourceFacts宿主真实性、随机或正常页面" }));
            var context = new LuaInvocationContext("qy014.cleanup-r1", saved == null ? "AbilityInvoked" : "EffectCompleted", player, 0, handlerVersion, 2, 0, 100, snapshot) {
                SourceFacts = Snapshot(sourceFacts) };
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("entrepreneur.ability.D09", ability, ability, handlerVersion, code, sourceHash), context);
            artifacts.WriteLabeledText("014本轮规则定位返回", JsonConvert.SerializeObject(new { result.Status, result.Diagnostic, result.Effects })); return result;
        }
        private static LuaEffectSpec Investment(LuaInvocationResult result, bool quick = false)
        { Assert.That(result.IsSuccess, Is.True, result.Diagnostic); return quick ? result.Effects.Single().NestedEffectGroups[1].Single() : result.Effects.Single(); }
        private LuaEffectSpec MainChoice(string[] faces, int[] dice, string ability = "entrepreneur.D09.investment")
        {
            var roll = Investment(Invoke(faces, ability), ability != "entrepreneur.D09.investment").NestedEffectGroups[1].Single();
            foreach (int die in dice)
            {
                var rolled = Invoke(faces, ability, roll.ContinuationContext, Object("diceResult", NormalizedValue.CreateInteger(die)));
                Assert.That(rolled.IsSuccess, Is.True, rolled.Diagnostic); roll = rolled.Effects.Single();
            }
            Assert.That(roll.EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.Choice));
            var loaded = Invoke(faces, ability, roll.ContinuationContext, NormalizedValue.CreateString("0"));
            Assert.That(loaded.IsSuccess, Is.True, loaded.Diagnostic); return loaded.Effects.Single();
        }
        private static string[] Options(LuaEffectSpec spec) => Value(spec.RawArguments, "options").Items.Select(v => v.StringValue).ToArray();

        [TestCase("entrepreneur.D07.quick", 3, "retired")][TestCase("entrepreneur.D08.quick", 2, "original_supply")]
        [TestCase("entrepreneur.D01.exchange", 2, "original_supply")][TestCase("entrepreneur.D02.exchange", 2, "original_supply")]
        public void TokenFeesUseOnlyTheExactCarrierOwnedInventoryAndOneAtomicOperation(string ability, int cost, string destination)
        {
            var stock = TiagoInvestmentTestFixture.IsolatedTokens(8, "another.carrier", zone: "another.reputation");
            foreach (JObject token in TiagoInvestmentTestFixture.IsolatedTokens(8, owner: "p2")) { token["itemId"] = "foreign-owner." + (string)token["itemId"]; stock.Add(token); }
            foreach (var token in TiagoInvestmentTestFixture.IsolatedTokens(6)) stock.Add(token);
            string before = stock.ToString();
            var result = Invoke(new[] { "D01", "D05" }, ability, tokenInventory: stock);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects.Single().NestedEffectGroups[0].Count, Is.EqualTo(1));
            var transfer = result.Effects.Single().NestedEffectGroups[0].Single();
            TiagoInvestmentTestFixture.AssertTokenCost(transfer, cost, destination);
            Assert.That(Value(transfer.RawArguments, "itemIds").Items.Select(v => v.StringValue),
                Is.EqualTo(Enumerable.Range(1, cost).Select(i => "carrier.marker." + i.ToString("D3"))));
            Assert.That(stock.ToString(), Is.EqualTo(before));
        }
        [TestCase("entrepreneur.D07.quick", 3)][TestCase("entrepreneur.D08.quick", 2)]
        [TestCase("entrepreneur.D01.exchange", 2)][TestCase("entrepreneur.D02.exchange", 2)]
        public void ShortageDoesNotSkipTheLeftOrSubmitAPartialFee(string ability, int cost)
        {
            var result = Invoke(new[] { "D01", "D05" }, ability, tokenInventory: TiagoInvestmentTestFixture.IsolatedTokens(1));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            var left = result.Effects.Single().NestedEffectGroups[0]; Assert.That(left.Count, Is.EqualTo(1));
            Assert.That(left.Single().EffectTypeId, Is.EqualTo(ItemTransferEffectExecutor.TypeId));
            Assert.That(Number(left.Single().RawArguments, "requiredCount"), Is.EqualTo(cost));
            Assert.That(Value(left.Single().RawArguments, "itemIds").Items, Is.Empty);
            Assert.That(result.Effects.Single().NestedEffectGroups[1], Is.Not.Empty, "后继仍由真实左式成功门禁保护，不能短缺改成免费成功");
        }
        [Test] public void OriginalSupplyRemainsPerItemAndDoesNotBecomeAFixedPlayerDestination()
        {
            var stock = TiagoInvestmentTestFixture.IsolatedTokens(2);
            stock[0]["originalSupplyZoneId"] = "original.one"; stock[1]["originalSupplyZoneId"] = "original.two";
            var result = Invoke(new[] { "D01", "D05" }, "entrepreneur.D01.exchange", tokenInventory: stock);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            var args = result.Effects.Single().NestedEffectGroups[0].Single().RawArguments;
            Assert.That(Text(Value(args, "destination"), "kind"), Is.EqualTo("original_supply"));
            Assert.That(Value(args, "itemIds").Items.Select(v => v.StringValue), Is.EqualTo(new[] { "carrier.marker.001", "carrier.marker.002" }));
            Assert.That(Value(Value(args, "destination"), "zoneId"), Is.Null);
        }
        [TestCase("D01", "D03", "D05", 6, 1, 2, 3)][TestCase("D01", "D03", "D06", 10, 0, 3, 4)]
        [TestCase("D01", "D04", "D05", 2, 2, 2, 2)][TestCase("D01", "D04", "D06", 6, 1, 3, 3)]
        [TestCase("D02", "D03", "D05", 8, 1, 2, 3)][TestCase("D02", "D03", "D06", 12, 0, 3, 4)]
        [TestCase("D02", "D04", "D05", 4, 2, 2, 2)][TestCase("D02", "D04", "D06", 8, 1, 3, 3)]
        public void CurrentSourcesPreserveAllEightOfficialInvestmentCombinations(string one, string three, string five, int gold, int total, int dice, int reputation)
        {
            var investment = Investment(Invoke(new[] { one, three, five }));
            Assert.That(investment.NestedEffectGroups[0].Count, Is.EqualTo(1)); var pay = investment.NestedEffectGroups[0].Single().RawArguments;
            Assert.That(Number(Value(pay, "cost"), "GoldVoucher"), Is.EqualTo(gold)); Assert.That(Number(pay, "amount"), Is.EqualTo(total));
            var c = Value(investment.NestedEffectGroups[1].Single().ContinuationContext, "parameters");
            Assert.That(Number(c, "dice"), Is.EqualTo(dice)); Assert.That(Number(c, "reputation"), Is.EqualTo(reputation));
            Assert.That(investment.NestedEffectGroups[1].Single().ContinuationContext.IsValid(), Is.True, "不得突破原归一化上下文预算");
        }
        [TestCase("wrong_receipt")][TestCase("wrong_definition")][TestCase("wrong_carrier")][TestCase("wrong_player")][TestCase("withdrawn")]
        public void CachedRoundChoiceCannotReplaceTheCurrentSelectedSource(string invalid)
        {
            var result = Invoke(new[] { "D03" }, alter: (sources, carrier) => {
                carrier["roundChoices"] = new JArray(new JObject { ["selectedCandidateId"] = "entrepreneur.candidate.D03", ["activeFaceId"] = "D03",
                    ["candidates"] = new JArray(new JObject { ["candidateId"] = "entrepreneur.candidate.D03", ["faceId"] = "D03",
                        ["entityItemId"] = "selected.D03", ["definitionId"] = "entrepreneur.round.D.3" }) });
                var selected = (JObject)sources.Last;
                if (invalid == "wrong_receipt") selected["selectionReceipt"] = null;
                else if (invalid == "wrong_definition") selected["definitionId"] = "diy.same_face";
                else if (invalid == "wrong_carrier") selected["abilityOwnerItemId"] = "another.carrier";
                else if (invalid == "wrong_player") selected["authorizedPlayerId"] = "p2";
                else sources.Remove(selected);
            });
            var pay = Investment(result).NestedEffectGroups[0].Single().RawArguments;
            Assert.That(Number(Value(pay, "cost"), "GoldVoucher"), Is.EqualTo(4));
        }
        [Test] public void ThreeBOffersZeroAndSevenAndRetainsTheOriginalPhysicalDice()
        {
            var main = MainChoice(new[] { "D04" }, new[] { 1, 6 });
            Assert.That(Options(main), Is.EqualTo(new[] { "0", "1", "2", "5", "6", "7" }));
            Assert.That(Value(main.ContinuationContext, "dice").Items.Select(v => v.IntegerValue), Is.EqualTo(new long[] { 1, 6 }));
            Assert.That(Value(main.ContinuationContext, "offeredPoints").Items.Select(v => v.IntegerValue), Is.EqualTo(new long[] { 0, 1, 2, 5, 6, 7 }));
        }
        [TestCase("3")][TestCase("4")][TestCase("2.5")][TestCase("8")][TestCase("-1")]
        public void CompletionCannotSelectANumberOutsideTheFrozenOfferedPoints(string answer)
        {
            var main = MainChoice(new[] { "D04" }, new[] { 1, 6 });
            var invalid = Invoke(new[] { "D04" }, saved: main.ContinuationContext, answer: NormalizedValue.CreateString(answer));
            Assert.That(invalid.IsSuccess, Is.False); Assert.That(invalid.Diagnostic, Does.Contain(answer == "3" || answer == "4" ? "tiago_point_not_offered" : "tiago_point_answer_invalid"));
        }
        [TestCase(0)][TestCase(7)] public void LegalModifiedEndpointsStillProduceTheirOrdinaryEffect(int point)
        {
            var main = MainChoice(new[] { "D04" }, new[] { 1, 6 });
            var result = Invoke(new[] { "D04" }, saved: main.ContinuationContext, answer: NormalizedValue.CreateString(point.ToString()));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects, Is.Not.Empty);
        }
        [TestCase("fraction")][TestCase("seven")][TestCase("old_protocol")][TestCase("wrong_player")][TestCase("extra_roll")]
        public void InvalidDiceOrAnUnrelatedContinuationCannotAdvanceTheInvestment(string failure)
        {
            var roll = Investment(Invoke(Array.Empty<string>())).NestedEffectGroups[1].Single(); var saved = roll.ContinuationContext.Clone();
            NormalizedValue result = failure == "fraction" ? Object("diceResult", NormalizedValue.CreateString("2.5")) : Object("diceResult", NormalizedValue.CreateInteger(failure == "seven" ? 7 : 2));
            if (failure == "extra_roll") saved.Properties.Single(p => p.Name == "dice").Value = NormalizedValue.CreateArray(new List<NormalizedValue> { NormalizedValue.CreateInteger(1), NormalizedValue.CreateInteger(2) });
            if (failure == "old_protocol") saved.Properties.Single(p => p.Name == "version").Value = NormalizedValue.CreateInteger(1);
            var invocation = Invoke(Array.Empty<string>(), saved: saved, answer: result, player: failure == "wrong_player" ? "p2" : "p1");
            Assert.That(invocation.IsSuccess, Is.False);
            string diagnostic = failure == "old_protocol" ? "tiago_context_version_invalid" : failure == "wrong_player" ? "tiago_context_player_invalid" :
                failure == "extra_roll" ? "tiago_dice_count_invalid" : "tiago_dice_result_invalid";
            Assert.That(invocation.Diagnostic, Does.Contain(diagnostic));
        }
        [TestCase(false)][TestCase(true)] public void ThreeAStillUsesDirectOtherPointForTwoDiceAndARealChoiceForThree(bool triple)
        {
            var faces = triple ? new[] { "D03", "D06" } : new[] { "D03" };
            var main = MainChoice(faces, triple ? new[] { 2, 4, 4 } : new[] { 2, 5 });
            var selected = Invoke(faces, saved: main.ContinuationContext, answer: NormalizedValue.CreateString("2"));
            Assert.That(selected.IsSuccess, Is.True, selected.Diagnostic); var extra = selected.Effects[1];
            Assert.That(extra.NestedEffectGroups[0].Count, Is.EqualTo(1), "额外费用须为一次足额通用token转移");
            if (triple) Assert.That(Options(extra.NestedEffectGroups[1].Single()), Is.EqualTo(new[] { "4" }));
            else Assert.That(extra.NestedEffectGroups[1].Count, Is.EqualTo(3));
        }
        [Test] public void TradeBatchesKeepTheirContinuationAndDoNotCreateAnExtraGain()
        {
            var main = MainChoice(new[] { "D04" }, new[] { 4, 5 });
            var trade = Invoke(new[] { "D04" }, saved: main.ContinuationContext, answer: NormalizedValue.CreateString("4"));
            Assert.That(trade.IsSuccess, Is.True, trade.Diagnostic);
            for (int batch = 0; batch < 2; batch++)
            {
                var sale = trade.Effects.Single();
                Assert.That(Text(sale.RawArguments, "saleMode"), Is.EqualTo("batch"));
                Assert.That(Text(sale.RawArguments, "pricePolicy"), Is.EqualTo("core.resource_sale"));
                trade = Invoke(new[] { "D04" }, saved: sale.ContinuationContext, answer: Object("revenue", NormalizedValue.CreateInteger(3),
                    "soldCount", NormalizedValue.CreateInteger(1), "finished", NormalizedValue.CreateBoolean(false)));
                Assert.That(trade.IsSuccess, Is.True, trade.Diagnostic);
            }
            var done = Invoke(new[] { "D04" }, saved: trade.Effects.Single().ContinuationContext,
                answer: Object("finished", NormalizedValue.CreateBoolean(true)));
            Assert.That(done.IsSuccess, Is.True, done.Diagnostic); Assert.That(done.Effects, Is.Empty);
        }
        [Test] public void EqualNormalDiceDoNotChargeThreeAOrCreateAnEmptyExtraChoice()
        {
            var main = MainChoice(new[] { "D03" }, new[] { 2, 2 });
            Assert.That(Options(main), Is.EqualTo(new[] { "2" }));
            var reward = Invoke(new[] { "D03" }, saved: main.ContinuationContext, answer: NormalizedValue.CreateString("2"));
            Assert.That(reward.IsSuccess, Is.True, reward.Diagnostic); Assert.That(reward.Effects.Count, Is.EqualTo(1));
            Assert.That(reward.Effects.Single().Amount, Is.EqualTo(15));
        }
        [TestCase("entrepreneur.D07.quick")][TestCase("entrepreneur.D08.quick")]
        public void SevenAModeDoesNotEraseSevenBNormalAndThreeBModifiedChoices(string ability)
        {
            var main = MainChoice(new[] { "D04", "D06" }, new[] { 1, 1, 6 }, ability);
            Assert.That(Options(main), Is.EqualTo(ability == "entrepreneur.D07.quick"
                ? Enumerable.Range(0, 8).Select(n => n.ToString()).ToArray() : new[] { "0", "1", "2", "5", "6", "7" }));
            Assert.That(Number(main.RawArguments, "minSelections"), Is.EqualTo(1));
            Assert.That(Number(main.RawArguments, "maxSelections"), Is.EqualTo(1));
        }
        [Test] public void SevenAKeepsRealDiceAndAllowsMainAndExtraToChooseTheSamePoint()
        {
            const string quick = "entrepreneur.D07.quick"; var faces = new[] { "D03", "D06" };
            var main = MainChoice(faces, new[] { 2, 2, 2 }, quick);
            Assert.That(Options(main), Is.EqualTo(Enumerable.Range(0, 8).Select(n => n.ToString())));
            var selected = Invoke(faces, quick, main.ContinuationContext, NormalizedValue.CreateString("2"));
            Assert.That(selected.IsSuccess, Is.True, selected.Diagnostic); var extra = selected.Effects[1].NestedEffectGroups[1].Single();
            Assert.That(Options(extra), Is.EqualTo(Options(main)));
            var reward = Invoke(faces, quick, extra.ContinuationContext, NormalizedValue.CreateString("2"));
            Assert.That(reward.IsSuccess, Is.True, reward.Diagnostic); Assert.That(reward.Effects.Single().Amount, Is.EqualTo(15));
            Assert.That(Value(extra.ContinuationContext, "dice").Items.Select(v => v.IntegerValue), Is.EqualTo(new long[] { 2, 2, 2 }));
        }
    }
}
