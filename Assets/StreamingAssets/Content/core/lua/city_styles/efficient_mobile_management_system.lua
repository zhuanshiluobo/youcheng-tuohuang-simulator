return function(ctx)
    local p = ctx.payload
    local effects = {}
    for i = 1, p.freeMoveCount do
        table.insert(effects, Effect.MoveCity({
            player = ctx.playerId,
            movementMode = "free",
            costPolicy = "waived",
            allowDecline = true, decisionPlayer = ctx.playerId,
            consumeMainAction = false
        }))
    end
    return effects
end
