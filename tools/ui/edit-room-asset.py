"""显式编辑 StartMenuRoot 的房间子树；不由测试/构建调用，不覆盖其他界面。"""
import hashlib
import json
import re
import uuid
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PREFAB = ROOT / 'Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab'
source = PREFAB.read_text(encoding='utf-8')
if 'm_Name: RoomSettingsPanel' in source:
    raise SystemExit('房间子树已编辑，禁止再次覆盖；请按当前资产做增量修改。')
blocks = {int(m[2]): m[0] for m in re.finditer(r'^--- !u!(\d+) &(\d+)\n.*?(?=^--- !u!|\Z)', source, re.M | re.S)}
original_ids = list(blocks)
go_template = next(v for v in blocks.values() if v.startswith('--- !u!1 '))
rt_template = next(v for v in blocks.values() if v.startswith('--- !u!224 '))
renderer_template = next(v for v in blocks.values() if v.startswith('--- !u!222 '))
def script_block(guid):
    return next(v for v in blocks.values() if f'guid: {guid}' in v)
image_template = script_block('fe87c0e1cc204ed48ad3b37840f39efc')
button_template = script_block('4e29b1a8efbd4b44bb3f3716e73f07ff')
text_template = script_block('5f7201a12d95ffc409449d95f23cf332')
metadata_template = (ROOT / 'Assets/YC/Presentation/Maps/Highlights/resource-selected@4x.png.meta').read_text()
def field(block, key, value):
    return re.sub(r'^  '+re.escape(key)+r':.*$', lambda m:'  '+key+': '+str(value), block, flags=re.M)
def identity(template, ident, go=None):
    result = re.sub(r'^(--- !u!\d+ &)\d+', lambda m:m[1]+str(ident), template, count=1)
    if go is not None: result = field(result, 'm_GameObject', '{fileID: '+str(go)+'}')
    return result
def ref(ident): return '{fileID: '+str(ident)+'}'
def sid(name):
    ident = int.from_bytes(hashlib.sha256(('room.'+name).encode()).digest()[:8], 'little') & 0x7fffffffffffffff
    assert ident not in original_ids, name
    return ident
def component(go, ident):
    blocks[go] = re.sub(r'(  m_Component:\n)', r'\1  - component: '+ref(ident)+'\n', blocks[go], count=1)
parents = {k:int(re.search(r'm_Father: \{fileID: (\d+)\}',v)[1]) for k,v in blocks.items() if 'm_Father:' in v}
rtgo = {k:int(re.search(r'm_GameObject: \{fileID: (\d+)\}',blocks[k])[1]) for k in parents}
touched = set()
def setrect(rt, parent, rect, anchors=(0,1,0,1), pivot=(0,1)):
    x,y,w,h=rect
    b=blocks[rt]
    for key,value in [('m_Father',ref(parent)),('m_AnchorMin',f'{{x: {anchors[0]}, y: {anchors[1]}}}'),('m_AnchorMax',f'{{x: {anchors[2]}, y: {anchors[3]}}}'),('m_Pivot',f'{{x: {pivot[0]}, y: {pivot[1]}}}'),('m_AnchoredPosition',f'{{x: {x}, y: {-y}}}'),('m_SizeDelta',f'{{x: {w}, y: {h}}}')]: b=field(b,key,value)
    touched.add(parents.get(rt,0));touched.add(parent);parents[rt]=parent;blocks[rt]=b
def node(name,parent,rect,active=True,anchors=(0,1,0,1),pivot=(0,1)):
    go=sid(name+'.object');rt=sid(name+'.rect')
    b=identity(go_template,go)
    b=field(b,'m_Name',name);b=field(b,'m_IsActive',int(active));b=field(b,'m_Layer',5)
    b=re.sub(r'  m_Component:\n.*?(?=  m_Layer:)', '  m_Component:\n  - component: '+ref(rt)+'\n',b,flags=re.S)
    blocks[go]=b;blocks[rt]=identity(rt_template,rt,go);rtgo[rt]=go
    blocks[rt]=re.sub(r'  m_Children:.*?(?=  m_Father:)', '  m_Children: []\n',blocks[rt],flags=re.S)
    setrect(rt,parent,rect,anchors,pivot)
    return go,rt
def mono(go,name,guid,data):
    ident=sid(name+'.component')
    blocks[ident]=f'''--- !u!114 &{ident}
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {ref(go)}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {guid}, type: 3}}
  m_Name:
  m_EditorClassIdentifier:
{data}
''';component(go,ident);return ident
z=zipfile.ZipFile(ROOT/'游城拓荒/UI素材/Room-UI-v1.1-VerticalMaps.zip');base='Room-UI-v1.1-VerticalMaps/'
assets=json.loads(z.read(base+'assets.json'));layout=json.loads(z.read(base+'layout.json'))
assert assets['version']==layout['version']=='1.1' and assets['sliceOrder']==['left','top','right','bottom']
assets=assets['assets']
checksum_count=0
for line in z.read(base+'SHA256SUMS.txt').decode().splitlines():
    checksum,path=line.split(None,1);path=path.strip()
    assert hashlib.sha256(z.read(base+path)).hexdigest()==checksum, path
    checksum_count+=1
sprite_dir=ROOT/'Assets/YC/Presentation/StartMenu/Sprites/Room';sprite_dir.mkdir(parents=True,exist_ok=True)
for folder in [sprite_dir.parent.parent,sprite_dir.parent,sprite_dir]:
    meta=Path(str(folder)+'.meta')
    if not meta.exists():meta.write_text(f'fileFormatVersion: 2\nguid: {uuid.uuid4().hex}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n')
needed=['screen-fill','screen-frame','topbar-fill','topbar-frame','paper-fill','module-frame','section-fill','seat-header-fill','avatar-fill','avatar-frame','footer-fill','footer-frame','map-frame-default','map-frame-hover','map-frame-selected','map-frame-disabled','map-label-fill','map-disabled-overlay','checkbox-unchecked','scroll-track','scroll-thumb-default','scroll-thumb-hover','scroll-thumb-drag','reading-fill','dim-overlay']
needed += [f'button-{skin}-{state}' for skin in ['neutral','accent','dark'] for state in ['default','hover','pressed','disabled']]
needed += [f'seat-{kind}-{color}' for color in ['green','yellow','blue','red'] for kind in ['bar','square']]
needed += ['icon-'+name for name in ['empty-seat-dark','crown-dark','check-dark','lock-dark','copy-dark','book-dark','swap-dark','invite-dark','start-dark','back-light','close-dark','remove-danger','bot-muted','hotseat-muted']]
sizes={len(z.read(base+assets[key]['path'])) for key in needed}
existing={}
for path in (ROOT/'Assets').rglob('*.png'):
    if path.stat().st_size in sizes:
        existing.setdefault(hashlib.sha256(path.read_bytes()).hexdigest(),[]).append(path)
mapping={};sprites={}
for key in needed:
    asset=assets[key];data=z.read(base+asset['path']);sha=hashlib.sha256(data).hexdigest()
    l,t,r,b=asset['sliceLTRB'];border=f'{{x: {l}, y: {b}, z: {r}, w: {t}}}'
    reuse=next((p for p in existing.get(sha,[]) if Path(str(p)+'.meta').exists() and f'spriteBorder: {border}' in Path(str(p)+'.meta').read_text() and 'spriteMode: 1' in Path(str(p)+'.meta').read_text()),None)
    target=reuse or sprite_dir/(key+'.png')
    if not reuse:
        target.write_bytes(data)
        meta=metadata_template
        meta=re.sub(r'^guid:.*$',f'guid: {uuid.uuid4().hex}',meta,flags=re.M)
        meta=re.sub(r'spritePixelsToUnits:.*','spritePixelsToUnits: 100',meta)
        meta=re.sub(r'spriteBorder:.*','spriteBorder: '+border,meta)
        meta=re.sub(r'spriteID:.*','spriteID: '+uuid.uuid4().hex,meta)
        meta=re.sub(r'maxTextureSize:.*','maxTextureSize: 2048',meta)
        meta=re.sub(r'textureCompression:.*','textureCompression: 0',meta)
        meta=re.sub(r'enableMipMap:.*','enableMipMap: 0',meta)
        Path(str(target)+'.meta').write_text(meta)
    guid=re.search(r'^guid: (\w+)',Path(str(target)+'.meta').read_text(),re.M)[1]
    sprites[key]=f'{{fileID: 21300000, guid: {guid}, type: 3}}'
    mapping[key]={'source':asset['path'],'target':target.relative_to(ROOT).as_posix(),'sha256':sha,'sliceLTRB':asset['sliceLTRB'],'unityBorderLBRT':[l,b,r,t],'ppu':100,'density':1,'reused':bool(reuse)}
def image(go,name,key=None,ray=False,preserve=False):
    ident=sid(name+'.image');renderer=sid(name+'.renderer')
    blocks[renderer]=identity(renderer_template,renderer,go);component(go,renderer)
    b=identity(image_template,ident,go)
    for k,v in [('m_Sprite',sprites.get(key,key) or ref(0)),('m_Type',int(key in mapping and any(mapping[key]['sliceLTRB']))),('m_PreserveAspect',int(preserve)),('m_RaycastTarget',int(ray)),('m_Color','{r: 1, g: 1, b: 1, a: 1}')]:b=field(b,k,v)
    blocks[ident]=b;component(go,ident);return ident
def picture(name,parent,rect,key,ray=False,preserve=False,active=True):
    go,rt=node(name,parent,rect,active);return go,rt,image(go,name,key,ray,preserve)
def text(name,parent,rect,caption,size=24,color='{r: 0.141, g: 0.169, b: 0.173, a: 1}',align=3):
    go,rt=node(name,parent,rect);ident=sid(name+'.text');renderer=sid(name+'.renderer')
    blocks[renderer]=identity(renderer_template,renderer,go);component(go,renderer)
    b=identity(text_template,ident,go)
    for k,v in [('m_Text',json.dumps(caption,ensure_ascii=True)),('m_Color',color),('m_RaycastTarget',0)]:b=field(b,k,v)
    for k,v in [('m_FontSize',size),('m_BestFit',0),('m_Alignment',align),('m_HorizontalOverflow',0),('m_VerticalOverflow',0)]:b=re.sub(r'^(    '+k+r':).*$',r'\g<1> '+str(v),b,flags=re.M)
    blocks[ident]=b;component(go,ident);return go,rt,ident
def button(name,parent,rect,caption,skin='neutral',icon=None,size=23):
    go,rt,graphic=picture(name,parent,rect,'button-'+skin+'-default',True)
    ident=sid(name+'.button');b=identity(button_template,ident,go)
    b=field(b,'m_TargetGraphic',ref(graphic));b=field(b,'m_Transition',2)
    for k,st in [('m_HighlightedSprite','hover'),('m_PressedSprite','pressed'),('m_SelectedSprite','hover'),('m_DisabledSprite','disabled')]:
        b=re.sub(r'^(    '+k+r':).*$',r'\g<1> '+sprites['button-'+skin+'-'+st],b,flags=re.M)
    blocks[ident]=b;component(go,ident)
    if icon:picture(name+'Icon',rt,(12,(rect[3]-28)/2,28,28),'icon-'+icon,preserve=True)
    label=text(name+'Caption',rt,(46 if icon else 8,4,rect[2]-(54 if icon else 16),rect[3]-8),caption,size,
               '{r: 0.94, g: 0.91, b: 0.85, a: 1}' if skin=='dark' else '{r: 0.141, g: 0.169, b: 0.173, a: 1}',4)[2]
    return go,rt,ident,label
def fillframe(name,parent,rect,fill='paper-fill',frame='module-frame'):
    go,rt,im=picture(name,parent,rect,fill)
    picture(name+'Frame',rt,(0,0,rect[2],rect[3]),frame)
    return go,rt
def section(name,parent,rect,caption):
    go,rt,_=picture(name,parent,rect,'section-fill');text(name+'Title',rt,(16,0,rect[2]-32,rect[3]),caption,28)
def scroll(name,parent,rect,width,height):
    go,rt=node(name,parent,rect);vgo,vrt=node(name+'Viewport',rt,(0,0,rect[2]-20,rect[3]))
    image(vgo,name+'Viewport',None,True)
    mono(vgo,name+'Mask','31a19414c41e5ae4aae2af33fee712f6','  m_ShowMaskGraphic: 0')
    cgo,crt=node(name+'Content',vrt,(0,0,width,height))
    bgo,brt,track=picture(name+'Scrollbar',rt,(rect[2]-16,0,12,rect[3]),'scroll-track',True)
    hgo,hrt,thumb=picture(name+'Thumb',brt,(0,0,12,90),'scroll-thumb-default',True)
    scrollbar=mono(bgo,name+'Scrollbar','2a4db7a114972834c8e4117be1d82ba3',f'''  m_Navigation:
    m_Mode: 0
  m_Transition: 2
  m_Colors:
    m_NormalColor: {{r: 1, g: 1, b: 1, a: 1}}
    m_HighlightedColor: {{r: 1, g: 1, b: 1, a: 1}}
    m_PressedColor: {{r: 1, g: 1, b: 1, a: 1}}
    m_SelectedColor: {{r: 1, g: 1, b: 1, a: 1}}
    m_DisabledColor: {{r: 1, g: 1, b: 1, a: 0.5}}
    m_ColorMultiplier: 1
    m_FadeDuration: 0.1
  m_SpriteState:
    m_HighlightedSprite: {sprites['scroll-thumb-hover']}
    m_PressedSprite: {sprites['scroll-thumb-drag']}
    m_SelectedSprite: {sprites['scroll-thumb-hover']}
    m_DisabledSprite: {{fileID: 0}}
  m_Interactable: 1
  m_TargetGraphic: {ref(thumb)}
  m_HandleRect: {ref(hrt)}
  m_Direction: 2
  m_Value: 1
  m_Size: 0.5
  m_NumberOfSteps: 0
  m_OnValueChanged:
    m_PersistentCalls:
      m_Calls: []''')
    mono(go,name+'Scroll','1aa08ab6e0800fa44ae55d278d1423e3',f'''  m_Content: {ref(crt)}
  m_Horizontal: 0
  m_Vertical: 1
  m_MovementType: 2
  m_Elasticity: 0.1
  m_Inertia: 1
  m_DecelerationRate: 0.135
  m_ScrollSensitivity: 45
  m_Viewport: {ref(vrt)}
  m_HorizontalScrollbar: {{fileID: 0}}
  m_VerticalScrollbar: {ref(scrollbar)}
  m_HorizontalScrollbarVisibility: 1
  m_VerticalScrollbarVisibility: 1
  m_HorizontalScrollbarSpacing: 0
  m_VerticalScrollbarSpacing: 0
  m_OnValueChanged:
    m_PersistentCalls:
      m_Calls: []''')
    return go,rt,crt
roomgo=8090827862394571888;roomrt=2449212558186486926
setrect(roomrt,parents[roomrt],(0,0,0,0),(0,0,1,1),(.5,.5))
rootimage=next(k for k,v in blocks.items() if 'guid: fe87c0e1cc204ed48ad3b37840f39efc' in v and f'm_GameObject: {ref(roomgo)}' in v)
blocks[rootimage]=field(field(blocks[rootimage],'m_Sprite',sprites['screen-fill']),'m_Color','{r: 1, g: 1, b: 1, a: 1}')
# 保留旧组件/稳定引用；隐藏旧模板与 Actions，不在运行时生成座位。
blocks[6567326959179234631]=field(blocks[6567326959179234631],'m_IsActive',0)
blocks[8707371715298905206]=field(blocks[8707371715298905206],'m_IsActive',0)
topgo,toprt=fillframe('RoomTopbar',roomrt,(0,0,1920,72),'topbar-fill','topbar-frame')
setrect(toprt,roomrt,(0,0,0,72),(0,1,1,1),(0,1))
text('RoomTitle',toprt,(28,8,450,56),'游城拓荒 · 等待房间',34)
password=button('RoomPassword',toprt,(1276,9,216,54),'密码服务不可用',icon='lock-dark',size=19)[2]
helpbtn=button('RoomHelp',toprt,layout['helpButton'],'',icon=None)[2]
# 原复制/房号组件保留；用锚点保证窄窗顶部操作可达。
setrect(2185183748961297849,toprt,(-420,16,220,42),(1,1,1,1),(0,1))
setrect(3224620780032218012,toprt,(-178,9,68,54),(1,1,1,1),(0,1))
for k,v in list(blocks.items()):
    if f'm_GameObject: {ref(6284776005644932875)}' in v and 'guid: fe87c0e1cc204ed48ad3b37840f39efc' in v:
        blocks[k]=field(field(field(v,'m_Sprite',sprites['button-neutral-default']),'m_Type',1),'m_Color','{r: 1, g: 1, b: 1, a: 1}')
setrect(410067826245328874,3224620780032218012,(0,0,0,0),(0,0,1,1),(.5,.5))
for k,v in list(blocks.items()):
    if f'm_GameObject: {ref(5010782611137514305)}' in v and 'm_Text:' in v:blocks[k]=field(v,'m_Text','""')
picture('RoomCopyIcon',3224620780032218012,(20,13,28,28),'icon-copy-dark',preserve=True)
help_rt=int(re.search(r'm_GameObject: \{fileID: (\d+)',blocks[helpbtn])[1]);help_rt=next(k for k,g in rtgo.items() if g==help_rt)
setrect(help_rt,toprt,(-90,9,68,54),(1,1,1,1),(0,1));picture('RoomHelpIcon',help_rt,(20,13,28,28),'icon-book-dark',preserve=True)
password_rt=next(k for k,g in rtgo.items() if ref(g) in blocks[password]);setrect(password_rt,toprt,(-664,9,216,54),(1,1,1,1),(0,1))
# 主内容区在小窗可横纵拖动；顶栏/底栏不随滚动移动。
bodygo,bodyrt,bodycontent=scroll('RoomBody',roomrt,(16,84,1888,912),1888,912)
setrect(bodyrt,roomrt,(0,0,-32,-168),(0,0,1,1),(.5,.5))
bodyviewport=next(k for k,g in rtgo.items() if field(blocks[g],'m_Name','').startswith('never')) if False else sid('RoomBodyViewport.rect')
setrect(bodyviewport,bodyrt,(0,0,-20,0),(0,0,1,1),(.5,.5))
setrect(sid('RoomBodyScrollbar.rect'),bodyrt,(-16,0,12,0),(1,0,1,1),(0,1))
blocks[sid('RoomBodyScroll.component')]=field(blocks[sid('RoomBodyScroll.component')],'m_Horizontal',1)
membergo,memberrt=fillframe('RoomMemberList',bodycontent,(0,0,1004,912))
section('RoomMemberHeader',memberrt,(12,10,980,50),'房间成员')
setrect(9178542847706922814,memberrt,(12,70,980,832))
settingsgo,settingsrt=fillframe('RoomSettingsPanel',bodycontent,(1016,0,872,912))
section('RoomSettingsHeader',settingsrt,(12,10,848,50),'对局设置')
seatbindings=[]
for i,color in enumerate(['green','yellow','blue','red']):
    name='RoomSeat'+color.title();sgo,srt=fillframe(name,9178542847706922814,(0,i*210,980,202))
    picture(name+'Header',srt,(7,7,966,50),'seat-header-fill')
    picture(name+'ColorBar',srt,(12,12,10,40),'seat-bar-'+color)
    picture(name+'ColorSquare',srt,(34,17,30,30),'seat-square-'+color,preserve=True)
    nametext=text(name+'Name',srt,(80,10,675,45),'',28)[2]
    badgego,badgert,_=picture(name+'HostBadge',srt,(892,16,32,32),'icon-crown-dark',preserve=True)
    avatar=fillframe(name+'Avatar',srt,(20,60,136,136),'avatar-fill','avatar-frame')[1]
    picture(name+'OccupantIcon',avatar,(42,42,52,52),'icon-empty-seat-dark',preserve=True)
    state=text(name+'State',srt,(178,68,455,64),'',23)[2]
    invstate=text(name+'Invitation',srt,(178,139,430,42),'邀请后等待成员自行入座',19)[0]
    buttons={}
    for key,cap,icon,rect in [
        ('SwapButton','换位置','swap-dark',(780,78,178,50)),
        ('RemoveButton','移除不可用','remove-danger',(780,78,178,50)),
        ('InviteButton','邀请','invite-dark',(636,68,154,50)),
        ('CloseButton','关闭座位','close-dark',(802,68,154,50)),
        ('HotSeatButton','热座未支持','hotseat-muted',(636,132,154,50)),
        ('BotButton','人机未支持','bot-muted',(802,132,154,50)),
        ('OpenButton','开启座位','invite-dark',(780,78,178,50))]:
        buttons[key]=button(name+key,srt,rect,cap,icon=icon,size=20)[2]
    seatbindings.append(dict(NameText=nametext,StateText=state,HostBadge=badgego,InviteState=invstate,**buttons))
mapsgo,mapsrt=fillframe('RoomMapOptions',settingsrt,(12,70,414,832))
section('RoomMapsHeader',mapsrt,(8,8,398,42),'地图')
mapoptions=[]
for idx,(ident,cap,guid) in enumerate([('map-three-players','三人地图','9756ed9c18ed9c64a8d957a9068c595f'),('map-four-players','四人地图','37ec701aa09b4714a901b0b7e07d20d5')]):
    name='RoomThreePlayerMap' if idx==0 else 'RoomFourPlayerMap'
    go,rt,im=picture(name,mapsrt,(28,54+idx*386,358,374),'map-frame-default',True)
    btn=sid(name+'.button');b=identity(button_template,btn,go);b=field(b,'m_TargetGraphic',ref(im));b=field(b,'m_Transition',2)
    for k,st in [('m_HighlightedSprite','hover'),('m_PressedSprite','selected'),('m_SelectedSprite','hover'),('m_DisabledSprite','disabled')]:b=re.sub(r'^(    '+k+r':).*$',r'\g<1> '+sprites['map-frame-'+st],b,flags=re.M)
    blocks[btn]=b;component(go,btn)
    picture(name+'Image',rt,(16,8,326,326),f'{{fileID: 21300000, guid: {guid}, type: 3}}',preserve=True)
    picture(name+'LabelBackground',rt,(4,334,350,36),'map-label-fill')
    text(name+'Label',rt,(12,334,298,36),cap,23,align=4)
    selection=picture(name+'Selected',rt,(307,335,30,30),'icon-check-dark',preserve=True)[2]
    dgo,drt,_=picture(name+'Disabled',rt,(16,8,326,326),'map-disabled-overlay',active=False)
    picture(name+'Lock',drt,(135,135,56,56),'icon-lock-dark',preserve=True)
    mapoptions.append(dict(Id=ident,DisplayName=cap,Button=btn,Selection=selection,DisabledOverlay=dgo,Implemented=1))
modego,modert=fillframe('RoomRuleModeOptions',settingsrt,(438,70,422,130))
section('RoomModeHeader',modert,(8,8,406,42),'游戏模式')
modeoptions=[]
for ident,cap,x,w,implemented in [('standard','标准模式',16,172,1),('entrepreneur','企业家',200,206,0)]:
    name='RoomMode'+ident.title();go,rt,btn,label=button(name,modert,(x,60,w,50),cap+(' · 未实现' if not implemented else ''),size=20)
    selection=picture(name+'Selected',rt,(8,16,18,18),'icon-check-dark',preserve=True)[2]
    modeoptions.append(dict(Id=ident,DisplayName=cap,Button=btn,Selection=selection,DisabledOverlay=0,Implemented=implemented))
expgo,exprt=fillframe('RoomExpansionList',settingsrt,(438,212,422,374))
section('RoomExpansionsHeader',exprt,(8,8,406,42),'扩展包')
_,_,expcontent=scroll('RoomExpansionScroll',exprt,(24,62,378,290),350,276)
# 原生纵列和 ContentSizeFitter：长名称换行增加行高，溢出时滚动条自动显示。
mono(rtgo[expcontent],'RoomExpansionLayout','59f8146938fff824cb5fd77236b75775','''  m_Padding:
    m_Left: 0
    m_Right: 0
    m_Top: 0
    m_Bottom: 0
  m_ChildAlignment: 0
  m_Spacing: 12
  m_ChildForceExpandWidth: 1
  m_ChildForceExpandHeight: 0
  m_ChildControlWidth: 1
  m_ChildControlHeight: 1
  m_ChildScaleWidth: 0
  m_ChildScaleHeight: 0
  m_ReverseArrangement: 0''')
mono(rtgo[expcontent],'RoomExpansionFit','3245ec927659c4140ac4f8d17403cc18','  m_HorizontalFit: 0\n  m_VerticalFit: 2')
expoptions=[]
for idx,(ident,cap,description) in enumerate([('core','基础内容','固定启用'),('enterprise','企业扩展','可选；完整玩法尚未验收'),('infected','感染者扩展','依赖企业；完整玩法尚未验收')]):
    name='RoomExpansion'+ident.title();go,rt,btn,label=button(name,expcontent,(0,idx*90,350,78),'',size=23)
    # 行由子文本的 preferredHeight 驱动，避免固定高度截断长名称。
    mono(go,name+'Layout','59f8146938fff824cb5fd77236b75775','''  m_Padding:
    m_Left: 54
    m_Right: 12
    m_Top: 12
    m_Bottom: 12
  m_ChildAlignment: 0
  m_Spacing: 6
  m_ChildForceExpandWidth: 1
  m_ChildForceExpandHeight: 0
  m_ChildControlWidth: 1
  m_ChildControlHeight: 1
  m_ChildScaleWidth: 0
  m_ChildScaleHeight: 0
  m_ReverseArrangement: 0''')
    blocks[label]=field(blocks[label],'m_Text',json.dumps(cap,ensure_ascii=True));blocks[label]=re.sub(r'    m_Alignment:.*','    m_Alignment: 0',blocks[label])
    text(name+'Description',rt,(54,42,282,36),description,18)
    _,checkrt,_=picture(name+'Checkbox',rt,(14,18,28,28),'checkbox-unchecked')
    mono(rtgo[checkrt],name+'CheckboxIgnore','306cc8c2b49d7114eaa3623786fc2126','  m_IgnoreLayout: 1\n  m_MinWidth: -1\n  m_MinHeight: -1\n  m_PreferredWidth: -1\n  m_PreferredHeight: -1\n  m_FlexibleWidth: -1\n  m_FlexibleHeight: -1\n  m_LayoutPriority: 1')
    selgo,selrt,selection=picture(name+'Selected',rt,(15,19,26,26),'icon-check-dark',preserve=True)
    mono(selgo,name+'SelectedIgnore','306cc8c2b49d7114eaa3623786fc2126','  m_IgnoreLayout: 1\n  m_MinWidth: -1\n  m_MinHeight: -1\n  m_PreferredWidth: -1\n  m_PreferredHeight: -1\n  m_FlexibleWidth: -1\n  m_FlexibleHeight: -1\n  m_LayoutPriority: 1')
    expoptions.append(dict(Id=ident,DisplayName=cap,Button=btn,Selection=selection,DisabledOverlay=0,Implemented=1))
summarygo,summaryrt=fillframe('RoomSummaryPanel',settingsrt,(438,598,422,304))
section('RoomSummaryHeader',summaryrt,(8,8,406,42),'当前配置')
setrect(8986763648249241620,summaryrt,(20,62,382,138));blocks[6890541214495264730]=field(blocks[6890541214495264730],'m_Color','{r: 0.141, g: 0.169, b: 0.173, a: 1}');blocks[6890541214495264730]=re.sub(r'    m_FontSize:.*','    m_FontSize: 23',blocks[6890541214495264730])
maphint=text('RoomMapHint',summaryrt,(20,205,382,78),'',21)[2]
footgo,footrt=fillframe('RoomFooter',roomrt,(16,1008,1888,64),'footer-fill','footer-frame')
setrect(footrt,roomrt,(0,8,-32,64),(0,0,1,0),(.5,0))
setrect(2322681362276834776,footrt,(12,9,224,48))
setrect(6154721122509970096,footrt,(-310,7,298,52),(1,1,1,1),(0,1))
setrect(7745340870052418006,toprt,(510,9,200,54))
for go,skin in [(4396466916152842778,'dark'),(3301771202113221753,'accent'),(8800536188250814371,'neutral')]:
    for k,v in list(blocks.items()):
        if f'm_GameObject: {ref(go)}' in v and 'guid: fe87c0e1cc204ed48ad3b37840f39efc' in v:blocks[k]=field(field(field(v,'m_Sprite',sprites['button-'+skin+'-default']),'m_Type',1),'m_Color','{r: 1, g: 1, b: 1, a: 1}')
        if f'm_GameObject: {ref(go)}' in v and 'guid: 4e29b1a8efbd4b44bb3f3716e73f07ff' in v:
            v=field(v,'m_Transition',2)
            for key,st in [('m_HighlightedSprite','hover'),('m_PressedSprite','pressed'),('m_SelectedSprite','hover'),('m_DisabledSprite','disabled')]:v=re.sub(r'^(    '+key+r':).*$',r'\g<1> '+sprites['button-'+skin+'-'+st],v,flags=re.M)
            blocks[k]=v
setrect(2459884493535825600,footrt,(258,5,1020,52))
blocks[6748572949741022701]=re.sub(r'    m_FontSize:.*','    m_FontSize: 21',blocks[6748572949741022701]);blocks[6748572949741022701]=field(blocks[6748572949741022701],'m_Color','{r: 0.141, g: 0.169, b: 0.173, a: 1}')
permissions=text('RoomPermissions',toprt,(728,8,475,56),'',19)[2]
# 两个独立模态窗口，只在内存开关；说明正文滚动，标题/关闭固定。
helpgo,helprt,_=picture('RoomHelpPanel',roomrt,(0,0,0,0),'dim-overlay',True,active=False);setrect(helprt,roomrt,(0,0,0,0),(0,0,1,1),(.5,.5))
hwgo,hwrt=fillframe('RoomHelpWindow',helprt,(0,0,1320,884),'reading-fill');setrect(hwrt,helprt,(0,0,1320,884),(.5,.5,.5,.5),(.5,.5))
text('RoomHelpTitle',hwrt,(40,24,1060,64),'房间操作说明',34)
helpclose=button('RoomHelpClose',hwrt,(1224,22,64,64),'',icon='close-dark')[2]
_,_,hcontent=scroll('RoomHelpReading',hwrt,(54,122,1228,704),1172,920)
text('RoomHelpBody',hcontent,(0,0,1140,900),'房间配置\n\n房主可选择三人或四人地图。满四人时自动使用四人地图；减员后保留当前选择。\n\n基础内容固定启用。感染者依赖企业。企业家是独立模式，尚未实现，不能启用。\n\n准备与开始\n\n成员需确认当前配置并点击准备。地图、扩展或座位变化会取消人工准备。房主点击开始确认当前配置；连接、身份、内容版本和能力仍由房间服务复验。\n\n座位\n\n开放空座可关闭，也可重新开启。换到有人座位需对方确认；座位颜色留在原位置，房主身份跟随玩家。\n\n服务状态\n\nSteam 好友邀请按当前服务可用性开放。密码、热座、人机和移除成员暂未提供后台能力，界面会保持不可用。完整企业和感染者玩法仍需后续整合验收。',26)
swapgo,swaprt,_=picture('RoomSeatSwapPanel',roomrt,(0,0,0,0),'dim-overlay',True,active=False);setrect(swaprt,roomrt,(0,0,0,0),(0,0,1,1),(.5,.5))
_,swrt=fillframe('RoomSeatSwapWindow',swaprt,(0,0,820,660),'reading-fill');setrect(swrt,swaprt,(0,0,820,660),(.5,.5,.5,.5),(.5,.5))
text('RoomSwapTitle',swrt,(32,20,620,60),'选择座位 / 确认交换',30)
swapclose=button('RoomSwapClose',swrt,(718,20,70,54),'',icon='close-dark')[2]
swapprompt=text('RoomSwapPrompt',swrt,(32,86,756,66),'选择开放座位；有人时需对方同意。',23)[2]
targets=[button('RoomSwapTarget'+color.title(),swrt,(32,164+i*76,756,64),'')[2] for i,color in enumerate(['green','yellow','blue','red'])]
accept=button('RoomSwapAccept',swrt,(32,498,350,58),'同意交换','accent')[2]
decline=button('RoomSwapDecline',swrt,(406,498,350,58),'拒绝交换')[2]
roomview=536233927053355161
primarytext=next(k for k,v in blocks.items() if f'm_GameObject: {ref(1547942319014377991)}' in v and 'm_Text:' in v)
blocks[primarytext]=field(blocks[primarytext],'m_Color','{r: 0.141, g: 0.169, b: 0.173, a: 1}');blocks[primarytext]=re.sub(r'    m_FontSize:.*','    m_FontSize: 29',blocks[primarytext])
for ident in [601001218699912562,6890541214495264730]:
    blocks[ident]=re.sub(r'    m_BestFit:.*','    m_BestFit: 0',blocks[ident]);blocks[ident]=re.sub(r'    m_Alignment:.*','    m_Alignment: 0',blocks[ident]);blocks[ident]=field(blocks[ident],'m_Color','{r: 0.141, g: 0.169, b: 0.173, a: 1}')
extra=''
for key,values in [('seats',seatbindings),('maps',mapoptions),('ruleModes',modeoptions),('expansions',expoptions)]:
    extra+='  '+key+':\n'
    for value in values:
        for idx,(fieldname,fieldvalue) in enumerate(value.items()):
            formatted=str(fieldvalue) if fieldname=='Implemented' else json.dumps(fieldvalue,ensure_ascii=True) if fieldname in ['Id','DisplayName'] else ref(fieldvalue)
            extra+=('  - ' if idx==0 else '    ')+fieldname+': '+formatted+'\n'
reasons=[('not_room_host','只有房主可以修改配置。'),('configuration_stale','房间配置已变化，请按最新状态重试。'),('unsupported_player_count','当前实际人数不满足开局条件。'),('map_requires_four_player','四名玩家必须使用四人地图。'),('missing_enterprise_dependency','启用感染者需要同时启用企业。'),('rule_mode_not_implemented','企业家模式尚未实现，不能启用。'),('content_not_ready','所选内容组合或人数配方尚未完成。'),('content_version_mismatch','玩家内容或规则版本与房间不一致。'),('player_not_ready','等待成员连接、身份验证及当前配置准备。'),('seat_occupied','目标座位已占用或关闭。'),('seat_swap_stale','换座请求已失效，请重新申请。'),('service_unavailable','该服务暂不可用。'),('room_protocol_mismatch','房间协议不匹配。'),('legacy_entity_state_unsupported','旧对局状态无法可靠恢复。')]
extra+='  reasons:\n'+''.join('  - Code: '+code+'\n    Caption: '+json.dumps(cap,ensure_ascii=True)+'\n' for code,cap in reasons)
for key,ident in dict(primaryCaption=primarytext,permissionsText=permissions,mapHintText=maphint,swapPromptText=swapprompt,passwordButton=password,helpButton=helpbtn,helpCloseButton=helpclose,swapAcceptButton=accept,swapDeclineButton=decline,helpPanel=helpgo,swapPanel=swapgo,swapCloseButton=swapclose).items():extra+='  '+key+': '+ref(ident)+'\n'
extra+='  swapTargets:\n'+''.join('  - '+ref(ident)+'\n' for ident in targets)
blocks[roomview]+=extra
for parent in touched:
    if parent not in blocks:continue
    children=[k for k,v in parents.items() if v==parent]
    # 原有兄弟顺序优先；新节点追加。只有房间子树内父级进入 touched。
    childtext='  m_Children:\n'+''.join('  - '+ref(k)+'\n' for k in children) if children else '  m_Children: []\n'
    blocks[parent]=re.sub(r'  m_Children:.*?(?=  m_Father:)',lambda m:childtext,blocks[parent],flags=re.S)
PREFAB.write_text(source[:source.index('--- !u!')]+''.join(blocks.values()),encoding='utf8')
evidence=ROOT/'Logs/ExpansionValidation/RoomConfigurationUi/asset-source';evidence.mkdir(parents=True,exist_ok=True)
(evidence/'素材映射.json').write_text(json.dumps({'zipSHA256':hashlib.sha256(z.fp.read()).hexdigest() if False else hashlib.sha256((ROOT/'游城拓荒/UI素材/Room-UI-v1.1-VerticalMaps.zip').read_bytes()).hexdigest(),'version':'1.1','verifiedChecksumEntries':checksum_count,'originalPrefabSHA256':hashlib.sha256(source.encode()).hexdigest(),'scene':'Assets/Scenes/StartScene.unity','asset':'Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab','ownership':'源Prefab房间子树，生产StartScene无房间覆盖','sprites':mapping},ensure_ascii=False,indent=2),encoding='utf8')
print('已编辑房间子树；校验',checksum_count,'个包文件；引用',len(mapping),'项素材，其中复用',sum(v['reused'] for v in mapping.values()),'项。')
