return function(ctx)
    local branches = {}
    local options = {}
    local labels = { originium = 'core:interaction.silverash.sale.originium', ['originium-shard'] = 'core:interaction.silverash.sale.shard', iron = 'core:interaction.silverash.sale.iron' }
    for _, resource in ipairs({ 'originium', 'originium-shard', 'iron' }) do
        for _, player in ipairs(GameData.GetPlayers()) do
            local target = player.playerId
            if true then
                options[#options + 1] = { candidateId = resource .. '|' .. target, label = Text.Ref(labels[resource], { player = Text.Player(target) }) }
                branches[#branches + 1] = { id = resource .. '|' .. target, effects = {
                    Effect.SellResource({ apiVersion = 1, player = ctx.playerId, targetPlayer = target, resourceType = resource, saleMode = 'all', unitPrice = 4 }),
                    Effect.GainScore({ recipient = ctx.playerId, amount = 1 })  } }
            end
        end
    end
    return { Effect.WithPresentation(Effect.Choice({ player = ctx.playerId, branches = branches, promptKey = 'operator.choose_resource_and_player' }), { interaction = { prompt = Text.Ref('core:interaction.silverash.sale.choose'), options = options } }) }
end
