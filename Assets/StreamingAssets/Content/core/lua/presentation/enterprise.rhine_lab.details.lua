function Present(context)
    if context.purpose ~= 'enterprise_detail' then error('enterprise_detail_required') end
    local descriptions = context.facts.enterpriseLevelDescriptions
    if descriptions == nil or #descriptions ~= 1 then error('enterprise_level_descriptions_missing') end
    local details = { Text.Ref(descriptions[1].key, descriptions[1].arguments) }
    if context.facts.activeDepartmentDefinitionId == nil then
        details[#details + 1] = Text.Ref('core:enterprise.rhine_lab.department_missing.description')
    else
        local rewards = context.facts.activeDepartmentLevelDescriptions
        if rewards == nil or #rewards ~= 4 then error('active_department_level_descriptions_missing') end
        for _, description in ipairs(rewards) do
            details[#details + 1] = Text.Ref(description.key, description.arguments)
        end
    end
    return { details = details }
end
