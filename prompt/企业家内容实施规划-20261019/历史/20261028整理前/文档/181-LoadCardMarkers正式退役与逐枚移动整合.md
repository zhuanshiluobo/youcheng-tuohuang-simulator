> 历史记录（整理前原文）。不作为现行派发授权；当前要求见 [现行总览](../../../README.md)。本页链接已按新位置调整，原始字节见 [原始快照](../原始字节快照.zip)。

# LoadCardMarkers正式退役与逐枚移动整合

## 最新依据与前置

执行轮：QY-005 LOAD-MARKERS-ACTUAL-RETIREMENT-R17。176原用户弃用要求继续有效，179最新用户裁定优先；178的G1/G2新设计已撤回，不恢复。013R13、014R12、009R7完整交付由根核后才实际派本轮。根派发时将附前置状态；不要依据本文存在自行开工。

用户最新模型：先结清会影响供应库存的相关响应，再运行Lua生成数量选项；Lua按当前真实库存和原规则判断可选数量；回答后生成若干单枚MoveItem，每枚独立校验，缺失的单枚失败，成功兄弟不回滚。不增供应屏障/整批预留/足额交付专用机制，不把字段换名字保留旧Load。

## 实际写集和清理范围

使用本家R15后继逐文件清单作为定位索引，**其中所有新通用足额交易/新供应等待替代要求都已由179撤销，不能机械执行旧方案**。以当前真实引用为准最小修改：
- Domain/Effects内Load注册/执行/版本升级、专用CardMarkerFlow、CardMarkerPaymentPlanner、FixedMarkerDelivery及仅它们使用的付款/容量/收尾钩子；Domain/Items、Cards、State中仅被退役的专用字段与挂接。
- Infrastructure/Lua内公开构造器、编译器、固定交付透传/专用查询字段/指纹。普通Condition/Choice/MoveItem、CardMarkerCarrier/载体权限、其他Token和卡牌转移、真实费用和资源替代、其它消费者的响应/供应依赖完整保留。
- Application仅HostRecovery.cs里退役专用字段校验/旧合同有限明确拒绝。不写GameSessionRecoveryState.cs、SimulationExecution等008文件；不改旧档，不静默忽略旧非空状态。
- Content仅统一pack版本/精确模块哈希、presentation/defaults.json旧Load映射、无消费者的interaction.store_markers.lua及meta、共享文本中已无消费者的旧Load专属键；逐键改，保用户文案和009新键。三家新Lua/定义原则只读最终结果。若pendingIncomingCount随退役成为无效投影，可精确删除martin_rounds.lua的child_state透传及same_kind对应比较成员，保实际子区/层序逻辑；此唯一联动明确授权，不借机改马丁卡面。不要把carrier.load能力声明当LoadCardMarkers专用API删除。
- 仅直接受删API/字段影响的既有测试与必要独立本轮集成测试，按用途退役旧正向Load检查，保其它功能。不整删混合partial类/共享fixture。不写019/017驱动。
- 017 R58已完整结束并根核，本轮明确释放CharacterAbilityInteractionUiCoordinator.cs中amountKind=player_marker死分支。数量页仍服务资源amountRange，不删整页、不改场景/预制体/静态样式。

明确禁止改Domain/State/GameStateCloneService.cs、StateCopyMetadata.cs（018R20单写），也不写008固定导出文件、017通用UI热点文件或任何旧Build/正式存档/用户保留窗口。

## 语义核验重点

1. 新Content不再有Effect.LoadCardMarkers、fixedDeliveryRequirement/deliveryRef等旧专用调用。没有可执行的旧API别名或内部换名管线。相关仅有历史拒绝字符串可保留并列位置/理由。
2. 在真实现有内核链核三个代表：
   - 普通数量Choice从相关前序响应结清后的Lua调用生成；不能提前生成候选再仅延迟展示。
   - 多个单枚MoveItem遇某枚缺失/已不在供应时，该枚失败，其余合法兄弟仍成功；没有外层批次回滚或因构造空列表让整组在编译前全部失败。
   - 蒂亚戈多枚结束只接一次骰点流程，艾格尼丝原数量/费用/响应保持；0与无库存不弹空页，角色原生命周期保留。
   这些是已有机制语义的有界集成核验，不新造C#规则。若Lua续接挂点不对，报精确归属由根交原作者，别自行跨写。无需重测相同多effect执行顺序全矩阵。
3. 旧Load正向存档不算新运行时可恢复。明确旧非空/旧节点/旧私有字段的拒绝入口，不靠反序列化吞字段；同用户保留R57/2.0.2/确认506原档不读写混搭运行，当前新内容统一版本身份。既有有效通用合同版本不可连带改坏。
4. 真实实例来源/权限/owner/controller/区位、异质token、资源数量UI与一般资源付款都要保留；不足失败只是单枚移动结果，不是移除校验。

## 验证、删除和交付

本轮先纯.NET/既有有限规则检查与完整编译一致性，明确区分隔离和正常验收。不启动Unity/Build/Player、不恢复019或qy。保首次失败原件，旧废弃规范注明不再适用，不改旧结果冒通过。并行源有变化先按本轮实际SHA保存，不依赖反复重编全项目拖延；最终合流根统一编译。

删除限本次已无用途的明确项目内文件，删除前逐个列绝对路径核引用和备份/可恢复性，不能通配递归；保本家/他人未提交改动，git只查具体安全范围，受限原UI档案禁止访问。用户已授权本次代码清理，无需为明确死文件再问审批。

交实际最小patch/删文件清单/前后SHA、当前生产残留扫描（历史拒绝与有效carrier权限分别列明）、新统一内容版本/模块指纹、真实有界检查原件/未验清单。不要建大兼容框架或在C#写卡名，不删除有其他消费者的通用代码。交付后停写，不直接通知其它任务；根整轮wait后读取最终。

## 实际派发补充（2026-10-26）

013R13、014R12、009R7均整轮完成并根核最小差异与现行SHA；正式五处Load调用清零。三家当前源码可作为迁移输入，不等原生/019验收才删死接口。013时点未闭合已独立交006按182处理，不是本轮删API阻塞。

006单写DepartureResponseScheduling.cs、InfluencePlaceAttachment.cs、InfluenceDepartureExecutor.cs、InfluenceDepartureBatchExecutor.cs、InfluenceDepartureProtocol.cs、Infrastructure/Lua/DepartureSourceLuaCatalog.cs（六文件）。005不得写这些文件，即使旧Load清理有关联先登记精确联动交根。DeferredResponseScheduling是另一个文件，可以按实际死引用处理。018R20/008R19/017R58已完成，但各家成果仍只读；HostRecovery的CreateArchive/CreateSnapshot对固定捕获图必须只读，保内部防御性复制，不把旧Load清理扩到保存流程。

013R13在每个Move外套了无UI的Condition(left=真实Move,right=空)来消费失败，因为隔离发现普通Move的Failed会上冒continuation_child_failed/行动Fault。009/014的多个单枚Move没套该层。请在真实既有执行链核验三者，不满足用户“仅该单枚effect失败”时，修已有通用预期失败传播边界，EffectKernel/AbilityActionExecutor允许精确最小必要修改。不要为玩家标记加专属失败豁免、吞权限/非法状态故障，不能全局把任意Failed视作成功，成功/失败/Condition结果须仍可观察。先给实际失败传播证据再修，允许常规机制修复，无需再问审批。不能让内容作者到处加无语义Condition来绕过内核；若需去掉013这一包装，提供明确已验证合同由根派013本人改，不跨写agnes.lua。保原付费条件式本身。

最终报告分别说明：公开API退役、旧存档明确拒绝、单枚结果与行动继续的真实边界、006待合流项、尚待原生编译/正常验收。没有新的用户决定阻塞；生产/隔离失败如实修，不伪造通过。
