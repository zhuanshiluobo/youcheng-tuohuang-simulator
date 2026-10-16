function Present(context)
    if context.purpose ~= 'interaction' then error('interaction_required') end
    if context.abilityId ~= 'facility.entry.build_additional_facility' and
        context.abilityId ~= 'facility.entry.build_extension_hub' then return {} end
    if context.sourceItemId == nil or context.sourceItemId == '' then return {} end
    local facts = context.facts
    local prompt = facts.promptKey
    local key = nil
    if prompt == 'facility.entry.choose_additional_build' then
        key = 'core:interaction.facility.build.choose'
    elseif prompt == 'effect.build.choose_payment' then
        key = 'core:interaction.facility.build.payment'
    else
        return {}
    end
    local options = {}
    for _, id in ipairs(facts.candidateIds) do
        local label = nil
        if prompt == 'facility.entry.choose_additional_build' then
            if id ~= 'choice.skip' then
                label = Text.Ref('core:interaction.entity.item', { entity = Text.Item(id) })
            end
        elseif id == 'resources' or id == 'gold' then
            label = Text.Ref('core:interaction.facility.build.' .. id)
        end
        if label ~= nil then options[#options + 1] = { candidateId = id, label = label } end
    end
    return { interaction = { prompt = Text.Ref(key), options = options } }
end
