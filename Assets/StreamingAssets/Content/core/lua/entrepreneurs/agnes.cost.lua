-- QY-013 / 1.4.0。名称由R6固定动作名冻结；候选ID及正文入口不变。
local tactics={
    ['entrepreneur.A09.tactic.quick']='1A·集中驻扎',
    ['entrepreneur.A08.tactic.quick']='1B·分遣部署',
    ['entrepreneur.A06.tactic.quick']='3A·后勤保障',
    ['entrepreneur.A07.tactic.quick']='3B·合作战略',
    ['entrepreneur.A05.tactic.quick']='5B·特勤小组'
}
return function(ctx) return Shared.agnes.quickEntryCondition(ctx,tactics[ctx.payload.actionId]) end
