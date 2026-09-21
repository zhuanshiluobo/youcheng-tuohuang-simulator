using NUnit.Framework;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class LuaRuntimeHostEditModeTests
    {
        [Test]
        public void UnimplementedConstructors_AreNotAdvertisedToContent()
        {
            var result = Invoke(@"return function(ctx)
                local names = { 'Repeat', 'RollDice', 'ApplyResourceDiscount', 'PlaceRoad',
                    'OperateToken', 'ShuffleFacilityDeck', 'RotateFacilityCard',
                    'ResolveCharacterCardEffect', 'RevealCharacterCard', 'SetCharacterDoubleUseRule', 'DispatchOperator',
                    'UpgradeEnterprise', 'ActivateEnterpriseSpecial', 'SwitchDepartment',
                    'Collect', 'OpenPlayerTaskGroup', 'MainActionDeploy', 'MainActionDispatch', 'MainActionBuild',
                    'MainActionMoveCity', 'MainActionSpecial', 'DeclareCityStyle' }
                for _, name in ipairs(names) do if Effect[name] ~= nil then error(name) end end
                local player = GameData.GetPlayers()[1]
                local resources = PlayerData.GetResources(player)
                return { Effect.GainScore({ recipient = player, amount = 1 + resources.originium + resources.originiumShard + resources.iron + resources.pureOriginium }) }
            end");
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(result.Effects[0].Amount, Is.EqualTo(1));
        }
        [TestCase("options={'other'}")]
        [TestCase("minSelections=1")]
        [TestCase("maxSelections=2")]
        public void BranchChoice_RejectsConflictingSelectionContracts(string field)
        {
            var result = Invoke("return function(ctx) return { Effect.Choice({player=ctx.playerId," + field +
                ",branches={{id='one',effects={Effect.GainScore({recipient=ctx.playerId,amount=1})}}}}) } end");
            Assert.That(result.IsSuccess, Is.False);
        }
        [Test]
        public void ValidHandler_ReadsSnapshotsAndReturnsNormalizedEffectSpec()
        {
            const string source = @"
return function(ctx)
    local game = GameData.Get()
    local player = PlayerData.Get(ctx.playerId)
    return {
        Effect.GainResource({
            recipient = ctx.playerId,
            resourceType = 'gold_voucher',
            amount = game.playerCount + 1,
            reasonId = 'event.demo'
        })
    }
end";

            LuaInvocationResult result = Invoke(source);

            Assert.That(result.Status, Is.EqualTo(LuaInvocationStatus.Success));
            Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.None));
            Assert.That(result.Effects, Has.Count.EqualTo(1));
            Assert.That(result.Effects[0].EffectTypeId, Is.EqualTo("effect.resource.gain"));
            Assert.That(result.Effects[0].RecipientPlayerId, Is.EqualTo("p1"));
            Assert.That(result.Effects[0].ResourceTypeId, Is.EqualTo("gold_voucher"));
            Assert.That(result.Effects[0].Amount, Is.EqualTo(3));
            Assert.That(result.Effects[0].ReasonId, Is.EqualTo("event.demo"));
        }

        [TestCase(@"return function(ctx) return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = '2' }) } end", LuaFailureCode.InvalidFieldType)]
        [TestCase(@"return function(ctx) return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 101 }) } end", LuaFailureCode.OutOfBounds)]
        [TestCase(@"return function(ctx) return { Effect.GainResource({ recipient = 'p2', resourceType = 'gold_voucher', amount = 1 }) } end", LuaFailureCode.InvalidTarget)]
        [TestCase(@"return function(ctx) return { { kind = 'effect_spec', effectTypeId = 'effect.unknown', recipient = ctx.playerId, amount = 1 } } end", LuaFailureCode.UnknownEffectType)]
        public void InvalidEffectSpec_IsRejectedByCSharpNormalizer(string source, LuaFailureCode expectedCode)
        {
            LuaInvocationResult result = Invoke(source);

            Assert.That(result.Status, Is.EqualTo(LuaInvocationStatus.Faulted));
            Assert.That(result.FailureCode, Is.EqualTo(expectedCode));
            Assert.That(result.Effects, Is.Empty);
        }

        [TestCase(@"return function(ctx) return os.time() end", LuaFailureCode.ForbiddenApi)]
        [TestCase(@"return function(ctx) ctx.playerId = 'p2'; return {} end", LuaFailureCode.ReadOnlySnapshot)]
        [TestCase(@"return function(ctx) error('script_fault') end", LuaFailureCode.ScriptError)]
        public void ForbiddenApiReadOnlyContextAndExceptions_AreFaulted(string source, LuaFailureCode expectedCode)
        {
            LuaInvocationResult result = Invoke(source);

            Assert.That(result.Status, Is.EqualTo(LuaInvocationStatus.Faulted));
            Assert.That(result.FailureCode, Is.EqualTo(expectedCode));
        }

        [Test]
        public void InfiniteLoop_ExhaustsInstructionBudget()
        {
            const string source = @"
return function(ctx)
    while true do
        local x = 1 + 1
    end
end";
            LuaExecutionBudget budget = new LuaExecutionBudget(
                maxInstructions: 32,
                maxMilliseconds: 1000,
                maxTableDepth: 8,
                maxTableEntries: 128,
                maxEffectSpecs: 16,
                maxScriptLength: 32 * 1024);

            LuaInvocationResult result = Invoke(source, budget);

            Assert.That(result.Status, Is.EqualTo(LuaInvocationStatus.Faulted));
            Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.InstructionBudgetExceeded));
        }

        [Test]
        public void VersionMismatch_IsRejectedBeforeScriptExecution()
        {
            const string source = @"return function(ctx) error('must_not_run') end";
            LuaInvocationResult result = Invoke(source, contextVersion: "2.0.0");

            Assert.That(result.Status, Is.EqualTo(LuaInvocationStatus.Faulted));
            Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.VersionMismatch));
        }

        [Test]
        public void ContentHashMismatch_IsRejectedBeforeScriptExecution()
        {
            const string source = @"return function(ctx) return {} end";
            LuaScriptDefinition definition = new LuaScriptDefinition(
                "content.test",
                "ability.test",
                "handler.test",
                "1.0.0",
                source,
                "0000000000000000000000000000000000000000000000000000000000000000");

            LuaInvocationContext context = CreateContext("1.0.0");
            LuaInvocationResult result = new MoonSharpLuaRuntimeHost().Invoke(definition, context);

            Assert.That(result.Status, Is.EqualTo(LuaInvocationStatus.Faulted));
            Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.ContentHashMismatch));
        }

        [Test]
        public void NonArrayReturnShape_IsRejected()
        {
            const string source = @"return function(ctx) return { named = 1 } end";

            LuaInvocationResult result = Invoke(source);

            Assert.That(result.Status, Is.EqualTo(LuaInvocationStatus.Faulted));
            Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.InvalidReturnShape));
        }

        private static LuaInvocationResult Invoke(
            string source,
            LuaExecutionBudget budget = null,
            string definitionVersion = "1.0.0",
            string contextVersion = null)
        {
            LuaScriptDefinition definition = new LuaScriptDefinition(
                "content.test",
                "ability.test",
                "handler.test",
                definitionVersion,
                source,
                LuaContentHasher.ComputeSha256(source));

            return new MoonSharpLuaRuntimeHost(budget ?? LuaExecutionBudget.Default).Invoke(
                definition,
                CreateContext(contextVersion ?? definitionVersion));
        }

        private static LuaInvocationContext CreateContext(string definitionVersion)
        {
            return new LuaInvocationContext(
                eventId: "event.test",
                eventType: "event.demo",
                playerId: "p1",
                stateRevision: 7,
                definitionVersion: definitionVersion,
                playerCount: 2,
                playerScore: 10,
                goldVoucherCount: 4);
        }
    }
}
