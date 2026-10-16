"""生成独立角色定义；不修改正式清单、切片或界面资产。"""
import json
from pathlib import Path
import uuid

ROOT = Path(__file__).resolve().parents[2]
CONTENT = ROOT / 'Assets/StreamingAssets/Content/core'
cards = [
 ('amiya','阿米娅','I26','调用凭证','合作协议','normal','persistent'),
 ('muelsyse','缪尔赛思','I03','生机勃发','流形技艺','normal','persistent'),
 ('jessica_the_liberated','涤火杰西卡','I27','仗义之举','不义之财','normal','persistent'),
 ('swire','诗怀雅','I28','分散投资','和气生财','normal','persistent'),
 ('lin','林','I29','特殊渠道','劫富济贫','normal','persistent'),
 ('silence_the_paradigmatic','淬羽赫默','I30','技术顾问','锁定协议','normal','normal'),
 ('hellagur','赫拉格','I31','诡道兵法','运筹帷幄','normal','normal'),
 ('gavial_the_invincible','百炼嘉维尔','I32','丛林之魂','撼地巨斧','normal','persistent'),
 ('ceobe','刻俄柏','I33','可靠伙伴','我都想要','normal','normal'),
 ('flowing_shape_alpha','流形甲','I11','分流','汇聚','normal','one_shot'),
 ('flowing_shape_beta','流形乙','I13','洞悉','沟通','normal','one_shot'),
 ('flowing_shape_gamma','流形丙','I14','滋养','交流','normal','one_shot')]

def write(path, text):
    path = CONTENT / path
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text.rstrip()+'\n', encoding='utf-8')
    meta = Path(str(path)+'.meta')
    if not meta.exists(): meta.write_text('fileFormatVersion: 2\nguid: '+uuid.uuid4().hex+'\nTextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n', encoding='utf-8')

place = "Effect.PlaceInfluence({executingPlayer=ctx.playerId, ownerSubject=ctx.playerId, influenceSource='player_supply'})"
move = "Effect.SelectInfluence({player=ctx.playerId, operation='move', subjectFilter='self', count=1})"
remove = "Effect.SelectInfluence({player=ctx.playerId, operation='remove', subjectFilter='any', count=1})"
gain = lambda resource,amount: "Effect.GainResource({player=ctx.playerId,resourceType='%s',amount=%s})"%(resource,amount)
pay = lambda resource,amount: "Effect.PayResource({player=ctx.playerId,resourceType='%s',amount=%s})"%(resource,amount)
repeat = lambda n,body,stop=False: "Effect.Repeat({apiVersion=1,count=%d,allowEarlyStop=%s,effects={%s}})"%(n,str(stop).lower(),body)
flow = lambda name,args='': "Effect.%s({apiVersion=1,player=ctx.playerId%s})"%(name,(','+args) if args else '')
condition = lambda cost,reward: 'Effect.Condition({leftEffects={'+cost+'},rightEffects={'+reward+'}})'
scripts = {}
def script(card, mode, body): scripts[card,mode] = 'return function(ctx)\n'+body+'\nend'
def simple(card,mode,body): script(card,mode,'    return {'+body+'}')

simple('amiya','strategy',"Flow.DispatchOperator({apiVersion=1,player=ctx.playerId,drawCount=3,excludedReturnItemIds={ctx.payload.cardInstanceRef}})")
simple('amiya','tactic',flow('RetainCharacter'))
script('muelsyse','strategy','    return {'+place+','+move+'}')
script('muelsyse','tactic',"""    if ctx.eventType == 'CharacterCardCovered' or ctx.eventType == 'CharacterUseFinished' then
        return {Effect.ChooseAdditionalCover({apiVersion=1,player=ctx.playerId,acceptedTypeId='core.generated_character_card',reusable=true,blockCurrentRound=false})}
    elseif ctx.eventType == 'CollectionStarted' then
        return {Effect.RecycleGeneratedCover({apiVersion=1,player=ctx.playerId})}
    end
    return {Effect.AcquireGeneratedCharacters({apiVersion=1,player=ctx.playerId,poolRole='flowing_shapes'}),Effect.ChooseAdditionalCover({apiVersion=1,player=ctx.playerId,acceptedTypeId='core.generated_character_card',reusable=true,blockCurrentRound=false})} """)
simple('jessica_the_liberated','strategy',flow('BorrowEnterpriseSpecial'))
simple('jessica_the_liberated','tactic',condition("Effect.LoseScore({player=ctx.playerId,amount=2})",flow('BuildAndBindCharacter')))
simple('swire','strategy',flow('ActivateDistinctSpecials','count=2,minLevel=1,maxLevel=2'))
script('swire','tactic',"""    if ctx.eventType == 'PlayerCleanupStarted' then
        return {Effect.ConsumeMarkerAndUpgrade({apiVersion=1,player=ctx.playerId,localZoneId='markers',goldReward=6})}
    end
    return {Effect.TakePlayerMarkers({apiVersion=1,player=ctx.playerId,localZoneId='markers',maxCount=2,distinctOwners=true})}""")
simple('lin','strategy',flow('TradeResources',"count=10,buyDiscount=2,prices={originium=3,originium_shard=4,iron=5,pure_originium=15}"))
script('lin','tactic',"""    if ctx.eventType == 'PlayerCleanupStarted' then
        return {Effect.ConsumeMarkerAndTransfer({apiVersion=1,player=ctx.playerId,localZoneId='markers',minimumGold=20,amount=10})}
    end
    return {Effect.TakePlayerMarkers({apiVersion=1,player=ctx.playerId,localZoneId='markers',maxCount=2,distinctOwners=false})}""")
simple('silence_the_paradigmatic','strategy',flow('ChoosePublicStyleSpecial','tier=1'))
simple('silence_the_paradigmatic','tactic',"Effect.GainScore({player=ctx.playerId,amount=1}),"+flow('CreateCityInfluenceLock',"definitionId='infected.component.influence_lock_token'"))
script('hellagur','strategy',"""    if ctx.eventType == 'PlayerCleanupStarted' then
        return {Effect.SelectInfluence({player=ctx.playerId,operation='remove',subjectFilter='self',count=2})}
    end
    return {"""+repeat(3,"Effect.Choice({player=ctx.playerId,branches={{id='place',effects={"+place+"}},{id='remove',effects={"+remove+"}}}})")+'}')
simple('hellagur','tactic',flow('ChooseAdditionalCover',"acceptedTypeId='core.character_card',reusable=false,blockCurrentRound=true,discardFirst=true"))
script('gavial_the_invincible','strategy',"""    if ctx.eventType == 'EffectCompleted' then
        if ctx.payload.effectTypeId == 'effect.city.move' and ctx.payload.outcome == 'completed' and (ctx.payload.normalizedResult.removedOpponentCount or 0) > 0 then
            return {"""+place+"""}
        end
        return {}
    end
    return {Effect.MoveCity({player=ctx.playerId,targetPolicy='explored',waiveBaseCost=true,clearInfluencePolicy='remove',consumeMainAction=false})}""")
script('gavial_the_invincible','tactic',"""    if ctx.eventType == 'PlayerCleanupStarted' then
        return {Effect.MoveTokenAndInfluence({apiVersion=1,player=ctx.playerId,definitionId='infected.component.gavial_token'})}
    end
    return {Effect.CreatePersistentMapToken({apiVersion=1,player=ctx.playerId,definitionId='infected.component.gavial_token'})}""")
actions="'deploy','dispatch','explore','move_city','build'"
simple('ceobe','strategy',condition(pay('originium_shard',1),"Effect.ExecuteMainAction({player=ctx.playerId,executionMode='immediate',allowedActionTypes={"+actions+'}})'))
script('ceobe','tactic',"""    if ctx.eventType ~= 'EffectCompleted' then
        return {Effect.RollDice({apiVersion=1,player=ctx.playerId,count=1,sides=6})}
    end
    if ctx.payload.effectTypeId ~= 'effect.random.roll_dice' or ctx.payload.outcome ~= 'completed' then return {} end
    local result = ctx.payload.normalizedResult.diceResult
    if result <= 2 then return {"""+remove+','+repeat(2,place)+"""} end
    if result <= 4 then return {"""+gain('pure_originium',2)+"""} end
    return {Effect.ExecuteMainAction({player=ctx.playerId,executionMode='immediate',allowedActionTypes={"""+actions+",'special'}}),"+flow('TransferCurrentCharacter')+'}')
simple('flowing_shape_alpha','strategy',repeat(2,move))
simple('flowing_shape_alpha','tactic',place)
script('flowing_shape_beta','strategy',"""    local deck
    for _,zone in ipairs(Global.Content.FindZones()) do if zone.localZoneId == 'facility_deck' then deck=zone.id end end
    if not deck then error('facility_deck_missing') end
    return {Flow.TakeSupplyFacilities({apiVersion=1,player=ctx.playerId,maxCount=2,distinctBy='name',destination={zoneId=deck}})}""")
simple('flowing_shape_beta','tactic',"Effect.ActivateEnterpriseSpecial({apiVersion=1,player=ctx.playerId,unlockedByPlayer=ctx.playerId,minLevel=1,maxLevel=2})")
simple('flowing_shape_gamma','strategy',repeat(2,'Effect.Choice({player=ctx.playerId,branches={'+','.join("{id='%s',effects={%s}}"%(r,gain(r,n)) for r,n in [('originium',1),('originium_shard',1),('iron',1),('gold_voucher',4)])+'}})'))
script('flowing_shape_gamma','tactic',"""    local branches={}
    for _,item in ipairs(Global.Content.FindInstances()) do
        if item.contentType == 'enterprise' then branches[#branches+1]={id=item.id,effects={Effect.UpgradeEnterprise({apiVersion=1,player=ctx.playerId,enterpriseItemId=item.id,levels=1})}} end
    end
    if #branches == 0 then return {} end
    return {Effect.Choice({player=ctx.playerId,promptKey='enterprise.choose_upgrade',branches=branches})}""")

for key,name,source,strategy,tactic,st,tt in cards:
    id='infected.character.'+key
    role='initial' if key=='amiya' else 'generated' if key.startswith('flowing_shape') else 'operator_deck'
    typ='core.basic_character_card' if role=='initial' else 'core.generated_character_card' if role=='generated' else 'core.operator_card'
    abilities=[]
    for mode,skill in [('strategy',st),('tactic',tt)]:
        path='lua/characters/'+id+'.'+mode+'.lua'
        write(path,scripts[key,mode])
        ability=dict(abilityId=id+'.'+mode,mode=mode,skillType=skill,subscriptionId='content.'+id+'.'+mode,script=path,version='1.0.0',completionHandlerId='',cleanupScript='',hasSpecialZone=False,specialZoneId='')
        if (key,mode) in [('ceobe','tactic'),('gavial_the_invincible','strategy')]: ability['completionHandlerId']='content.'+id+'.'+mode+'.continue'
        if (key,mode) in [('muelsyse','strategy'),('hellagur','strategy'),('swire','tactic'),('lin','tactic'),('gavial_the_invincible','tactic')]: ability['cleanupScript']=path
        if key=='muelsyse' and mode=='tactic': ability.update(observedEvents=['CharacterCardCovered','CharacterUseFinished','CollectionStarted'],observerSubjectPolicy='self')
        if key in ['swire','lin'] and mode=='tactic': ability.update(hasSpecialZone=True,specialZoneId='markers')
        if key=='amiya' and mode=='tactic': ability.update(settlementActionId='character.grant_current_double_use',titleKey='合作协议',descriptionKey='弃置阿米娅，按所选顺序使用当前角色的策略与计谋。')
        if key=='jessica_the_liberated' and mode=='tactic':
            ability.update(cleanupScript='lua/characters/'+id+'.cleanup.lua',settlementActionId='character.finish_facility_binding',titleKey='不义之财',descriptionKey='支付绑定设施卡面金券费用加2，获得2分并弃置角色。')
            write(ability['cleanupScript'],'return function(ctx)\n    return {'+flow('OpenFacilityBindingSettlement')+'}\nend')
        abilities.append(ability)
    zones=[]
    if key in ['swire','lin']:
        zones=[dict(localZoneId='markers',zoneTemplateId='core.zone.card_cube_storage',acceptedItemKinds=['Cube'],acceptedTypeIds=['core.player_marker'],entryPolicyId='core.explicit_transfer',exitPolicyId='core.card_payment',capacityPolicy='unbounded',orderingKind='Unordered',visibilityPolicyId='core.public_items',carrierExitPolicyId='core.return_contents_before_exit',displayAnchorId='markers',emptyExitPolicyId='core.discard_carrier_when_empty',paymentPolicyId='',maxMarkersPerPayment=0)]
    definition=dict(definitionId=id,contentType='character',version='1.0.0',displayName=name,expansionId='infected',replaces=None,artwork='artwork/characters/'+id+'.jpg',artworkSpriteId='',tags=[],playerMarkerZones=[],itemTypeId=typ,cardZones=zones,abilities=abilities,data=dict(templateId=id,cardId=id,name=name,acquisitionRole=role,instanceTypeId=typ,strategyAbilityId=id+'.strategy',tacticAbilityId=id+'.tactic',strategyName=strategy,tacticName=tactic,sourceReference=source))
    write('character/'+id+'.json',json.dumps(definition,ensure_ascii=False,indent=2))
for key,name in [('gavial_token','嘉维尔指示物'),('influence_lock_token','影响力锁定指示物')]:
    id='infected.component.'+key
    write('component/'+id+'.json',json.dumps(dict(definitionId=id,contentType='component',version='1.0.0',displayName=name,expansionId='infected',replaces=None,artwork='artwork/components/'+id+'.png',artworkSpriteId='',tags=[],playerMarkerZones=[],itemTypeId='infected.'+key,cardZones=[],abilities=[],data=dict(instanceTypeId='infected.'+key,quantityPolicy='finite')),ensure_ascii=False,indent=2))
