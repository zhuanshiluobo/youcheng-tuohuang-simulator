function Present(context)
    if context.purpose ~= 'interaction' then error('interaction_required') end
    local facts = context.facts
    local interaction = {}
    if facts.completedCount ~= nil and facts.totalCount ~= nil then
        interaction.progress = Text.Ref('core:effect.repeat.progress', {
            completed = facts.completedCount, total = facts.totalCount
        })
    end
    local candidates = facts.candidateIds
    if candidates ~= nil and #candidates == 2 and facts.minSelections == 1 and facts.maxSelections == 1 then
        local hasContinue, hasStop = false, false
        for _, candidate in ipairs(candidates) do
            if candidate == 'continue' then hasContinue = true end
            if candidate == 'stop' then hasStop = true end
        end
        if hasContinue and hasStop then
            interaction.prompt = Text.Ref('core:effect.repeat.continue.prompt')
            interaction.options = {}
            for _, candidate in ipairs(candidates) do
                interaction.options[#interaction.options + 1] = {
                    candidateId = candidate,
                    label = Text.Ref(candidate == 'continue' and 'core:effect.repeat.continue' or 'core:effect.repeat.stop')
                }
            end
        end
    end
    return { interaction = interaction }
end
