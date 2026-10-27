return function(ctx)
    local p = ctx.payload
    local rules = {}
    local player = tonumber(string.match(ctx.playerId, '^p(%d+)$'))
    if p.queryKind == 'resource_payment_substitution.v1'
        and player ~= nil and tonumber(p.facts.playerId) == player then
        rules[1] = {
            ruleId = 'passenger.stored_markers', purposeKind = 'payment',
            allowedResourceTypes = {'Originium', 'OriginiumShard', 'Iron'},
            maxUnits = 2, quotaGroupId = 'passenger.stored_markers.per_payment',
            quotaLimit = 2, quotaScope = 'payment', unitResourceCost = {},
            itemCost = {localZoneId = 'markers', countPerUnit = 1,
                destinationKind = 'original_supply'}
        }
    end
    return {queryId = p.queryId, basisDigest = p.basisDigest, items = rules}
end
