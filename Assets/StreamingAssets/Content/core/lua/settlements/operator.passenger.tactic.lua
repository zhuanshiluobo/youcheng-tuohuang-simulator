-- 独立纯资格声明；正式目录未接入前不替换现有能力正文。
local rule = {
    entryKind = 'optional_insertion',
    localId = 'operator.passenger.tactic.optional_insertion',
    availabilityHandlerId = 'operator.passenger.tactic.settlement.availability',
    presentation = {
        label = { key = 'core:settlement.operator.passenger.tactic.label' },
        description = { key = 'core:settlement.operator.passenger.tactic.description' },
        ariaLabel = { key = 'core:settlement.operator.passenger.tactic.label' },
        iconKey = 'core:settlement.substitute', contentMode = 'icon_text'
    }
}
local function eligible(ctx, source)
    local payment, cost = ctx.facts.payment, ctx.window.paymentCost
    return ctx.window.kind == 'resource_payment_before_commit' and source ~= nil and source.persistentActive == true and
        type(source.markerCount) == 'number' and source.markerCount > 0 and payment ~= nil and payment.canConfirm == true and payment.isCommitted == false and
        cost ~= nil and type(cost.originium) == 'number' and type(cost.originiumShard) == 'number' and type(cost.iron) == 'number' and
        cost.originium + cost.originiumShard + cost.iron > 0
end
function rule.availability(ctx)
    local enabled = eligible(ctx, ctx.facts.source)
    return { enabled = enabled, disabledReasonKey = enabled and '' or 'core:presentation.unavailable' }
end
function rule.offers(ctx)
    local offers = {}
    for _, source in ipairs(ctx.facts.sourceItems or {}) do
        if source.abilityId == 'operator.passenger.tactic' and eligible(ctx, source) and
            type(source.storageZoneId) == 'string' and source.storageZoneId ~= '' then
            offers[#offers + 1] = Settlement.Offer({apiVersion = 1, localId = rule.localId,
                player = ctx.playerId, sourceItemId = source.itemId, windowId = ctx.window.id,
                ownerEffectId = ctx.window.ownerEffectId, maxUses = 1, visibility = 'owner',
                availabilityHandlerId = rule.availabilityHandlerId, presentation = rule.presentation,
                effects = { Effect.PaymentSubstitution({apiVersion = 1, player = ctx.playerId,
                    paymentOwnerEffectId = ctx.window.ownerEffectId, sourceZoneId = source.storageZoneId,
                    substitutionBudget = 2}) }
            })
        end
    end
    return offers
end
return rule

