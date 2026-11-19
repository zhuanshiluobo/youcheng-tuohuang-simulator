using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class PreparationFlowPlanTests
    {
        private static PreparationStepDeclaration Step(string id, string execution = "once") =>
            new PreparationStepDeclaration {
                StepId = id, SourcePackId = "example.mode", SourcePackVersion = "1.0.0",
                Kind = "builtin", HandlerId = "core.setup.example", HandlerVersion = "1.0.0", Execution = execution
            };

        private static PreparationFlowPlan EnterprisePlan() => new PreparationFlowPlan {
            ModeId = "example.mode",
            Enterprise = new PreparationEnterprisePlan {
                Pool = new PreparationPoolPolicy { Kind = "requiredRandom", Count = 8 },
                PlayerCounts = new List<PreparationEnterpriseCountRule> {
                    new PreparationEnterpriseCountRule { PlayerCount = 2, Draw = 4, Keep = 2, FinalCount = 4 },
                    new PreparationEnterpriseCountRule { PlayerCount = 3, Draw = 2, Keep = 1, ExtraRandom = 1, FinalCount = 4 },
                    new PreparationEnterpriseCountRule { PlayerCount = 4, Draw = 2, Keep = 1, FinalCount = 4 }
                }
            }
        };

        [Test]
        public void EmptyWindowsAndOfficialEnterpriseCountsAreValid()
        {
            var plan = EnterprisePlan();
            Assert.DoesNotThrow(plan.Validate);
            Assert.DoesNotThrow(new PreparationFlowPlan { ModeId = "standard" }.Validate);
        }

        [Test]
        public void StepsPreserveDeclaredWindowOrderAndIdsAreUniqueAcrossWindows()
        {
            var plan = EnterprisePlan();
            plan.BeforeDistribution.Add(Step("before.first"));
            plan.BeforeDistribution.Add(Step("before.second", "eachInOrder"));
            plan.BetweenDistributionAndConfirmation.Add(Step("middle", "eachIndependent"));
            plan.AfterConfirmation.Add(Step("after"));
            Assert.DoesNotThrow(plan.Validate);
            Assert.That(plan.BeforeDistribution.Select(x => x.StepId), Is.EqualTo(new[] { "before.first", "before.second" }));
            plan.AfterConfirmation.Add(Step("before.first"));
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Does.StartWith("preparation_step_duplicate:"));
        }

        [TestCase("eachRandom")]
        [TestCase("")]
        public void InvalidExecutionModeIsRejected(string execution)
        {
            var plan = EnterprisePlan();
            plan.BeforeDistribution.Add(Step("before", execution));
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Does.StartWith("preparation_step_invalid:"));
        }

        [Test]
        public void OutputBindingMustReferToBeforeWindowWithMatchingPlayerScope()
        {
            var plan = EnterprisePlan();
            plan.BeforeDistribution.Add(Step("choose", "eachInOrder"));
            plan.AfterConfirmation.Add(Step("later"));
            var binding = new PreparationOutputBinding { StepId = "choose", EffectTypeId = "effect.choose",
                EffectStableKey = "choice:0", ResultField = "selectedIds", PlayerScope = "each", Target = "candidateDefinitionIds" };
            plan.DistributionInputs.Add(binding);
            Assert.DoesNotThrow(plan.Validate);
            binding.PlayerScope = "once";
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Does.StartWith("preparation_output_scope_mismatch:"));
            binding.PlayerScope = "each";
            binding.StepId = "later";
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Does.StartWith("preparation_output_step_not_before:"));
        }

        [Test]
        public void EnterprisePoolsMustSupplyEveryCandidateAndPublicExtra()
        {
            var plan = EnterprisePlan();
            plan.Enterprise.Pool.Count = 7;
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Is.EqualTo("preparation_enterprise_count_invalid:2"));
            plan.Enterprise.Pool.Count = 8;
            plan.Enterprise.PlayerCounts.Single(x => x.PlayerCount == 3).FinalCount = 3;
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Is.EqualTo("preparation_enterprise_count_invalid:3"));
        }

        [Test]
        public void ExplicitEnterprisePoolRejectsDuplicatesAndFixedItemsOutsideIt()
        {
            var plan = EnterprisePlan();
            var pool = plan.Enterprise.Pool;
            pool.Kind = "fixed";
            pool.CandidateDefinitionIds = Enumerable.Range(0, 9).Select(i => "example.enterprise." + i).ToList();
            pool.Count = pool.CandidateDefinitionIds.Count;
            plan.Enterprise.Fixed.Add(new PreparationFixedEnterprise { DefinitionId = "example.enterprise.0", OwnerOrderIndex = 0 });
            foreach (var rule in plan.Enterprise.PlayerCounts) rule.FinalCount++;
            Assert.DoesNotThrow(plan.Validate);
            plan.Enterprise.Fixed[0].DefinitionId = "example.enterprise.unknown";
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Does.StartWith("preparation_enterprise_fixed_outside_pool:"));
            plan.Enterprise.Fixed[0].DefinitionId = "example.enterprise.0";
            pool.CandidateDefinitionIds[1] = pool.CandidateDefinitionIds[0];
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Does.StartWith("preparation_pool_invalid:"));
        }

        [Test]
        public void RequiredAndRandomCityStylePoolRejectsInsufficientOrUnknownRequiredDefinitions()
        {
            var plan = EnterprisePlan();
            plan.CityStyles = new PreparationCityStylePlan {
                LevelOne = new PreparationPoolPolicy { Kind = "requiredRandom", Count = 2,
                    CandidateDefinitionIds = new List<string> { "style.a", "style.b", "style.c" },
                    RequiredDefinitionIds = new List<string> { "style.a" } },
                LevelTwo = new PreparationPoolPolicy { Kind = "requiredRandom", Count = 0 }
            };
            Assert.DoesNotThrow(plan.Validate);
            plan.CityStyles.LevelOne.Count = 4;
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Does.StartWith("preparation_pool_random_invalid:"));
            plan.CityStyles.LevelOne.Count = 2;
            plan.CityStyles.LevelOne.RequiredDefinitionIds[0] = "style.missing";
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Does.StartWith("preparation_pool_random_invalid:"));
        }

        [TestCase("../escape.lua")]
        [TestCase("lua/../escape.lua")]
        [TestCase("lua/x\\escape.lua")]
        [TestCase("lua/x?.lua")]
        public void LuaSourcePathCannotEscapeOrUseInvalidWindowsNames(string path)
        {
            var plan = EnterprisePlan();
            var step = Step("lua.step");
            step.Kind = "lua";
            step.ScriptPath = path;
            step.ScriptHash = new string('a', 64);
            plan.BeforeDistribution.Add(step);
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Does.StartWith("preparation_lua_invalid:"));
            step.ScriptPath = "lua/setup/step.lua";
            Assert.DoesNotThrow(plan.Validate);
        }

        [TestCase("01.0.0")]
        [TestCase("2147483648.0.0")]
        [TestCase("1.-1.0")]
        public void SourceVersionUsesTheSameSyntaxAsDiyPackages(string version)
        {
            var plan = EnterprisePlan();
            var step = Step("step");
            step.SourcePackVersion = version;
            plan.BeforeDistribution.Add(step);
            Assert.That(Assert.Throws<InvalidOperationException>(plan.Validate).Message, Does.StartWith("preparation_step_invalid:"));
        }

        [Test]
        public void LegacyRecipeOmitsNullPreparationFromIdentityAndNewPlanClonesIndependently()
        {
            var recipe = GameSetupRecipe.CreateRegisteredStandard(StaticMapDefinitions.FourPlayerMapId);
            recipe.Validate();
            Assert.That(JObject.FromObject(recipe).Property("Preparation"), Is.Null);
            recipe.Preparation = EnterprisePlan();
            recipe.Preparation.BeforeDistribution.Add(Step("before"));
            recipe.Validate();
            var clone = GameStateCloneService.DeepClone(recipe);
            clone.Preparation.BeforeDistribution[0].StepId = "changed";
            Assert.That(recipe.Preparation.BeforeDistribution[0].StepId, Is.EqualTo("before"));
            Assert.That((string)JObject.FromObject(recipe)["Preparation"]["ModeId"], Is.EqualTo("example.mode"));
        }
    }
}
