-- QY-015 正式内容 1.1.1；保存由Host真实节点/事件/续接收据负责。
return function(ctx)
    return {Effect.WithPresentation(Effect.Condition({ruleCondition=true,decisionPlayer=ctx.playerId,
        leftEffects={Effect.PayResource({player=ctx.playerId,cost={GoldVoucher=4}})},
        rightEffects={Effect.SelectInfluence({player=ctx.playerId,operation='replace',subjectFilter='self',count=1}),
            Effect.SelectInfluence({player=ctx.playerId,operation='move',subjectFilter='self',count=1})}}),
        {interaction={prompt=Text.Ref('core:entrepreneur.P.P02.condition')}})}
end
