return function(ctx)
    local count = #PlayerData.GetDiscard(ctx.playerId)
    local effects = { Effect.MoveCharacterCard({ player = ctx.playerId, sourceZone = 'discard', destinationZone = 'hand' }) }
    for i = 1, count do
        table.insert(effects, Effect.Choice({ player = ctx.playerId,
            interactionType = 'character.ability.choice.reward', promptKey = 'character.tin_man.tactic.choose_discard_resolution',
            branches = {
                { id = 'gain-gold', effects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 5 }) } },
                { id = 'move-influence', effects = { Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', ownerFilter = 'self', count = 1,
                    interactionType = 'character.ability.choice.move', promptKey = 'character.tin_man.tactic.choose_discard_resolution' }) } }
            }
        }))
    end
    return effects
end
