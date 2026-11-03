return function(ctx)
    local discarded = PlayerData.GetDiscard(ctx.playerId)
    local count = #discarded
    local discard = Global.Content.FindZones({localZoneId='discard',owner=ctx.playerId})
    local hand = Global.Content.FindZones({localZoneId='hand',owner=ctx.playerId})
    if #discard ~= 1 or #hand ~= 1 then error('character_recovery_zone_invalid') end
    local effects = { Effect.MoveItem({ apiVersion=4, player=ctx.playerId, operation='transfer',
        selector={mode='explicit',itemIds=discarded,expectedSource={zoneId=discard[1].id}},
        destination={zoneId=hand[1].id},insertion={kind='unordered'},
        operationProfileId='core.character_card_transfer',requiredCount=count }) }
    for i = 1, count do
        table.insert(effects, Effect.WithPresentation(Effect.Choice({ player = ctx.playerId,
            interactionType = 'character.ability.choice.reward', promptKey = 'character.tin_man.tactic.choose_discard_resolution',
            branches = {
                { id = 'gain-gold', effects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 5 }) } },
                { id = 'move-influence', effects = { Effect.MoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter= 'self', count = 1,
                    interactionType = 'character.ability.choice.move', promptKey = 'character.tin_man.tactic.choose_discard_resolution' }) } }
            }
        }), { interaction = {
            prompt = Text.Ref('core:interaction.character.tin_man.tactic.reward.prompt'),
            options = {
                { candidateId = 'gain-gold', label = Text.Ref('core:interaction.character.tin_man.tactic.reward.gain_gold') },
                { candidateId = 'move-influence', label = Text.Ref('core:interaction.character.tin_man.tactic.reward.move_influence') }
            }
        } }))
    end
    return effects
end
