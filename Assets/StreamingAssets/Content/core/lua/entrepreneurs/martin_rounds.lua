-- CONTENT-R3：规则留在Lua；来源、层序和转移提交均使用冻结公共合同。
local function shown(effect,key,args)
  return Effect.WithPresentation(effect,{interaction={prompt=Text.Ref('core:entrepreneur.M.'..key,args or {})}})
end
local function place(ctx)
  return Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,
    influenceSource='player_supply',causeKind='other_intrinsic_flow'})
end
local function gain(ctx,resource,amount)
  return Effect.GainResource({recipient=ctx.playerId,resourceType=resource,amount=amount})
end
local function normal_build(ctx)
  return shown(Effect.ChooseBuild({player=ctx.playerId,sourceZone='supply',paymentMode='auto',
    allowDecline=true,includeStoredFacilities=true,excludeRainbow=false,consumeMainAction=false}),'build.normal')
end
local function to_bottom(ctx)
  -- 本节点执行时重新查全部供应顶牌，包括刚插入的牌；空槽才沿原操作补牌。
  return shown(Effect.MoveFacilityCard({player=ctx.playerId,sourceZone='supply',
    destinationZone='deck_bottom',interactionType='facility.supply.select',
    promptKey='core:entrepreneur.M.blueprint.to_bottom'}),'blueprint.to_bottom')
end
local function hub(ctx)
  return shown(Effect.ChooseBuild({player=ctx.playerId,sourceZone='reserve',
    effectId='reserve_extension_hub',paymentMode='free',allowDecline=false,consumeMainAction=false}),'build.hub')
end
local function source_for(ctx,abilityId)
  local cursor,revision,content,found=0,nil,nil,nil
  repeat
    local page=SourceData.GetCurrentSources({cursor=cursor,definitionId='entrepreneur.ability.M13',
      abilityId=abilityId,expectedStateRevision=revision,expectedContentHash=content})
    revision,content=page.stateRevision,page.contentHash
    for _,source in ipairs(page.items) do
      if source.authorizedPlayerId==ctx.playerId then
        if found then error('martin_source_ambiguous') end
        found=source
      end
    end
    cursor=page.nextCursor
  until cursor==-1
  return found
end
local function storage(ctx)
  local source=source_for(ctx,'entrepreneur.M13.blueprint_source')
  if not source then return nil,nil end
  local zones=Global.Content.FindZones({parentId=source.itemId,localZoneId='blueprints',
    templateId='core.zone.card_facility_storage'})
  if #zones~=1 then error('martin_blueprint_zone_invalid') end
  return source,zones[1]
end
local function inventory(zone,color)
  local items={}
  if not zone then return items end
  for _,item in ipairs(Global.Content.FindInstances({zoneId=zone.id,contentType='facility'})) do
    local definition=Global.Content.GetDefinition(item.definitionId)
    if definition and definition.contentType=='facility' and (not color or definition.color==color) then
      items[#items+1]=item
    end
  end
  table.sort(items,function(a,b) return a.id<b.id end)
  return items
end
local function supply_item(id)
  for _,slot in ipairs(Global.Facilities.GetSupplyLayers()) do
    if slot.topItemId==id then
      local item=Global.Content.GetInstance(id)
      local definition=item and Global.Content.GetDefinition(item.definitionId)
      if definition and definition.color~='rainbow' then return slot,item,definition end
    end
  end
  return nil,nil,nil
end
local function equal(a,b,skipIdentity)
  if type(a)~=type(b) then return false end
  if type(a)~='table' then return a==b end
  local ignored={id=true,zoneId=true,slotId=true}
  for key,value in pairs(a) do
    if not (skipIdentity and ignored[key]) and not equal(value,b[key],false) then return false end
  end
  for key in pairs(b) do
    if not (skipIdentity and ignored[key]) and a[key]==nil then return false end
  end
  return true
end
local function child_state(item)
  local zones={}
  for _,zone in ipairs(Global.Content.FindZones({parentId=item.id})) do
    zones[#zones+1]={localZoneId=zone.localZoneId,templateId=zone.templateId,capacity=zone.capacity,
      itemCount=zone.itemCount,
      items=Global.Content.FindInstances({zoneId=zone.id})}
  end
  table.sort(zones,function(a,b) return a.localZoneId<b.localZoneId end)
  return zones
end
local function same_kind(items)
  if #items==0 then return false end
  local first=items[1]
  local definition=Global.Content.GetDefinition(first.definitionId)
  -- 非空、真实定义、唯一组和公开的相关实例状态均相同才可自动取；不按名称/贴图/颜色认同种。
  for i=2,#items do
    local d=Global.Content.GetDefinition(items[i].definitionId)
    if items[i].definitionId~=first.definitionId or d.uniqueGroupId~=definition.uniqueGroupId
        or not equal(first,items[i],true) or not equal(child_state(first),child_state(items[i]),false) then return false end
  end
  return true
end
local function order(zone)
  return {zoneId=zone.id or zone.slotZoneId,digest=zone.orderDigest,
    orderRevision=zone.orderRevision,expectedTopItemId=zone.topItemId or ''}
end
local function leg(id,from,to,kind)
  return {itemRefs={{itemId=id}},expectedSource={zoneId=from},destination={zoneId=to},
    insertion={kind=kind},operationProfileId='core.facility_card_transfer',requiredCount=1,carrierPolicy='preserve'}
end
local function transfer(ctx,mode,zone,slot,item)
  local legs={leg(item.id,zone.id,slot.slotZoneId,'top')}
  if mode=='exchange' then
    legs[#legs+1]=leg(slot.topItemId,slot.slotZoneId,zone.id,'unordered')
  elseif mode=='under' then
    -- 同一批次先移出两张，再将图纸置顶、原顶牌置顶：结果原顶/图纸/原下层。
    -- 原顶不离开供应槽，已有下层的相对顺序保持；无中间补槽。
    legs[#legs+1]=leg(slot.topItemId,slot.slotZoneId,slot.slotZoneId,'top')
  elseif mode~='over' then error('martin_pair_mode_invalid') end
  return Effect.MoveItem({apiVersion=2,player=ctx.playerId,
    operationId='martin:'..mode..':'..ctx.eventId,transfers=legs,
    expectedOrders={order(zone),order(slot)}})
end
local function pair_context(stage,mode,source,zone,slot)
  return {stage=stage,mode=mode,sourceKey=source.sourceKey,blueprintZoneId=zone.id,
    supplyItemId=slot.topItemId,supplyZoneId=slot.slotZoneId,supplyDigest=slot.orderDigest,
    supplyRevision=slot.orderRevision,blueprintDigest=zone.orderDigest,blueprintRevision=zone.orderRevision}
end
local function item_choice(ctx,items,key,context,interactionType)
  local options,presentation={},{}
  for _,item in ipairs(items) do
    options[#options+1]=item.id
    presentation[#presentation+1]={candidateId=item.id,label=Text.Ref('core:entrepreneur.M.blueprint.item',{item=Text.Item(item.id)})}
  end
  return Effect.WithContext(Effect.WithPresentation(Effect.Choice({player=ctx.playerId,options=options,
    minSelections=1,maxSelections=1,promptKey='core:entrepreneur.M.'..key,interactionType=interactionType or 'lua.choice'}),
    {interaction={prompt=Text.Ref('core:entrepreneur.M.'..key),options=presentation}}),context)
end
local function begin_pair(ctx,mode)
  local source,zone=storage(ctx)
  if not source then return {} end
  local blueprints=inventory(zone)
  local colors={}
  for _,item in ipairs(blueprints) do colors[Global.Content.GetDefinition(item.definitionId).color]=true end
  local candidates={}
  for _,slot in ipairs(Global.Facilities.GetSupplyLayers()) do
    if slot.topItemId~='' then
      local item=Global.Content.GetInstance(slot.topItemId)
      local d=item and Global.Content.GetDefinition(item.definitionId)
      if d and d.color~='rainbow' and colors[d.color] then candidates[#candidates+1]=item end
    end
  end
  if #candidates==0 then return {} end
  return {item_choice(ctx,candidates,'blueprint.choose_supply',
    {stage='M.pair.supply',mode=mode,sourceKey=source.sourceKey,blueprintZoneId=zone.id},'facility.supply.select')}
end
local function selected(value)
  if type(value)=='table' then return value.value end
  return value
end
local function after_supply(ctx,p,saved)
  local source,zone=storage(ctx)
  local slot,item,definition=supply_item(selected(p.normalizedResult))
  if not source or source.sourceKey~=saved.sourceKey or zone.id~=saved.blueprintZoneId or not slot then
    return begin_pair(ctx,saved.mode)
  end
  local items=inventory(zone,definition.color)
  if #items==0 then return begin_pair(ctx,saved.mode) end
  if saved.mode~='exchange' and same_kind(items) then return {transfer(ctx,saved.mode,zone,slot,items[1])} end
  return {item_choice(ctx,items,'blueprint.choose_card',pair_context('M.pair.blueprint',saved.mode,source,zone,slot))}
end
local function after_blueprint(ctx,p,saved)
  local source,zone=storage(ctx)
  local slot,_,definition=supply_item(saved.supplyItemId)
  if not source or source.sourceKey~=saved.sourceKey or not slot or zone.id~=saved.blueprintZoneId
      or slot.slotZoneId~=saved.supplyZoneId or slot.orderDigest~=saved.supplyDigest
      or slot.orderRevision~=saved.supplyRevision or zone.orderDigest~=saved.blueprintDigest
      or zone.orderRevision~=saved.blueprintRevision then return begin_pair(ctx,saved.mode) end
  for _,item in ipairs(inventory(zone,definition.color)) do
    if item.id==selected(p.normalizedResult) then return {transfer(ctx,saved.mode,zone,slot,item)} end
  end
  return begin_pair(ctx,saved.mode)
end
local function tower(ctx,after)
  local source=source_for(ctx,'entrepreneur.M13.tower_source')
  if not source then error('martin_tower_source_unavailable') end
  local found=nil
  for _,zone in ipairs(Global.Content.FindZones({templateId='core.zone.player_supply',owner=ctx.playerId})) do
    for _,item in ipairs(Global.Content.FindInstances({zoneId=zone.id,
        definitionId='entrepreneur.facility.additional_core_tower',owner=ctx.playerId})) do
      if found then error('martin_additional_tower_ambiguous') end
      found={item=item,zone=zone}
    end
  end
  if not found then return after and {hub(ctx)} or {} end
  local build=Effect.Build({player=ctx.playerId,facilityId=found.item.id,sourceKind='authorized',
    sourceKey=source.sourceKey,expectedSourceZoneId=found.zone.id,consumeMainAction=false,
    paymentMode='free',reserveForFree=false})
  if after then build=Effect.WithContext(build,{stage=after}) end
  return {build}
end
return function(ctx)
  local p = ctx.payload or {}
  if ctx.eventType == 'EffectCompleted' and not p.abilityId then
    local saved = p.continuationContext or {}
    if saved.stage == 'M11.panel' then
      if p.outcome ~= 'completed' then return {} end
      local target = p.normalizedResult
      if not target or not target.facilityItemId or not target.cellZoneId or not target.orderDigest then
        error('martin_retire_panel_result_invalid')
      end
      -- 与外层Condition使用同一真实续接scope，回盒成为Condition的直接左式结尾。
      -- 若Retire普通失败，Condition据此失败；不以面板选中成功代替真实回盒成功。
      return {Effect.WithContext(Effect.RetireFacility({apiVersion=2,player=ctx.playerId,
        facilityItemId=target.facilityItemId,expectedCellZoneId=target.cellZoneId,
        expectedOrderDigest=target.orderDigest}),{stage='M11.retire'})}
    end
    if saved.stage == 'M05.after_intro' then return begin_pair(ctx,'exchange') end
    if saved.stage == 'M06.after_move' then return begin_pair(ctx,'under') end
    if saved.stage == 'M.pair.supply' then return after_supply(ctx,p,saved) end
    if saved.stage == 'M.pair.blueprint' then return after_blueprint(ctx,p,saved) end
    if saved.stage == 'M08.after_tower' then return {hub(ctx)} end
    if saved.stage == 'M11.retire' or saved.stage == 'M11.condition'
        or saved.stage == 'M07.final' or saved.stage == 'M08.final' then return {} end
    -- 无显式context的普通兄弟完成，无附加业务。
    if not saved.stage then return {} end
    error('martin_round_unknown_continuation')
  end
  local ability = p.abilityId
  if ability == 'entrepreneur.face.M05' then
    local intro = Effect.WithPresentation(Effect.Choice({player=ctx.playerId,branches={
      {id='explore',effects={Effect.ChooseExplore({player=ctx.playerId})}},
      {id='place_twice',effects={place(ctx),place(ctx)}}},
      promptKey='core:entrepreneur.M.M05.choice',interactionType='lua.choice'}),{interaction={prompt=Text.Ref('core:entrepreneur.M.M05.choice'),options={
      {candidateId='explore',label=Text.Ref('core:entrepreneur.M.M05.explore')},
      {candidateId='place_twice',label=Text.Ref('core:entrepreneur.M.M05.place_twice')}}}})
    -- 一次挂好两独立有序Place；之后交换需真实配对及原子批量接口，不能两次移位冒充。
    return {Effect.WithContext(intro,{stage='M05.after_intro'})}
  end
  if ability == 'entrepreneur.face.M06' then
    local movement = Effect.Condition({decisionPlayer=ctx.playerId,allowDecline=true,
      leftEffects={Effect.PayResource({player=ctx.playerId,cost={OriginiumShard=1}})},
      rightEffects={Effect.MoveCity({player=ctx.playerId,movementMode='free',
        waiveBaseCost=true,consumeMainAction=false})}})
    -- 条件后独立续接；即使拒付/移动无目标，也进入下插，不再次消费轨道。
    return {Effect.WithContext(shown(movement,'M06.condition'),{stage='M06.after_move'})}
  end
  if ability == 'entrepreneur.M07.setup' or ability == 'entrepreneur.M08.setup' then
    return tower(ctx,ability=='entrepreneur.M08.setup' and 'M08.after_tower' or nil)
  end
  if ability == 'entrepreneur.face.M07' then
    local count=0
    for _,cell in ipairs(Global.Facilities.GetCityLayers(ctx.playerId)) do
      if #cell.coveredItemIds > 0 then count=count+1 end
    end
    if count==0 then return {} end
    return {Effect.WithContext(Effect.WithPresentation(Effect.GainScore({recipient=ctx.playerId,amount=count}),
      {succeeded=Text.Ref('core:entrepreneur.M.M07.scored',{count=count})}),{stage='M07.final',cellCount=count}),
      gain(ctx,'iron',count)}
  end
  if ability == 'entrepreneur.face.M08' then
    local selected=nil
    local count=0
    for _,cell in ipairs(Global.Facilities.GetCityLayers(ctx.playerId)) do
      local n=#cell.allItemIds
      if n>count or (n>0 and n==count and (not selected or cell.slotIndex<selected)) then
        selected=cell.slotIndex
        count=n
      end
    end
    if count==0 then return {} end
    return {Effect.WithContext(Effect.WithPresentation(Effect.GainScore({recipient=ctx.playerId,amount=2*count}),
      {succeeded=Text.Ref('core:entrepreneur.M.M08.scored',{count=count,amount=2*count})}),
      {stage='M08.final',selectedSlotIndex=selected,facilityCount=count})}
  end
  if ability == 'entrepreneur.M09.setup' or ability == 'entrepreneur.M10.setup' then
    local effects=Shared.award_grant(ctx)
    if ability == 'entrepreneur.M09.setup' then effects[#effects+1]=place(ctx) end
    -- 非Condition兄弟：Place普通无目标不吞10券；奖励发放不另设付款来源。
    effects[#effects+1]=gain(ctx,'gold_voucher',10)
    return effects
  end
  if ability == 'entrepreneur.face.M09' then
    local effects=begin_pair(ctx,'over')
    -- 两项是独立兄弟；无配对只完成上插，仍执行后句。
    effects[#effects+1]=to_bottom(ctx)
    return effects
  end
  if ability == 'entrepreneur.face.M10' then
    local source,zone=storage(ctx)
    if not source then error('martin_blueprint_source_unavailable') end
    return {shown(Effect.Condition({decisionPlayer=ctx.playerId,allowDecline=true,
      leftEffects={Effect.PayResource({player=ctx.playerId,cost={GoldVoucher=2}})},
      rightEffects={Effect.ChooseBuild({player=ctx.playerId,sourceZone='authorized',
        sourceKey=source.sourceKey,expectedSourceZoneId=zone.id,consumeMainAction=false,
        paymentMode='auto',allowDecline=true,includeStoredFacilities=false})}}),'M10.condition')}
  end
  if ability == 'entrepreneur.face.M11' then
    local panel=Effect.WithContext(shown(Effect.SelectFacilityPanel({apiVersion=1,
      player=ctx.playerId,allowDecline=true,operationProfile='effect.facility.retire',
      emptyIsFailure=true,promptKey='core:entrepreneur.M.M11.panel'}),'M11.panel'),{stage='M11.panel'})
    return {Effect.WithContext(shown(Effect.Condition({decisionPlayer=ctx.playerId,allowDecline=true,
      leftEffects={panel},rightEffects={place(ctx),shown(Effect.ChooseResources({player=ctx.playerId,amount=2}),
        'M11.resources')}}),'M11.condition'),{stage='M11.condition'})}
  end
  if ability == 'entrepreneur.face.M12' then
    return {shown(Effect.Condition({decisionPlayer=ctx.playerId,allowDecline=true,
      leftEffects={Effect.PayResource({player=ctx.playerId,cost={PureOriginium=1}})},
      rightEffects={normal_build(ctx),place(ctx)}}),'M12.condition')}
  end
  error('martin_round_unknown_entry')
end
