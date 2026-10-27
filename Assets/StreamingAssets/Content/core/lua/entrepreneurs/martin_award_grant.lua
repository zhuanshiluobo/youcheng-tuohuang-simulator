-- M09/M10选面生效时调用；不发初始奖励、不写付款替代、不进入随机池。
return function(ctx)
  local p = ctx.payload or {}
  if p.activeFaceId ~= 'M09' and p.activeFaceId ~= 'M10' then
    error('martin_award_requires_selected_face')
  end
  if not p.authorizationRef or p.authorizationRef == '' then
    error('martin_award_authorization_missing')
  end
  local result = {}
  for _,player in ipairs(GameData.GetPlayers()) do
    if player.playerId ~= ctx.playerId then
      result[#result+1] = Effect.WithPresentation(Effect.GrantReward({apiVersion=1,
        player=ctx.playerId,definitionId='entrepreneur.award.M01',typeId='core.award_card',
        count=1,recipientPlayer=player.playerId,destination='core.zone.player_rewards',
        operationId='martin-concession:'..p.authorizationRef..':'..player.playerId}),
        {succeeded=Text.Ref('core:entrepreneur.M.award.granted',{who=Text.Player(player.playerId)})})
    end
  end
  return result
end
