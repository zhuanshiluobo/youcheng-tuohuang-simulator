return function(ctx)
    local control = Global.Scoring.GetFinalControl()
    if control == nil or control.sessionId == nil then
        error('entrepreneur.T.final.session_missing')
    end
    local face = ctx.payload.activeFaceId
    local amount = 0
    if face == 'T01' then
        local resources = PlayerData.GetResources(ctx.playerId)
        local count = resources.pureOriginium
        -- 非负整数以余数求整除；不消费资源，也不替换默认资源计分配方。
        amount = (count - count % 4) / 4
    elseif face == 'T02' then
        local player = tonumber(string.match(ctx.playerId, '^p(%d+)$'))
        local seen = {}
        for _, region in ipairs(control.regions) do
            if region.controllerPlayerId == player and not seen[region.regionId] then
                seen[region.regionId] = true
                amount = amount + 2
            end
        end
    else
        error('entrepreneur.T.final.active_face_invalid')
    end
    if amount == 0 then return {} end
    return { Effect.WithPresentation(Effect.GainScore({ recipient = ctx.playerId,
        amount = amount, reasonId = 'entrepreneur.' .. face .. '.final' }),
        { succeeded = Text.Ref('core:entrepreneur.T.final.result', { amount = amount }) }) }
end
