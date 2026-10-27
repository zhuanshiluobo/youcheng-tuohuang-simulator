-- QY-013 / 1.8.1。冻结模块；仅构造Effect，不保存VM状态或写Host。
-- 入口薄脚本及其依赖由唯一整合者登记。rules来自同版本候选ruleRefs，非回合数推断。
local M = {}
local function pid(p)
    local n = tonumber(string.match(p, '^p(%d+)$'))
    if n == nil then error('agnes_player_invalid') end
    return n
end
local function copy(v)
    if type(v) ~= 'table' then return v end
    local r = {}; for k, x in pairs(v) do r[k] = copy(x) end; return r
end
local function number(v, name)
    if type(v) ~= 'number' or v < 0 or v % 1 ~= 0 then error('agnes_fact_invalid:' .. name) end
    return v
end
local function single(values, name)
    if #values ~= 1 then error('agnes_unique_fact_missing:' .. name) end
    return values[1]
end
local function ability(ctx)
    local card = single(Global.Content.FindInstances({definitionId='entrepreneur.ability.A01',controller=ctx.playerId}),'A01')
    if card == nil or card.controller ~= ctx.playerId then error('agnes_ability_owner_invalid') end
    return card
end
local function zone(card, localId)
    return single(Global.Content.FindZones({parentId=card,localZoneId=localId}), localId)
end
local function active(item, player)
    if item == nil or item.controller ~= player then return false end
    local z = Global.Content.FindZones({id=item.zoneId})
    return #z == 1 and z[1].templateId == 'core.zone.entrepreneur_active'
end
local function has_rule(ctx, rules, rule)
    local card = ability(ctx)
    for _, g in ipairs(card.roundChoices or {}) do
        if g.selectedCandidateId ~= nil and g.selectedCandidateId ~= '' then
            for _, c in ipairs(g.candidates or {}) do
                if c.candidateId == g.selectedCandidateId and c.faceId == g.activeFaceId then
                    local item = Global.Content.GetInstance(c.entityItemId)
                    if active(item, ctx.playerId) then
                        -- 显式冻结依赖，DIY未声明该规则不会被官方回合号/A-B隐式启用。
                        for _, ref in ipairs(rules[c.candidateId] or {}) do if ref == rule then return true end end
                    end
                end
            end
        end
    end
    return false
end
local function text(key, args)
    if args == nil then return Text.Ref('core:entrepreneur.A.' .. key) end
    return Text.Ref('core:entrepreneur.A.' .. key, args)
end
local function present(effect, key, args)
    return Effect.WithPresentation(effect, {started=text(key,args)})
end
local function present_condition(effect, key, args)
    local prompt=text(key,args)
    return Effect.WithPresentation(effect, {started=prompt,interaction={prompt=prompt}})
end
local function saved(effect, c, stage)
    local next = copy(c); next.stage = stage
    return Effect.WithContext(effect, next)
end
local function choice(ctx, ids, labels, optional, key, integerLabel)
    local options = {}; for _, id in ipairs(ids) do
        if labels[id]~=nil then options[#options+1]={candidateId=id,label=labels[id]} end
    end
    return Effect.WithPresentation(Effect.Choice({player=ctx.playerId,options=ids,
        minSelections=optional and 0 or 1,maxSelections=1,interactionType='effect.choice.execute'}),
        {interaction={prompt=text(key),options=options,candidateIntegerLabel=integerLabel}})
end
local function selected(ctx)
    local value = ctx.payload.normalizedResult
    if type(value) == 'table' then
        if value.value ~= nil then value=value.value
        elseif #value == 0 then return nil
        elseif #value == 1 then value=value[1]
        else error('agnes_choice_result_invalid') end
    end
    if value == nil or value == '' or value == false then return nil end
    if type(value) ~= 'string' then error('agnes_choice_result_invalid') end
    return value
end
local function gain(resource, amount, player)
    return Effect.GainResource({recipient=player,resourceType=resource,amount=amount})
end
local function command_supply(ctx)
    local player=ctx.playerId
    local supply=single(Global.Content.FindZones({templateId='core.zone.player_supply',owner=player}),'player_supply')
    local ids={}
    for _, item in ipairs(Global.Content.FindInstances({zoneId=supply.id,typeId='core.player_marker'})) do
        if item.ownerSubject==player then ids[#ids+1]=item.id end
    end
    table.sort(ids)
    return supply.id,ids
end
local function single_command_move(player, supply, destination, id)
    if id~=false and (type(id)~='string' or id=='') then error('agnes_marker_context_invalid') end
    return Effect.MoveItem({apiVersion=3,player=player,itemIds=id and {id} or {},expectedSource={zoneId=supply},
        destination={zoneId=destination},insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=1})
end
local function single_command_moves(ctx, card, supply, destination, markers)
    if ability(ctx).id~=card then error('agnes_ability_owner_invalid') end
    if type(markers)~='table' or #markers<1 or #markers>16 or type(supply)~='string' or supply=='' or
        destination~=zone(card,'commands').id then error('agnes_marker_context_invalid') end
    local effects={}
    local player=ctx.playerId
    for i=1,#markers do
        local id=markers[i]
        -- 179：生成单枚后由节点冻结真实ID；缺项不虚构ID，仅该空集/requiredCount=1移动失败。
        effects[#effects+1]=single_command_move(player,supply,destination,id)
    end
    return effects
end
local function transfer_commands(ctx, card, k, supply, inventory)
    number(k,'commandGainCount');if k<1 or k>99 then error('agnes_command_gain_count_invalid') end
    if inventory==nil then supply,inventory=command_supply(ctx) end
    local destination=zone(card,'commands').id
    local effects={}
    for first=1,k,16 do
        local markers={};local last=first+15;if k<last then last=k end
        local begin=k<=16 and first or first+1
        for i=begin,last do markers[#markers+1]=inventory[i] or false end
        if k<=16 then return single_command_moves(ctx,card,supply,destination,markers) end
        -- 续接只挂实际首枚移动，不用空式占位；成功或失败均继续其余已确定的单枚，不重试首枚。
        local effect=single_command_move(ctx.playerId,supply,destination,inventory[first] or false)
        if #markers>0 then effect=Effect.WithContext(effect,
            {stage='command_moves',card=card,supply=supply,destination=destination,markers=markers}) end
        effects[#effects+1]=effect
    end
    return effects
end
local function purchase(ctx, card, k, supply, inventory)
    return present_condition(Effect.Condition({decisionPlayer=ctx.playerId,ruleCondition=true,
        leftEffects={Effect.PayResource({player=ctx.playerId,cost={GoldVoucher=2*k}})},
        rightEffects=transfer_commands(ctx,card,k,supply,inventory)}), 'command.buy', {count=k,cost=2*k})
end
function M.purchaseCommand(ctx,k)
    number(k,'purchaseCount'); if k<1 or k>99 then error('agnes_purchase_count_invalid') end
    local card=ability(ctx).id
    local supply,inventory=command_supply(ctx)
    local target=zone(card,'commands')
    local capacity=target.capacity
    if type(capacity)~='number' or capacity < -1 or capacity%1~=0 then error('agnes_capacity_unavailable') end
    if #inventory<k or capacity~=-1 and capacity-number(target.itemCount,'itemCount')<k then return {} end
    return {purchase(ctx,card,k,supply,inventory)}
end
local function legal_counts(ctx, card, n, paid)
    number(n,'departureCount') -- 完整n保留，不截到29/99。
    local z=zone(card,'commands')
    local capacity=z.capacity
    if type(capacity)~='number' or capacity < -1 or capacity%1~=0 then error('agnes_capacity_unavailable') end
    local presentCount=number(z.itemCount,'itemCount')
    local supply,inventory=command_supply(ctx)
    local stock=#inventory
    local gold=number(PlayerData.GetResources(ctx.playerId).availableGoldVoucherCount,'availableGoldVoucherCount')
    local ids, labels={},{}
    -- 保留既有合法数量范围；n可更大，不把n改为库存或候选上限。
    local largest=99
    if n<largest then largest=n end
    if stock<largest then largest=stock end
    if capacity~=-1 and capacity-presentCount<largest then largest=capacity-presentCount end
    local affordable=(gold-gold%2)/2
    if paid and affordable<largest then largest=affordable end
    for k=largest,1,-1 do
        -- ID保留原数字字符串；标签沿紧凑整数模板消费，不展开99份标签。
        ids[#ids+1]=tostring(k)
    end
    return ids,labels
end
function M.gainCommand(ctx,rules,n)
    local card=ability(ctx).id
    local paid=has_rule(ctx,rules,'entrepreneur.rule.A.command_price')
    if n==0 then return {} end
    if n==1 and paid then return M.purchaseCommand(ctx,1) end
    local ids,labels=legal_counts(ctx,card,n,paid)
    if #ids==0 then return {} end
    local integerLabel=paid and {key='core:entrepreneur.A.command.buy',arguments={{name='cost',multiplier=2},{name='count',multiplier=1}}}
        or {key='core:entrepreneur.A.command.gain',arguments={{name='count',multiplier=1}}}
    return {saved(choice(ctx,ids,labels,true,paid and 'command.choose_paid' or 'command.choose',integerLabel),{card=card,paid=paid,n=n},'command_selected')}
end
local function source_pages(player,query,args,visit)
    local cursor,revision,content,total=0,nil,nil,nil
    local read=0
    repeat
        -- args是调用方新建的私有表；复用游标参数，不逐页递归复制筛选条件。
        args.cursor=cursor
        args.expectedStateRevision=revision;args.expectedContentHash=content
        local page=query(args)
        if page.protocolVersion~=1 or page.authorizedPlayerId~=player then error('agnes_inventory_scope_invalid') end
        local pageRevision,pageContent,pageTotal=page.stateRevision,page.contentHash,page.totalCount
        if total==nil then
            revision,content,total=pageRevision,pageContent,number(pageTotal,'inventoryTotal')
        elseif pageRevision~=revision or pageContent~=content or pageTotal~=total then
            error('agnes_inventory_page_changed')
        end
        local items=page.items;local count=#items
        visit(items,count)
        read=read+count
        local nextCursor=page.nextCursor
        if nextCursor~=-1 and (nextCursor<=cursor or nextCursor>total) then error('agnes_inventory_cursor_invalid') end
        cursor=nextCursor
    until cursor==-1
    if read~=total then error('agnes_inventory_pages_incomplete') end
end
local function command_inventory(ctx,card)
    local player=ctx.playerId
    if card==nil then
        -- availability没有Global；通过同次授权来源全集找本人真实A01，不借跨卡库存总数。
        local ids,seen={},{}
        source_pages(player,SourceData.GetCurrentSources,{definitionId='entrepreneur.ability.A01'},function(items,count)
            for i=1,count do
                local s=items[i]
                if s.authorizedPlayerId~=player then error('agnes_ability_owner_invalid') end
                local id=s.itemId
                if not seen[id] then seen[id]=true;ids[#ids+1]=id end
            end
        end)
        card=single(ids,'A01_source')
    end
    local ids={}
    -- 三个精确筛选由正式SourceData合同执行，不在Lua再次读取相同代理字段。
    source_pages(player,SourceData.GetTransferableTokens,
        {carrierItemId=card,localZoneId='commands',typeId='core.player_marker'},function(items,count)
            for index=1,count do
                local t=items[index]
                if t.ownerSubject==player then
                    local operations=t.operations or {}
                    local permitted=operations[1]=='transfer.original_supply'
                    if not permitted then
                        for i=2,#operations do if operations[i]=='transfer.original_supply' then permitted=true;break end end
                    end
                    if permitted then
                        -- 正式集合由真实实例ID的HashSet构造；字符串/唯一性由查询合同保证。
                        -- 选定数组仍由MoveItem在提交时核真实身份、重复项和足额，不在Lua重建身份索引。
                        ids[#ids+1]=t.itemId
                    end
                end
            end
        end)
    -- A01费用只数本人普通标记；ID决定确定顺序，不合并真实实例或原供应。
    table.sort(ids)
    return ids
end
function M.payCommand(ctx,n)
    number(n,'commandCost');if n<1 or n>99 then error('agnes_command_cost_invalid') end
    local card=ability(ctx).id
    local inventory=command_inventory(ctx,card)
    local take=n;if #inventory<take then take=#inventory end
    local ids={};for i=1,take do ids[#ids+1]=inventory[i] end
    -- 不足仍构造整笔requiredCount，交通用预检全零失败，绝不返回空效果绕过费用。
    return Effect.MoveItem({apiVersion=3,player=ctx.playerId,itemIds=ids,
        expectedSource={zoneId=zone(card,'commands').id},destination={kind='original_supply'},
        insertion={kind='unordered'},operationProfileId='core.player_marker_storage',requiredCount=n})
end
local function place(ctx)
    return Effect.PlaceInfluence({executingPlayer=ctx.playerId,
        influenceSource='player_supply',ownerSubject=ctx.playerId})
end
local function remove(ctx)
    return Effect.SelectInfluence({player=ctx.playerId,operation='remove',subjectFilter='any',
        count=1,requiredCount=0,allowDecline=false})
end
local function move(ctx, own)
    return Effect.SelectInfluence({player=ctx.playerId,operation='move',subjectFilter=own and 'self' or 'any',
        count=1,requiredCount=0,allowDecline=false})
end
local function alternatives(ctx, kind)
    local effects=kind=='move' and {move(ctx,true),remove(ctx)} or {place(ctx),remove(ctx)}
    local id=kind=='move' and 'move' or 'place'
    return Effect.WithPresentation(Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',branches={
        {id=kind=='move' and 'move' or 'place',effects={effects[1]}},{id='remove',effects={effects[2]}}}}),
        {started=text(kind=='move' and 'tactic.move_remove' or 'tactic.place_remove'),
         interaction={prompt=text('tactic.choose'),options={{candidateId=id,label=text('map.'..id)},{candidateId='remove',label=text('map.remove')}}}})
end
function M.a08Placements(ctx) return {place(ctx),place(ctx)} end
function M.a08Cleanup(ctx)
    return {Effect.SelectInfluence({player=ctx.playerId,operation='remove',subjectFilter='self',count=1,
        requiredCount=0,allowDecline=false,distinctPolicy='instance',interactionType='effect.influence.remove.target',promptKey='core:entrepreneur.A.cleanup.remove'})}
end
function M.a08Tactic(ctx)
    local effects=M.a08Placements(ctx)
    effects[#effects+1]=Effect.ScheduleCleanupObligation({apiVersion=1,player=ctx.playerId,
        handlerRef='entrepreneur.A08.cleanup_due',dueRound=GameData.GetRound(),
        context={protocolVersion=1,cleanupKind='remove_own',count=1,requiredCount=0,
            settlementPresentation={label=text('cleanup.obligation.name'),description=text('cleanup.obligation.description'),ariaLabel=text('cleanup.obligation.name')}}})
    return effects
end
function M.cleanupDue(ctx)
    -- 只消费登记时冻结的业务上下文；来源已退出仍必须履约，不发现新来源/调用权。
    if ctx.eventType=='EffectCompleted' then return {} end
    local c=ctx.payload.arguments
    if c==nil or c.protocolVersion~=1 or c.cleanupKind~='remove_own' or c.count~=1 or c.requiredCount~=0 then
        error('agnes_cleanup_context_invalid')
    end
    return M.a08Cleanup(ctx)
end
function M.cover(ctx,rules)
    -- 正式阶段事件PlayerId=-1；coveredPlayerIds才是本阶段实际完成玩家集合。
    local included=false
    for _, player in ipairs(ctx.payload.coveredPlayerIds or {}) do if player==ctx.playerId then included=true end end
    if not included then return {} end
    if not has_rule(ctx,rules,'entrepreneur.rule.A.cover_command') then return {} end
    return M.gainCommand(ctx,rules,1)
end
local function callable_actions(ctx)
    local cursor,revision,content,total,enabled,transferable=0,nil,nil,nil,nil,nil
    local items,seen={},{}
    repeat
        -- 处理器身份由真实SourceUse/已激活Action/cleanup收据决定；Lua不提供H0/H1或政策自授权。
        local page=SourceData.GetCallableActions({cursor=cursor,expectedStateRevision=revision,expectedContentHash=content})
        if page.protocolVersion~=1 or page.authorizedPlayerId~=ctx.playerId then error('agnes_callable_scope_invalid') end
        if total==nil then
            revision,content=page.stateRevision,page.contentHash
            total=number(page.totalCount,'callableTotal'); enabled=number(page.enabledCount,'enabledCount')
            transferable=number(page.transferableTokenCount,'transferableTokenCount')
        elseif page.totalCount~=total or page.enabledCount~=enabled or page.transferableTokenCount~=transferable then
            error('agnes_callable_page_changed')
        end
        for _, action in ipairs(page.items) do
            if seen[action.optionId] then error('agnes_duplicate_callable_action') end
            seen[action.optionId]=true;items[#items+1]=action
        end
        local nextCursor=page.nextCursor
        if nextCursor~=-1 and (nextCursor<=cursor or nextCursor>total) then error('agnes_callable_cursor_invalid') end
        cursor=nextCursor
    until cursor==-1
    local actualEnabled=0;for _, action in ipairs(items) do if action.enabled then actualEnabled=actualEnabled+1 end end
    if #items~=total or actualEnabled~=enabled then error('agnes_callable_pages_incomplete') end
    return items,transferable,enabled
end
local function tactic_options(ctx)
    local ids,labels,refs={}, {}, {}
    local actions=callable_actions(ctx)
    for _, a in ipairs(actions) do
        if a.enabled then
            -- 续接只冻结选择所需的三个Invoke参数，不保存SourceData完整来源/分页事实或自造policy。
            ids[#ids+1]=a.optionId; refs[a.optionId]={itemId=a.itemId,actionId=a.actionId,callPolicyRef=a.callPolicyRef}
            labels[a.optionId]=text('tactic.call',{source=Text.Item(a.itemId)})
        end
    end
    return ids,labels,refs
end
function M.cleanup(ctx)
    local ids,labels,refs=tactic_options(ctx)
    if #ids==0 then return {} end -- when_available已由宿主注入前核资格；正文再核实时合法候选，无则不扣费。
    local choose=saved(choice(ctx,ids,labels,true,'cleanup.choose'),{actions=refs},'tactic_selected')
    return {present_condition(Effect.Condition({decisionPlayer=ctx.playerId,ruleCondition=true,
        leftEffects={M.payCommand(ctx,1)},rightEffects={choose}}),'cleanup.pay')}
end
function M.availability(ctx)
    local _,_,enabled=callable_actions(ctx)
    if enabled==0 then return {enabled=false,disabledReasonKey='core:entrepreneur.A.cleanup.no_tactic'} end
    if #command_inventory(ctx)<1 then return {enabled=false,disabledReasonKey='core:entrepreneur.A.cleanup.insufficient'} end
    return {enabled=true,disabledReasonKey=''}
end
function M.scoring(ctx)
    local f=ctx.payload.facts; local pass=f.pass; local me=pid(ctx.playerId); local items={}
    if ctx.payload.abilityId=='entrepreneur.A03.scoring' and pass=='influence_delta' then
        for _, c in ipairs(f.cityCounts) do if c.playerId==me and c.count>0 then
            items[1]={kind='influence_delta',regionId=f.regionId,playerId=me,delta=2}
        end end
    elseif ctx.payload.abilityId=='entrepreneur.A02.scoring' then
        if pass=='tie_claim' then
            local found=false; for _, n in ipairs(f.positiveHighest) do if n==me then found=true end end
            if found and #f.positiveHighest>1 then items[1]={kind='tie_claim',regionId=f.regionId,playerId=me,ruleId='entrepreneur.A02.tie'} end
        elseif pass=='region_value_delta' and f.controllerPlayerId==me and f.controlReason=='tie_claim' then
            -- 同名ruleId不代表本来源的主张；只读本次授权scope的原不透明key，不拿第一页当来源全集。
            local scope=SourceData.GetCurrentSources()
            local sourceKey=scope.ruleOwnerSourceKey
            if type(sourceKey)~='string' or sourceKey=='' then error('agnes_scoring_source_scope_invalid') end
            for _, c in ipairs(f.controlClaims) do if c.playerId==me and c.ruleId=='entrepreneur.A02.tie' then
                if c.sourceKey==sourceKey then
                    items[1]={kind='region_value_delta',regionId=f.regionId,delta=-2,reasonRef='claim:entrepreneur.A02.tie'}
                end
            end end
        end
    end
    return {queryId=ctx.payload.queryId,basisDigest=ctx.payload.basisDigest,items=items}
end
function M.final(ctx,rules)
    local view=Global.Scoring.GetFinalControl(); local me=pid(ctx.playerId)
    if ctx.payload.abilityId=='entrepreneur.A02.final' then
        local count=0; for _, r in ipairs(view.regions) do if r.controllerPlayerId==me then count=count+1 end end
        if count==0 then return {} end
        return {Effect.GainScore({recipient=ctx.playerId,amount=count})}
    end
    if not has_rule(ctx,rules,'entrepreneur.rule.A.physical_final') then return {} end
    local counts={}; for _, p in ipairs(GameData.GetPlayers()) do counts[pid(p.playerId)]=0 end
    for _, x in ipairs(view.physicalInfluences) do if x.subjectKind=='player' and counts[x.playerId]~=nil then counts[x.playerId]=counts[x.playerId]+1 end end
    local highest=true; for player,n in pairs(counts) do if player~=me and n>=counts[me] then highest=false end end
    return {highest and Effect.GainScore({recipient=ctx.playerId,amount=2}) or Effect.LoseScore({player=ctx.playerId,amount=1})}
end
function M.invoke(ctx,rules)
    if ctx.eventType=='EffectCompleted' then
        local c=ctx.payload.continuationContext
        if c==nil then return {} end
        if c.stage=='command_moves' then
            if ctx.payload.outcome~='completed' and ctx.payload.outcome~='failed' then return {} end
            return single_command_moves(ctx,c.card,c.supply,c.destination,c.markers)
        end
        if ctx.payload.outcome~='completed' then return {} end
        if c.stage=='command_selected' then
            local id=selected(ctx); if id==nil then return {} end
            local k=tonumber(string.match(id,'^(%d+)$'))
            if k==nil or k<1 or k>99 or k>c.n then error('agnes_quantity_choice_invalid') end
            if c.paid then return {purchase(ctx,c.card,k)} end
            local effects=transfer_commands(ctx,c.card,k)
            effects[1]=present(effects[1],'command.gain',{count=k})
            return effects
        elseif c.stage=='tactic_selected' then
            local id=selected(ctx); if id==nil then return {} end
            local a=c.actions[id]; if a==nil then error('agnes_action_choice_invalid') end
            return {Effect.InvokeAction({apiVersion=1,player=ctx.playerId,itemId=a.itemId,actionId=a.actionId,callPolicyRef=a.callPolicyRef})}
        elseif c.stage=='after_map_gain' then
            return M.gainCommand(ctx,rules,1)
        end
        return {}
    end
    local a=ctx.payload.abilityId
    if ctx.payload.queryKind=='scoringModifiers.v1' then return M.scoring(ctx) end
    if a=='entrepreneur.A09.immediate.1' or a=='entrepreneur.A08.immediate.1' or a=='entrepreneur.A04.immediate.1' then return M.gainCommand(ctx,rules,2) end
    if a=='entrepreneur.A06.immediate.1' or a=='entrepreneur.A07.immediate.1' then
        return {Effect.LoseScore({player=ctx.playerId,amount=1}),Flow.DispatchOperator({apiVersion=1,player=ctx.playerId,drawCount=3})}
    end
    if a=='entrepreneur.A07.immediate.2' then
        local result=M.gainCommand(ctx,rules,4)
        local candidates={}
        for _, display in ipairs(Global.Content.FindZones({templateId='core.zone.enterprise_display'})) do
            for _, x in ipairs(Global.Content.FindInstances({zoneId=display.id,contentType='enterprise',typeId='core.board_card'})) do
                candidates[#candidates+1]=x.id
            end
        end
        result[#result+1]=Effect.UpgradeEnterprise({apiVersion=1,player=ctx.playerId,enterpriseItemIds=candidates,levels=2})
        return result
    end
    if a=='entrepreneur.A02.immediate.1' then return {Effect.GainScore({recipient=ctx.playerId,amount=1})} end
    if a=='entrepreneur.A03.immediate.1' then return {gain('pure_originium',4,ctx.playerId)} end
    if a=='entrepreneur.A01.cover' then return M.cover(ctx,rules) end
    if a=='entrepreneur.A01.final' or a=='entrepreneur.A02.final' then return M.final(ctx,rules) end
    if a=='entrepreneur.A06.departure' then
        if ctx.payload.ownerPlayerId~=pid(ctx.playerId) then return {} end
        local n=ctx.payload.departureCount
        -- 保留现有逐枚资格预演：其返回式由HasEventResponseBody构造后丢弃，不是正式数量请求。
        -- 完整批次正文仍用departureCount；预演/旁路激活相对去向响应的生成时点缺口另交根归一。
        if n==nil and ctx.payload.eventProtocolVersion==2 and type(ctx.payload.departureId)=='string' and ctx.payload.departureId~='' then n=1 end
        return M.gainCommand(ctx,rules,number(n,'departureCount'))
    end
    if a=='entrepreneur.A04.city_move' then
        if ctx.payload.triggeringPlayerId~=pid(ctx.playerId) then return {} end
        return {gain('originium_shard',2,ctx.playerId),move(ctx,true)}
    end
    if a=='entrepreneur.A05.cleanup' then return M.cleanup(ctx) end
    if a=='entrepreneur.A09.tactic' then return {place(ctx),gain('gold_voucher',5,ctx.playerId)} end
    if a=='entrepreneur.A08.tactic' then return M.a08Tactic(ctx) end
    if a=='entrepreneur.A06.tactic' then
        return {alternatives(ctx,'place'),Effect.WithPresentation(Effect.Choice({player=ctx.playerId,interactionType='effect.choice.execute',branches={
            {id='originium',effects={gain('originium',1,ctx.playerId)}},
            {id='originium_shard',effects={gain('originium_shard',1,ctx.playerId)}},
            {id='iron',effects={gain('iron',1,ctx.playerId)}}}}),{interaction={prompt=text('resource.choose'),options={
                {candidateId='originium',label=text('resource.originium')},
                {candidateId='originium_shard',label=text('resource.originium_shard')},
                {candidateId='iron',label=text('resource.iron')}}}})}
    end
    if a=='entrepreneur.A07.tactic' then
        return {present_condition(Effect.Condition({decisionPlayer=ctx.playerId,ruleCondition=true,
            leftEffects={Effect.PayResource({player=ctx.playerId,cost={OriginiumShard=2}})},rightEffects={remove(ctx),place(ctx)}}),'tactic.cooperation')}
    end
    if a=='entrepreneur.A05.tactic' then
        return {saved(alternatives(ctx,'move'),{},'after_map_gain')}
    end
    if a=='entrepreneur.A03.special' then
        local function free_move() return Effect.MoveCity({decisionPlayer=ctx.playerId,costPolicy='waived',consumeMainAction=false}) end
        return {present_condition(Effect.Condition({decisionPlayer=ctx.playerId,ruleCondition=true,
            leftEffects={Effect.PayResource({player=ctx.playerId,cost={PureOriginium=1}})},rightEffects={free_move(),free_move()}}),'special.city')}
    end
    error('agnes_handler_not_bound:'..tostring(a))
end
function M.quickEntryCondition(ctx,tactic)
    local abilityId=ctx.payload.abilityId
    if ctx.eventType~='AbilityInvoked' or type(abilityId)~='string' or ctx.payload.actionId~=abilityId..'.quick' or type(tactic)~='string' or tactic=='' then error('agnes_quick_condition_entry_invalid') end
    -- 仅确认及费用门；正文仍由原BodyHandlerRef在真实激活后构造，成本入口无Lua续接。
    -- tactic由薄入口冻结为回合面·动作名；text参数为字符串，不嵌套Text.Ref。
    local prompt=Text.Ref('core:entrepreneur.A.quick.condition',{tactic=tactic})
    return {Effect.WithPresentation(Effect.Condition({decisionPlayer=ctx.playerId,ruleCondition=true,
        leftEffects=M.quickCost(ctx),rightEffects={}}),
        {started=prompt,interaction={prompt=prompt}})}
end
-- 仅由quickEntryCondition作为单笔左式调用；不得再作为第二笔快速费用叠加。
function M.quickCost(ctx) return {M.payCommand(ctx,2)} end
function M.specialAfter(ctx) return {gain('gold_voucher',10,ctx.playerId)} end
return M
