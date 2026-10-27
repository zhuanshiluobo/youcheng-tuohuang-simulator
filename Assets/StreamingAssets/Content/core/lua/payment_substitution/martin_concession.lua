return function(ctx)
    local p = ctx.payload
    local q = p.facts
    local rules = {}
    local player = tonumber(string.match(ctx.playerId, '^p(%d+)$'))
    if p.queryKind == 'resource_payment_substitution.v1'
        and q.purposeKind == 'build' and q.buildIdentity ~= ''
        and player ~= nil and tonumber(q.playerId) == player then
        -- 本人即使错误持有奖励，也不取得本项卡面资格。
        local ownMartin = Global.Content.FindInstances({
            definitionId = 'entrepreneur.ability.M13', controller = ctx.playerId
        })
        if #ownMartin == 0 then
            rules[1] = {
                ruleId = 'martin.concession', purposeKind = 'build',
                allowedResourceTypes = {'Originium', 'OriginiumShard', 'Iron'},
                maxUnits = 1, quotaGroupId = 'martin.concession.per_build',
                quotaLimit = 1, quotaScope = 'build',
                unitResourceCost = {GoldVoucher = 1},
                insertion = {
                    apiVersion = 1, maxUses = 1,
                    presentation = {
                        label = Text.Ref('core:entrepreneur.M.M01.name'),
                        description = Text.Ref('core:entrepreneur.M.M01.description'),
                        ariaLabel = Text.Ref('core:entrepreneur.M.M01.name'),
                        contentMode = 'text'
                    }
                }
            }
        end
    end
    return {queryId = p.queryId, basisDigest = p.basisDigest, items = rules}
end
