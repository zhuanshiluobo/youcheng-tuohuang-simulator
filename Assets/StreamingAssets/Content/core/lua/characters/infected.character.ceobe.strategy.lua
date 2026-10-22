return function(ctx)
    return {Effect.WithPresentation(Effect.Condition({leftEffects={Effect.PayResource({player=ctx.playerId,resourceType='originium_shard',amount=1})},rightEffects={Effect.ExecuteMainAction({player=ctx.playerId,executionMode='immediate',allowedActionTypes={'deploy','dispatch','explore','move_city','build'}})}}), { interaction = { prompt = Text.Ref('core:condition.infected.character.ceobe.strategy') } })}
end
