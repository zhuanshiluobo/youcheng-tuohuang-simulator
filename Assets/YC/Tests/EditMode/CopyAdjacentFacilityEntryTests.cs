using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class CopyAdjacentFacilityEntryTests
    {
        private static GameState State()
        {
            var state = new GameState { GameId = "copy-adjacent", Phase = GamePhase.ActionRound2,
                Round = 1, ActionRound = 2, CurrentPlayerId = 3, ContentDefinitions = new GameContentDefinitions() };
            for (int player = 1; player <= 4; player++) state.Players.Add(new PlayerState { PlayerId = player });
            state.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = "enterprise-board", LocalZoneId = "enterprises",
                TemplateId = "core.zone.enterprise_display", VisibilityPolicyId = ItemZoneVisibility.Public });
            state.ItemZones.Items.Add(new ItemInstanceState { ItemId = "enterprise-instance", DefinitionId = "enterprise-definition",
                TypeId = "core.enterprise", ZoneId = "enterprise-board", PhysicalKind = ItemPhysicalKind.Card });
            state.ItemZones.Zones.Add(new ZoneInstanceState { ZoneId = "player-hand", LocalZoneId = "hand", OwnerPlayerId = 3,
                TemplateId = "core.zone.character_hand", VisibilityPolicyId = ItemZoneVisibility.Owner });
            state.ItemZones.Items.Add(new ItemInstanceState { ItemId = "character-instance", DefinitionId = "character-definition",
                TypeId = "core.character", ZoneId = "player-hand", PhysicalKind = ItemPhysicalKind.Card,
                OwnerSubject = ItemSubjectRef.Player(3), ControllerSubject = ItemSubjectRef.Player(3) });
            return state;
        }

        private static void Place(GameState state, string id, int player, int slot, string color = "blue", bool entry = true)
        {
            state.ContentDefinitions.Facilities.Add(new FacilityCardDefinition { FacilityId = id, Color = color, HasEntryEffect = entry });
            state.Map.Facilities.Add(new FacilityPlacement { ContentInstanceId = id + "-instance", FacilityCardId = id,
                PlayerId = player, CityBoardSlotIndex = slot });
        }

        private static LuaInvocationResult Invoke(GameState state, string source, int? sourceSlot = 4)
        {
            var script = new LuaScriptDefinition("facility.copy-adjacent", "copy_adjacent_entry_effect", "copy_adjacent_entry",
                "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var definition = new LuaHandlerDefinition("copy-adjacent-check", "FacilityEntryActivated", "copy-adjacent", script);
            var entries = new List<NormalizedValueEntry>();
            if (sourceSlot.HasValue) entries.Add(new NormalizedValueEntry { Name = "sourceSlotIndex", Value = NormalizedValue.CreateInteger(sourceSlot.Value) });
            var ruleEvent = new RuleEvent { EventId = "copy-adjacent-event", EventType = definition.EventType, PlayerId = 3,
                HostOnlyPayload = NormalizedValue.CreateObject(entries) };
            var handlerContext = new EffectEventHandlerContext(state, ruleEvent, new EffectNodeRuntimeState { PlayerId = 3 }, null);
            var method = typeof(LuaEffectEventDispatcher).GetMethod("CreateInvocationContext", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var context = (LuaInvocationContext)method.Invoke(null, new object[] { definition, handlerContext });
            return new MoonSharpLuaRuntimeHost().Invoke(script, context);
        }

        private static string ProductionScript()
        {
            string frozenPath = Environment.GetEnvironmentVariable("YC_COPY_ADJACENT_SCRIPT");
            return File.ReadAllText(string.IsNullOrEmpty(frozenPath)
                ? Path.Combine(Environment.CurrentDirectory, "Assets/StreamingAssets/Content/core/lua/facilities/copy_adjacent_entry.lua")
                : frozenPath);
        }

        private static void AssertSuccess(LuaInvocationResult result) => Assert.That(result.IsSuccess, Is.True, result.Diagnostic);

        [Test]
        public void BuiltFacilities_UsesRequestedCityBoardAndPreservesOrderInMixedFacade()
        {
            var state = State();
            Place(state, "first", 3, 5); Place(state, "other", 2, 1); Place(state, "second", 3, 1);
            state.ItemZones.Items.Add(new ItemInstanceState { ItemId = "unbuilt-facility", DefinitionId = "first",
                TypeId = "core.facility", ZoneId = "player-hand", PhysicalKind = ItemPhysicalKind.Card,
                OwnerSubject = ItemSubjectRef.Player(3), ControllerSubject = ItemSubjectRef.Player(3) });
            var result = Invoke(state, @"return function(ctx)
                local own = PlayerData.GetBuiltFacilities(ctx.playerId)
                assert(#own == 2 and own[1].id == 'first-instance' and own[2].id == 'second-instance')
                assert(own[1].owner == ctx.playerId and own[1].slotIndex == 5 and own[1].hasEntryEffect)
                local other = PlayerData.GetBuiltFacilities('p2')
                assert(#other == 1 and other[1].id == 'other-instance' and other[1].owner == 'p2')
                assert(PlayerData.GetBuiltFacilities({ playerId = 'p2' })[1].id == other[1].id)
                assert(#PlayerData.GetBuiltFacilities('p1') == 0)
                local all = Global.Content.FindInstances()
                local enterprise, character, unbuilt = false, false, false
                for _, item in ipairs(all) do
                    if item.id == 'enterprise-instance' then enterprise = true; assert(item.slotIndex == nil) end
                    if item.id == 'character-instance' then character = true; assert(item.slotIndex == nil) end
                    if item.id == 'unbuilt-facility' then unbuilt = true; assert(item.slotIndex == nil) end
                end
                assert(enterprise and character and unbuilt)
                return {}
            end");
            AssertSuccess(result);
        }

        [Test]
        public void CopyAdjacent_PreservesEligibleBranchOrderAndPresentation()
        {
            var state = State();
            Place(state, "vertical", 3, 1); Place(state, "other-player", 2, 5);
            Place(state, "rainbow", 3, 3, "rainbow"); Place(state, "no-entry", 3, 7, "blue", false);
            Place(state, "diagonal", 3, 0); Place(state, "horizontal", 3, 5); Place(state, "source", 3, 4);
            var result = Invoke(state, ProductionScript());
            AssertSuccess(result);
            var effect = result.Effects.Single();
            Assert.That(effect.RawArguments.Properties.Single(p => p.Name == "options").Value.Items.Select(v => v.StringValue),
                Is.EqualTo(new[] { "vertical-instance", "horizontal-instance" }));
            Assert.That(effect.NestedEffectGroups.Select(group => group.Single().RawArguments.Properties.Single(p => p.Name == "instanceId").Value.StringValue),
                Is.EqualTo(new[] { "vertical-instance", "horizontal-instance" }));
            Assert.That(effect.Presentation.Interaction.Options.Select(option => option.CandidateId),
                Is.EqualTo(new[] { "vertical-instance", "horizontal-instance" }));
            List<EffectSpec> compiled; LuaEffectCompilationFault fault;
            Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext {
                PlayerId = 3, SourceId = result.ContentId, AbilityId = "copy_adjacent_entry_effect", StableKeyPrefix = "copy-adjacent-check"
            }, out compiled, out fault), Is.True, fault?.Diagnostic);
            Assert.That(compiled.Count, Is.EqualTo(1));
        }

        [Test]
        public void CopyAdjacent_DoesNotWrapAcrossRowsOrDefaultMissingSource()
        {
            var state = State(); Place(state, "wrap", 3, 3); Place(state, "vertical", 3, 5);
            var result = Invoke(state, ProductionScript(), 2);
            AssertSuccess(result);
            Assert.That(result.Effects.Single().RawArguments.Properties.Single(p => p.Name == "options").Value.Items.Select(v => v.StringValue),
                Is.EqualTo(new[] { "vertical-instance" }));
            var missingSource = Invoke(state, ProductionScript(), null);
            Assert.That(missingSource.IsSuccess, Is.False);
            Assert.That(missingSource.Diagnostic, Does.Contain("nil"));
        }

        [Test]
        public void CopyAdjacent_NoEligibleNeighborReturnsNoEffects()
        {
            var state = State(); Place(state, "source", 3, 0); Place(state, "rainbow", 3, 1, "rainbow");
            Place(state, "other-player", 2, 3); Place(state, "distant", 3, 5);
            var result = Invoke(state, ProductionScript(), 0);
            AssertSuccess(result);
            Assert.That(result.Effects, Is.Empty);
        }

        [Test]
        public void BuiltFacilities_IsReadOnlyAndRejectsInvalidPlayerUsingExistingContract()
        {
            var state = State(); Place(state, "entry", 3, 1);
            foreach (string mutation in new[] { "facilities[1].slotIndex = 8", "facilities[1] = {}" })
            {
                var result = Invoke(state, "return function(ctx) local facilities = PlayerData.GetBuiltFacilities(ctx.playerId); " + mutation + "; return {} end");
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.Diagnostic, Does.Contain("nmc_read_only_snapshot"));
            }
            foreach (string argument in new[] { "'p99'", "nil" })
            {
                var result = Invoke(state, "return function(ctx) PlayerData.GetBuiltFacilities(" + argument + "); return {} end");
                var existing = Invoke(state, "return function(ctx) PlayerData.GetResources(" + argument + "); return {} end");
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.FailureCode, Is.EqualTo(existing.FailureCode));
                Assert.That(result.Diagnostic.Split(':')[0], Is.EqualTo(existing.Diagnostic.Split(':')[0]));
            }
            Assert.That(state.Map.Facilities.Single().CityBoardSlotIndex, Is.EqualTo(1));
        }

        [Test]
        public void BudgetBoundary_BusinessAndUnidentifiedTablesKeepOriginalLimits()
        {
            var state = State();
            var deepBusiness = Invoke(state, @"return function(ctx)
                local effect = Effect.ActivateFacilityEntry({ player = ctx.playerId, instanceId = 'entry-instance' })
                local head = effect
                for i = 1, 8 do head.child = {}; head = head.child end
                return { effect }
            end");
            Assert.That(deepBusiness.IsSuccess, Is.False);
            Assert.That(deepBusiness.FailureCode, Is.EqualTo(LuaFailureCode.TableDepthExceeded));

            var crowdedBusiness = Invoke(state, @"return function(ctx)
                local effect = Effect.ActivateFacilityEntry({ player = ctx.playerId, instanceId = 'entry-instance' })
                effect.fields = {}
                for i = 1, 129 do effect.fields[i] = i end
                return { effect }
            end");
            Assert.That(crowdedBusiness.IsSuccess, Is.False);
            Assert.That(crowdedBusiness.FailureCode, Is.EqualTo(LuaFailureCode.TableEntryLimitExceeded));

            var unidentified = Invoke(state, @"return function(ctx)
                local ordinary = { presentation = {} }
                local head = ordinary.presentation
                for i = 1, 8 do head.child = {}; head = head.child end
                return { ordinary }
            end");
            Assert.That(unidentified.IsSuccess, Is.False);
            Assert.That(unidentified.FailureCode, Is.EqualTo(LuaFailureCode.TableDepthExceeded));
        }

        [Test]
        public void BudgetBoundary_PresentationKeepsClosedTypeDepthAndElementLimits()
        {
            var state = State();
            foreach (var scenario in new[] {
                new[] { "local head = effect.presentation; for i = 1, 9 do head.child = {}; head = head.child end", "显示声明嵌套过深" },
                new[] { "effect.presentation.interaction = { options = {} }; for i = 1, 129 do effect.presentation.interaction.options[i] = { candidateId = 'entry-instance' } end", "显示表项过多" },
                new[] { "effect.presentation.unknown = 'unregistered'", "未知显示字段" }
            })
            {
                var result = Invoke(state, "return function(ctx) local effect = Effect.ActivateFacilityEntry({ player = ctx.playerId, instanceId = 'entry-instance' }); " +
                    "effect.presentation = {}; " + scenario[0] + "; return { effect } end");
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.FailureCode, Is.EqualTo(LuaFailureCode.InvalidEffectSpec));
                Assert.That(result.Diagnostic, Does.Contain(scenario[1]));
            }
        }
    }
}
