return function(ctx)
    -- 建设取消、无资源或无空位后仍继续洗牌；恢复沿洗牌收据。
    local decks = Global.Content.FindZones({ localZoneId = 'facility_deck' })
    if #decks ~= 1 then error('facility_deck_zone_missing') end
    return { Effect.Build({ apiVersion=3, targetMode='select', player = ctx.playerId, sourceKind = 'deck', paymentMode = 'auto', allowDecline = true , consumeMainAction=false }),
        Effect.ShuffleFacilityDeck({ apiVersion = 1, player = ctx.playerId, deckZone = { zoneId = decks[1].id } }) }
end
