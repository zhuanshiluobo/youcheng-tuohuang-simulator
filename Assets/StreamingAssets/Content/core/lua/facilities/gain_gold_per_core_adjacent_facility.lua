return function(ctx)
    local count = 0
    for _, instance in ipairs(Global.Content.FindInstances()) do
        if instance.owner == ctx.playerId and instance.adjacentToCore then count = count + 1 end
    end
    if count == 0 then return {} end
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 4 * count }) }
end
