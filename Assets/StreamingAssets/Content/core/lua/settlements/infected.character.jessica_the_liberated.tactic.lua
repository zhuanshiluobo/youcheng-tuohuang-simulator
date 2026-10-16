-- 独立纯资格声明；正式目录未接入前不替换现有能力正文。
local rule = {
    entryKind = 'cleanup_order',
    localId = 'infected.character.jessica_the_liberated.tactic.cleanup_order',
    availabilityHandlerId = 'infected.character.jessica_the_liberated.tactic.settlement.availability',
    wholeEffectPolicy = 'optional',
    presentation = {
        label = { key = 'core:settlement.infected.character.jessica_the_liberated.tactic.label' },
        description = { key = 'core:settlement.infected.character.jessica_the_liberated.tactic.description' },
        ariaLabel = { key = 'core:settlement.infected.character.jessica_the_liberated.tactic.label' },
        iconKey = 'core:settlement.trigger_ready', contentMode = 'icon_text'
    }
}
function rule.availability(ctx)
    local binding, payment = ctx.facts.binding, ctx.facts.payment
    local enabled = ctx.window.kind == 'player_cleanup' and binding ~= nil and binding.active == true and type(binding.goldVoucherCost) == 'number' and
        binding.goldVoucherCost >= 0 and payment ~= nil and payment.available ~= nil and type(payment.available.goldVoucher) == 'number' and
        payment.available.goldVoucher >= binding.goldVoucherCost + 2
    return { enabled = enabled, disabledReasonKey = enabled and '' or 'core:presentation.unavailable' }
end
return rule

