using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Enterprises;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    // 只读展示出口。公开定义不能授予候选访问，调用方仍须核当前可见请求及实例。
    public interface IActiveContentPresentation
    {
        string InstalledContentHash { get; }
        string ContentPackHash { get; }
        string PresentationContentHash { get; }
        bool MatchesConfiguration(GameRuleConfiguration configuration, string activeHash, string installedHash);
        bool TryPresentEnterprise(string scriptPath, YC.Domain.Presentation.GameplayPresentationContext context,
            out YC.Domain.Presentation.GameplayPresentationResult result, out LuaFailureCode code, out string diagnostic);
    }

    internal sealed class ActiveContentPresentation : IActiveContentPresentation
    {
        private readonly ExternalContentPack pack;
        private readonly GameRuleConfiguration configuration;
        public string InstalledContentHash { get; }
        public string ContentPackHash => pack.ContentHash;
        public string PresentationContentHash => pack.PresentationContentHash;
        internal ActiveContentPresentation(ExternalContentPack pack, GameRuleConfiguration configuration, string installedHash)
        {
            this.pack = pack; this.configuration = configuration.Clone(); InstalledContentHash = installedHash;
        }
        public bool MatchesConfiguration(GameRuleConfiguration current, string activeHash, string installedHash)
        {
            if (current == null || activeHash != ContentPackHash || current.ContentPackHash != ContentPackHash ||
                installedHash != InstalledContentHash || current.SchemaVersion != configuration.SchemaVersion ||
                current.MapId != configuration.MapId || current.RuleModeId != configuration.RuleModeId ||
                !new HashSet<string>(current.ActiveRuleModeIds, StringComparer.Ordinal).SetEquals(configuration.ActiveRuleModeIds) ||
                current.PlayerCount != configuration.PlayerCount || current.ConfigurationRevision != configuration.ConfigurationRevision ||
                current.RuleContractVersion != configuration.RuleContractVersion ||
                current.OfficialPreparationContract != configuration.OfficialPreparationContract ||
                current.PlayerStageSchedulingMode != configuration.PlayerStageSchedulingMode ||
                current.EnabledExpansionIds == null || current.EnabledExpansionIds.Count != configuration.EnabledExpansionIds.Count ||
                current.LockedContentPacks == null || configuration.LockedContentPacks == null ||
                current.LockedContentPacks.Count != configuration.LockedContentPacks.Count) return false;
            foreach (var expected in configuration.EnabledExpansionIds)
                if (!current.EnabledExpansionIds.Contains(expected)) return false;
            foreach (var expected in configuration.LockedContentPacks)
                if (expected == null || current.LockedContentPacks.Count(actual => actual != null && actual.PackId == expected.PackId &&
                    actual.Version == expected.Version && actual.ContentHash == expected.ContentHash) != 1) return false;
            return true;
        }
        public bool TryPresentEnterprise(string scriptPath, YC.Domain.Presentation.GameplayPresentationContext context,
            out YC.Domain.Presentation.GameplayPresentationResult result, out LuaFailureCode code, out string diagnostic)
        {
            if (context?.Purpose != "enterprise_detail")
            { result = null; code = LuaFailureCode.InvalidDefinition; diagnostic = "active_presentation_scope_invalid"; return false; }
            return pack.TryPresent(scriptPath, context, out result, out code, out diagnostic);
        }
    }

    public sealed class ActiveContentCatalog : IGameRuleCatalog
    {
        internal ExternalContentPack Pack { get; }
        public ExternalContentPack FrozenPack => Pack;
        private readonly GameSetupRecipe recipe;
        public IReadOnlyList<ExternalContentDefinition> Definitions => Pack.ActiveDefinitions;
        public IReadOnlyList<PlayerDetailDisplayDefinition> PlayerDetailDefinitions => PlayerDetailDisplayCatalog.Read(Definitions, Pack.Presentation);
        public IReadOnlyDictionary<string, string> Scripts { get; }
        public string ContentPackHash { get; }
        public IReadOnlyList<string> EnabledExpansionIds { get; }
        public string RuleModeId { get; }
        public YC.Domain.Presentation.IGameplayTextCatalogSnapshot TextCatalog => Pack.Presentation;
        public IActiveContentPresentation Presentation { get; }
        public GameSetupRecipe SetupRecipe => GameStateCloneService.DeepClone(recipe);
        public GameContentDefinitions ContentDefinitions => new GameContentDefinitions { Entrepreneurs = EntrepreneurContentCatalog.Read(Pack.ActiveDefinitions), UsageTracks = Pack.ActiveDefinitions.SelectMany(d => d.UsageTracks).Select(GameStateCloneService.DeepClone).ToList(), AbilitySources = AbilitySourceLuaCatalog.CreateDeclarations(Pack), Awards = ContentLifecycleLuaCatalog.CreateAwards(Pack), Components = Pack.ActiveDefinitions.Where(d => d.ContentType == "component").Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId, QuantityPolicy = (string)d.Data["quantityPolicy"], CubeAppearance = GameStateCloneService.DeepClone(d.CubeAppearance) }).ToList(), Characters = Pack.CreateCharacters().ToList(), Facilities = Pack.CreateFacilities().ToList(),
            Events = Pack.CreateEvents().ToList(), CityStyles = Pack.CreateCityStyles().ToList(), SpecialActions = Pack.CreateSpecialActions().ToList(),
            EnterpriseDefinitionIds = Pack.ActiveDefinitions.Where(d => d.ContentType == "enterprise").Select(d => d.DefinitionId)
                .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList() };
        internal ActiveContentCatalog(ExternalContentPack pack, GameRuleConfiguration configuration, GameSetupRecipe recipe, IDictionary<string, string> scripts, string hash, string installedHash)
        {
            Pack = pack; this.recipe = GameStateCloneService.DeepClone(recipe); ContentPackHash = hash;
            Presentation = new ActiveContentPresentation(pack, configuration, installedHash);
            EnabledExpansionIds = Array.AsReadOnly(configuration.EnabledExpansionIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            RuleModeId = configuration.RuleModeId;
            Scripts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(scripts));
        }
        public void Register(EffectRegistry registry)
        {
            if (!string.IsNullOrEmpty(registry.ContentPackHash) && registry.ContentPackHash != ContentPackHash) throw new InvalidOperationException("content_version_mismatch");
            registry.ContentPackHash = ContentPackHash;
            registry.FrozenRuleCatalog = this;
            registry.SetupRecipe = SetupRecipe;
            registry.ContentDefinitions = ContentDefinitions;
            LuaContentCatalog.Register(registry, this);
            if (recipe.Preparation != null)
            {
                EntrepreneurFlow.RegisterPreparationSteps(registry, recipe.Preparation, (string)Pack.Manifest["version"]);
                PreparationLuaCatalog.Register(registry, Pack, recipe.Preparation);
            }
            CityDestinationLuaCatalog.Register(registry, Pack);
            FacilityLayerLuaCatalog.Register(registry);
            YC.Domain.Scoring.ScoringModifierQueryService.Register(registry);
        }
        public bool TryValidateConfiguration(GameRuleConfiguration configuration, out string reasonCode)
        {
            bool matches = Presentation.MatchesConfiguration(configuration, ContentPackHash, Presentation.InstalledContentHash);
            reasonCode = matches ? "" : "content_version_mismatch";
            return matches;
        }
    }

    public static class ActiveContentCatalogBuilder
    {
        public static readonly IReadOnlyList<string> ImplementedCapabilities = RuleModeReadiness.ImplementedCapabilities;
        // 静态名单必须对应此内容组合中实际可分发的企业定义；动态结果仍由执行时校验。
        private static void ValidatePreparationEnterpriseReferences(GameSetupRecipe recipe, IReadOnlyList<ExternalContentDefinition> definitions)
        {
            var preparation = recipe.Preparation;
            var enterprise = preparation?.Enterprise;
            if (enterprise == null) return;
            var definitionsById = new HashSet<string>(definitions.Where(item => item.ContentType == "enterprise")
                .Select(item => item.DefinitionId), StringComparer.Ordinal);
            var available = new HashSet<string>(recipe.Entries.Where(entry => entry.Condition.Kind != "entrepreneur_selected" &&
                definitionsById.Contains(entry.DefinitionId)).Select(entry => entry.DefinitionId), StringComparer.Ordinal);
            var dynamicTargets = new HashSet<string>(preparation.DistributionInputs.Select(item => item.Target), StringComparer.Ordinal);
            var required = enterprise.Fixed.Select(item => item.DefinitionId);
            if (!dynamicTargets.Contains("candidateDefinitionIds")) required = required.Concat(enterprise.Pool.CandidateDefinitionIds);
            if (!dynamicTargets.Contains("requiredDefinitionIds")) required = required.Concat(enterprise.Pool.RequiredDefinitionIds);
            foreach (string id in required.Distinct(StringComparer.Ordinal))
                if (!available.Contains(id)) throw new InvalidOperationException("preparation_pool_definition_missing:enterprise:" + id);
        }
        public static ActiveContentCatalog Build(ExternalContentPack core, GameRuleConfiguration configuration, DiyResolvedContent resolved)
            => Build(ExternalContentPack.ComposeDiy(core, resolved), configuration);
        public static ActiveContentCatalog Build(ExternalContentPack installedPack, GameRuleConfiguration configuration)
        {
            if (installedPack == null || configuration == null) throw new ArgumentNullException();
            if (installedPack.DiyComposition == null)
            {
                if (!configuration.TryValidate(GameplayOptionsCatalog.CoreOnly(installedPack), out string reason)) throw new InvalidOperationException(reason);
            }
            else
            {
                ValidateDiyConfiguration(installedPack.DiyComposition, configuration);
                if (!configuration.TryValidate(GameplayOptionsCatalog.CoreOnly(installedPack), out string reason)) throw new InvalidOperationException(reason);
            }
            var enabled = configuration.EnabledExpansionIds;
            var installedDefinitions = installedPack.Definitions;
            var manifest = installedPack.Manifest;
            var required = (manifest["requiredCapabilities"] as JArray)?.Values<string>().ToList() ?? new List<string>();
            foreach (string expansion in enabled.Where(id => id != "core"))
            {
                if (installedPack.SchemaVersion != 3) throw new InvalidOperationException("content_not_ready");
                var declaration = (manifest["expansionDependencies"] as JArray)?.OfType<JObject>().SingleOrDefault(d => (string)d["expansionId"] == expansion);
                if (declaration == null || !(declaration["requires"] is JArray needs) || needs.Values<string>().Any(id => !enabled.Contains(id))) throw new InvalidOperationException("content_not_ready");
                if (declaration["conflicts"] is JArray conflicts && conflicts.Values<string>().Any(enabled.Contains)) throw new InvalidOperationException("expansion_conflict");
                required.AddRange((declaration["requiredCapabilities"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>());
            }
            var modes = (manifest["ruleModes"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(mode => configuration.ActiveRuleModeIds.Contains((string)mode["modeId"])).ToArray();
            if (modes.Length != configuration.ActiveRuleModeIds.Count || modes.Any(mode => !(mode["requires"] is JArray needs) || needs.Values<string>().Any(id => !enabled.Contains(id))))
                throw new InvalidOperationException("rule_mode_dependency_missing");
            installedPack.ApplyOfficialPreparation(manifest, configuration);
            var selectedMechanisms = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mode in modes)
            {
                required.AddRange((mode["requiredCapabilities"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>());
                if (mode["preparation"] != null) required.Add(DiyPreparationDeclaration.Capability);
                selectedMechanisms.UnionWith(GameplayMechanismCatalog.Selected(manifest, mode, enabled, out var mechanismCapabilities));
                required.AddRange(mechanismCapabilities);
            }
            var mechanisms = selectedMechanisms.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var applicable = installedDefinitions.Where(d => d.Enabled && enabled.Contains(d.ExpansionId) && GameplayMechanismCatalog.AppliesToModes(d, configuration.ActiveRuleModeIds, mechanisms)).ToList();
            required.AddRange(applicable.SelectMany(d => d.RequiredCapabilities));
            if (configuration.RuleModeId != "standard" && RuleModeReadiness.Missing(required).Length > 0) throw new InvalidOperationException("rule_mode_not_ready:" + RuleModeReadiness.Missing(required));
            if (required.Any(capability => !ImplementedCapabilities.Contains(capability))) throw new InvalidOperationException("content_not_ready");
            bool infected = enabled.Contains("infected");
            if (enabled.Any(id => !installedDefinitions.Any(d => d.Enabled && d.ExpansionId == id) &&
                (installedPack.DiyComposition == null || !installedPack.DiyComposition.Packs.Any(p => p.Snapshot.Manifest.Expansions.Any(e => e.ExpansionId == id)))))
                throw new InvalidOperationException("content_not_ready");
            var selectedDefinitions = applicable;
            if (selectedDefinitions.Where(d => d.Replaces != null).GroupBy(d => d.Replaces.DefinitionId).Any(g => g.Count() > 1)) throw new InvalidOperationException("replacement_conflict");
            var pack = installedPack.CreateActiveSnapshotForModes(enabled, "", configuration.ActiveRuleModeIds, mechanisms);
            // 属性返回隔离副本；同次构建只读取一次，避免逐配方条目重复复制整个目录。
            var activeDefinitions = pack.ActiveDefinitions;
            PlayerDetailDisplayCatalog.Read(activeDefinitions, pack.Presentation);
            foreach (var definition in activeDefinitions.Where(d => d.ContentType == "event" && d.Replaces != null))
            {
                if (installedPack.GetDefinitionSourcePackId(definition.DefinitionId) != "core") continue;
                var original = installedDefinitions.Single(d => d.DefinitionId == definition.Replaces.DefinitionId);
                var before = (JObject)original.Data.DeepClone(); var after = (JObject)definition.Data.DeepClone();
                foreach (string metadata in new[] { "cardId", "name", "description" }) { before.Remove(metadata); after.Remove(metadata); }
                if (JToken.DeepEquals(Canonicalize(before), Canonicalize(after))) throw new InvalidOperationException("replacement_rules_missing");
            }
            if (activeDefinitions.Select(d => d.RuntimeId).Distinct(StringComparer.Ordinal).Count() != activeDefinitions.Count) throw new InvalidOperationException("duplicate_runtime_id");
            var recipe = SetupRecipeComposer.Compose(pack, manifest, configuration, mechanisms, out var recipeCompositionIdentity);
            if (enabled.Contains("enterprise"))
            {
                var enterprise = LuaContentCatalog.CreateEnterpriseDefinitions(new EffectRegistry(), activeDefinitions, pack.GetScript, pack);
                var officialIds = new HashSet<string>(installedDefinitions.Where(d => installedPack.GetDefinitionSourcePackId(d.DefinitionId) == "core").Select(d => d.DefinitionId), StringComparer.Ordinal);
                var officialActive = activeDefinitions.Where(d => officialIds.Contains(d.RuntimeId)).ToList();
                if (officialActive.Count(d => d.ContentType == "enterprise") != (infected ? 10 : 8) || officialActive.Count(d => d.ContentType == "department") != 3 ||
                    officialActive.Count(d => d.ContentType == "character" && (string)d.Data["acquisitionRole"] == "operator_deck") != (infected ? 16 : 8) ||
                    officialActive.Count(d => d.ContentType == "city_style" && (int?)d.Data["level"] == 1) != (infected ? 10 : 8) || officialActive.Count(d => d.ContentType == "city_style" && (int?)d.Data["level"] == 2) != (infected ? 5 : 4) ||
                    !activeDefinitions.Any(d => d.RuntimeId == "event_red_03" && d.Replaces != null) || !activeDefinitions.Any(d => d.RuntimeId == "event_yellow_02" && d.Replaces != null)) throw new InvalidOperationException("content_not_ready");
            }
            if (mechanisms.Contains("entrepreneur_selection.v1")) EntrepreneurContentCatalog.ValidateReady(pack, recipe);
            if (installedPack.DiyComposition != null) recipe.Entries.AddRange(installedPack.DiyAppendEntries);
            // 在替换定义前按配方槽位筛选，避免用最终卡牌 ID 猜测地图限制。
            if (recipe.Entries.Any(e => e.ApplicableMapIds == null ||
                e.ApplicableMapIds.Any(id => id != StaticMapDefinitions.ThreePlayerMapId && id != StaticMapDefinitions.FourPlayerMapId) ||
                e.ApplicableMapIds.Distinct(StringComparer.Ordinal).Count() != e.ApplicableMapIds.Count))
                throw new InvalidOperationException("setup_recipe_map_restriction_invalid");
            recipe.Entries.RemoveAll(e => e.ApplicableMapIds.Count > 0 && !e.ApplicableMapIds.Contains(configuration.MapId));
            foreach (var entry in recipe.Entries)
            {
                var definition = activeDefinitions.SingleOrDefault(d => d.RuntimeId == entry.DefinitionId || d.DefinitionId == entry.DefinitionId);
                if (definition == null)
                {
                    if (!new[] { "core.player_marker", "core.mobile_city" }.Contains(entry.DefinitionId)) throw new InvalidOperationException("content_not_ready");
                    entry.TypeId = entry.DefinitionId;
                    continue;
                }
                entry.DefinitionId = definition.DefinitionId;
                string type = definition.ItemTypeId;
                if (string.IsNullOrEmpty(type)) type = definition.ContentType == "character" ? "core.basic_character_card" : definition.ContentType == "facility" ? "core.facility_card" : definition.ContentType == "event" ? "core.event_card" : "core.board_card";
                entry.TypeId = type;
            }
            recipe.CardZoneDeclarations = activeDefinitions.Where(d => d.CardZones.Count > 0).Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = GameStateCloneService.DeepClone(d.CardZones) }).ToList();
            recipe.Validate();
            ValidatePreparationEnterpriseReferences(recipe, activeDefinitions);
            if (infected) ValidateInfectedRecipe(pack, recipe);
            var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var definition in activeDefinitions)
            {
                var choices = definition.ContentType == "event" ? definition.Data["choices"] as JArray : null;
                var paths = definition.Abilities.SelectMany(a => new[] { a.Script, a.CleanupScript })
                    .Concat(definition.AbilityHandlers.Select(h => h.Script)).Concat(new[] { (string)definition.Data["effectScript"] })
                    .Concat(choices?.Select(choice => (string)choice["script"]) ?? Enumerable.Empty<string>())
                    .Concat(new[] { (string)(definition.Data["immediateResolve"]?["script"]) });
                foreach (string path in paths.Where(p => !string.IsNullOrEmpty(p))) scripts[path.Replace('\\', '/')] = pack.GetScript(path).Replace("\r\n", "\n");
            }
            var settlementHandlerIds = new HashSet<string>(activeDefinitions.SelectMany(d => d.Abilities).Where(a => a.Settlement != null)
                .SelectMany(a => new[] { a.Settlement.OfferHandlerId, a.Settlement.AvailabilityHandlerId }).Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);
            var settlementHandlers = pack.SettlementHandlers.Where(h => settlementHandlerIds.Contains(h.HandlerId)).OrderBy(h => h.HandlerId, StringComparer.Ordinal).ToList();
            if (settlementHandlers.Count != settlementHandlerIds.Count) throw new InvalidOperationException("content_not_ready");
            foreach (var handler in settlementHandlers) scripts[handler.Script] = pack.GetScript(handler.Script).Replace("\r\n", "\n");
            if (recipe.Preparation != null)
                foreach (var step in recipe.Preparation.BeforeDistribution.Concat(recipe.Preparation.BetweenDistributionAndConfirmation)
                    .Concat(recipe.Preparation.AfterConfirmation).Where(step => step.Kind == "lua"))
                    foreach (string relative in new[] { step.ScriptPath, step.Completion?.ScriptPath }.Where(path => !string.IsNullOrEmpty(path)))
                    {
                        string mounted = SetupRecipeComposer.PreparationScriptPath(step.SourcePackId, step.SourcePackVersion, relative);
                        scripts[mounted] = pack.GetScript(mounted).Replace("\r\n", "\n");
                    }
            if (pack.DiyComposition != null)
                foreach (var binding in (pack.Manifest["diyRuleBindings"] as JArray ?? new JArray()).OfType<JObject>())
                    foreach (string path in new[] { (string)binding["script"], (string)binding["completion"]?["script"] }.Where(p => !string.IsNullOrEmpty(p)))
                        scripts[path] = pack.GetScript(path).Replace("\r\n", "\n");
            foreach (string source in scripts.Values)
            {
                foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(source, @"\b(Effect|Flow)\s*\.\s*([A-Za-z_]\w*)\s*\("))
                    if (!(call.Groups[1].Value == "Effect" ? MoonSharpLuaRuntimeHost.SupportedEffectConstructors : MoonSharpLuaRuntimeHost.SupportedFlowConstructors).Contains(call.Groups[2].Value)) throw new InvalidOperationException("content_not_ready");
            }
            var fingerprint = new StringBuilder(GameProtocolVersions.Identity + "|" + configuration.RuleModeId + "|" + configuration.RuleContractVersion + "|" + manifest["version"] + "|" + string.Join(",", enabled.OrderBy(id => id, StringComparer.Ordinal)));
            if (configuration.AdditionalRuleModeIds?.Count > 0)
                fingerprint.Append("\nmodes.v1:").Append(string.Join(",", configuration.AdditionalRuleModeIds.OrderBy(id => id, StringComparer.Ordinal)));
            if (installedPack.DiyComposition != null) fingerprint.Append("\ndiy.lock.v1:").Append(installedPack.DiyComposition.LockHash);
            if (installedPack.DiyComposition != null) fingerprint.Append("\n").Append(DiyRuleBindingCatalog.Version);
            fingerprint.Append('|').Append(YC.Domain.Presentation.AbilityActionPresentationDefinition.Version);
            fingerprint.Append("\nsetup.composition.v1:").Append(Canonicalize(recipeCompositionIdentity).ToString(Formatting.None));
            fingerprint.Append('\n').Append(Canonicalize(JToken.FromObject(recipe)).ToString(Formatting.None));
            if (!string.IsNullOrEmpty(pack.PresentationContentHash)) fingerprint.Append("\npresentation.v1:").Append(pack.PresentationContentHash);
            fingerprint.Append("\nsettlement.handlers.v1:").Append(Canonicalize(JToken.FromObject(settlementHandlers)).ToString(Formatting.None));
            fingerprint.Append('\n').Append(string.Join(",", required.Distinct().OrderBy(id => id, StringComparer.Ordinal)));
            fingerprint.Append("\nentrepreneur_selection.").Append(EntrepreneurContent.Version);
            fingerprint.Append("\nsetup.v1|round.mainline.v1|collection.v1|enterprise.v1|character.v1|item_transfer.v1|deck_shuffle.v1");
            fingerprint.Append("|ability_sources.v1|declared_actions.v1|authorized_actions.v1|ability_rule_queries.v1|character_lifecycle.").Append(CharacterLifecycleService.DefinitionVersion);
            fingerprint.Append("|operator_dispatch.").Append(OperatorDispatchFlow.DefinitionVersion);
            fingerprint.Append("|double_use_source.").Append(CharacterDoubleUseConsumption.DefinitionVersion);
            fingerprint.Append("|typed_influence_supply.1.0.0|player_areas.2|usage_tracks.").Append(UsageTracks.Version).Append("|cube_appearance.").Append(CubeAppearance.Version);
            fingerprint.Append("|ability_action_executor.").Append(AbilityActionExecutor.Version).Append("|character_action_arguments.").Append(CharacterLifecycleService.ActionArgumentProtocolVersion);
            fingerprint.Append('|').Append(AbilityActionExecutor.CostReceiptPolicyVersion).Append('|').Append(ResourcePaymentSourceService.TokenCostContractVersion);
            fingerprint.Append("|departure_protocol.").Append(InfluenceDepartureProtocol.Version).Append("|influence_place_attachment.").Append(InfluencePlaceAttachment.Version);
            fingerprint.Append("|card_markers.").Append(CardMarkerCarrier.Version);
            // 局部机制/保存形状必须随实际执行器进入内容身份；不提升Host/Room等全局协议。
            fingerprint.Append("|local_mechanisms.v1|strict_remove_input.shortfall.v1");
            fingerprint.Append("|event_card_executor.").Append(EventCardEffectExecutor.DefinitionVersion)
                .Append("|event_choice_candidate.v2|").Append(RuleStateDigestService.DigestVersion);
            fingerprint.Append("|ability_source_continuation.").Append(AbilitySourceLuaCatalog.ContinuationBindingVersion);
            fingerprint.Append("|resource_payment.").Append(ResourceEffectExecutor.PaymentDefinitionVersion);
            fingerprint.Append("|resource_payment_choice.").Append(ResourcePaymentChoiceEffectExecutor.DefinitionVersion);
            fingerprint.Append("|item_transfer_executor.").Append(ItemTransferEffectExecutor.TransferDefinitionVersion).Append("|token_transfer.").Append(ItemTransferService.TokenTransferProtocolVersion.ToString(CultureInfo.InvariantCulture)).Append('|').Append(ItemTransferService.TokenRequestDigestVersion);
            fingerprint.Append("|payment_protocol.2|payment_demand.1|payment_sources.1|payment_total_max.").Append(ResourcePaymentDemandParser.MaximumTotal.ToString(CultureInfo.InvariantCulture));
            fingerprint.Append('|').Append(ResourcePaymentDraftHost.SourceQueryVersion).Append(".protocol.").Append(ResourcePaymentDraftHost.SourceQueryProtocolVersion.ToString(CultureInfo.InvariantCulture))
                .Append(".page_size.").Append(ResourcePaymentDraftHost.SourceQueryItemPageSize.ToString(CultureInfo.InvariantCulture));
            fingerprint.Append("|retired_card_load.v1|").Append(ItemTransferEffectExecutor.IndependentItemFailurePolicyVersion);
            fingerprint.Append('|').Append(InfluenceSelectionFlow.ProtocolVersion);
            fingerprint.Append('|').Append(FacilityBuildContract.Version).Append('|').Append(ExplorationContract.Version);
            fingerprint.Append("|immediate_main_action.").Append(GenericLuaEffectExecutor.ImmediateMainActionVersion)
                .Append("|immediate_special.").Append(ImmediateSpecialActionFlow.Version)
                .Append("|").Append(ImmediateSpecialActionFlow.ResponseChildSelectionVersion).Append("|").Append(ImmediateSpecialActionFlow.EmptyArgumentsVersion)
                .Append("|ordinary_special_choice.").Append(OrdinarySpecialActionChoice.Version)
                .Append("|payment_insertion.").Append(ResourcePaymentInsertionService.Version).Append("|payment_insertion_pick.").Append(ResourcePaymentInsertionService.PickItemVersion)
                .Append("|").Append(ResourcePaymentInsertionService.ItemSelectionRecoveryVersion)
                .Append("|transfer_count.").Append(TransferCountChoiceFlow.Version).Append("|").Append(TransferCountChoiceFlow.MovementProofVersion).Append("|").Append(TransferCountChoiceFlow.DeclineContractVersion)
                .Append("|carrier_pair_choice.").Append(CarrierTokenPairReturnChoice.Version);
            fingerprint.Append('|').Append(CharacterCardMovement.Version);
            fingerprint.Append('|').Append(LuaQuerySnapshotBuilder.HandDestinationMetadataVersion);
            fingerprint.Append('|').Append(InfluenceDepartureProtocol.BatchFactsVersion).Append('|').Append(AbilitySourceService.SubscriptionLinkVersion);
            fingerprint.Append("|cleanup_sources.").Append(CleanupSourceService.Version).Append('|').Append(CleanupSourceService.RequestPolicy).Append("|cleanup_entry.1|cleanup_invocation.1");
            fingerprint.Append("|collection_plan.schema.").Append(YC.Domain.Harvest.CollectionPlanService.ProtocolVersion.ToString(CultureInfo.InvariantCulture)).Append(".executor.").Append(YC.Domain.Harvest.CollectionPlanService.Version);
            fingerprint.Append("|facility_build_journal.").Append(YC.Domain.Facilities.FacilityBuildJournal.Version);
            fingerprint.Append('|').Append(FacilityEntryEffectExecutor.EnterpriseDeclinePolicyVersion);
            fingerprint.Append("|facility_layers.").Append(FacilityLayerLuaCatalog.Version).Append("|facility_layer_evidence.").Append(YC.Domain.Facilities.FacilityLayerEvidence.Version);
            fingerprint.Append("|facility_placement_observation.").Append(FacilityBuildEffectExecutor.PlacementObservationProtocolVersion)
                .Append("|facility_placement_validation.").Append(YC.Domain.Facilities.FacilityPlacementObservations.ValidationVersion)
                .Append("|facility_panel_validation.").Append(FacilitySelectionEffectExecutor.PanelValidationVersion)
                .Append("|strict_remove_input.validation.").Append(MoonSharpLuaRuntimeHost.StrictRemoveInputValidationVersion);
            fingerprint.Append("|facility_build_sources.validation.").Append(FacilityBuildEffectExecutor.SourceValidationVersion);
            fingerprint.Append("|operation_main_owner.").Append(OperationWindowService.MainOwnerValidationVersion);
            fingerprint.Append("|").Append(OperationWindowService.EffectBuildAdmissionVersion);
            fingerprint.Append("|").Append(AbilityActionExecutor.DirectAdmissionVersion).Append("|").Append(EffectExecutionBinding.ValidationVersion);
            fingerprint.Append("|").Append(CleanupObligation.Version);
            fingerprint.Append("|").Append(SettlementLuaCatalog.AvailabilityBindingVersion);
            fingerprint.Append("|").Append(SettlementHostCatalog.EligibilityVersion);
            fingerprint.Append("|").Append(SourceFactsLuaCatalog.Version).Append(".page_size.").Append(SourceFactsLuaCatalog.PageSize);
            fingerprint.Append("|").Append(PlayerDetailDisplayCatalog.Version);
            fingerprint.Append("|").Append(AbilityEventLuaFacts.Version);
            fingerprint.Append("|item_transfer_batch.receipt.").Append(ItemTransferService.BatchReceiptProtocolVersion.ToString(CultureInfo.InvariantCulture)).Append('|').Append(ItemTransferService.BatchRequestDigestVersion)
                .Append("|facility_execution_binding.1|facility_candidate_sources.1");
            fingerprint.Append("|final_scoring.").Append(YC.Domain.Scoring.ScoringModifierProtocol.Version).Append("|final_session.1|scoring_control.count_facts.1");
            fingerprint.Append('|').Append(FrozenLuaModuleProtocol.Version).Append("|reward_grant.").Append(RewardGrantEffectExecutor.Version);
            if (infected) fingerprint.Append("|infected.v1|reward.acquire.v1");
            // 多张切片共享同一张已加载原图；一次构建只计算一次原图摘要，指纹字节保持不变。
            var artworkHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var definition in activeDefinitions.OrderBy(d => d.RuntimeId, StringComparer.Ordinal))
            {
                fingerprint.Append('\n').Append(definition.RuntimeId).Append(':').Append(Canonicalize(JToken.FromObject(definition)).ToString(Formatting.None));
                if (definition.ArtworkSlice != null) fingerprint.Append(Canonicalize(JToken.FromObject(definition.ArtworkSlice)).ToString(Formatting.None));
                if (!string.IsNullOrEmpty(definition.Artwork))
                {
                    if (!artworkHashes.TryGetValue(definition.Artwork, out string artworkHash))
                    {
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                            artworkHash = Convert.ToBase64String(sha.ComputeHash(pack.GetArtworkBytes(definition.Artwork)));
                        artworkHashes.Add(definition.Artwork, artworkHash);
                    }
                    fingerprint.Append(artworkHash);
                }
            }
            foreach (var pair in scripts.OrderBy(p => p.Key, StringComparer.Ordinal)) fingerprint.Append('\n').Append(pair.Key).Append(':').Append(pair.Value);
            fingerprint.Append('\n').Append(Canonicalize(JToken.FromObject(ItemTypeRegistry.CreateBuiltIn().Definitions)).ToString(Formatting.None));
            fingerprint.Append('\n').Append(Canonicalize(JToken.FromObject(ZoneTemplateRegistry.CreateBuiltIn(ItemTypeRegistry.CreateBuiltIn()).Definitions.OrderBy(d => d.TemplateId, StringComparer.Ordinal))).ToString(Formatting.None));
            string hash = LuaContentHasher.ComputeSha256(fingerprint.ToString());
            if (!string.IsNullOrEmpty(configuration.ContentPackHash) && configuration.ContentPackHash != hash) throw new InvalidOperationException("content_version_mismatch");
            pack.SetActiveHash(hash);
            pack.UseDefinitionIds = true;
            return new ActiveContentCatalog(pack, configuration, recipe, scripts, hash, installedPack.DiyComposition?.CoreHash ?? installedPack.ContentHash);
        }
        private static void ValidateDiyConfiguration(DiyResolvedContent resolved, GameRuleConfiguration configuration)
        {
            if (!configuration.TryValidateStructure(out string structuralReason)) throw new InvalidOperationException(structuralReason);
            if (configuration.RuleModeId != resolved.ModeId || !new HashSet<string>(configuration.ActiveRuleModeIds, StringComparer.Ordinal).SetEquals(resolved.ModeIds) || configuration.MapId != resolved.MapId || configuration.EnabledExpansionIds == null ||
                configuration.EnabledExpansionIds.Count != resolved.ExpansionIds.Count ||
                configuration.EnabledExpansionIds.Distinct(StringComparer.Ordinal).Count() != configuration.EnabledExpansionIds.Count ||
                !new HashSet<string>(configuration.EnabledExpansionIds, StringComparer.Ordinal).SetEquals(resolved.ExpansionIds))
                throw new InvalidOperationException("diy_locked_configuration_mismatch");
            var expectedLocks = resolved.CreateRuleLocks();
            var actualLocks = configuration.LockedContentPacks.OrderBy(x => x.PackId, StringComparer.Ordinal).ToArray();
            if (actualLocks.Length != expectedLocks.Count || actualLocks.Where((item, index) => item.PackId != expectedLocks[index].PackId ||
                item.Version != expectedLocks[index].Version || item.ContentHash != expectedLocks[index].ContentHash).Any())
                throw new InvalidOperationException("diy_content_lock_mismatch");
            foreach (var locked in resolved.Packs)
            {
                string missing = RuleModeReadiness.Missing(locked.Snapshot.Manifest.RequiredCapabilities);
                if (missing.Length > 0) throw new InvalidOperationException("diy_pack_capability_missing:" + locked.PackId + ":" + missing);
            }
        }
        private static void ValidateInfectedRecipe(ExternalContentPack pack, GameSetupRecipe recipe)
        {
            bool Has(string id, string type, string role, int quantity, bool perPlayer = false) => recipe.Entries.Count(e => e.DefinitionId == id) == 1 && recipe.Entries.Count(e => e.DefinitionId == id && e.TypeId == type && e.InitialZoneRole == role && e.Quantity == quantity && e.PerPlayer == perPlayer) == 1;
            if (recipe.InitialBasicCharacterCount != 8 || recipe.BasicCharacterKeepCount != 5 || recipe.MaxRetainedBasicCharacterCount != 5 ||
                !Has("infected.character.amiya", "core.basic_character_card", "hand", 1, true) ||
                !Has("infected.component.company_marker", "infected.company_marker", "company_markers", 8) ||
                !Has("infected.contract.lifetime_security", ItemTypeRegistry.ContractTokenTypeId, "contracts", 2) ||
                !Has("infected.facility.temporary_settlement", "core.facility_card", "facility_deck", 3) ||
                recipe.Entries.Where(e => e.TypeId == "core.operator_card" && e.InitialZoneRole == "operator_deck" && !e.PerPlayer && e.Condition.Kind == "always").Sum(e => e.Quantity) != 16 ||
                recipe.Entries.Where(e => e.TypeId == "core.generated_character_card" && e.InitialZoneRole == "flowing_shapes" && !e.PerPlayer && e.Condition.Kind == "always").Sum(e => e.Quantity) != 3 ||
                recipe.Entries.Where(e => e.TypeId == "core.award_card" && e.InitialZoneRole == "awards" && !e.PerPlayer).Sum(e => e.Quantity) != 4 ||
                !pack.ActiveDefinitions.Any(d => d.DefinitionId == "infected.enterprise.rhodes_island" && d.RuntimeId == "enterprise.rhodes_island") ||
                pack.ActiveDefinitions.Any(d => d.DefinitionId == "enterprise.rhodes_island")) throw new InvalidOperationException("content_not_ready");
            foreach (string color in new[] { "red", "blue", "yellow" })
                if (!Has("infected.facility.rhodes_office_" + color, "core.facility_card", "facility_deck", 1)) throw new InvalidOperationException("content_not_ready");
            foreach (var entry in recipe.Entries.Where(e => new[] { "core.operator_card", "core.generated_character_card", "core.award_card" }.Contains(e.TypeId)))
                if (entry.Quantity != 1 || entry.PerPlayer || recipe.Entries.Count(e => e.DefinitionId == entry.DefinitionId) != 1 ||
                    entry.TypeId == "core.award_card" && (entry.Condition.Kind != "enterprise_selected" || entry.Condition.RuntimeId != "infected.enterprise.maylander_foundation")) throw new InvalidOperationException("content_not_ready");
            foreach (var entry in recipe.Entries.Where(e => e.TypeId == "infected.company_marker" || e.TypeId == ItemTypeRegistry.ContractTokenTypeId))
                if (entry.Condition.Kind != "enterprise_selected" || entry.Condition.RuntimeId != "infected.enterprise.stonehoove_security") throw new InvalidOperationException("content_not_ready");
            // 缺模式/空脚本不能作为感染者已齐备的证据；实际逐卡生命周期最终由正式集成验收。
            foreach (var definition in pack.ActiveDefinitions.Where(d => d.ExpansionId == "infected" && (d.ContentType == "character" || d.ContentType == "award")))
            {
                if (definition.ContentType == "award" && definition.Abilities.Count == 0 && definition.AbilitySources.Count > 0 &&
                    definition.AbilitySources.All(s => s.Kind == "ruleQuery" && s.Actions.Count == 0 && s.ObservedEvents.Count == 0 &&
                        definition.AbilityHandlers.Any(h => h.HandlerId == s.HandlerRef && !string.IsNullOrWhiteSpace(pack.GetScript(h.Script))))) continue;
                if (definition.Abilities.Count == 0 || definition.ContentType == "character" &&
                    (!definition.Abilities.Any(a => a.Mode == "strategy") || !definition.Abilities.Any(a => a.Mode == "tactic")) ||
                    definition.Abilities.Any(a => string.IsNullOrWhiteSpace(a.Script) ||
                        !System.Text.RegularExpressions.Regex.IsMatch(pack.GetScript(a.Script), @"\b(Effect|Flow)\s*\.\s*\w+\s*\(") &&
                        !(a.SkillType == "persistent" && System.Text.RegularExpressions.Regex.IsMatch(pack.GetScript(a.Script),
                            @"\A\s*return\s+function\s*\(\s*\w*\s*\)\s*return\s*\{\s*\}\s*end\s*\z"))))
                    throw new InvalidOperationException("content_not_ready");
            }
        }
        private static JToken Canonicalize(JToken value) => value is JObject obj ? new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonicalize(p.Value)))) : value is JArray array ? new JArray(array.Select(Canonicalize)) : value.DeepClone();
    }
}
