> 历史记录（整理前原文）。不作为现行派发授权；当前要求见 [现行总览](../../../README.md)。本页链接已按新位置调整，原始字节见 [原始快照](../原始字节快照.zip)。

# Lua内容并行实施边界（正文轮定稿）

2026-10-21，协调者定稿。17 V2最终同源927/927及18增量文案/icon已审查接受，公共机制前置满足，007停止写入。**每位执行者收到协调者本次明确派发后，开始本人正文轮；未收到者不能因本文件存在而启动。** 本轮仅012—016各自独占JSON/Lua、文案及必要测试源码并行，公共代码与清单冻结，不各自运行Unity。G0保持到内容、页面及全局恢复/联机条件全部满足。

## 已核对的文件归属

仅从五份已结束的第1轮准备报告提取计划路径；没有读取007运行中的源码或产物。012、013、014、015、016分别为10、6、6、21、12个JSON/Lua文件，共55个；统一分隔符并忽略大小写后跨任务重叠为0。这是拟写入边界核验，不是文件已创建、内容已实现或接口已可用的证据。

以下列出完整路径。本轮只允许本人列出的JSON/Lua、下方独占文案、必要独占测试及对应meta；既有定义须保留004实体、候选、选面、组件、立即处理器身份等已交付事实，不重做设置或额外发初始组件。共享目录meta已由007建立并冻结，不能由多名执行者同时生成。

### QY-012

- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.ability.T09.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.T.1.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.T.3.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.T.5.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.T.7.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/fifth_number_response.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/fifth_number_cleanup.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/fifth_number_round_primary.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/fifth_number_round_secondary.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/fifth_number_final.lua`

### QY-013

- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.ability.A01.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.A.1.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.A.3.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.A.5.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.A.7.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/agnes.lua`

### QY-014

- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.ability.D09.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.D.1.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.D.3.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.D.5.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.D.7.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/tiago.lua`

### QY-015

- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.ability.P09.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.P.1.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.P.3.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.P.5.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.P.7.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.place_rewrite.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.departure_replenish.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p01.quick.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p02.quick.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p03_p04.immediate.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p03_p04.cleanup.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p03.quick.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p04.collection_free.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p05.immediate.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p06.immediate.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p05_p06.quick.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p05.influence_delta.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p06.city_destination.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p07.resource_profile.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p08.region_value.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/paul.p08.final_bonus.lua`

### QY-016

- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.ability.M13.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.M.1.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.M.3.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.M.5.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/entrepreneur/entrepreneur.round.M.7.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/facility/entrepreneur.facility.M02.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/facility/entrepreneur.facility.M03.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/facility/entrepreneur.facility.M04.json`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/martin_ability.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/martin_rounds.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/facilities/martin_urbanization.lua`
- `G:/NoMadCity/Assets/StreamingAssets/Content/core/lua/entrepreneurs/martin_award_grant.lua`

## 共同冻结范围

### 独占测试文件

以下是正文轮的测试文件边界，全部位于`G:/NoMadCity/Assets/YC/Tests/EditMode/ExpansionRules/`。2026-10-21正式派发前协调者再次核这18个精确.cs及.meta路径，全部尚不存在，与公共测试无交集。012/013/015按职责定名，014沿R1两测试类并预留本人夹具，016沿本人R1六文件。需要调整名称先登记边界，不覆盖已有文件。

| 执行者 | 独占文件 |
| --- | --- |
| 012 | FifthNumberContentTests.cs；FifthNumberContentRecoveryTests.cs；FifthNumberContentTestFixture.cs |
| 013 | AgnesContentTests.cs；AgnesContentRecoveryTests.cs；AgnesContentTestFixture.cs |
| 014 | TiagoInvestmentLuaTests.cs；TiagoInvestmentHostRecoveryTests.cs；TiagoInvestmentTestFixture.cs |
| 015 | PaulContentTests.cs；PaulContentRecoveryTests.cs；PaulContentTestFixture.cs |
| 016 | MartinContentDeclarationTests.cs；MartinBlueprintActionTests.cs；MartinUrbanizationEffectTests.cs；MartinAwardAndScoringTests.cs；MartinContentRecoveryTests.cs；MartinContentTestFixture.cs |

只按实际必要程度创建这些文件，不以凑齐文件数为目标。本人夹具复用既有IsolatedTestArtifacts和真实Host装配，不能复制一套并行输出框架、写C#假卡面或修改共用辅助。具体正例/拒绝/恢复矩阵沿本人已审R1责任和届时最终合同。

`core/lua/entrepreneurs`目录及meta已由007单一创建，GUID为01b01d9a1c4a49f198df7987c29d3dc5，meta SHA为689de9eecc2fb62c56e8d87b02a37fc424686a1c4b743ccba833d6aba8ed0002；当前.gitkeep为空壳保留。`ExpansionRules.meta`及独立文案目录meta均已存在。各作者仅负责自己新增文件对应meta，已有GUID保持，不重建父目录meta。

- 本轮计划仅为正式JSON/Lua正文、各自独占的必要测试源码和本人新一轮实施记录。测试验证真实声明、Lua与正式Host组合、恢复及实质边界，不用C#假处理器替代卡面，不写锁定静态文案/样式的测试。
- 公共C#内核、宿主、schema、pack登记、全局机制指纹、共享测试辅助代码、其他任务文件在内容并行期保持冻结。若实际正式卡面暴露通用缺口，列出精确接口、真实调用及最小必要修正，继续独立内容；由协调者在写入者停止后指定单一整合者处理。
- 所有卡面条件、数字、费用组合、修正和顺序仍由Lua承担。公共能力不支持时不得按卡名写C#、私改状态、伪造调用权限或吞掉效果；不得擅自照准备报告中的拟接口造同名实现。
- 共享pack/脚本依赖登记由之后指定的一位整合者落地。每人交付精确路径、入口、版本、完整脚本SHA、声明权限及依赖，不并行改共享清单；不运行任意require/loadfile/dofile。
- 所有写入者结束后，才由明确指定的一位运行Unity编译和必要组合测试。并行内容作者不得各自启动编译、Unity、构建或测试进程，不读取其他执行者运行中产物。
- 不访问受限UI原档案，不改场景/预制体、静态界面或017用途登记；不删除、不改真实用户存档、不提交发布、不创建任务/子agent。每人完整提示词、01裁定、02约束、逐条责任和根AGENTS继续有效。

## 内容文本交付

2026-10-20用户询问012—016是否包含文本工作，协调者核对012/013/014准备报告后明确：游戏内本企业家所需的内容文本属于实装交付。每人随正式内容交付必要的中文效果说明、选择项文字、提示、文本键及引用，逐项对齐真实卡面、当前裁定和实际Lua效果；已有正确名称/文案复用，不为交付量新增不必要文字。

共享 `Assets/StreamingAssets/Content/core/presentation/texts.zh-CN.json` 已由用户通知暂定完成并追加修改，不再挂旧“等待编辑完成”前置；现有人工文字必须保留。17曾另行允许统一运行者修复一个缺失的加粗闭合字符，该定点修复已经完成，不构成自由改文案许可。用户进一步明确企业家文本使用额外JSON、面向DIY支持外部增量及自定义icon；因此撤销旧“把企业家新增键汇入基础texts”安排。每人应按18已经交付的最终作者合同及下方独占路径交文案JSON及所需图标声明，并列参数类型、调用位置、所处请求/页面和来源依据。基础文件不添加企业家文本；公共包清单仍由唯一整合者登记。017负责既定页面消费和实际显示核对，不承担替各内容任务猜测规则文字的工作。

完整卡图内已印文字沿用用户批准的现行原图，不重画、不重复生成替代图。场景/预制体中的静态标题、按钮名和说明继续以资产为准；本条不授权内容作者覆盖这些资产或在控制器中硬编码固定文案。多状态文案使用既有检查器配置或专门文案资产；共享登记和实际显示测试归单写入整合/017阶段。

转译模板的加粗使用 `[b]文字[/b]`，成对闭合；不是Markdown或HTML语法。2026-10-20协调者核对GameplayTextResolver与006 a25基准SHA一致后只读确认实际解析分支。实施前重读当前源文，核对用户后续人工校订与第1轮摘录的差异，不把旧准备报告全文当作最终文案。

## 需要保留的已审查责任边界

- 012：T09初始2枚已由004交付；这里只补真实离场独立收容、每付3枚必选兑换、所有立即和特殊终局。T03无目标不吞条件外Gain；两立即和两个Place保留独立身份。
- 013：A05使用更正为1指令的收尾图/规则，A01快速入口仍2。保留共用指令来源/轨道及真实批次去重；A06数量上限按007整合实证合同处理，不截量。A08已成立收尾义务不能因来源退出消失。
- 014：投资、声望及八面全部组合按第1轮已审方案在Lua实现；采用真实保存随机、合法候选和付款，不创建企业家专属C#骰子/次数系统。
- 015：使用第1轮补正1。严格移除短targets应进入真实左式失败，不能禁整条行动；P04免费集合/P06全玩家城市禁入/P07完整资源profile均消费已冻结通用查询。帮派组件和配方已交付，本轮不重写。
- 016：M13按U20落位计分→必做移除及响应→入场；本人后对手在真实授权执行对象中复用冻结Lua正文。M01替代付款声明归007，本人只按真实定向发放接口实施M09/M10授予；追加塔/初始图纸配方已有004。基础逐塔Lua与公共层结构不并行重写。M05/M06沿U09；017映射误写U08的文档更正归017后续。

## 实际冻结交付与本轮完成条件

### 18已交付的独占文案路径

2026-10-21，18的加载/显示机制与实际67项、PRESENTATION-I01测试补正及单项复验均已审查接受。以下五文件及meta已由007单一创建并在pack.json登记；当前messages为空，不能把空文件视为正文完成。后续作者只写本人文件，不回填基础texts或改共享manifest；已有meta GUID保持。

| 作者 | 正式文案文件 | 新键前缀 |
| --- | --- | --- |
| 012 | G:/NoMadCity/Assets/StreamingAssets/Content/core/presentation/entrepreneurs/T.zh-CN.json | core:entrepreneur.T. |
| 013 | G:/NoMadCity/Assets/StreamingAssets/Content/core/presentation/entrepreneurs/A.zh-CN.json | core:entrepreneur.A. |
| 014 | G:/NoMadCity/Assets/StreamingAssets/Content/core/presentation/entrepreneurs/D.zh-CN.json | core:entrepreneur.D. |
| 015 | G:/NoMadCity/Assets/StreamingAssets/Content/core/presentation/entrepreneurs/P.zh-CN.json | core:entrepreneur.P. |
| 016 | G:/NoMadCity/Assets/StreamingAssets/Content/core/presentation/entrepreneurs/M.zh-CN.json | core:entrepreneur.M. |

真实schema、图标/图片归属、参数及启动清单见实施记录/QY-007-PRESENTATION-R1/最终作者合同.md。新文件可以含本包自有icons/assets；没有新增图片需要时复用已授权语义的既有图标ID即可，不为展示新能力而制造图片需求。若实际卡面需要新资源，先按可用库要求列精确用途和文件边界，不能越权借图或改静态界面。

### 实际接口与本轮交付

公共实际接口以实施记录/QY-007-B2-V2/作者接口合同.md（SHA 3ff7e2e35ca81cab53afa2291c7ac2fb2a15094996713a1cb3f2e979eea8485c）为准；读取其引用的18最终作者合同。V2最终交付清单与证据索引.json的SHA为3176df150af76f2e166629c0a4630201b39ddf3ab11a1df3e2bd4c15ab20e25a，最终文件清单与SHA.json为c60d6c2ab88ea15e105b14b3ec1c900c1a57409c20ec095ba7805c1dffc62a21。实际代码指纹b5d8cbb42a13fcd3f9f92d0faa03bb0534ec9d12ad0579a90e547f9673cc9458，正文前内容树5338a6fd5e881e8d59e1ace9b8581f37a5762937de7b6c1f3632c0a124d1a5f4；两者不是活动内容hash。17末节及协调复核两份JSON给出独立接受证据。

1. 每人从本人已审R1（015含补正1）继续，重读当前裁定、卡面依据、原提示词和完整逐条责任；完成本企业家全部正式Lua/JSON与必要中文文案，不再停留准备稿。旧拟API、旧前置未完状态以本次真实冻结合同替换。保留既有实体/选面/设置身份，禁止额外设置发放。
2. 只写上述独占文件、对应meta及本人新`实施记录/QY-012-CONTENT-R2`等目录。必要测试源码覆盖本人真实正文、选择/条件/顺序/恢复、有效来源和与通用机制的实质边界；复用现有隔离辅助及真实装配，不靠C#假卡效果。**本轮不启动编译、Unity、测试或构建**；结束后由007单一登记清单与统一验证，届时另派。
3. 新共享manifest、模块和依赖登记以本人目录内的精确登记提案交付：实际入口、版本、脚本原文SHA、模块/依赖、来源许可和引用；不改共享pack/schema/全局能力/G0。为测试制作私有输入替换时也只写本人测试源码，不在共享目录直接落临时包。
4. 按来源编号逐条回填实际位置、效果/文案/参数依据、实现状态、必要用例与预期结果；未运行明确标记待统一验证。交完整文件清单、before/after SHA、差异、文案键与参数、用户需手工编辑的位置及剩余依赖。真实公共缺口列精确调用和最小建议，继续可独立正文，不私改C#或截量绕过。
5. 正文与测试源码实际交付后结束，等待协调者整轮审查；不能用“已就位待命”代替已授权工作。压缩后读取本文件与本人最新进度，从实际未完项继续。只有真正依赖新增用户规则/页面裁定的部分暂停，已有RK/J-Q不重问。
6. 五位全部结束并完成静态审查后，再派007单一整合/必要验证；017实际页面、018全局/G1、019正常多局仍另行派发。当前927/927只能证明正文前公共组合，不能借它宣称随后新增内容已验收。

2026-10-21，协调者已分别向012、013、014、015、016原聊天发送CONTENT-R2正式实施指令，五条消息均送达。本轮五位并行写入权仅限本文各自明确文件，其他执行者保持停止；各自完成真实交付后结束，协调者在结束后读取输出。


## 2026-10-21 CONTENT-R2完成后的当前边界

以上空文案、文件尚不存在与正文前V2指纹都是原派发时点的历史记录。012—016的CONTENT-R2/I01均已交付并完成限定静态审查，当前五份独立文案已有31/42/33/38/34键；尚不表示运行验收完成。公共整合按19推进，007已交CONTENT-INTEGRATION-R1 a029，当前由协调者与002/008/010作只读审查。所有正文作者及007保持停止写入；待审查闭合后，协调者另行明确派发回填，不能仅因读到新接口合同自行启动。当前任务状态以08最新记录和明确消息为准；旧公共指纹不再作为当前源码断言。

## CONTENT-R3当前派发

007公共补正已通过协调审查，013—016已收到21-公共补正后正文回填.md全文及各自完整原提示词，开始本人独占回填。最新实际边界与派发前SHA见实施记录/CONTENT-R3-派发前边界.json；本轮写入/禁止运行范围以21为准。012及007保持停止，待全部作者结束审查后另派单一整合；旧阶段的“所有作者停止”仅为历史记录。


## CONTENT-R3结束后的当前状态

2026-10-21，013—016均已结束并经协调者限定静态审查接受；012—016现在全部停止写入。正式登记/编译/规则及恢复运行仍待007按22单独实施，不能借I01公共147项宣称正文通过。当前五份独立文案键数T/A/D/P/M=31/42/33/38/40；M原34对象保留、新增6。以上旧“运行中”“空文案”等为历史时点记录。最终状态以08与明确派发消息为准。
