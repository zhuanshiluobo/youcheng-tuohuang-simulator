return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    return { Effect.SwitchDepartment({ apiVersion = 1, player = player, enterpriseItemId = ctx.payload.enterpriseItemId }) }
end
