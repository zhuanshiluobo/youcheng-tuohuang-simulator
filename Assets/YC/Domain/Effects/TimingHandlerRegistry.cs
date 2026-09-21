using System;
using System.Collections.Generic;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    /// <summary>一次性固定时点处理器。登记、Lua 编译和挂树均由内核工作副本事务提交。</summary>
    public static class TimingHandlerRegistry
    {
        public static void RegisterPlayerCleanupAfterResolveOnce(
            EffectRegistry registry, string sourceType, string abilityId, string version,
            string contentHash, Func<EffectEventHandlerContext, IList<EffectSpec>> handler)
        {
            string key = abilityId + ".playerCleanup";
            if (registry.HasEventHandler(key)) return;
            string type = "timing.handler." + key;
            registry.Register(new EffectRegistration(type, context => Execute(context, version, contentHash),
                EffectExecutorKind.IntrinsicFlow, version));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration(
                key + ".register", "EffectCompleted", "*", key + ".register", context =>
                {
                    var source = context.OwnerNode;
                    if (source.EffectTypeId != sourceType || source.PendingOutcome != EffectPendingOutcome.Completed ||
                        Read(source.NormalizedArguments, "abilityId") != abilityId)
                        return new List<EffectSpec>();
                    string id = StableIdFactory.Create("timing", source.EffectId, key, version);
                    if (!source.TimingBindings.Exists(binding => binding.BindingId == id))
                        source.TimingBindings.Add(new TimingHandlerBinding
                        {
                            BindingId = id, SourceEffectId = source.EffectId, AbilityId = abilityId,
                            HandlerSlot = "playerCleanup", DefinitionVersion = version, ContentHash = contentHash,
                            RoundNumber = context.State.Round, ScopedPlayerId = source.PlayerId
                        });
                    return new List<EffectSpec>();
                }) { EffectTypeFilter = sourceType });
            registry.RegisterEventHandler(new EffectEventHandlerRegistration(
                key, "PlayerCleanupStarted", "*", key, context =>
                {
                    var effects = new List<EffectSpec>();
                    foreach (var source in context.State.EffectRuntime.EffectNodes)
                    foreach (var binding in source.TimingBindings)
                    {
                        if (binding.AbilityId != abilityId || binding.HandlerSlot != "playerCleanup" ||
                            binding.RoundNumber != context.State.Round || binding.ScopedPlayerId != context.Event.PlayerId ||
                            binding.Status != "registered") continue;
                        Validate(binding, version, contentHash);
                        var spec = new EffectSpec(type)
                        {
                            PlayerId = binding.ScopedPlayerId, DefinitionVersion = version,
                            StableKey = binding.BindingId,
                            NormalizedArguments = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                            {
                                new NormalizedValueEntry { Name = "sourceEffectId", Value = NormalizedValue.CreateString(source.EffectId) },
                                new NormalizedValueEntry { Name = "bindingId", Value = NormalizedValue.CreateString(binding.BindingId) }
                            })
                        };
                        spec.Children.AddRange(handler(context));
                        binding.Status = "attached";
                        effects.Add(spec);
                    }
                    return effects;
                }, definitionVersion: version, contentHash: contentHash, abilityId: abilityId));
        }

        public static string ValidateBindings(GameState state, EffectRegistry registry)
        {
            if (state == null || state.EffectRuntime == null || state.EffectRuntime.EffectNodes == null) return string.Empty;
            foreach (var node in state.EffectRuntime.EffectNodes)
            {
                if (node == null || node.TimingBindings == null) continue;
                foreach (var binding in node.TimingBindings)
                {
                    if (binding == null) return "固定时点绑定为空。";
                    if (binding.Status == "completed" || binding.Status == "cancelled") continue;
                    var registration = registry.FindEventHandler(binding.AbilityId + "." + binding.HandlerSlot);
                    if (registration == null) return "固定时点处理器未注册：" + binding.AbilityId;
                    if (registration.DefinitionVersion != binding.DefinitionVersion || registration.ContentHash != binding.ContentHash)
                        return "固定时点绑定的内容版本或哈希不匹配：" + binding.BindingId;
                }
            }
            return string.Empty;
        }

        private static EffectStepResult Execute(EffectExecutionContext context, string version, string hash)
        {
            var source = context.State.EffectRuntime.EffectNodes.Find(node =>
                node.EffectId == Read(context.Node.NormalizedArguments, "sourceEffectId"));
            var binding = source == null ? null : source.TimingBindings.Find(item =>
                item.BindingId == Read(context.Node.NormalizedArguments, "bindingId"));
            if (binding == null) throw new InvalidOperationException("固定时点绑定丢失。");
            Validate(binding, version, hash);
            binding.AttachedEffectId = context.Node.EffectId;
            if (context.Node.FlowStage == string.Empty)
            {
                var step = EffectStepResult.Continue("settling");
                foreach (var child in context.Node.NestedEffects) step.AddChild(EffectRegistry.FromRuntimeSpec(child));
                return step;
            }
            foreach (var child in context.ChildNodes)
            {
                if (child.Status == EffectNodeStatus.Failed)
                {
                    binding.Status = "completed";
                    return EffectStepResult.Failed(child.FailureReason);
                }
                if (child.Status != EffectNodeStatus.Completed) return EffectStepResult.NoProgress("等待收尾子效果。");
            }
            binding.Status = "completed";
            return EffectStepResult.Completed();
        }

        private static void Validate(TimingHandlerBinding binding, string version, string hash)
        {
            if (binding.DefinitionVersion != version || binding.ContentHash != hash)
                throw new InvalidOperationException("固定时点绑定的内容版本或哈希不匹配。");
        }

        private static string Read(NormalizedValue value, string name)
        {
            if (value == null || value.Properties == null) return string.Empty;
            var entry = value.Properties.Find(item => item.Name == name);
            return entry == null || entry.Value == null ? string.Empty : entry.Value.StringValue;
        }
    }
}
