return function(ctx)
    local origin, slots = ctx.payload.targetLocationId, {}
    local map = Global.Map.GetMap()
    for _, route in ipairs(map.routes) do
        if not route.hasRoad then
            local adjacent = false
            for _, location in ipairs(route.coveredLocationIds) do if location == origin then adjacent = true end end
            if adjacent then for _, slot in ipairs(route.influenceSlotIds) do table.insert(slots, slot) end end
        end
    end
    table.sort(slots)
    return { Effect.Choice({ player = ctx.playerId, options = slots, minSelections = 1, maxSelections = 1,
        interactionType = 'effect.influence.place.target', resultContext = { origin = origin }, completionHandlerId = 'event_yellow_05.choice_01.completed' }) }
end
