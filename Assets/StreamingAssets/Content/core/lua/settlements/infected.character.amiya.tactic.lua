-- 独立纯资格声明；正式目录未接入前不替换现有能力正文。
local rule = {
    entryKind = 'optional_insertion',
    localId = 'infected.character.amiya.tactic.optional_insertion',
    availabilityHandlerId = 'infected.character.amiya.tactic.settlement.availability',
    presentation = {
        label = { key = 'core:settlement.infected.character.amiya.tactic.label' },
        description = { key = 'core:settlement.infected.character.amiya.tactic.description' },
        ariaLabel = { key = 'core:settlement.infected.character.amiya.tactic.label' },
        iconKey = 'core:settlement.chain_link', contentMode = 'icon_text'
    }
}
local function eligible(ctx, source)
    local use = ctx.facts.currentUse
    return ctx.window.kind == 'character_use_before_mode' and source ~= nil and source.persistentActive == true and
        use ~= nil and type(source.itemId) == 'string' and type(use.cardInstanceId) == 'string' and source.itemId ~= use.cardInstanceId and
        use.modeStarted == false and use.sourceConsumed == false and use.doubleUseGranted == false and use.hasTwoUsableModes == true
end
function rule.availability(ctx)
    local enabled = eligible(ctx, ctx.facts.source)
    return { enabled = enabled, disabledReasonKey = enabled and '' or 'core:presentation.unavailable' }
end
function rule.offers(ctx)
    local offers = {}
    if type(ctx.window.targetUseId) ~= 'string' or ctx.window.targetUseId == '' then return offers end
    for _, source in ipairs(ctx.facts.sourceItems or {}) do
        if source.abilityId == 'infected.character.amiya.tactic' and eligible(ctx, source) then
            offers[#offers + 1] = Settlement.Offer({apiVersion = 1, localId = rule.localId,
                player = ctx.playerId, sourceItemId = source.itemId, windowId = ctx.window.id,
                ownerEffectId = ctx.window.ownerEffectId, maxUses = 1, visibility = 'owner',
                availabilityHandlerId = rule.availabilityHandlerId, presentation = rule.presentation,
                effects = { Effect.Condition({
                    ruleCondition = false,
                    leftEffects = { Effect.ConsumeCharacterDoubleUseSource({apiVersion = 1, player = ctx.playerId,
                        useId = ctx.window.targetUseId, sourceItemId = source.itemId}) },
                    rightEffects = { Effect.SetCharacterDoubleUseRule({apiVersion = 1, player = ctx.playerId,
                        scope = 'current_use', useId = ctx.window.targetUseId, sourceItemId = source.itemId}) }
                }) }
            })
        end
    end
    return offers
end
return rule

