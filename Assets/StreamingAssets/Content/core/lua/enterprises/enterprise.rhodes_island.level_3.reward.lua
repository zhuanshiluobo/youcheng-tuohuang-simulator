return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    -- 特派张数来自当前真实人数；干员只来自共享牌堆。
    return { Flow.DispatchOperator({ apiVersion = 2, player = player, deckZoneId = Global.Content.FindZones({localZoneId='operator_deck'})[1].id, excludedReturnItemIds = {} }) }
end
