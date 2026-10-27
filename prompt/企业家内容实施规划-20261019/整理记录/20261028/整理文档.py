"""QY 文档整理：封存、生成现行文档和检查；实际移动只由同目录 PowerShell 脚本执行。"""
from pathlib import Path
import json, hashlib, zipfile, re, os, sys, urllib.parse
ROOT=Path('G:/NoMadCity'); BASE=ROOT/'prompt/企业家内容实施规划-20261019'
OUT=BASE/'整理记录/20261028'; HIST=BASE/'历史/20261028整理前'; DOC=HIST/'文档'
def sha(data):return hashlib.sha256(data).hexdigest()
def save(path,obj):path.write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
def write(path,text):path.write_text(text.strip()+'\n',encoding='utf-8')
def prepare():
    if (OUT/'移动前绝对路径清单.json').exists():raise RuntimeError('准备清单已存在，不覆盖')
    DOC.mkdir(parents=True,exist_ok=True)
    files=sorted(p for p in BASE.iterdir() if p.is_file() and (p.suffix=='.md' or p.name=='任务定义.json' or p.name.startswith('协调等待-')))
    rows=[]
    with zipfile.ZipFile(HIST/'原始字节快照.zip','x',compression=zipfile.ZIP_DEFLATED) as z:
        for p in files:
            if p.is_symlink() or not p.resolve().is_relative_to(ROOT):raise RuntimeError('目标越界或链接')
            data=p.read_bytes();dest=DOC/p.name
            if dest.exists():raise RuntimeError('归档目标已存在')
            z.writestr(p.name,data)
            rows.append(dict(source=str(p),destination=str(dest),bytes=len(data),sha256=sha(data),snapshotEntry=p.name))
    with zipfile.ZipFile(HIST/'原始字节快照.zip') as z:
        for r in rows:
            if sha(z.read(r['snapshotEntry']))!=r['sha256']:raise RuntimeError('快照字节不符')
    save(OUT/'移动前绝对路径清单.json',rows)
    save(HIST/'原路径映射.json',rows)
    save(OUT/'快照校验.json',dict(files=len(rows),markdown=sum(Path(r['source']).suffix=='.md' for r in rows),bytes=sum(r['bytes'] for r in rows),archive=str(HIST/'原始字节快照.zip'),archiveBytes=(HIST/'原始字节快照.zip').stat().st_size,archiveSha256=sha((HIST/'原始字节快照.zip').read_bytes()),allEntriesVerified=True))
    print(json.dumps(read(OUT/'快照校验.json'),ensure_ascii=False,indent=2))

LINK=re.compile(r'(?P<head>!?\[[^\]\n]*\]\()(?P<url><[^>\n]*>|[^\s)]+)(?P<tail>\))')
def finish():
    rows=read(OUT/'移动前绝对路径清单.json');mapping={str(Path(r['source'])).casefold():Path(r['destination']) for r in rows}
    old_tasks=read(DOC/'任务定义.json');changes=[]
    with zipfile.ZipFile(HIST/'原始字节快照.zip') as z:
        for r in rows:
            src,dest=Path(r['source']),Path(r['destination']);raw=z.read(r['snapshotEntry'])
            if dest.read_bytes()!=raw:raise RuntimeError('移动后原字节发生变化，拒绝覆写:'+str(dest))
            if src.suffix!='.md':continue
            text=raw.decode('utf-8-sig');count=0
            def replace(m):
                nonlocal count
                target=m['url'].strip('<>');decoded=urllib.parse.unquote(target)
                if decoded.startswith(('#','http:','https:','mailto:','plugin:','codex:','file:')) or 'UI素材' in decoded:return m[0]
                pathpart,sep,anchor=decoded.partition('#')
                if not pathpart:return m[0]
                resolved=Path(os.path.abspath(src.parent/pathpart)) if not re.match(r'^[A-Za-z]:[\\/]',pathpart) else Path(os.path.normpath(pathpart))
                if not resolved.is_relative_to(ROOT):return m[0]
                final=mapping.get(str(resolved).casefold(),resolved)
                new=os.path.relpath(final,dest.parent).replace('\\','/')+(sep+anchor if sep else '')
                if new!=target:count+=1
                return m['head']+'<'+new+'>'+m['tail']
            text=LINK.sub(replace,text)
            # 历史文本保留原义，仅添加历史标识并调整 Markdown 链接。原字节由ZIP保存。
            banner='> 历史记录（整理前原文）。不作为现行派发授权；当前要求见 [现行总览](../../../README.md)。本页链接已按新位置调整，原始字节见 [原始快照](../原始字节快照.zip)。\n\n'
            dest.write_text(banner+text,encoding='utf-8')
            r['navigableSha256']=sha(dest.read_bytes());r['linkAdjustments']=count
            changes.append(dict(path=str(dest),links=count))
    save(HIST/'原路径映射.json',rows);save(OUT/'历史链接调整.json',changes)
    generate(old_tasks)
    print(json.dumps(dict(archived=len(rows),markdown=sum(Path(r['source']).suffix=='.md' for r in rows),linksAdjusted=sum(x['links'] for x in changes)),ensure_ascii=False))

def hist(n):
    matches=list(DOC.glob(f'{n:02}-*.md')) if n<10 else list(DOC.glob(f'{n}-*.md'))
    if len(matches)!=1:raise RuntimeError('历史编号不唯一:'+str(n))
    return matches[0].relative_to(BASE).as_posix()
def generate(old_tasks):
    write(BASE/'README.md','''# QY 工作总览

更新：2026-10-28。**QY-001—016 原定实现主体已交付；017 UI、018 最终联机验收、019 正常完整局尚未全部收尾。019 与自动续测 qy 按用户要求暂停。**“已实现”不等于全部卡面、页面或联机整局已验收。

日常只读以下入口；历史派发不再作为当前授权：

| 内容 | 唯一维护入口 |
| --- | --- |
| 现行裁定、实施准则、页面与验收边界 | [01 现行裁定](01-依据审校与差异处置.md) |
| 19 项实际状态、证据及验证限制 | [03 任务状态](03-任务清单与依赖.md)、[机器可读状态](任务定义.json) |
| 五类待移除内容及其余未完成事项 | [04 待办](04-高风险语义与回归清单.md) |
| 本轮协作与发布交接 | [08 当前协调](08-协调台账与派发记录.md)、[Git 提交范围](Git提交范围.md) |

本轮用户授权：精简合并文档、构建新安装包、上传现有 `origin/txym`；五类待移除内容只登记。本整理任务只改文档和提交准备清单，构建由017独占Unity，提交推送待包交接后另行执行。保留所有人工源码、文案及资产改动。长正文容量暂缓。

已测内存样本显示持续数GiB膨胀得到控制，离局卡图释放有效；秒级点击、建设显示及采集可用前等待仍未解决。多萝西已改“两枚组合直接执行”，仅有限Lua验证；数量条目2 PPU及加减1.2倍随本轮新包纳入，尚不能据此宣称页面验收通过。

239份顶层历史Markdown及7份JSON已移入[历史目录](历史/20261028整理前/README.md)。历史浏览版调整了链接；[原始字节快照](历史/20261028整理前/原始字节快照.zip)保留整理前全部原文，[原路径映射](历史/20261028整理前/原路径映射.json)用于恢复旧引用。01—08保留同名当前入口或短指引，不保留两套现行权威。

`实施记录/`、`依据快照/`、`覆盖/`、`任务要求/`、`执行者提示词/`、`核验/`中的原始证据保持原位原字节；它们描述各自历史时点，不代表当前完成状态或自动恢复任务的授权。未改证据正文中的旧绝对路径，可通过映射定位归档文档。原规划核验器验证的是2026-10-19基线，不是当前游戏验收器。
''')
    write(BASE/'01-依据审校与差异处置.md','''# 现行裁定与实施准则

更新：2026-10-28。本页合并原01、02、04、05、06的现行要求；已被用户撤回的安排不再并列为默认。**最新用户明确要求优先，其次项目AGENTS、当前卡面及规则、现行通用合同；历史派发仅供追溯。**技术字段与常规实现可自行决定，真正的规则或页面歧义才询问用户。

## 两条实施准则

- UI不得超出文档规定的页面链路；链路不能实现功能或存在真实歧义时，必须征询用户意见，继续不依赖答复的工作。
- 游戏C#内核不实现企业家、角色牌卡面效果；Lua落实卡面条件、顺序、费用、数量、选择和规则修正。C#提供通用实例、权限、调度、原语与恢复机制。不能把卡名、U编号或专属文案键做成C#/UI路由。

JSON声明实体、来源、轨道、表现和脚本引用；Lua组合通用规则；UI消费正式请求并提交答案，不预扣、复制库存、重掷或推测规则。ItemZones及真实实例是库存唯一事实。预览与提交同一资格口径；Host复验来源、玩家、版本、窗口、候选、费用与库存。受理、费用、激活、正文、主要额度完成按原分阶段保存，不能把跨付款整条行动强改一次事务。查询不执行Effect、交互或随机。

## 已确认规则

| 项目 | 当前要求 |
| --- | --- |
| A05/5B | 每次收尾支付1指令；战术快速入口仍2指令。无可用战术不注入，已付内部放弃不退，选定才移轨道 |
| D03/D04 | D03为3A、D04为3B。未计5回合，1B＋3A为8券/3声望；1B＋3B为4券＋合计1基础资源/2声望 |
| M13/U20 | 合法叠建先落位、分层、计分，必做移除及响应结清后原入场；确认后不放弃，不空目标放行，不重复建设或计分 |
| M08 | 自动按本人单格全层设施最多数×2分，并列稳定格序；不弹选格页，保存计数及入账收据 |
| 数字表示 | 实际受益人、时点、费用、数量、上限、生命周期等价即可；M01不提前生效、不进随机池、不惠及马丁本人、不重复减费 |
| 选人选面 | 原企业候选本人预览→末位逆时针选企业家→用原候选选初始企业；1/3/5/7回合裁供应后、盖角色前按行动顺序选面，旧面持续 |
| 实体与终局 | 5能力＋20双面实体/40面；图纸三色各3、追加塔1、帮派4、骰3。终局特殊计分→区控→资源；公司非玩家所有，持约者逐区floor(公司数/2)，合约正常采集不扩到额外采集 |

来源共享真实轨道，所有收尾及必要响应结束才复位；独立立即各有节点/收据，同条内保序。条件左式失败不建右式，但不得吞条件外兄弟效果。空集合完成不冒足额支付。恢复延续当前阶段，不能重抽、重复扣费、交付、入场、计分或补造历史。

## 当前内容与交互裁定

- 卡牌与token转移分开；token不限于Cube，包括嘉维尔指示物和终身合作标记。不复活标记专用付款原语。费用与正文依原合同，不能仅为等价实例ID增加选择页。
- 玩家标记装载按2026-10-26最终要求：相关真实响应先结清，Lua再读库存生成选项，逐枚真实MoveItem，单枚不足仅该effect失败；不加供应屏障、整批预留或新的足额包装。旧LoadCardMarkers已退役。不能把2026-10-22旧整批装载措辞覆盖此裁定。
- 艾格尼丝快速战术外套支付2指令条件式，确认后执行原战术，不与quickCost重复收费；外层放弃不付、不执行、不消耗战术，内部放弃按原已用规则。A05仍1指令。
- 2026-10-28多萝西异质标记移除由Lua列举两枚合法组合，`effect.choice.execute`与普通EffectRow点击直接执行；等价去重保真实归属/原供应/状态，不改初始装载、捕获及原Condition右式。旧tokens_selected待答上下文不自动迁移。
- 诗怀雅策略选两个不同1—2级企业特效，不额外要求已解锁；杰西卡策略保对手已解锁要求。由Lua筛正式集合，不加“已用特效排除”专用接口。诗怀雅计谋选对手、供应空则跳过，不另选具体标记。
- 正式内容12脚本13处Effect.Repeat已展开普通effect列表；通用内核和旧数据解码尚属待移除审计，不与收尾来源once/unbounded语义混淆。
- 付款替代使用原结算区插入结算按钮，沿来源/权限/修订/正式后继结清；撤“编辑付款替代来源”按钮及专路。合计资源只显示请求允许类型，保配比、执行/放弃、付款原子性与真实拒绝。

## 页面、文案与资产

U01—U22是验收链路，不能按编号派发卡牌专属代码。完整规划表保留在[历史05](历史/20261028整理前/文档/05-页面链路与素材边界.md)，以下纠正优先；表内旧“未实现模式拒绝”不能恢复为已取消的额外房间锁。

| 类别 | 当前边界 |
| --- | --- |
| U02/U03/U06 | 企业候选本人只读预览用原企业选择页；企业家及回合面用卡牌选择页。候选不重抽、不提前选；DIY保N候选 |
| U05 | 未抉择回合槽显示对应A面，仅预览，不启用；选后显示实际面；49整图对应正面/放大/真实候选许可仍有效 |
| U10/U11 | 每个条件式显示对应确认句和动态参数，文本经Lua/JSON转译；普通执行条目、数量、资源、玩家、卡牌按真实请求区分，不拿整卡正文替代 |
| U12/U13/U18 | A03/M06付款后移动不额外添加主动放弃；企业特效直接选正式特效；骰面直选“点数N、效果”，不先选骰再修正 |
| U15/U20/U21/U22 | 收尾稳定来源及原横向滚动；图纸建设直接原建设链，纯供应选择不伪装免费Build；设施格复用建设面板；每个来源独立收容/放弃 |
| 查看与收起 | 非答题只读卡页点非卡区域关闭；企业家选择沿原收起/展开返回同一待答页，保选择/滚动/权限，不加浏览前置或“继续选择”专用按钮 |
| 布局 | 设施格/合作板发放页仅修上下边界，原横向范围保持；详情固定高度，按当前稳定可见顺序左右逐行交替；长正文容量暂缓；供应下层上移125n牌面贴图像素按缩放换算 |
| 快速行与文本 | 快速行只名称和禁用原因。效果详情仅对应时机效果段，卡牌资料与效果详情分开；固定nA/nB全名放JSON，保用户人工文案及真正动态参数 |
| 房间/HUD | 单房主可配置扩展，保正式人数/房主/双扩依赖；对手席位随实际人数，顶栏按正式回合行动顺序显示玩家色方格 |
| 图标与悬停 | 按用户中文释义和登记语义用icon；公司/嘉维尔地图不叠名称；插入结算按钮只关闭悬停文字，保hover视觉、disabled/业务状态、无障碍名称 |

当前场景/预制体是静态结构、位置、尺寸、样式和固定文案唯一来源。普通测试/运行/构建不得Rebuild、生成覆盖或运行时重写已有静态文案。指定修改须说明场景覆盖或源预制体，验证实际运行的同一资产。UI素材唯一入口是项目`游城拓荒/可用UI素材/README.md`及现行用途标准；受限原档案、历史原包工具与替代旧副本禁止访问，缺图如实记录。

## 保存、联机、性能与验收

- 联机获许可owner在操作窗口内连续本地执行、异步复制，窗口边界按序号/缺号/去重/回显汇合；不能仅解按钮锁冒称实现，不为提速删规则或同步检查。
- 联机窗口结束且确认点就绪才自动保存；热座自动/手动/退出共用安全捕获边界，稳定待答可保续接，末端子effect结算中不可捕获。**返回主菜单不自动保存**，不得因槽满导入保存页循环。完整读取、内容身份和恢复校验保持。
- 正常验收从可见独立程序正常菜单/建房/开局及合法行动进入，不注入牌、资源、随机或状态；隔离Lua/原生测试不能算正常UI覆盖。固定包、内容哈希、GameId及实际证据，多局累积；未抽到不是通过。
- 内容或夹具预期不同但仍可合法推进时保首次证据，按真实可见合法步骤继续；真实崩溃、不同步、必答权限丢失或无合法下一步才中断受影响部分。不得改期望掩盖失败、热换运行包或为了目标没出现立即重开。
- 复用既有UI驱动，不依赖computer-use；系统防火墙提示本身不等于阻塞，也不授权代点或改系统权限。旧失败首证、唯一存档及恢复包保留，不把历史测试前清槽授权外推为本轮删档。
- 内存目标看真实Private/WorkingSet的持续操作和停手5/15/30秒曲线，不以累计分配、单独Mono或关必要保存/重启/修剪工作集冒达标。已有R18/R22/R23是有限正常样本，未解决秒级等待。
- 019与qy暂停；017本轮独占Unity构建。不同只读/文档任务可并行，同文件写入、同Unity编译构建、同可见窗口互斥。包内内容与后续人工文案差异如实记录，不回退或热换。整理文档不授权重开验收。

来源：用户2026-10-28本轮要求、[原01完整裁定](历史/20261028整理前/文档/01-依据审校与差异处置.md)、[原02合同](历史/20261028整理前/文档/02-共同约束与架构归属.md)、[原08台账](历史/20261028整理前/文档/08-协调台账与派发记录.md)。[五类待移除](04-高风险语义与回归清单.md)只登记，尚未执行。
''')
    states={
      1:('原定合同交付','依据、规则与通用合同已定稿；合同审查不代表游戏全验收。',1),
      2:('原定实现主体交付','统一能力来源、声明行动及部署库存边界已有源码/有限验证；正常页面缺口归019。',112),
      3:('原定实现主体交付','区域、轨道、颜色及地图标记已实现；R3原生/共存截图有限，不追认所有正常页面。',129),
      4:('原定实现主体交付','模式、定义、选人选面已实现；R18/R22/R23有正常内存样本，R23整份测量合同并非全过。',237),
      5:('原定实现主体交付','载体/标记转移已实现，LoadCardMarkers于R17/R18正式退役；R19测试初始化补正有限收口。',185),
      6:('原定实现主体交付','离场、独立收容、挂链及响应时点已有有限/恢复验证；不能据此冒正常完整局通过。',185),
      7:('原定实现主体交付；最新增量有限验证','合计资源/多来源付款已实现；多萝西组合选择源码已交付，有限真实Lua解析/续接验证，未实跑物品移动或界面。',152),
      8:('原定实现主体交付','收尾来源、窗口执行及保存改动已交付；保存内存改进经004 R18/R22/R23正常样本，联机最终验收未闭合。',237),
      9:('原定实现主体交付','免费采集、城市目的地及角色逐枚标记移动已实现；保有限验证与正常覆盖之别。',185),
      10:('原定实现主体交付','设施分层、建设对象及查询复用已实现；有限结果不能声称秒级建设卡顿已消失。',118),
      11:('原定实现主体交付','终局、区控与合约Lua迁移已有交付；仍需最终正常终局/联机覆盖。',58),
      12:('原定内容主体交付','第五号能力/八面Lua及文案已交付；未覆盖正常卡面场景继续列缺口。',92),
      13:('原定内容主体交付','艾格尼丝正文/战术/指令与逐枚移动已交付，有限/恢复验证；正常页面未全覆盖。',185),
      14:('原定内容主体交付','蒂亚戈投资/声望/八面Lua及沙箱补正已交付；正常页面未全覆盖。',185),
      15:('原定内容主体交付','保罗帮派/分享/八面Lua与原主修正已交付；正常页面未全覆盖。',92),
      16:('原定内容主体交付','马丁图纸/奖励/设施Lua与付款插入已交付；原失败和后继核验各留证，不冒整局通过。',135),
      17:('UI主体交付；本轮新包构建中','正常页面覆盖未闭合；2026-10-28新授权构建安装包，旧“无打包授权/不再派017”不再有效。最终包结果待017交接。',237),
      18:('技术迁移/恢复/回归交付；最终联机未闭合','离局卡图释放有R23正常数据；技术交叉回归与019整局验收分开，不重复计通过。',237),
      19:('用户暂停；整局未完成','R30四端本地联机至第5回合收尾。auto-3确认506/SourceEpoch4→目标Epoch5；保存点不等同本地末尾511。',175)
    }
    records=[];table=[]
    for old in old_tasks:
        n=int(old['id'].split('-')[1]);status,note,evidence=states[n]
        extra=[]
        if n==7:extra=['实施记录/QY-007-DOROTHY-PAIR-CHOICE-20261028']
        if n==19:extra=['实施记录/QY-019-MAP-INPUT-FIX-FOUR-PEER-RECOVERY-R30']
        if n in (4,8,18):extra=['实施记录/QY-004-SAVE-PEAK-REDUCTION-MEMORY-VALIDATION-R18','实施记录/QY-004-ATTRIBUTION-WIRING-AND-REGISTERED-BUILD-RETEST-R22','实施记录/QY-004-ARTWORK-RELEASE-NORMAL-MEMORY-VALIDATION-R23']
        rec=dict(id=old['id'],title=old['title'],deps=old['deps'],status=status,summary=note,verificationBoundary='源码/合同或有限验证不代表全部正常页面、全卡面或最终联机完整局验收。',evidence=[hist(evidence),*extra],originalContract='历史/20261028整理前/文档/任务定义.json',updated='2026-10-28')
        records.append(rec);table.append(f"| {rec['id']} | {rec['title']} | {status} | {note} [依据]({hist(evidence)}) |")
    save(BASE/'任务定义.json',records)
    write(BASE/'03-任务清单与依赖.md','''# 19项任务实际状态

更新：2026-10-28。原始任务职责及依赖保留在[历史任务定义](历史/20261028整理前/文档/任务定义.json)；[当前JSON](任务定义.json)与本表同一状态。001—016“主体交付”只表示相应源码/合同及有限验证，不表示全部正常页面或完整局通过。

| 任务 | 职责 | 当前状态 | 证据与限制 |
| --- | --- | --- | --- |
'''+ '\n'.join(table)+'''

## 当前关键证据

- 多萝西：[本轮记录](实施记录/QY-007-DOROTHY-PAIR-CHOICE-20261028)。真实Lua有限结果：2+1枚生成2项、2+2生成3项、1+1+1生成3项；未实际执行物品移动，未UI/整局验收，旧待答不自动迁移。
- 内存：[R18](实施记录/QY-004-SAVE-PEAK-REDUCTION-MEMORY-VALIDATION-R18)、[R22](实施记录/QY-004-ATTRIBUTION-WIRING-AND-REGISTERED-BUILD-RETEST-R22)、[R23](实施记录/QY-004-ARTWORK-RELEASE-NORMAL-MEMORY-VALIDATION-R23)。R16四人基础218成功Private +2807.340MiB；R18 220成功+163.523MiB。随机路径不同，不作精确AB百分比。R22企业家224成功+448.895MiB、Heap+14MiB，两个无新图50次段+0.930/+5.215MiB。R23主120/次40成功，主段+363.492MiB；两次离局Private下降455.641/390.504MiB，菜单基线+2.258MiB；157保存完整读取并自然退出。活局停手不清图，WorkingSet不等幅回落，不承诺任意时长/配置无增长。
- 暂停局：[R30原记录](实施记录/QY-019-MAP-INPUT-FIX-FOUR-PEER-RECOVERY-R30)，GameId `1d6fdd42570641779eabf9afabad210b`，恢复包 `Builds/QY017-R57-20261025-map-input-recovery`。正常地图分支已有证据，付款成功后旧请求等待使夹具误停；整局及全面根复核未完成。不得恢复、代跑或把旧内容与新Runtime混用。
- 本轮发布由017写入 `实施记录/QY-017-RELEASE-20261028`，包SHA/内容身份/构建及安装检查仅采用其最终交接；本次文档整理不查看或移动在途发布记录，不提前填“构建成功”。

未完成与暂缓事项集中在[04待办](04-高风险语义与回归清单.md)。原始验收记录中的失败、不覆盖和预算偏差继续有效，归档不会把它们改成通过。
''')
    write(BASE/'04-高风险语义与回归清单.md','''# 待移除与未完成事项

更新：2026-10-28。用户本轮要求**登记**下列五类待移除内容；本轮没有实施这些删除，也没有修改生产源码、测试或资产。下述检索是静态定位，不是运行验收。

| 编号 | 当前事实与精确入口 | 后续处理及完成判据 |
| --- | --- | --- |
| REMOVE-01 旧次数式Effect.Repeat | 12脚本13处内容调用此前已展开；本轮`core/lua`精确检索Effect.Repeat为0。仍有`Assets/YC/Domain/Effects/RepeatEffectExecutor.cs`、GenericLuaEffectExecutor注册、LuaEffectSpecCompiler与MoonSharpLuaRuntimeHost编译校验，以及测试合成脚本和`presentation/interaction.repeat.lua` | 核引擎注册/类型ID、序列化与旧档恢复、表现及测试文档调用后再退役；收尾来源once/unbounded是另一机制，不能一并删除；内容零字符串不等于内核残留已清 |
| REMOVE-02 无卡面候选多选显示链路 | 多萝西已改普通组合执行选择。`EffectDialogPrimitives.cs`的AddSelection仍有无卡面CreateOptionRow/SetSelected分支，供CharacterCardEffectChoiceDialog与FacilityEffectChoiceDialog等通用宿主消费；同一模板还用于普通选项/支付来源，不能整模板删。诗怀雅计谋仍有0—2对手Choice，应落玩家页，标记返供为0—1单选；这不是已证明仍走旧多选页 | 建立“真实调用→目标语义→Min/Max→是否有卡面/玩家页→替代UI”完整表，覆盖动态Lua/内容及保存待答；确认其他合法消费者已迁移后才拆。当前不能宣称其余调用为0；保卡牌、玩家多选与普通执行选择 |
| REMOVE-03 Effect.LoadCardMarkers残留 | **005 R17/R18已正式退役API**。当前生产Domain/Infrastructure/core内容中精确LoadCardMarkers及旧名变体检索无命中；`Tests/EditMode/ExpansionRules/CleanupSourceTestFixture.cs:117`仍构造旧调用，另有QY005退役反例检查 | 清理或迁移失效夹具与旧文档用法；保“拒绝旧API”的有意义负例及版本拒绝证据。复查构造器/编译器/权限/类型及新旧内容身份，不能写成仍活跃API或重新包装复活 |
| REMOVE-04 企业家未实际显示文本 | 企业家增量JSON、基础texts、Lua Text.Ref、拼接键、展示脚本、正式UI消费者共同决定是否可达；静态rg零命中不足以判无用。用户有人工改文案；多萝西removal.prompt/removal.pair是本轮新增正式键 | 按每键记录来源、动态拼接/JSON引用、实际UI消费者及不可达证明，再最小删除；不删尚未正常抽到的内容，不把“暂不显示快速完整正文/长正文暂缓”自动等同文本无用途，保用户手改 |
| REMOVE-05 插入结算按钮悬浮文字链路 | 2026-10-25最终裁定只关闭文字显示。当前SettlementEffectChipView.OnPointerEnter/Exit仅改hover并RefreshState，类内静态检索无Tooltip字符串；共享提示组件、资产序列化引用及历史无效字段尚需完整核对 | 只审计和清理该按钮的文字提示入口/绑定/无效配置，不删hover视觉、背景图标颜色切换、点击/拖动/禁用态、检查详情或无障碍名称。不能扩大为全局Tooltip清理 |

REMOVE-02静态已见的真实通用入口：`CharacterCardEffectChoiceDialog.ShowSelection`、`FacilityEffectChoiceDialog.ShowSelection`→`EffectDialogShell.AddSelection`；另有CharacterHandPanel、EntrepreneurSelectionUiCoordinator、CardCollectionBrowser及GameplayInformationPages共用选择壳。需按真实spec分类，不能把全部AddSelection都算无卡面多选。多萝西的`effect.choice.execute`明确要求1选1；将合法两枚组合编码成一项，不是删多选规则校验。

## 仍未完成、暂缓及发布前待办

- 017正常页面覆盖、018最终联机验收、019正常完整局未闭合；019/qy暂停，等待用户明确恢复。已有有限技术检查不替代这些结论。
- 秒级点击/建设显示延迟、地图显示至结束行动可用的等待仍在；未进一步改写的整图复制是独立性能待办。内存改善仅限03所列样本。
- R23精确图像路径/slice/尺寸/UV绑定、次局HUD Logo全覆盖、部分测量预算与资源出售收束不足保留；诊断单行修正只完成编译，不能追认旧样本新诊断已测。
- 长正文溢出/容量用户暂缓；固定行高和快速行仅名称/禁用原因继续，不借本轮文档整理修UI。
- 多萝西组合选择未正常界面验收，旧tokens_selected待答兼容未自动迁移。数量条目2 PPU/加减1.2倍由017本轮新包纳入，构建成功本身不等于显示验收。
- 新安装包与提交待017最终交接：核准确包/内容身份、构建结果、已知限制，再以[Git范围](Git提交范围.md)精确暂存；保在途和用户新增变化，不用git add -A，不把旧清理归档或存档上传。

证据：[原165内容Repeat迁移](历史/20261028整理前/文档/165-正式Lua弃用Effect.Repeat并展开多个effect.md)、[原181 Load正式退役](历史/20261028整理前/文档/181-LoadCardMarkers正式退役与逐枚移动整合.md)、[原154悬浮文字纠正](历史/20261028整理前/文档/154-移除插入结算按钮悬停态.md)、[最新状态](03-任务清单与依赖.md)。静态检索范围为项目Assets/YC及正式core内容，未读受限UI原档案，未运行Unity、Player或测试。
''')
    redirects={
     '02-共同约束与架构归属.md':('共同约束入口','01-依据审校与差异处置.md','实施准则、职责分层、通用不变量和权限现集中维护于01。'),
     '05-页面链路与素材边界.md':('页面链路入口','01-依据审校与差异处置.md','当前页面纠正、文案、布局与素材边界集中维护于01；原U01—U22表供历史追溯，不恢复过时模式门禁。'),
     '06-验收计划与记录模板.md':('验收边界入口','01-依据审校与差异处置.md','正常多局、固定包、真实阻塞与中断、恢复和性能口径集中维护于01；当前缺口见04，019仍暂停。'),
     '07-完整覆盖核验.md':('覆盖状态入口','03-任务清单与依赖.md','原规划条目覆盖不代表功能验收；当前19项状态以03为准，历史逐条来源仍在覆盖目录。')}
    for name,(title,target,note) in redirects.items():write(BASE/name,f'# {title}\n\n{note}\n\n[当前唯一要求]({target}) · [整理前原文](历史/20261028整理前/文档/{name}) · [总览](README.md)\n\n本页仅兼容旧编号引用，不单独维护第二套要求。')
    write(BASE/'08-协调台账与派发记录.md','''# 当前协调与发布交接

更新：2026-10-28。长篇派发流水已完整归入[历史08](历史/20261028整理前/文档/08-协调台账与派发记录.md)。本页只维护当前安排，状态明细统一见[03](03-任务清单与依赖.md)，裁定统一见[01](01-依据审校与差异处置.md)，待办统一见[04](04-高风险语义与回归清单.md)。

本轮用户要求：先报告001—019完成情况，再精简合并工作文档、构建新安装包、上传txym分支，记录五类待移除内容。根已报告真实状态；新授权覆盖旧轮“不打包/不提交”，但没有恢复019或授权执行五项删除。

| 工作 | 执行/写入边界 | 当前结果 |
| --- | --- | --- |
| 文档整理与Git清单 | 本整理任务仅文档及清单；不改源码/资产/测试、不暂存提交推送 | 历史封存、现行入口与待办已整理；完整性见整理记录/20261028 |
| 新安装包 | 017独占Unity；发布记录固定`实施记录/QY-017-RELEASE-20261028` | 等017整轮最终交接，不读在途产物、不预报构建/安装成功 |
| 上传Git | 根在包完成后另行派后继提交；现有`txym`跟踪`origin/txym` | 本整理任务只fetch/核差异与精确路径清单；提交推送未执行 |
| 019/qy | 保R30原档、R57恢复包和首次证据 | 用户暂停，不代跑 |
| 五类旧内容 | 只登记真实调用、迁移前提和边界 | 见04；未实施删除 |

Git读取时`HEAD`与`origin/txym`同为`1bc3a5e3b3684659225cb9e255c836e8b6bd4020`，ahead/behind 0/0；origin/main另有更新，不在本轮擅自合并。具体候选及检查时间见[Git范围](Git提交范围.md)。017完成后需更新准确包身份与本轮状态，不能沿用旧R67或R23作为这次新包。
''')
    write(HIST/'README.md','''# 2026-10-28整理前历史归档

这里是历史资料，不是现行派发授权。[当前总览](../../README.md)是入口。

- `文档/`：239份旧顶层Markdown，以及原任务定义和6份协调等待JSON。Markdown加历史标识并修正链接；JSON保原字节。
- [原始字节快照](原始字节快照.zip)：整理前246份文件的完整原字节，不包含海量实施记录。
- [原路径映射](原路径映射.json)：逐项绝对源/目标、原SHA256、快照条目、浏览版SHA及链接调整数。
- [执行记录](../../整理记录/20261028)：移动前清单、原生PowerShell移动结果、完整性与链接检查、Git准备清单。

原01/02/05/06互有历史修订；只查看当时证据，不据旧停止、旧模式门禁、旧API或旧悬停视觉要求开工。历史原文里的代码/日志路径保持取证意义；清理轮次已封存的旧测试输入须按各清理恢复索引恢复，不冒充仍在原路径。

实施记录、唯一存档、日志首证、依据快照及其他来源目录未移动未修改。其原文中指向旧顶层09—238的绝对引用通过本映射定位；保留原证据字节优先于改写证据正文。
''')

def verify():
    rows=read(HIST/'原路径映射.json');bad=[]
    with zipfile.ZipFile(HIST/'原始字节快照.zip') as z:
        for r in rows:
            if sha(z.read(r['snapshotEntry']))!=r['sha256']:bad.append(dict(path=r['source'],reason='原字节快照不符'))
            if sha(Path(r['destination']).read_bytes())!=r.get('navigableSha256',r['sha256']):bad.append(dict(path=r['destination'],reason='浏览版不符'))
    tasks=read(BASE/'任务定义.json')
    if len(tasks)!=19 or len({t['id'] for t in tasks})!=19:bad.append(dict(reason='任务数量/ID异常'))
    old_tasks=read(DOC/'任务定义.json')
    if [(t['id'],t['title'],t['deps']) for t in tasks]!=[(t['id'],t['title'],t['deps']) for t in old_tasks]:bad.append(dict(reason='原任务ID/标题/依赖兼容变化'))
    for t in tasks:
        for target in t['evidence']:
            if 'UI素材' in target or not (BASE/target).exists():bad.append(dict(reason='任务证据入口不完整',task=t['id'],target=target))
    new_files=sorted(BASE.glob('*.md')); broken=[]; historical_broken=[]; restricted=0
    for path in [*new_files,HIST/'README.md',*DOC.glob('*.md')]:
        for m in LINK.finditer(path.read_text(encoding='utf-8-sig')):
            url=urllib.parse.unquote(m['url'].strip('<>')).partition('#')[0]
            if not url or re.match(r'^(https?|mailto|plugin|codex|file):',url):continue
            if 'UI素材' in url:restricted+=1;continue
            dest=Path(os.path.abspath(path.parent/url))
            if dest.is_relative_to(ROOT) and not dest.exists():
                row=dict(source=str(path),target=url)
                (historical_broken if path.parent==DOC else broken).append(row)
    result=dict(originalFiles=len(rows),originalMarkdown=sum(Path(r['source']).suffix=='.md' for r in rows),currentTopMarkdown=len(new_files),allOriginalBytesVerified=not bad,integrityFailures=bad,currentBrokenLinks=broken,historicalMissingTargets=historical_broken,restrictedLinksNotAccessed=restricted,tasks=19,implementationRecordsMovedOrEdited=False,rawArchiveAccessed=False)
    save(OUT/'整理完整性与链接核验.json',result)
    print(json.dumps({k:v for k,v in result.items() if k!='historicalMissingTargets'},ensure_ascii=False,indent=2));print('历史缺失目标数量:',len(historical_broken))
    if bad or broken:raise RuntimeError('文档核验尚有差异')

if __name__=='__main__':{'prepare':prepare,'finish':finish,'verify':verify}[sys.argv[1]]()
