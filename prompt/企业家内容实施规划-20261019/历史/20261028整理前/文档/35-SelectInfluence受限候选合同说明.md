> 历史记录（整理前原文）。不作为现行派发授权；当前要求见 [现行总览](../../../README.md)。本页链接已按新位置调整，原始字节见 [原始快照](../原始字节快照.zip)。

# SelectInfluence 受限候选合同说明

2026-10-22，依据007的ARC-CONTRACT-REVIEW-R1及协调独立核验采用。本页补充现行合同说明，不改变生产行为、执行器版本、内容版本或保存格式；原合同、首次失败与各轮证据保持原件。主要依据为`ContentPrimitiveEffectExecutor.cs:391–554`、Lua桥/编译器、EffectKernel答案校验、真实离场执行器。具体绝对路径、SHA和摘录见[007证据恢复索引](<../../../实施记录/清理旧日志旧构建-20261027-R5/第七批压缩原件清单.json>)及[协调复核](<../../../实施记录/协调复核-QY007-ARC-CONTRACT-REVIEW-R1.json>)。

## 适用范围

`candidateIds` 是 SelectInfluence v1 中候选受限的严格单枚移除参数，并非任意影响力操作的通用过滤参数。只要字段存在就启用该合同：Lua未提供或赋nil沿原路径；空数组仍启用。必须同时满足 `operation='remove'`、`count=1`、`requiredCount=1`、`requireCompleteSelection=true`。数组可包含多个候选，最终选择一枚；元素必须是非空字符串且按Ordinal唯一。作者应显式传整数，不依赖非整数回落默认值。

组合守卫发生在领域执行阶段；Lua桥开放字段并归一化，编译器复制这些参数，不可宣称所有非法组合已在Lua编译阶段被拒绝。不符合操作/数量/形状约束时失败为`influence_selection_arguments_invalid`。空数组或全部候选失效时实时交集不足，失败为`insufficient_items`；混合有效和失效ID只保留合法项，不因一个失效ID整项拒绝。

当前候选为白名单与当前真实影响力、主体筛选和区域的交集。`subjectFilter`优先于`ownerFilter`，均缺省为self；player表示所有玩家，opponent表示非本人主体（含公司），any表示有主体的影响力。`candidateScope`只接受空串、resource_points、routes；资源点按当前真实资源点标记核定。严格分支不消费`excludedSubjectKeys`或逐次`distinctPolicy`；需要额外排除时由Lua依据公开事实形成白名单。帮派身份、收益、受益人等卡面判断继续属于Lua。

## 请求和答案

| 调用形状 | 请求类型 | 发布的答案协议 | 成功返回 |
| --- | --- | --- | --- |
| 有candidateIds且满足上述前置 | 固定effect.influence.remove.target，覆盖显式interactionType | candidate_id，Min=Max=1 | completedCount=1、changed=true及removedInfluences |
| 无candidateIds，严格remove/replace且requiredCount=count | 保留显式interactionType，未提供通常为lua.choice | candidate_id_array，即使数量为1也发布数组协议 | 原严格结果，不新增removedInfluences |
| 无candidateIds，逐枚remove/replace | 保留显式interactionType，默认lua.choice | candidate_id，每次一枚 | 原逐枚结果 |
| 无candidateIds，逐枚move | 保留自定义类型；空/default lua.choice映为character.ability.choice.move | candidate_id，候选同时标识实例与目的槽 | 原移动结果 |

公共答案校验器对标量/稳定引用/数组存在容器宽容，按数量、去重和候选成员校验；严格选择随后统一读取。但UI必须按发布协议提交，不能以领域可接受另一容器为由改变协议。A08保留逐枚地图请求与无目标完成；M13保留无candidateIds的严格数组地图请求，不为“统一格式”增添candidateIds。

## 结算与恢复事实

提交入口检查请求存在且开放、回答者、revision及已发布候选；恢复执行时再求当前真实交集。选中实例失效但仍有候选时重新请求，不自动替换；不足时失败。严格子移除提交前仍执行真实实例复验及预演。

仅当严格子移除Completed、actualRemovedCount足额，并且该子自己的真实离场记录数量/实例匹配、没有open去向时，受限操作才成功。否则失败为`incomplete_influence_payment`；不得先发收益后补查。必要响应结清后返回`removedInfluences`，每项包含`influenceId`、`departureId`、`originalPosition{zoneId,slotId}`和`locationId`。路线也合法，其locationId可为空，原区域/槽仍由originalPosition表示；调用者不能假定每次都来自资源点。

在途白名单保存在NormalizedArguments，选中实例保存在ContinuationData，原位置来自该次离场记录。SelectInfluence仍为v1，严格移除执行器为2.0.0；本次只是说明既有行为。今后如放宽到move/replace/多枚，或改变类型优先级、发布schema、收据触发，应另行评估通用合同/执行器版本及旧档继续、迁移或拒绝，不能只改文档掩盖行为变更。

## 证据范围与处置

当前静态核验支持不同合法Lua来源复用此受限操作，尚未发现现有合法调用的兼容缺陷。007 a003的五项定向测试及五份恢复凭证、30的A08和M13定向证据按各自身份引用，不能把跨轮通过相加成一个完整同源批次。旧失败保持。

非保罗使用candidateIds的动态正例、空/重复/混合失效集合、显式类型覆盖等组合尚未直接运行；本页不填写通过。现行实现没有因本次复核改变，不据此安排历史全集重跑或专用内核重构。若后续真实问题涉及这些边界，按实际影响定向补证。017仍按31完成通用消费，018仍须其必要版本/恢复/联机与G1构建证据，019仍须正常热座完整局及后续多局验收。本页不替代它们。

> 2026-10-28链接整理：上述证据原件已于R5第七批无损封存，原路径为`实施记录/QY-007-ARC-CONTRACT-REVIEW-R1/证据.json`；SHA256 `b4445a3529261e7e15430493ddb33fee36f3865ccf212cd35ae9e8612a18a239`。请通过R5恢复脚本Batch 7恢复后阅读。本次只修正导航，没有改动或重新生成证据。
