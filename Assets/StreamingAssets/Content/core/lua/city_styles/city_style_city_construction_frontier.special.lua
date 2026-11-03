return function(ctx)
    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.effectTypeId == 'effect.facility.build' and type(ctx.payload.normalizedResult) == 'table' and ctx.payload.normalizedResult.buildEffectId ~= nil then
            local decks = Global.Content.FindZones({ localZoneId = 'facility_deck' })
            if #decks ~= 1 then error('facility_deck_zone_missing') end
            return { Effect.ShuffleFacilityDeck({ apiVersion = 1, player = ctx.playerId, deckZone = { zoneId = decks[1].id } }) }
        end
        return {}
    end
    return { Effect.LoseScore({ player = ctx.playerId, amount = 1 }),
        Effect.Build({ apiVersion=3, targetMode='select', player = ctx.playerId, sourceKind = 'deck', allowDecline = true , paymentMode='auto' , consumeMainAction=false }) }
end
