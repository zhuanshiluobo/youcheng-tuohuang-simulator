> 历史记录（整理前原文）。不作为现行派发授权；当前要求见 [现行总览](../../../README.md)。本页链接已按新位置调整，原始字节见 [原始快照](../原始字节快照.zip)。

# 165-正式Lua弃用Effect.Repeat并展开多个effect

2026-10-25，用户明确要求：“把这13个Effect.Repeat的次数式全拆成挂多个effect，就是放弃使用Effect.Repeat”。随后明确：不需要核对顺序，执行顺序由内核推进，已有同时挂多个效果的实现；相同效果响应顺序无需另做验证。本轮严格按这次澄清执行，不复用旧148对count=1包装的实现要求。

## 执行归属与目标

由已有QY-009负责 CONTENT-REPEAT-EXPANSION-R5。根已list登记并wait确认其前轮completed/cursor69；R3组合实现与R4证据已核，当前正式内容以实际文件为准。019R25继续冻结R50包四端完整局，017R51已结束并停写。本轮只改内容与必要登记，不改内核，不占Unity或游戏窗口，不热换运行包。

把当前core正式Lua中12个文件13处Effect.Repeat全部展开为多个普通effect。同一次数式里原effects列表按原声明重复追加，每个effect构造独立对象。固定次数可直接列表或Lua循环生成；动态次数只有先锋哨站的ctx.payload.declarationMarkerCount，取原值生成相应数量，0生成空列表。保原数量、参数、Condition左右式、已选组合、原有完成续接和后续效果。

## 精确内容写集（均位于Assets/StreamingAssets/Content/core/lua/）

1. characters/operator.w.strategy.lua：3次放置。
2. characters/operator.dorothy.strategy.lua：原Condition右式3次放置。
3. characters/operator.silverash.tactic.lua：原移除后2次放置。
4. enterprises/infected.enterprise.stonehoove_security.level_2.special.lua：2次公司影响力放置。
5. characters/infected.character.flowing_shape_alpha.strategy.lua：2次移动。
6. characters/infected.character.flowing_shape_gamma.strategy.lua：2个独立的原资源选择effect，保原选项和表现声明。
7. characters/infected.character.ceobe.tactic.lua：原骰子分支中的2次放置，保其它分支和既有续接。
8. awards/infected.award.roads.acquired.lua：2次原Flow.ClearRouteAndPlaceRoad，仍在20金券之后、奖励退盒之前。
9. city_styles/city_style_pioneer_outpost.special.lua：固定2次放置及按declarationMarkerCount生成移动，共两处。
10. departments/enterprise.department.defense.level_4.reward.lua：去count=1包装，原一次4有序组合选择、付款及原完成回调保留，所选组合直接返回操作effect列表。
11. enterprises/enterprise.blacksteel.level_4.special.lua：去count=1包装，原一次4组合选择的各分支直接普通effect列表。
12. characters/infected.character.hellagur.strategy.lua：去count=1包装，原一次8组合选择的各分支直接普通effect列表，原收尾移除义务不变。

根清点当前为13处真实调用；执行前重查正式Content，不把测试字符串或旧实施原件混为正式调用。目标是正式内容不再使用Effect.Repeat，不用别名、count=1、隐藏封装或换名的重复节点保留使用。当前通用Repeat实现和历史存档解码不在本轮删除范围，不做无关公共API迁移。

## 边界与校验

允许上述12脚本及直接关联的定义/pack中必要脚本哈希、版本登记；只针对确实变动的登记项更新，不全量改文案或重写用户编辑。允许本轮文档与最小静态检查记录。脚本本来未受哈希登记约束时不擅自新增注册层。

用户已明确不做顺序核对：不另造顺序验证、不追加响应/恢复测试矩阵，不为了原count=1注释新增WithContext阶段、Sequence接口、专用C#或复杂调度。现有相同效果列表直接交内核。不改变已有测试去声称全通过；旧针对Repeat节点形状的断言如果受影响，记录具体未运行的旧检查，不能为保断言重新造包装，也不扩大修改内核。

完成后只做与本改动相称的Lua语法/内容加载、精确13处迁移清点及所需哈希一致检查；使用既有非Unity入口即可。未实际运行的检查如实记未运行。禁止Unity/Build/Player/真实存档操作，不占019四端，不因用户文案保存重构建。C#生产、表现UI、场景/Prefab/素材、网络保存、019夹具均不在写集；遵守根AGENTS与原档案禁访。

## 交付

在实施记录/QY-009-CONTENT-REPEAT-EXPANSION-R5交付：12文件13处前后对应表、完整最小patch、必要登记变更及SHA、实际静态/加载结果和失败原件、未运行检查、停写声明。不得把有限检查写成正常页面/完整联机验收。整轮结束后根核，019继续旧冻结包，后继按统一时点验证新内容。本轮不等待根对运行中逐条输出作答。

