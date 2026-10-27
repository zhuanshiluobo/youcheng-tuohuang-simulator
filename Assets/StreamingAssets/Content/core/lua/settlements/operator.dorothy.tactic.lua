-- 正式收尾资格：本来源仅声明本牌markers区的一种返供权限，计数对应同一授权库存。
local rule = {
    entryKind = 'cleanup_order',
    localId = 'operator.dorothy.tactic.cleanup_order',
    availabilityHandlerId = 'operator.dorothy.tactic.settlement.availability',
    wholeEffectPolicy = 'optional',
    presentation = {
        label = { key = 'core:settlement.operator.dorothy.tactic.label' },
        description = { key = 'core:settlement.operator.dorothy.tactic.description' },
        ariaLabel = { key = 'core:settlement.operator.dorothy.tactic.label' },
        iconKey = 'core:settlement.trigger_ready', contentMode = 'icon_text'
    }
}
function rule.availability(ctx)
    local source = ctx.facts.source
    local enabled = ctx.window.kind == 'player_cleanup' and source ~= nil and source.persistentActive == true and type(source.transferableTokenCount) == 'number' and source.transferableTokenCount >= 2
    return { enabled = enabled, disabledReasonKey = enabled and '' or 'core:presentation.unavailable' }
end
return rule

