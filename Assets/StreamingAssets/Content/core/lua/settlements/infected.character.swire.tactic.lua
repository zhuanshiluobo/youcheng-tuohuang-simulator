-- 独立纯资格声明；正式目录未接入前不替换现有能力正文。
local rule = {
    entryKind = 'cleanup_order',
    localId = 'infected.character.swire.tactic.cleanup_order',
    availabilityHandlerId = 'infected.character.swire.tactic.settlement.availability',
    wholeEffectPolicy = 'optional',
    presentation = {
        label = { key = 'core:settlement.infected.character.swire.tactic.label' },
        description = { key = 'core:settlement.infected.character.swire.tactic.description' },
        ariaLabel = { key = 'core:settlement.infected.character.swire.tactic.label' },
        iconKey = 'core:settlement.target_other', contentMode = 'icon_text'
    }
}
function rule.availability(ctx)
    local source = ctx.facts.source
    local enabled = ctx.window.kind == 'player_cleanup' and source ~= nil and source.persistentActive == true and type(source.markerCount) == 'number' and source.markerCount >= 1
    return { enabled = enabled, disabledReasonKey = enabled and '' or 'core:presentation.unavailable' }
end
return rule

