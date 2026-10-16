using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    /// <summary>将内容声明接入真实宣告、奖励取得及收尾事件。</summary>
    public static class ContentLifecycleLuaCatalog
    {
        public static List<AwardContentDefinition> CreateAwards(ExternalContentPack pack) => pack.ActiveDefinitions.Where(d => d.ContentType == "award")
            .Select(d => new AwardContentDefinition { DefinitionId = d.DefinitionId, Lifecycle = (string)d.Data["lifecycle"] ?? "",
                TitleKey = (string)d.Data["titleKey"] ?? d.DisplayName, DescriptionKey = (string)d.Data["descriptionKey"] ?? "" }).ToList();

        public static void Register(EffectRegistry registry, ExternalContentPack pack)
        {
            ContentLifecycleEffectExecutor.Register(registry);
            var settlements = SettlementHostCatalog.Register(registry, pack);
            var dispatcher = new LuaEffectEventDispatcher(registry);
            foreach (var content in pack.ActiveDefinitions.Where(d => new[] { "facility", "city_style", "award" }.Contains(d.ContentType)))
            foreach (var ability in content.Abilities.Where(a => new[] { "declared", "acquired", "quick", "cleanup" }.Contains(a.Mode)))
            {
                if (registry.HasEventHandler(ability.SubscriptionId)) continue;
                string eventType = ability.Mode == "declared" ? "CityStyleDeclared" : ability.Mode == "acquired" ? "reward.acquired" : ability.Mode == "quick" ? "reward.activated" : "reward.cleanup";
                string source = pack.GetScript(ability.Script);
                var definition = new LuaHandlerDefinition(ability.SubscriptionId, eventType, "",
                    new LuaScriptDefinition(content.DefinitionId, ability.AbilityId, ability.SubscriptionId, ability.Version, source, LuaContentHasher.ComputeSha256(source)),
                    EffectHandlerRole.Observer, contentInstanceId: content.DefinitionId, abilityId: ability.AbilityId);
                registry.RegisterEventHandler(new EffectEventHandlerRegistration(ability.SubscriptionId, eventType,
                    content.ContentType == "award" ? content.DefinitionId : "", ability.SubscriptionId, context =>
                    {
                        var ids = content.ContentType == "facility" ? Value(context.Event.Payload, "usedFacilityItemIds")?.Items.Select(v => v.StringValue).ToList() ?? new List<string>() :
                            new List<string> { Text(context.Event.Payload, content.ContentType == "city_style" ? "cityStyleItemId" : "rewardItemId") };
                        var effects = new List<EffectSpec>();
                        foreach (string id in ids.Distinct())
                        {
                            var item = context.State.ItemZones.Items.Find(i => i.ItemId == id && i.DefinitionId == content.DefinitionId);
                            if (item == null) continue;
                            if (content.ContentType == "award" && !ContentLifecycleEffectExecutor.IsHeld(context.State, item, context.Event.PlayerId)) continue;
                            var ruleEvent = GameStateCloneService.DeepClone(context.Event);
                            ruleEvent.Payload.Properties.RemoveAll(p => p.Name == "sourceItemId");
                            ruleEvent.Payload.Properties.Add(new NormalizedValueEntry { Name = "sourceItemId", Value = NormalizedValue.CreateString(id) });
                            ruleEvent.Payload.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                            var owner = context.State.EffectRuntime.EffectNodes.Find(n => n.EffectId == context.Event.OwnerNodeId);
                            effects.AddRange(dispatcher.Invoke(definition, new EffectEventHandlerContext(context.State, ruleEvent, owner, null)));
                        }
                        return effects;
                    }, EffectHandlerRole.Observer, contentInstanceId: content.DefinitionId, abilityId: ability.AbilityId,
                    definitionVersion: ability.Version, contentHash: LuaContentHasher.ComputeSha256(source), contentId: content.DefinitionId));
            }
            const string cleanupId = "content.reward.player_cleanup";
            if (!registry.HasEventHandler(cleanupId)) registry.RegisterEventHandler(new EffectEventHandlerRegistration(cleanupId, "PlayerCleanupStarted", "", cleanupId,
                context => context.State.ItemZones.Items.Where(i => ContentLifecycleEffectExecutor.IsHeld(context.State, i, context.Event.PlayerId) &&
                    context.State.ContentDefinitions.Awards.Any(d => d.DefinitionId == i.DefinitionId && d.Lifecycle == "persistent"))
                    .OrderBy(i => i.ItemId, StringComparer.Ordinal).Select(i =>
                    {
                        var root = ContentLifecycleEffectExecutor.AwardSpec(context.Event.PlayerId, i.ItemId, true);
                        var ability = pack.ActiveDefinitions.Single(d => d.DefinitionId == i.DefinitionId).Abilities
                            .SingleOrDefault(a => a.Mode == "cleanup" && a.Settlement?.EntryKind == SettlementEntryService.CleanupOrder);
                        return ability == null ? root : settlements.DecorateCleanupRoot(root, ability.AbilityId, i.ItemId,
                            ContentLifecycleEffectExecutor.Acquisition(context.State, i.ItemId)?.EffectId ?? "");
                    }).ToList(), EffectHandlerRole.Observer));
        }
    }
}
