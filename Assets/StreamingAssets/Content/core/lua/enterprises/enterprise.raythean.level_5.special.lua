return function(ctx)
    -- 建设取消、无资源或无空位后仍继续洗牌；恢复沿洗牌收据。
    return { Effect.ChooseBuild({ player = ctx.playerId, sourceZone = 'deck', paymentMode = 'auto', allowDecline = true }), Effect.ShuffleFacilityDeck({ player = ctx.playerId }) }
end
