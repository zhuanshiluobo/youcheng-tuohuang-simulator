using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Influence
{
    [Serializable]
    public sealed class InfluenceLockState
    {
        public string LockId = "", SourceItemId = "", BoundCityItemId = "";
        public int ExpiresBeforeActionRoundSequence;
        public bool Active;
    }

    public static class InfluenceLockRules
    {
        public const string ExpireTypeId = "effect.influence.expire_locks";
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(ExpireTypeId, out _)) registry.Register(new EffectRegistration(ExpireTypeId, c =>
            {
                foreach (var item in c.State.InfluenceLocks.Where(l => l.Active && TokenMovementProfileRegistry.IsExpiredBeforeActionRound(c.State,l.ExpiresBeforeActionRoundSequence)).ToList())
                {
                    new ItemTransferService().RelocateBoundCityToken(c,item.SourceItemId,FindZone(c.State,"retired").ZoneId);
                    item.Active=false;
                }
                return EffectStepResult.Completed();
            },EffectExecutorKind.Atomic,"v1"));
            if(!registry.HasEventHandler("influence.locks.expire"))registry.RegisterEventHandler(new EffectEventHandlerRegistration("influence.locks.expire","ActionRoundStarted","*","influence.locks.expire",
                c => new List<EffectSpec>{EffectSpec.Create(ExpireTypeId,playerId:c.Event.PlayerId)},EffectHandlerRole.Observer,priority:-1000));
        }
        public static string TokenZone(GameState state,string location)
        {
            var parent=state.ItemZones.Zones.Single(z=>z.TemplateId=="core.zone.map_location" && z.LocalZoneId==location);
            return state.ItemZones.Zones.Single(z=>z.ParentId==parent.ZoneId && z.TemplateId=="core.zone.map_tokens").ZoneId;
        }
        public static EffectStepResult Create(EffectExecutionContext c,string definition)
        {
            if(c.State.ContentDefinitions.Components.SingleOrDefault(d=>d.DefinitionId==definition)?.TypeId!="infected.influence_lock_token")return EffectStepResult.Failed("lock_definition_invalid");
            var city=c.State.ItemZones.Items.Single(i=>i.PhysicalKind==ItemPhysicalKind.MobileCity && i.OwnerSubject.PlayerId==c.Node.PlayerId);
            string location=TokenMovementProfileRegistry.GetLocationId(c.State,city.ItemId); if(location.Length==0)return EffectStepResult.Failed("city_not_on_map");
            string lockId=StableIdFactory.Create("influence_lock",c.Node.EffectId);
            if(c.State.InfluenceLocks.Any(l=>l.LockId==lockId))return EffectStepResult.Completed();
            if(!new ItemZoneStateFactory().TryCreateGrantedItem(c.State,c.Node.EffectId,"city_lock",definition,"infected.influence_lock_token",TokenZone(c.State,location),ItemSubjectRef.Player(c.Node.PlayerId),out string id,out string error))return EffectStepResult.Failed(error);
            c.State.InfluenceLocks.Add(new InfluenceLockState{LockId=lockId,SourceItemId=id,BoundCityItemId=city.ItemId,ExpiresBeforeActionRoundSequence=CharacterLifecycleService.GetActionRoundSequence(c.State)+1,Active=true});
            return EffectStepResult.Completed(Object("tokenItemId",NormalizedValue.CreateString(id)));
        }
        public static bool PlacementAllowed(GameState state,string slotId)
        {
            if(state.InfluenceLocks.Count==0)return true;
            var zone=state.ItemZones.Zones.FirstOrDefault(z=>z.TemplateId=="core.zone.influence_slots" && z.LegalSlotIds.Contains(slotId));
            var parent=zone==null?null:InfluenceSubjectQuery.GetMapParent(state,zone.ZoneId);
            string region=parent==null?"":state.ItemZones.Zones.Find(z=>z.ZoneId==parent.ParentId)?.LocalZoneId;
            return !state.InfluenceLocks.Any(l=>l.Active && InfluenceSubjectQuery.GetRegionId(state,l.BoundCityItemId)==region);
        }
        public static void FollowCity(EffectExecutionContext c,string cityId)
        {
            string zone=TokenZone(c.State,TokenMovementProfileRegistry.GetLocationId(c.State,cityId));
            foreach(var binding in c.State.InfluenceLocks.Where(l=>l.Active && l.BoundCityItemId==cityId))new ItemTransferService().RelocateBoundCityToken(c,binding.SourceItemId,zone);
        }
        public static bool Validate(GameState state)
        {
            if(state.InfluenceLocks==null || state.InfluenceLocks.Any(l=>l==null) || state.InfluenceLocks.Select(l=>l.LockId).Distinct().Count()!=state.InfluenceLocks.Count || state.InfluenceLocks.Select(l=>l.SourceItemId).Distinct().Count()!=state.InfluenceLocks.Count)return false;
            if(state.ItemZones.Items.Any(i=>i.TypeId=="infected.influence_lock_token" && TokenMovementProfileRegistry.GetLocationId(state,i.ItemId).Length>0 && !state.InfluenceLocks.Any(l=>l.Active && l.SourceItemId==i.ItemId)))return false;
            return state.InfluenceLocks.All(l=>l.LockId.Length>0 && l.ExpiresBeforeActionRoundSequence>1 &&
                state.ItemZones.Items.Any(i=>i.ItemId==l.SourceItemId && i.TypeId=="infected.influence_lock_token") &&
                state.ItemZones.Items.Any(i=>i.ItemId==l.BoundCityItemId && i.PhysicalKind==ItemPhysicalKind.MobileCity) &&
                (!l.Active || TokenMovementProfileRegistry.GetLocationId(state,l.SourceItemId).Length>0 && TokenMovementProfileRegistry.GetLocationId(state,l.SourceItemId)==TokenMovementProfileRegistry.GetLocationId(state,l.BoundCityItemId)));
        }
    }
}
