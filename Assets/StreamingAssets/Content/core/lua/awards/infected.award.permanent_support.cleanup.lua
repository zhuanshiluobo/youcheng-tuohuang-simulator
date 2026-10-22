return function(ctx)
    return { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 3 }),
        Effect.WithPresentation(Effect.ChooseResources({ player = ctx.playerId, amount = 1 }), {
            interaction = {
                prompt = Text.Ref('core:interaction.award.permanent_support.resources.prompt', { total = 1 }),
                quantityRows = {
                    { rowId = 'Originium', label = Text.Ref('core:interaction.resource.originium') },
                    { rowId = 'OriginiumShard', label = Text.Ref('core:interaction.resource.originium_shard') },
                    { rowId = 'Iron', label = Text.Ref('core:interaction.resource.iron') }
                }
            }
        }), Effect.SelectInfluence({ player = ctx.playerId, operation = 'move', subjectFilter = 'self', count = 1, requiredCount = 0 }) }
end
