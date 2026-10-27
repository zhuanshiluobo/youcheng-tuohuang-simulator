> 历史记录（整理前原文）。不作为现行派发授权；当前要求见 [现行总览](../../../README.md)。本页链接已按新位置调整，原始字节见 [原始快照](../原始字节快照.zip)。

# token调用端交付后的三路收口

2026-10-22。008 R2已整轮结束并停止写入，最终见实施记录/QY-008-TOKEN-CONTRACT-CONSUMPTION-R2。协调核62次当前源码/证据/开工快照指纹一致；4份快照text因规范化换行需复原CRLF（其中1份BOM），当前源按字节核同。18生产文件及两测试源码已接；31声明用例未运行。57七路均已结束，58的018登记轮正运行。

此轮是按已完成真实合同做有限整合与受影响测试迁移；三个作者可并行，不新增用户审批。卡牌与token合同分开、token不等于Cube、所有角色/企业家效果Lua、必要异质选择保留等01/56要求继续。

## 018：继续当前登记轮，追加两个基础设施接点

完整读取008的完成报告.md、共享登记与接口对照.json、最终来源SHA.json以及未应用-跨界费用投影与界面.patch。008全部文件停止写入，允许按最终字节消费，不再视作运行中合同。

- 在58 pack登记范围之外，本轮明确移交两个C#文件给018单写：Assets/YC/Infrastructure/Lua/ActiveContentCatalogBuilder.cs、SettlementHostCatalog.cs。011已结束，构建器必须以其最终token模型为基线，不回退类型/配方校验。
- 按008真实符号将CostReceiptPolicyVersion=cost_operation_receipts.v2、TokenCostContractVersion=resource_token_cost.v1、PayChoice1.1.0、ItemTransfer3.0.0及token协议/摘要纳入正式内容指纹。Action1.1.0和资源Pay3.1.0原消费保留；不无依据提升Host/Room等全局协议，不新增未被消费常量。
- 按未应用小补丁的SettlementHostCatalog部分，付款窗口在真实payment来源失效时markerCount=0，不回退任意卡上Cube总数；保持storageZoneId和现有字段，不能给tactic授transfer权限。
- 可新增本轮独占的小型定向测试源码验证真实失效payment投影及正式身份，不改其它作者测试，不启动Unity/编译器/游戏。若现有测试已覆盖，不为镜像实现再加重复测试。
- 008有效旧源兼容已经保留，007角色Lua无需因这些接点再造字段。008的TOKEN-WAIT-01已源码修正，006原正例原样等待运行。
- 共享pack/模块/版本仍只你写。完成后交实际diff/SHA和正式身份消费位置，明确尚未运行；结束本轮后协调再放017统一运行。

## 007：受影响旧资源付款测试迁移

角色R2已接受并停止写入，当前多萝西/异客/诗怀雅源码不再重做。本轮只独占以下6个既有测试文件：

- Assets/YC/Tests/EditMode/ExpansionRules/MultiSourceResourcePaymentTests.cs
- Assets/YC/Tests/EditMode/ExpansionRules/MultiSourceResourcePaymentTests.Ui.cs
- Assets/YC/Tests/EditMode/ExpansionRules/MultiSourceResourcePaymentTests.Channels.cs
- Assets/YC/Tests/EditMode/ExpansionRules/UnifiedResourcePaymentTests.cs
- Assets/YC/Tests/EditMode/EffectDeckBuildPaymentTests.cs
- Assets/YC/Tests/EditMode/GameplayPresentationContractTests.cs

按008最终合同把旧carrier.pay、旧v1/SelectedSubstitution及无真实ruleQuery目录的夹具迁到真实payment来源/new transfer许可/v2草稿和逐枚费用收据。保所有仍有效的正例语义：多来源同payment额度、整笔全零失败、草稿编辑/撤回/失效、资源扣款与足额token、不重付/恢复/来源权限、真实选择和UI投影。不要删除/Ignore/改恒真、宽catch、假造source/receipt、修改原件位置或自动补答旧付款页让测试过。

若旧断言本身已被用户明确撤销（旧付款选择页、等价ID强制选择等），改为验证现行用户期望并准确列出依据，不把旧失效接口当生产应恢复的功能。设施/建造/合计资源付款仍原职责，不能将其因为名称含payment一起删掉。未受影响方法保持；只改必要调用/夹具/类型引用，不全仓重写。MarkerPlans兼容别名不作为新测试正确性凭据，改验真实TokenPlans及请求/收据；不写008生产文件。

可加专属本轮夹具/测试仅在上述文件确需的最小范围；已有meta保留。不写006的9文件、自己的角色R2源/测试、企业家测试、017 PlayMode或018共享文件。若发现生产缺口，在最终报告交真实测试位置和最小期望，不能弱化断言。未运行就写未运行；本轮不启动Unity或构建。

输出实施记录/QY-007-RESOURCE-TEST-MIGRATION-R1，交前后字节SHA、精确diff、保留/调整方法清单及依据、供017受影响过滤器，结束并停止写入。

## 017：既有资源分配页的token消费接线

继续本任务已结束UI-DRIVER-PREP-R2，保留原用户反馈和已修多选原位刷新、详情返回、五张企业家切换、快速行动驱动、结算条、静态素材权威及首次失败原件。

- 读取008最终未应用补丁的PaymentSourceUiCoordinator部分，在该文件按实际当前代码完成既有付款分配编辑器对等价/唯一token组合的处理。008源事实提供SelectionGroupId、SelectionPolicy等真实字段；先读齐全部分页，仅唯一组合或规则允许且全部规则事实等价时自动确定真实实例，隐藏无意义逐ID选择；异质有意义组合仍保留。库存不足不能自动成功，分配变化/草稿变更/返回再进不得沿用旧选择。
- 此处是正式资源分配页的实例消费，不能为企业家快速战术另留一层选指令页面。快速/收尾实际token转移由Lua一次api3直接执行，后续进入有规则依据的正文选择；必要Condition执行/放弃不一刀切删除。
- 只写Presentation/PaymentSourceUiCoordinator.cs及本人必要独占测试/证据，保既有场景Prefab/静态文字，不Rebuild、不新增页、不按卡名做专路由。018负责SettlementHostCatalog和全局身份，007负责旧资源6测试；不得争写。
- 本轮可完成源码与有意义的定向案例准备，先结束交付；暂不启动整包编译/Unity/Player。018/007结束后，协调立即给你完整稳定输入开展唯一最终编译、受影响机制/Lua/Host/PlayMode与性能测量及构建。不能把旧s009或旧a001包当本轮新版。
- 最终性能验证须记录打开宣告、打开详情、快速行动入口、选项点击、确认和下一真实请求的耗时与分段；按实际生产链区分查询/校验/投影/重建/I/O，不靠删除合法校验或跳过Lua掩盖。当前先保必要测量接点，不预称卡顿已修。
- 019明确终止的正常局仍停止，不为本轮代码接线自行新开正常游戏。独立隔离结果也不称正常完整局。

输出实施记录/QY-017-TOKEN-UI-CONSUMPTION-R3，交源码diff/最终SHA、测试过滤器、实际未运行范围及剩余接点，完成后停止写入。用户之前允许处置本轮自启动且已结果完整/身份确认的退出挂起隐藏Unity，授权仍保留，但本轮代码准备没有启动需求。

## 协调和最终运行

各作者按当前真实工作区为基线，其他结束交付的SHA是取证身份，不拿它回退已授权新改动。根协调只写文档，继续wait保存游标，整轮结束后才读最终交付；不逐条响应运行中输出。018/007/017源码收口后，017单一编译运行，按实际错误分给准确作者，修复与独立测试不串行等无关项。所有源码用例数和静态核验不冒称已运行；旧日志及失败现场保留，不新增删除授权。

