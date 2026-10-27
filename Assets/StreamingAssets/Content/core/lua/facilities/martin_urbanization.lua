-- QY-016 CONTENT-R2；只返回普通效果，不更改实例、Build或执行对象。
-- 基础三张城市化由调用方传入冻结 baseUrban 复用010冻结正文，禁止复制邻接算法。
return function(ctx,baseUrban)
  local p = ctx.payload or {}
  local id = p.facilityId or p.effectId
  if id == 'building_015' or id == 'building_016' or id == 'building_017'
      or id == 'gain_gold_per_core_adjacent_facility' then
    if not baseUrban then error('martin_base_urban_module_required') end
    return baseUrban(ctx)
  end
  local count = #Global.Facilities.GetTowerAdjacency(ctx.playerId)
  if id == 'entrepreneur.facility.M02' or id == 'entrepreneur.M02.entry' then
    if count == 0 then return {} end
    return {Effect.WithPresentation(Effect.GainResource({recipient=ctx.playerId,
      resourceType='gold_voucher',amount=12*count}),
      {succeeded=Text.Ref('core:entrepreneur.M.urban.vouchers',{count=count,amount=12*count})})}
  end
  if id == 'entrepreneur.facility.M03' or id == 'entrepreneur.M03.entry' then
    local result = {}
    -- 每塔一个独立Place节点；不合并数量，每次由宿主重新检查地图和供应。
    for i=1,count do
      result[#result+1] = Effect.WithPresentation(Effect.PlaceInfluence({executingPlayer=ctx.playerId}),
        {interaction={prompt=Text.Ref('core:entrepreneur.M.urban.place',{index=i,count=count})}})
    end
    return result
  end
  if id == 'entrepreneur.facility.M04' or id == 'entrepreneur.M04.entry' then
    if count == 0 then return {} end
    return {Effect.WithPresentation(Effect.GainResource({recipient=ctx.playerId,
      resourceType='pure_originium',amount=count}),
      {succeeded=Text.Ref('core:entrepreneur.M.urban.pure',{count=count})})}
  end
  error('martin_urbanization_unknown_definition')
end
