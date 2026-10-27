-- CONTENT-R3：M13来源、叠建义务和原Build完成后的对手正文。
local function query(p,items) return {queryId=p.queryId,basisDigest=p.basisDigest,items=items} end
local function overlay_candidates(facts)
  local items={}
  for _,cell in ipairs(facts.cells) do
    if cell.topItemId~='' and facts.newScore>cell.topScore then
      items[#items+1]={slotIndex=cell.slotIndex,expectedTopItemId=cell.topItemId,
        orderDigest=cell.orderDigest,
        removalPrecondition={operation='remove',ownerFilter='self',requiredCount=1},
        requiredResponseAbilityId='entrepreneur.M13.overlay_response'}
    end
  end
  return items
end
local family={['entrepreneur.facility.M02']=true,['entrepreneur.facility.M03']=true,
  ['entrepreneur.facility.M04']=true,building_015=true,building_016=true,building_017=true}
local function result_player(value)
  if type(value)=='table' then return value.value end
  return value
end
-- 既有冻结模块的默认Place仅在此入口补全公开参数；数量/顺序/文案由原模块保留。
local function urban_effects(ctx)
  local effects=Shared.urbanization(ctx,Shared.base_urban)
  for _,effect in ipairs(effects) do
    if effect.effectTypeId=='effect.influence.place' and effect.executingPlayer==ctx.playerId
        and effect.ownerSubject==nil and effect.influenceSource==nil then
      effect.ownerSubject=ctx.playerId
      effect.influenceSource='player_supply'
      effect.causeKind='other_intrinsic_flow'
    end
  end
  return effects
end
return function(ctx)
  local p=ctx.payload or {}
  local saved=p.continuationContext
  if ctx.eventType=='EffectCompleted' and not p.abilityId and saved and saved.stage=='M13.opponent' then
    if p.outcome~='completed' then error('martin_required_opponent_choice_not_completed') end
    local chosen=result_player(p.normalizedResult)
    local allowed=false
    for _,player in ipairs(GameData.GetPlayers()) do
      if player.playerId==chosen and chosen~=ctx.playerId then allowed=true end
    end
    if not allowed or not p.interactionId or p.interactionId=='' then error('martin_opponent_choice_invalid') end
    -- 只把所选对手传给共享普通正文；城市/塔数/合法Place均使用该对手自身事实。
    local effects=urban_effects({playerId=chosen,payload={facilityId=saved.definitionId}})
    local result={}
    for _,effect in ipairs(effects) do
      result[#result+1]=Effect.WithContext(effect,{executionBinding={protocolVersion=1,
        parentBuildId=saved.buildId,choiceInteractionId=p.interactionId,
        playerId=tonumber(string.match(chosen,'^p(%d+)$'))}})
    end
    return result
  end
  if p.queryKind=='built_facilities.v1' then return query(p,p.facts.allCityItemIds) end
  if p.queryKind=='facility.build.sources.v1' then
    if p.facts.requestedSource~='authorized' then return query(p,{}) end
    local permitted={}
    if p.abilityId=='entrepreneur.M13.blueprint_source' then
      for _,zone in ipairs(Global.Content.FindZones({templateId='core.zone.card_facility_storage',
          parentId=p.sourceItemId,localZoneId='blueprints'})) do
        -- 交换回来的公共设施仍是该载体库存，不以实例原owner排除。
        for _,item in ipairs(Global.Content.FindInstances({zoneId=zone.id,contentType='facility'})) do permitted[item.id]=true end
      end
    elseif p.abilityId=='entrepreneur.M13.tower_source' then
      for _,zone in ipairs(Global.Content.FindZones({templateId='core.zone.player_supply',owner=ctx.playerId})) do
        for _,item in ipairs(Global.Content.FindInstances({zoneId=zone.id,
            definitionId='entrepreneur.facility.additional_core_tower',owner=ctx.playerId})) do permitted[item.id]=true end
      end
    end
    local result={}
    for _,id in ipairs(p.facts.candidatePoolIds) do if permitted[id] then result[#result+1]=id end end
    return query(p,result)
  end
  if p.queryKind=='facility.build.slot.v1' then return query(p,overlay_candidates(p.facts)) end
  if p.abilityId=='entrepreneur.M13.overlay_response' then
    if not p.buildFacts then error('martin_overlay_build_facts_missing') end
    if not p.buildFacts.overlay then return {} end
    -- facility.overlay.source唯一SourceUse包装；叶及包装均要求足额，确认后不能放弃。
    return {Effect.WithContext(Effect.SelectInfluence({player=ctx.playerId,operation='remove',
      ownerFilter='self',count=1,requiredCount=1,requireCompleteSelection=true,allowDecline=false,
      interactionType='effect.influence.remove.target',promptKey='core:entrepreneur.M.overlay.remove'}),
      {requiredOutcome={protocolVersion=1,resultField='completedCount',expectedValue=1}})}
  end
  if p.abilityId=='entrepreneur.M13.urbanization_after' then
    if ctx.eventType~='EffectCompleted' then return {} end
    local facts=p.buildFacts
    if not facts or facts.builderId~=tonumber(string.match(ctx.playerId,'^p(%d+)$'))
        or not family[facts.definitionId] then return {} end
    local options,labels={},{}
    for _,player in ipairs(GameData.GetPlayers()) do
      if player.playerId~=ctx.playerId then
        options[#options+1]=player.playerId
        labels[#labels+1]={candidateId=player.playerId,label=Text.Ref('core:entrepreneur.M.M13.opponent.player',
          {who=Text.Player(player.playerId)})}
      end
    end
    if #options==0 then error('martin_required_opponent_missing') end
    return {Effect.WithContext(Effect.WithPresentation(Effect.Choice({player=ctx.playerId,options=options,
      minSelections=1,maxSelections=1,promptKey='core:entrepreneur.M.M13.opponent.choose',interactionType='lua.choice'}),
      {interaction={prompt=Text.Ref('core:entrepreneur.M.M13.opponent.choose'),options=labels}}),
      {stage='M13.opponent',buildId=facts.buildId,definitionId=facts.definitionId})}
  end
  -- 普通收益/父条件的完成通知不能重新发主正文。
  if ctx.eventType=='EffectCompleted' then return {} end
  local id=p.facilityId or p.effectId
  if family[id] or id=='entrepreneur.M03.entry' then return urban_effects(ctx) end
  error('martin_ability_unknown_entry')
end
