return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    -- 特派张数来自当前真实人数；干员只来自共享牌堆。
    return { Flow.DispatchOperator({ apiVersion = 1, player = player, drawCount = GameData.Get().playerCount == 2 and 3 or 2, excludedReturnItemIds = {} }) }
end
