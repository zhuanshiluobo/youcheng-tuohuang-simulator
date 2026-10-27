-- QY-015 正式内容 1.1.0；保存由Host真实节点/事件/续接收据负责。
return function(ctx)
    local snapshot=Global.Scoring.GetFinalControl()
    local player=tonumber(string.match(ctx.playerId,'^p(%d+)$'))
    local regions={}
    for _, influence in ipairs(snapshot.physicalInfluences) do
        if influence.typeId == 'entrepreneur.gang_marker' and influence.subjectKind == 'player' and influence.playerId == player then regions[influence.regionId]=true end
    end
    local effects={}
    for _, region in ipairs(snapshot.regions) do
        if regions[region.regionId] then table.insert(effects,Effect.WithPresentation(Effect.GainScore({player=ctx.playerId,amount=1}),
            {succeeded=Text.Ref('core:entrepreneur.P.P08.bonus',{region=region.regionId})})) end
    end
    return effects
end
