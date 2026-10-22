function Present(context)
    if context.purpose ~= 'enterprise_detail' then error('enterprise_detail_required') end
    local descriptions = context.facts.enterpriseLevelDescriptions
    if descriptions == nil or #descriptions == 0 then error('enterprise_level_descriptions_missing') end
    local details = {}
    for _, description in ipairs(descriptions) do
        details[#details + 1] = Text.Ref(description.key, description.arguments)
    end
    return { details = details }
end
