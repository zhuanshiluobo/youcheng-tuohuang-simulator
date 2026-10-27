-- QY-015 正式内容 1.1.1；保存由Host真实节点/事件/续接收据负责。
return function(ctx)
    return {
        Effect.WithPresentation(Effect.SelectInfluence({player=ctx.playerId,operation='move',subjectFilter='self',count=1,allowDecline=true}),{interaction={prompt=Text.Ref('core:entrepreneur.P.P01.first_move')}}),
        Effect.SelectInfluence({player=ctx.playerId,operation='remove',subjectFilter='self',count=1}),
        Effect.SelectInfluence({player=ctx.playerId,operation='move',subjectFilter='self',count=1})}
end
