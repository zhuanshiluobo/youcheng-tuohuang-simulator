return function(ctx)
    local excluded = {}
    for _, row in ipairs(ctx.payload.context.destinations) do
        if row.locationId == 'A-01' then
            table.insert(excluded, row.candidateId)
        end
    end
    if #excluded == 0 then return {} end
    return {Candidate.Remove({ids = excluded, reasonCode = 'example.mixed.city'})}
end
