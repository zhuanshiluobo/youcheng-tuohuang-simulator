-- 独立纯资格声明；正式目录未接入前不替换现有能力正文。
local rule = {
    entryKind = 'cleanup_order',
    localId = 'operator.mudrock.tactic.cleanup_order',
    availabilityHandlerId = 'operator.mudrock.tactic.settlement.availability',
    wholeEffectPolicy = 'optional',
    presentation = {
        label = { key = 'core:settlement.operator.mudrock.tactic.label' },
        description = { key = 'core:settlement.operator.mudrock.tactic.description' },
        ariaLabel = { key = 'core:settlement.operator.mudrock.tactic.label' },
        iconKey = 'core:settlement.trigger_ready', contentMode = 'icon_text'
    }
}
function rule.availability(ctx)
    local source, payment = ctx.facts.source, ctx.facts.payment
    local enabled = false
    if ctx.window.kind == 'player_cleanup' and source ~= nil and source.persistentActive == true and payment ~= nil and payment.available ~= nil and
        type(payment.available.originium) == 'number' then
        local missing = 2 - payment.available.originium
        enabled = missing <= 0
        if not enabled and payment.markerSources ~= nil then
            for _, candidate in ipairs(payment.markerSources) do
                if candidate.eligible == true and type(candidate.markerCount) == 'number' and candidate.markerCount >= missing and missing <= 2 then enabled = true end
            end
        end
    end
    return { enabled = enabled, disabledReasonKey = enabled and '' or 'core:presentation.unavailable' }
end
return rule

