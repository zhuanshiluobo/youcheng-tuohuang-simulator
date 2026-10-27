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
                current.PlayerCount != configuration.PlayerCount || current.ConfigurationRevision != configuration.ConfigurationRevision ||
                current.RuleContractVersion != configuration.RuleContractVersion ||
                current.PlayerStageSchedulingMode != configuration.PlayerStageSchedulingMode ||
                current.EnabledExpansionIds == null || current.EnabledExpansionIds.Count != configuration.EnabledExpansionIds.Count) return false;
            foreach (var expected in configuration.EnabledExpansionIds)
                if (!current.EnabledExpansionIds.Contains(expected)) return false;
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

    public sealed class ActiveContentCatalog
    {
        internal ExternalContentPack Pack { get; }
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
            registry.SetupRecipe = SetupRecipe;
            registry.ContentDefinitions = ContentDefinitions;
            LuaContentCatalog.Register(registry, this);
            CityDestinationLuaCatalog.Register(registry, Pack);
            FacilityLayerLuaCatalog.Register(registry);
            YC.Domain.Scoring.ScoringModifierQueryService.Register(registry);
        }
    }

    public static class ActiveContentCatalogBuilder
    {
        public static readonly IReadOnlyList<string> ImplementedCapabilities = RuleModeReadiness.ImplementedCapabilities;
        public static ActiveContentCatalog Build(ExternalContentPack installedPack, GameRuleConfiguration configuration)
        {
            if (installedPack == null || configuration == null) throw new ArgumentNullException();
            if (!configuration.TryValidate(out string reason)) throw new InvalidOperationException(reason);
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
            if (configuration.RuleModeId != "standard")
            {
                var mode = (manifest["ruleModes"] as JArray)?.OfType<JObject>().SingleOrDefault(m => (string)m["modeId"] == configuration.RuleModeId);
                if (mode == null || !(mode["requires"] is JArray needs) || needs.Values<string>().Any(id => !enabled.Contains(id))) throw new InvalidOperationException("rule_mode_dependency_missing");
                required.AddRange((mode["requiredCapabilities"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>());
                required.AddRange(RuleModeReadiness.EntrepreneurRequirements);
            }
            var applicable = installedDefinitions.Where(d => d.Enabled && enabled.Contains(d.ExpansionId) && (d.ApplicableModes.Count == 0 || d.ApplicableModes.Contains(configuration.RuleModeId))).ToList();
            required.AddRange(applicable.SelectMany(d => d.RequiredCapabilities));
            if (configuration.RuleModeId != "standard" && RuleModeReadiness.Missing(required).Length > 0) throw new InvalidOperationException("rule_mode_not_ready:" + RuleModeReadiness.Missing(required));
            if (required.Any(capability => !ImplementedCapabilities.Contains(capability))) throw new InvalidOperationException("content_not_ready");
            bool infected = enabled.Contains("infected");
            if (enabled.Any(id => !installedDefinitions.Any(d => d.Enabled && d.ExpansionId == id))) throw new InvalidOperationException("content_not_ready");
            var selectedDefinitions = applicable;
            if (selectedDefinitions.Where(d => d.Replaces != null).GroupBy(d => d.Replaces.DefinitionId).Any(g => g.Count() > 1)) throw new InvalidOperationException("replacement_conflict");
            var pack = installedPack.CreateActiveSnapshot(enabled, mode: configuration.RuleModeId);
            // 属性返回隔离副本；同次构建只读取一次，避免逐配方条目重复复制整个目录。
            var activeDefinitions = pack.ActiveDefinitions;
            PlayerDetailDisplayCatalog.Read(activeDefinitions, pack.Presentation);
            foreach (var definition in activeDefinitions.Where(d => d.ContentType == "event" && d.Replaces != null))
            {
                var original = installedDefinitions.Single(d => d.DefinitionId == definition.Replaces.DefinitionId);
                var before = (JObject)original.Data.DeepClone(); var after = (JObject)definition.Data.DeepClone();
                foreach (string metadata in new[] { "cardId", "name", "description" }) { before.Remove(metadata); after.Remove(metadata); }
                if (JToken.DeepEquals(Canonicalize(before), Canonicalize(after))) throw new InvalidOperationException("replacement_rules_missing");
            }
            if (activeDefinitions.Select(d => d.RuntimeId).Distinct(StringComparer.Ordinal).Count() != activeDefinitions.Count) throw new InvalidOperationException("duplicate_runtime_id");
            var applicableRecipes = pack.SetupRecipes.Where(r => r.ApplicableModes.Count == 0 || r.ApplicableModes.Contains(configuration.RuleModeId)).ToList();
            foreach (var r in applicableRecipes) if (RuleModeReadiness.Missing(r.RequiredCapabilities).Length > 0) throw new InvalidOperationException("recipe_capability_missing:" + r.RecipeId);
            GameSetupRecipe recipe = applicableRecipes.SingleOrDefault(r => r.RecipeId == "standard");
            if (recipe == null) throw new InvalidOperationException("content_not_ready");
            if (enabled.Contains("enterprise"))
            {
                var supplement = applicableRecipes.SingleOrDefault(r => r.RecipeId == "enterprise");
                if (supplement == null) throw new InvalidOperationException("content_not_ready");
                recipe.Entries.AddRange(supplement.Entries);
                recipe.RecipeId = supplement.RecipeId; recipe.EnterpriseEntrance = true;
                recipe.InitialBasicCharacterCount = supplement.InitialBasicCharacterCount;
                recipe.BasicCharacterKeepCount = supplement.BasicCharacterKeepCount; recipe.MaxRetainedBasicCharacterCount = supplement.MaxRetainedBasicCharacterCount;
                recipe.CityStyleLevelOneDrawCount = supplement.CityStyleLevelOneDrawCount; recipe.CityStyleLevelTwoDrawCount = supplement.CityStyleLevelTwoDrawCount;
                var enterprise = LuaContentCatalog.CreateEnterpriseDefinitions(new EffectRegistry(), activeDefinitions, pack.GetScript, pack);
                if (activeDefinitions.Count(d => d.ContentType == "enterprise") != (infected ? 10 : 8) || activeDefinitions.Count(d => d.ContentType == "department") != 3 ||
                    activeDefinitions.Count(d => d.ContentType == "character" && (string)d.Data["acquisitionRole"] == "operator_deck") != (infected ? 16 : 8) ||
                    activeDefinitions.Count(d => d.ContentType == "city_style" && (int?)d.Data["level"] == 1) != (infected ? 10 : 8) || activeDefinitions.Count(d => d.ContentType == "city_style" && (int?)d.Data["level"] == 2) != (infected ? 5 : 4) ||
                    !activeDefinitions.Any(d => d.RuntimeId == "event_red_03" && d.Replaces != null) || !activeDefinitions.Any(d => d.RuntimeId == "event_yellow_02" && d.Replaces != null)) throw new InvalidOperationException("content_not_ready");
            }
            if (infected)
            {
                var supplement = applicableRecipes.SingleOrDefault(r => r.RecipeId == "infected");
                if (supplement == null) throw new InvalidOperationException("content_not_ready");
                recipe.Entries.AddRange(supplement.Entries);
                recipe.RecipeId = supplement.RecipeId;
                recipe.InitialBasicCharacterCount = supplement.InitialBasicCharacterCount;
                recipe.BasicCharacterKeepCount = supplement.BasicCharacterKeepCount;
                recipe.MaxRetainedBasicCharacterCount = supplement.MaxRetainedBasicCharacterCount;
                // 公共设置提升原八张干员的准备条件，保持原配方键及同一实例，不再生成第二副牌堆。
                foreach (var entry in recipe.Entries.Where(e => e.InitialZoneRole == "operator_deck")) entry.Condition = new SetupRecipeCondition();
            }
            if (configuration.RuleModeId == "entrepreneur")
            {
                var supplement = applicableRecipes.SingleOrDefault(r => r.RecipeId == "entrepreneur");
                if (supplement == null) throw new InvalidOperationException("entrepreneur_recipe_missing");
                recipe.Entries.AddRange(supplement.Entries);
                EntrepreneurContentCatalog.ValidateReady(pack, recipe);
            }
            recipe.OperatorDrawCount = infected || configuration.PlayerCount == 2 ? 3 : 2;
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
            // 事件牌池与所选地图一致；少于四名玩家也可选择四人地图。
            recipe.Entries.RemoveAll(e => configuration.MapId == StaticMapDefinitions.ThreePlayerMapId && activeDefinitions.Any(d => d.DefinitionId == e.DefinitionId && d.ContentType == "event" && EventCardDatabase.IsFourPlayerOnly(d.RuntimeId)));
            recipe.CardZoneDeclarations = activeDefinitions.Where(d => d.CardZones.Count > 0).Select(d => new ItemCardZoneDeclaration { DefinitionId = d.DefinitionId, CardZones = GameStateCloneService.DeepClone(d.CardZones) }).ToList();
            recipe.Validate();
            if (infected) ValidateInfectedRecipe(pack, recipe);
            var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var definition in activeDefinitions)
            {
                var paths = definition.Abilities.SelectMany(a => new[] { a.Script, a.CleanupScript }).Concat(definition.AbilityHandlers.Select(h => h.Script)).Concat(new[] { (string)definition.Data["effectScript"] });
                foreach (string path in paths.Where(p => !string.IsNullOrEmpty(p))) scripts[path.Replace('\\', '/')] = pack.GetScript(path).Replace("\r\n", "\n");
            }
            var settlementHandlerIds = new HashSet<string>(activeDefinitions.SelectMany(d => d.Abilities).Where(a => a.Settlement != null)
                .SelectMany(a => new[] { a.Settlement.OfferHandlerId, a.Settlement.AvailabilityHandlerId }).Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);
            var settlementHandlers = pack.SettlementHandlers.Where(h => settlementHandlerIds.Contains(h.HandlerId)).OrderBy(h => h.HandlerId, StringComparer.Ordinal).ToList();
            if (settlementHandlers.Count != settlementHandlerIds.Count) throw new InvalidOperationException("content_not_ready");
            foreach (var handler in settlementHandlers) scripts[handler.Script] = pack.GetScript(handler.Script).Replace("\r\n", "\n");
            foreach (string source in scripts.Values)
            {
                foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(source, @"\b(Effect|Flow)\s*\.\s*([A-Za-z_]\w*)\s*\("))
                    if (!(call.Groups[1].Value == "Effect" ? MoonSharpLuaRuntimeHost.SupportedEffectConstructors : MoonSharpLuaRuntimeHost.SupportedFlowConstructors).Contains(call.Groups[2].Value)) throw new InvalidOperationException("content_not_ready");
            }
            var fingerprint = new StringBuilder(GameProtocolVersions.Identity + "|" + configuration.RuleModeId + "|" + configuration.RuleContractVersion + "|" + manifest["version"] + "|" + string.Join(",", enabled.OrderBy(id => id, StringComparer.Ordinal)));
            fingerprint.Append('|').Append(YC.Domain.Presentation.AbilityActionPresentationDefinition.Version);
            fingerprint.Append('\n').Append(Canonicalize(JToken.FromObject(recipe)).ToString(Formatting.None));
            if (!string.IsNullOrEmpty(pack.PresentationContentHash)) fingerprint.Append("\npresentation.v1:").Append(pack.PresentationContentHash);
            fingerprint.Append("\nsettlement.handlers.v1:").Append(Canonicalize(JToken.FromObject(settlementHandlers)).ToString(Formatting.None));
            fingerprint.Append('\n').Append(string.Join(",", required.Distinct().OrderBy(id => id, StringComparer.Ordinal)));
            fingerprint.Append("\nentrepreneur_selection.").Append(EntrepreneurContent.Version);
            fingerprint.Append("\nsetup.v1|round.mainline.v1|collection.v1|enterprise.v1|character.v1|item_transfer.v1|deck_shuffle.v1");
            fingerprint.Append("|ability_sources.v1|declared_actions.v1|authorized_actions.v1|ability_rule_queries.v1|character_lifecycle.").Append(CharacterLifecycleService.DefinitionVersion);
            fingerprint.Append("|typed_influence_supply.1.0.0|player_areas.2|usage_tracks.").Append(UsageTracks.Version).Append("|cube_appearance.").Append(CubeAppearance.Version);
            fingerprint.Append("|ability_action_executor.").Append(AbilityActionExecutor.Version).Append("|character_action_arguments.").Append(CharacterLifecycleService.ActionArgumentProtocolVersion);
            fingerprint.Append('|').Append(AbilityActionExecutor.CostReceiptPolicyVersion).Append('|').Append(ResourcePaymentSourceService.TokenCostContractVersion);
            fingerprint.Append("|departure_protocol.").Append(InfluenceDepartureProtocol.Version).Append("|influence_place_attachment.").Append(InfluencePlaceAttachment.Version);
            fingerprint.Append("|card_markers.").Append(CardMarkerCarrier.Version);
            // 局部机制/保存形状必须随实际执行器进入内容身份；不提升Host/Room等全局协议。
            fingerprint.Append("|local_mechanisms.v1|strict_remove_input.shortfall.v1");
            fingerprint.Append("|resource_payment.").Append(ResourceEffectExecutor.PaymentDefinitionVersion);
            fingerprint.Append("|resource_payment_choice.").Append(ResourcePaymentChoiceEffectExecutor.DefinitionVersion);
            fingerprint.Append("|item_transfer_executor.").Append(ItemTransferEffectExecutor.TransferDefinitionVersion).Append("|token_transfer.").Append(ItemTransferService.TokenTransferProtocolVersion.ToString(CultureInfo.InvariantCulture)).Append('|').Append(ItemTransferService.TokenRequestDigestVersion);
            fingerprint.Append("|payment_protocol.2|payment_demand.1|payment_sources.1|payment_total_max.").Append(ResourcePaymentDemandParser.MaximumTotal.ToString(CultureInfo.InvariantCulture));
            fingerprint.Append('|').Append(ResourcePaymentDraftHost.SourceQueryVersion).Append(".protocol.").Append(ResourcePaymentDraftHost.SourceQueryProtocolVersion.ToString(CultureInfo.InvariantCulture))
                .Append(".page_size.").Append(ResourcePaymentDraftHost.SourceQueryItemPageSize.ToString(CultureInfo.InvariantCulture));
            fingerprint.Append("|retired_card_load.v1|").Append(ItemTransferEffectExecutor.IndependentItemFailurePolicyVersion);
            fingerprint.Append('|').Append(InfluenceDepartureProtocol.BatchFactsVersion).Append('|').Append(AbilitySourceService.SubscriptionLinkVersion);
            fingerprint.Append("|cleanup_sources.").Append(CleanupSourceService.Version).Append('|').Append(CleanupSourceService.RequestPolicy).Append("|cleanup_entry.1|cleanup_invocation.1");
            fingerprint.Append("|collection_plan.schema.").Append(YC.Domain.Harvest.CollectionPlanService.ProtocolVersion.ToString(CultureInfo.InvariantCulture)).Append(".executor.").Append(YC.Domain.Harvest.CollectionPlanService.Version);
            fingerprint.Append("|facility_build_journal.").Append(YC.Domain.Facilities.FacilityBuildJournal.Version);
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
            return new ActiveContentCatalog(pack, configuration, recipe, scripts, hash, installedPack.ContentHash);
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
                    definition.Abilities.Any(a => string.IsNullOrWhiteSpace(a.Script) || !System.Text.RegularExpressions.Regex.IsMatch(pack.GetScript(a.Script), @"\b(Effect|Flow)\s*\.\s*\w+\s*\(")))
                    throw new InvalidOperationException("content_not_ready");
            }
        }
        private static JToken Canonicalize(JToken value) => value is JObject obj ? new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonicalize(p.Value)))) : value is JArray array ? new JArray(array.Select(Canonicalize)) : value.DeepClone();
    }
}
