-- QY-015 正式内容 1.1.0；保存由Host真实节点/事件/续接收据负责。
return function(ctx)
    local items = {}
    if ctx.payload.purpose == 'resource_profile' then
        local rock,iron,gold=6,4,25
        if ctx.payload.facts.mapId == 'map-three-players' then rock,iron,gold=7,5,30 end
        items[1]={kind='resource_profile',playerId=ctx.payload.authorizedPlayerId,profileId='entrepreneur.P07.remaining',entries={
            {resourceType='Originium',units=rock,points=1},{resourceType='Iron',units=iron,points=1},
            {resourceType='PureOriginium',units=1,points=1},{resourceType='GoldVoucher',units=gold,points=1}}}
    end
    return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items=items}
end
