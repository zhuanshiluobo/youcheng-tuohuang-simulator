return function(ctx)
    return {
        Effect.GainResource({ recipient = ctx.playerId, resourceType = 'gold_voucher', amount = 4 }),
        Effect.WithPresentation(Effect.ChooseResources({ player = ctx.playerId, amount = 2 }), {
            interaction = {
                prompt = Text.Ref('core:interaction.enterprise.maylander.resources.prompt', { total = 2 }),
                quantityRows = {
                    { rowId = 'Originium', label = Text.Ref('core:interaction.resource.originium') },
                    { rowId = 'OriginiumShard', label = Text.Ref('core:interaction.resource.originium_shard') },
                    { rowId = 'Iron', label = Text.Ref('core:interaction.resource.iron') }
                }
            }
        })
    }
end
