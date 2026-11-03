return function(ctx)
    local players = {}
    for _, player in ipairs(GameData.GetPlayers()) do players[#players + 1] = 'player:' .. string.gsub(player.playerId, '^p', '') end
    return { Effect.ReplaceInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'opponent', count = 1, requiredCount = 0, promptKey = 'effect.influence.choose' }), Effect.MoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter = 'any', excludedSubjectKeys = players, count = 1, requiredCount = 0, promptKey = 'infected.company.move' }) }
end
