return function(ctx)
    local effects = { Effect.GainScore({ recipient = ctx.playerId, amount = 1 }) }
    for _, cost in ipairs({12, 15}) do
        table.insert(effects, Effect.Condition({ allowDecline = true, decisionPlayer = ctx.playerId,
            declinePromptKey = 'character.tin_man.purchase.' .. tostring(cost),
            leftEffects = { Effect.PayResource({ payer = ctx.playerId, resourceType = 'gold_voucher', amount = cost }) },
            rightEffects = { Effect.GainResource({ recipient = ctx.playerId, resourceType = 'pure_originium', amount = 1 }) }
        }))
    end
    return effects
end
