return function(ctx)
    local p = ctx.payload
    return { Effect.GainResource({
        recipient = ctx.playerId,
        resourceType = p.rewardResourceType,
        amount = p.rewardAmount,
        reasonId = 'facility.entry.reward'
    }) }
end
