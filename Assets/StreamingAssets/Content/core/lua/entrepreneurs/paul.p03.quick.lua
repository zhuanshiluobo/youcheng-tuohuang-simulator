-- QY-015 正式内容 1.1.0；保存由Host真实节点/事件/续接收据负责。
return function(ctx)
    return {Effect.WithPresentation(Effect.Condition({ruleCondition=true,decisionPlayer=ctx.playerId,
        leftEffects={Effect.PayResource({apiVersion=2,player=ctx.playerId,resourceType='total',
            allowedResourceTypes={'Originium','OriginiumShard','Iron'},amount=2})},
        rightEffects={Effect.PlaceInfluence({executingPlayer=ctx.playerId,ownerSubject=ctx.playerId,
            influenceSource='player_supply',causeKind='other_intrinsic_flow'})}}),
        {interaction={prompt=Text.Ref('core:entrepreneur.P.P03.condition')}})}
end
