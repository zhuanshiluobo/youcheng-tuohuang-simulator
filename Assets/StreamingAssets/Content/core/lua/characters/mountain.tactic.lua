return function(ctx)
    local player = string.gsub(ctx.playerId, '^p', '')
    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = 2 }) }, rightEffects = { Flow.UpgradeThenActivateEnterpriseSpecial({ apiVersion = 1, player = player, levels = 1 }) } }), { interaction = { prompt = Text.Ref('core:condition.mountain.tactic') } }) }
end
