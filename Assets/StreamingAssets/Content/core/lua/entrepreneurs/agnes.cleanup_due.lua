-- 艾格尼丝到期义务：只调用冻结模块的履约正文，不重新检查当前来源。
return function(ctx) return Shared.agnes.cleanupDue(ctx) end
