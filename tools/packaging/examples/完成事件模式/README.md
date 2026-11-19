# 完成事件模式示例

这是不添加卡牌的模式包。它引用基础开局配方，在每次 `effect.city.move` 成功完成后，为对应玩家增加 1 分，演示模式自身绑定 `EffectCompleted`。收益仅用于测试规则接入，不代表正式游戏平衡。

此包需要宿主声明 `diy.rule_bindings.completed.v1`。只读取已登记的完成事件载荷并返回现有 `Effect`，不依赖新增 Lua API。
