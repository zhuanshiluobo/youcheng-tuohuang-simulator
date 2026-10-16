-- 独立纯资格声明；正式目录未接入前不替换现有能力正文。
local rule = {
    entryKind = 'cleanup_order',
    localId = 'infected.character.muelsyse.strategy.cleanup_order',
    availabilityHandlerId = 'infected.character.muelsyse.strategy.settlement.availability',
    wholeEffectPolicy = 'optional',
    presentation = {
        label = { key = 'core:settlement.infected.character.muelsyse.strategy.label' },
        description = { key = 'core:settlement.infected.character.muelsyse.strategy.description' },
        ariaLabel = { key = 'core:settlement.infected.character.muelsyse.strategy.label' },
        iconKey = 'core:settlement.chain_defer', contentMode = 'icon_text'
    }
}
function rule.availability(ctx)
    -- 已挂段的顺序资格不受来源离场/候选为空影响；整段政策由显式配置另判。
    local enabled = ctx.window.kind == 'player_cleanup'
    return { enabled = enabled, disabledReasonKey = enabled and '' or 'core:presentation.unavailable' }
end
return rule

