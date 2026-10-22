function Present(context)
    if context.purpose ~= 'interaction' then error('interaction_required') end
    local facts = context.facts
    local total = facts.totalAmount
    if type(total) ~= 'number' or total % 1 ~= 0 or total < 1 or total > 99 then error('resource_total_required') end
    local ids = { 'Originium', 'OriginiumShard', 'Iron' }
    local keys = { 'originium', 'originium_shard', 'iron' }
    if facts.rowIds == nil or #facts.rowIds ~= 3 or facts.rowMaximums == nil or #facts.rowMaximums ~= 3 or
        facts.candidateIds == nil or #facts.candidateIds ~= 4 or facts.candidateIds[4] ~= 'total:' .. tostring(total) then
        error('resource_rows_required')
    end
    local rows = {}
    for i, id in ipairs(ids) do
        if facts.rowIds[i] ~= id or facts.candidateIds[i] ~= id or facts.rowMaximums[i] ~= total then error('resource_row_mismatch') end
        rows[i] = { rowId = id, label = Text.Ref('core:interaction.resource.' .. keys[i]) }
    end
    return { interaction = {
        prompt = Text.Ref('core:interaction.resource.choose.prompt', { total = total }), quantityRows = rows
    } }
end
