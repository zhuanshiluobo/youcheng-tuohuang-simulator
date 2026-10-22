return function(ctx)
    local sequence = (GameData.GetRound() - 1) * 2 + GameData.GetActionRound()
    return { Effect.GrantMainActions({ player = ctx.playerId, amount = 1, lockCharacterCard = false,
        allowedActionTypes = { 'build', 'explore', 'move_city' } }),
        Effect.SetCharacterDoubleUseRule({ apiVersion = 1, player = ctx.playerId, scope = 'action_round',
            expiresActionRoundSequence = sequence }) }
end
