local function cycle_choice(ctx,prompt)
  local branches={}
  for _,slot in ipairs(Global.Facilities.GetSupplyLayers()) do
    if slot.topItemId~='' then
      branches[#branches+1]={id=slot.topItemId,effects={Effect.MoveFacilityCard({apiVersion=4,
        player=ctx.playerId,operation='cycle_supply',selector={mode='explicit',itemIds={slot.topItemId},
          expectedSource={zoneId=slot.slotZoneId}}})}}
    end
  end
  if #branches==0 then return {} end
  local effect=Effect.Choice({player=ctx.playerId,branches=branches,interactionType='facility.supply.select',
    promptKey=prompt or 'core:interaction.item.transfer.prompt'})
  if prompt then effect=Effect.WithPresentation(effect,{interaction={prompt=Text.Ref(prompt)}}) end
  return {effect}
end
return function(ctx)
    local p=ctx.payload or {}
    if ctx.eventType=='EffectCompleted' then
      if (p.continuationContext or {}).stage=='texas.after_gain' then return cycle_choice(ctx) end
      return {}
    end
    return {Effect.WithContext(Effect.GainResource({recipient=ctx.playerId,resourceType='gold_voucher',amount=12}),
      {stage='texas.after_gain'})}
end
