return function(ctx)
    return { Flow.AcquireReward({ apiVersion = 1, player = ctx.playerId, poolRole = 'awards' }) }
end
