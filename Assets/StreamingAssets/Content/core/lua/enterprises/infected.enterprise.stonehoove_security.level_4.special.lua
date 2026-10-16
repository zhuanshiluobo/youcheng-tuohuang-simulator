return function(ctx)
    local players = {}
    for _, player in ipairs(GameData.GetPlayers()) do players[#players + 1] = 'player:' .. string.gsub(player.playerId, '^p', '') end
    return { Effect.SelectInfluence({ player = ctx.playerId, operation = 'replace', subjectFilter = 'opponent', count = 1, requiredCount = 0, promptKey = 'effect.influence.choose' }), Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', subjectFilter = 'any', excludedSubjectKeys = players, count = 1, requiredCount = 0, promptKey = 'infected.company.move' }) }
end
