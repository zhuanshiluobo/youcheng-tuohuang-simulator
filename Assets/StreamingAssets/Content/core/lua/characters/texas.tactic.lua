return function(ctx)
    return { Effect.WithPresentation(Effect.Condition({
        leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 3 }) },
        rightEffects = {
            Effect.RemoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter= 'any',
                interactionType = 'character.ability.target', promptKey = 'character.texas.tactic.choose_removal' }),
            Effect.MoveInfluence({ apiVersion=3, targetMode='select', executingPlayer= ctx.playerId, subjectFilter= 'self', count = 2, distinctPolicy = 'instance',
                interactionType = 'character.ability.choice.move', promptKey = 'character.texas.tactic.choose_first_move', nextPromptKey = 'character.texas.tactic.choose_second_move' })
        }
    }), { interaction = { prompt = Text.Ref('core:condition.texas.tactic') } }) }
end
