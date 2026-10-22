return function(ctx)
    local branches = {}
    local options = {}
    for _, opponent in ipairs(GameData.GetTurnOrder()) do
        if opponent.playerId ~= ctx.playerId then
            options[#options + 1] = { candidateId = opponent.playerId, label = Text.Ref('core:interaction.player.name', { player = Text.Player(opponent.playerId) }) }
            branches[#branches + 1] = { id = opponent.playerId, effects = { Effect.LoseScore({ player = opponent, amount = 1 }) } }
        end
    end
    return { Effect.GainScore({ recipient = ctx.playerId, amount = 1 }), Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches, promptKey = 'enterprise.fussen_bank.choose_opponent' }), { interaction = { prompt = Text.Ref('core:interaction.fussen.choose_opponent'), options = options } }) }
end
