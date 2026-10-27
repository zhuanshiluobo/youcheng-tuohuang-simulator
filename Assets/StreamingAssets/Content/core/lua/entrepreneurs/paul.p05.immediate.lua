-- QY-015 正式内容 1.1.0；保存由Host真实节点/事件/续接收据负责。
return function(ctx)
    return {Effect.LoseScore({player=ctx.playerId,amount=2}),
        Effect.GainResource({player=ctx.playerId,resourceType='PureOriginium',amount=1}),
        Effect.GainResource({player=ctx.playerId,resourceType='GoldVoucher',amount=15})}
end
