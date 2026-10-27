local function place(player)
    return Effect.PlaceInfluence({ executingPlayer = player, ownerSubject = player,
        influenceSource = 'player_supply', causeKind = 'other_intrinsic_flow' })
end

return function(ctx)
    if ctx.payload.activeFaceId == 'T03' then
        -- 逗号不是条件：没有目标时原选择原语正常结束，仍执行后面的资源节点。
        -- 有合法目标则必须选；真实移除及其必要去向响应结束后才进入兄弟Gain。
        return {
            Effect.WithPresentation(Effect.SelectInfluence({ player = ctx.playerId,
                operation = 'remove', subjectFilter = 'self', count = 1, allowDecline = false }),
                { interaction = { prompt = Text.Ref('core:entrepreneur.T.T03.remove.prompt') } }),
            Effect.GainResource({ recipient = ctx.playerId, resourceType = 'PureOriginium', amount = 2 })
        }
    end
    if ctx.payload.activeFaceId == 'T04' then
        return { place(ctx.playerId), place(ctx.playerId) }
    end
    error('entrepreneur.T.secondary.active_face_invalid')
end
