function Present(context)
    if context.purpose ~= 'interaction' then error('interaction_required') end
    local facts = context.facts
    if context.sourceDefinitionId == nil or context.sourceDefinitionId == '' then error('visible_source_required') end
    if facts.minAmount == nil or facts.maxAmount == nil or facts.minAmount < 0 or facts.maxAmount < facts.minAmount then error('quantity_bounds_required') end
    if facts.rowIds == nil or #facts.rowIds ~= 1 or facts.rowIds[1] ~= 'player_marker' or
       facts.rowMaximums == nil or #facts.rowMaximums ~= 1 or facts.rowMaximums[1] ~= facts.maxAmount then error('quantity_row_required') end
    return { interaction = {
        prompt = Text.Ref('core:character.store_markers.prompt', {
            maximum = facts.maxAmount, source = Text.Definition(context.sourceDefinitionId)
        }),
        quantityRows = { { rowId = 'player_marker', label = Text.Ref('core:quantity.player_marker.row') } }
    } }
end
