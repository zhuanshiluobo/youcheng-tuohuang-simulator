> 历史记录（整理前原文）。不作为现行派发授权；当前要求见 [现行总览](../../../README.md)。本页链接已按新位置调整，原始字节见 [原始快照](../原始字节快照.zip)。

# 通用token合同交接与七路续接

2026-10-22。005、008、007、012、013、014与017首轮均已整轮结束；协调逐项核交付来源，005当前19文件38次SHA核对一致。此处批准的是基于实际源码合同继续接线，不将静态检查当编译、运行或正常界面验收。原55/56范围在本文件明确调整；01最新用户裁定始终优先。

## 必须消费的实际合同

唯一合同：实施记录/QY-005-EQUIVALENT-MARKER-PAYMENT-R1/55-56-token接口合同.md，配套55-56-交付说明.md及after-55-56.json。Effect.MoveItem api3、执行器3.0.0，itemIds/expectedSource/destination/insertion/operationProfileId/requiredCount，来源声明carrier.transfer.*；SourceData.GetTransferableTokens完整分页及transferableTokenCount；SourceFacts版本v1.4.0.0。示例和边界必须读原合同，不从本摘要猜参数。

用户在005直接明确“token不一定是方块类的，比如嘉维尔指示物、岩蹄安保终身合作标记也是token”（消息01a162df-2f51-7b81-a04b-0bb10177a2e3）。不得继续把token限定为Cube，也不得因旧模型把岩蹄登记成Card就否定用户语义。原有地图、轨道、奖励取得等专用生命周期继续适用；通用库存转移不能绕过这些约束。嘉维尔无需为本轮清理改变其正式地图行动。

用户在007关于异质标记的补充按01执行：多萝西允许混合合法Cube，诗怀雅不同原主有真实作用；必要Choice由Lua编排。等价ID不制造多余页，唯一合法组合不强造选择，所有实例仍一笔足额原子转移。卡牌与token权限/生命周期分开，不保留或改名重造标记付款原语。

## 并行作者与独占范围

1. **008：费用调用方与资源替代完整收口。** 继续55的AbilityAction、Condition、Cleanup、ResourcePayment全链，新增独占 ResourcePaymentSourceContracts.cs、ResourcePaymentDraftService.cs 及相关费用调用文件；核实际路径。005已经停止写入，本轮将 ItemTransferBatch.cs、ItemTransferEffectExecutor.cs、CardMarkerPaymentPlanner.cs、CardMarkerCarrier.cs 的必要通用合同扩展交008单写。不得改变已交Lua api3字段、纯查询结构或基础语义，让四家内容继续同一合同。不得改011下列独占文件，真正需要跨界改动提供准确小补丁及理由。
   - 先修005指出的三处：IsTokenTransferOperation改用实际IsTokenProfile覆盖非Cube；按请求token ID匹配PositionChanges，允许保留合法空载体退出的附加位置；api3恢复核TryValidateTokenState，不只看结构收据。
   - 正规资源替代Plan/Draft/DraftHost/Preview/Substitution/Recovery/SourceContracts完成迁移；同一payment多来源费用必须在原整体原子边界内按真实来源执行授权，不能传任意source当能力证明，不能关闭requireCarrierAuthority或复活旧付款Planner。可在获分配的通用转移文件增加可审的来源证明/计划消费合同，C#不得编码角色卡面效果。
   - 保异客三基本资源、payment共享额度2、每单位1标记、原供应与支付前插入事实。等价实例处理不把不同原主合并。与007结束交付对齐；若需改变资源替代内容字段，交准确合同给协调而不抢写Lua。
   - CostReceiptPolicyVersion纳入正式有效身份：在本家调用/版本入口落实必要版本声明，给单一整合者精确注册/指纹接点；不以新增常量当已兼容。ResourcePaymentDraftHost已有外部增量005已声明非本人，按当前真实基线保留，不回滚到旧快照。

2. **012：第五号完成接线及本家旧测试。** fifth_number_cleanup.lua/T09声明与必要版本、自己的测试。改一次3枚api3，返各自原供应；保执行前Condition、付后必选Place/1至纯、同窗独立重复、清空留牌、不生地图离场。按同一真实source/card/localZone获取授权库存，不能用跨来源总数代替。首轮3case及两旧控制按最终合同更新，未运行如实写。

3. **013：艾格尼丝完成接线及本家旧测试。** agnes*.lua/A系定义和AgnesContent*、AgnesInterfaceLuaCleanupTests。迁17项旧carrier.pay、旧payable事实；quick2/A05收尾1、special无quick费、direct省目标cost/after/main但共轨，已付后弃选不退。保首轮A02同来源修复，并将旧合法-2正例补真实scope/sourceKey，不改成错误预期。移除旧测试自动回答payment_items的适配，改验自动转移及正文真实请求；新测试只覆盖必要费用/恢复边界，不无因扩全历史。

4. **014：蒂亚戈完成接线及本家旧测试。** tiago*.lua/D系定义和TiagoInvestment*、TiagoInterfaceLuaCleanupR1Tests。四类声望费用逐一换真实api3及新许可/纯资格；D07 3回盒、D08 2返供、D01/02 3且D05变2、D03额外2不混。保正规投资资源费、真实骰值/合法点数Choice、ctx2与offeredPoints。旧夹具补当前SourceFacts，旧费用辅助不补答多余页。旧ctx1明确拒绝/迁移，不能补造候选或重掷。

5. **007：角色H01/H02续接及本家测试。** 只写角色Lua/定义/资格和角色独占测试，保首轮3设施顶牌修复。多萝西api3+carrier.transfer.*，core.cube许可保持；从授权事实按真实类型/原主/原供应/规则状态决定必要Choice，唯一组合自动，混合实例同一足额事务，付后放置等待必要响应。诗怀雅Choice及Return/Store单实例现有合同保持，不假装其结果是多实例费用收据。异客先按已给许可迁移本家声明，资源替代规则正常保留；若仍需008新字段，先完成可独立接线并在结束交付精确列待接点。新增有意义的混合Cube和不同原主控制，不跑全集。

6. **011：岩蹄终身合作标记的真实token模型补正。** 本轮不是重做终局，依据当前已整合基线，旧G0/未登记报告不代表现状。将实际终身安保持有物从错误Card模型迁到token，保持真实定义、取得、持有上限1、来源持续资格、正常采集和Lua终局公式、公司独立主体、展示与恢复一致。
   - 独占内容：contract/infected.contract.lifetime_security.json及其必要独占Lua/类型/配方文件（不写pack/模块/共享索引）。
   - 独占C#：Domain/Influence/CompanyInfluenceRules.cs（含RewardAcquisitionFlow）、Domain/Items/ItemTypeRegistry.cs、ZoneTemplateRegistry.cs、ItemZoneQuery.cs、ItemZoneStateMigrator.cs、ItemTransferService.cs；Infrastructure/Lua/ExternalContentPack.cs、ActiveContentCatalogBuilder.cs；ContentPrimitiveEffectExecutor.cs仅奖励取得完成核验分支。这些已从005结束轮移交，008不得写，011也不得改008持有的batch/executor/费用文件。所有C#改动为必要类型/持有/奖励通用合同，不把合约卡面公式迁回C#。
   - 可新增本任务专属测试和修改原QY011/合约奖励独占测试；不改006接管的旧付款测试、017界面文件或现成场景Prefab。如确有显示消费者类型过滤问题，完成非UI接线并交具体文件/条件给017，不新造页面。
   - 原始旧存档用独立副本作验证方案，明确兼容迁移或可诊断拒绝，不修改实际用户档。保奖励响应、上限及持有来源，不能只改JSON、把reward_card_transfer放进token白名单或改PhysicalKind掩盖缺链。库存通用core.token_storage不自动授予奖励取得资格。
   - 当前共享配方若只能登记于pack，交单一整合者精确补丁和必需校验；不要在独立测试篡私有manifest伪称正式装配通过。

7. **006：受影响旧通用测试迁移。** 仅测试源码/本人证据，无生产代码、Lua、内容或全局注册写入。独占以下旧文件：
   - Assets/YC/Tests/EditMode/DeferredInfluenceDestinationTests.cs
   - Assets/YC/Tests/EditMode/ExpansionRules/CardMarkerContractTests.cs
   - Assets/YC/Tests/EditMode/ExpansionRules/CleanupSourceRecoveryTests.cs
   - Assets/YC/Tests/EditMode/ExpansionRules/CleanupSourceContractTests.cs
   - Assets/YC/Tests/EditMode/ExpansionRules/CleanupSourceTestFixture.cs
   - Assets/YC/Tests/EditMode/ExpansionRules/ContentEligibilityContractTests.cs
   - Assets/YC/Tests/EditMode/ExpansionRules/G1MechanismRecoveryTests.cs
   - Assets/YC/Tests/EditMode/ExpansionRules/QY006B2MarkerRecoveryTests.cs
   - Assets/YC/Tests/EditMode/ExpansionRules/QY006B2MarkerVersionTests.cs
   保原合法正例、数量/实例/响应/收据/恢复语义，将旧专用付款调用/许可改005真实api3；旧协议负例按明确退役策略保留。不能删测试、Ignore、宽catch、恒真、假来源或补答payment_items凑通过；更新测试来源/文案注释只为准确，不替用户改产品静态文字。若依赖008本轮资源替代的新合同，独立能做的先做，结束交付精确接点；不得读运行中的逐条输出当冻结合同。不写005新测试、各家Agnes/Tiago/Fifth/角色新测试或017 PlayMode测试。后续由017运行，006本轮不启动Unity。

## 统一收口

- 上述七路均立即执行，按真实已结束005合同和各自当前字节接线；不等待无关作者，也不把临时编译/模块SHA不匹配当用户审批问题。不得恢复旧快照。
- 新证据目录：实施记录/QY-编号-TOKEN-CONTRACT-CONSUMPTION-R2；011使用QY-011-LIFETIME-TOKEN-MODEL-R1，006使用QY-006-TOKEN-TEST-MIGRATION-R1。保留首轮原件，交实际diff、最终来源SHA、版本/权限/接口对照、精确共享登记提案、受影响测试过滤器及未运行项。
- 005目前停止写入，接口基础保持。共享pack/模块/依赖与全局版本指纹由最终单一整合者处理；本轮七路不争写。017最终Unity/构建保持唯一运行者，等待完成交付与统一装配；所有当前测试源码数不能当运行通过数。
- 隐藏Unity退出挂起处置已有用户在017限定授权，范围仅017本轮自启动且结果完整/身份核对后的进程；不扩到其它Editor或游戏。019已停止的正常游戏不恢复、不自动开新局。
- 根AGENTS、UI资产权威、受限原档案禁访、全部卡面效果Lua、真实页面歧义先问、正常合法多局验收规则不变。协调只改文档，逐任务保存wait游标并等整轮结束后核验，不按中途输出反复指挥。
