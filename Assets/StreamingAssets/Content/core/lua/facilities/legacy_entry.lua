return function(ctx)
    local p = ctx.payload
    return { Effect.ExecuteMainAction({
        player = ctx.playerId,
        allowedActionTypes = { p.effectId },
        executionMode = 'facility_entry',
        facilityId = p.facilityId,
        facilityInstanceId = p.contentInstanceId,
        sourceSlotIndex = p.sourceSlotIndex,
        sourceId = 'facility.entry.' .. p.effectId
    }) }
end
