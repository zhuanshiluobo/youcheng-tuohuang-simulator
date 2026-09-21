using System;
using System.Collections.Generic;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    /// <summary>
    /// 在一个 Effect 提交边界内更新地图开放地块集合。
    /// Lua 只提供稳定的 location 引用；地图定义、红区权限和当前开放状态都在这里重新解析。
    /// </summary>
    public sealed class SetLocationsOpenEffectExecutor
    {
        public const string EffectTypeId = "effect.map.set_locations_open";
        public const string DefinitionVersion = "1.0.0";
        public const int MaxLocationCount = 16;
        public const string UnknownLocationFailure = "unknown_location";
        public const string IllegalLocationFailure = "illegal_location";
        public const string TooManyLocationsFailure = "too_many_locations";
        public const string InvalidArgumentsFailure = "invalid_arguments";
        public const string MapMismatchFailure = "map_mismatch";

        private readonly IMapQueryService mapQueryService;

        public SetLocationsOpenEffectExecutor(IMapQueryService mapQueryService)
        {
            this.mapQueryService = mapQueryService ?? throw new ArgumentNullException(nameof(mapQueryService));
        }

        public static EffectRegistration CreateRegistration(IMapQueryService mapQueryService)
        {
            var executor = new SetLocationsOpenEffectExecutor(mapQueryService);
            return new EffectRegistration(
                EffectTypeId,
                executor.Execute,
                EffectExecutorKind.Atomic,
                DefinitionVersion)
            {
                Validator = ValidateSpec
            };
        }

        public static void Register(EffectRegistry registry, IMapQueryService mapQueryService)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.Register(CreateRegistration(mapQueryService));
        }

        private EffectStepResult Execute(EffectExecutionContext context)
        {
            EffectNodeRuntimeState node = context.Node;
            if (node.PendingOutcome != EffectPendingOutcome.None)
            {
                return EffectStepResult.Completed(node.NormalizedResult == null
                    ? NormalizedValue.CreateNull()
                    : node.NormalizedResult.Clone());
            }

            List<string> locationIds;
            bool isOpen;
            string reasonId;
            string diagnostic;
            if (!TryReadArguments(node.NormalizedArguments, out locationIds, out isOpen, out reasonId, out diagnostic))
            {
                return Failed(context, InvalidArgumentsFailure, diagnostic, new List<string>(), new List<string>(), isOpen);
            }

            if (locationIds.Count > MaxLocationCount)
            {
                return Failed(
                    context,
                    TooManyLocationsFailure,
                    "一次最多只能设置 " + MaxLocationCount + " 个地块。",
                    locationIds,
                    new List<string>(),
                    isOpen);
            }

            if (mapQueryService.Map == null ||
                (!string.IsNullOrEmpty(context.State.MapId) &&
                 !string.Equals(context.State.MapId, mapQueryService.Map.MapId, StringComparison.Ordinal)))
            {
                return Failed(
                    context,
                    MapMismatchFailure,
                    "Effect 使用的地图不是当前游戏地图。",
                    locationIds,
                    new List<string>(),
                    isOpen);
            }

            for (int i = 0; i < locationIds.Count; i++)
            {
                MapLocationDefinition location;
                try
                {
                    location = mapQueryService.GetLocation(locationIds[i]);
                }
                catch (Exception)
                {
                    return Failed(
                        context,
                        UnknownLocationFailure,
                        "地图地块不存在：" + locationIds[i],
                        locationIds,
                        new List<string>(),
                        isOpen);
                }

                if (location == null || !location.IsRedZone || location.ResourceSlotCount <= 0)
                {
                    return Failed(
                        context,
                        IllegalLocationFailure,
                        "地块不是允许由本 Effect 改变开放状态的红区资源点：" + locationIds[i],
                        locationIds,
                        new List<string>(),
                        isOpen);
                }
            }

            if (context.State.Map == null)
            {
                context.State.Map = new MapRuntimeState();
            }

            List<string> openLocationIds = context.State.Map.OpenLocationIds == null
                ? new List<string>()
                : new List<string>(context.State.Map.OpenLocationIds);
            SortDistinct(openLocationIds);

            var changed = new List<string>();
            var unchanged = new List<string>();
            for (int i = 0; i < locationIds.Count; i++)
            {
                bool currentlyOpen = openLocationIds.Contains(locationIds[i]);
                if (currentlyOpen == isOpen)
                {
                    unchanged.Add(locationIds[i]);
                }
                else
                {
                    changed.Add(locationIds[i]);
                    if (isOpen)
                    {
                        openLocationIds.Add(locationIds[i]);
                    }
                    else
                    {
                        openLocationIds.Remove(locationIds[i]);
                    }
                }
            }

            SortDistinct(openLocationIds);
            context.State.Map.OpenLocationIds = openLocationIds;
            NormalizedValue result = CreateResult(isOpen, changed, unchanged);
            var step = EffectStepResult.Completed(result);
            if (changed.Count > 0)
            {
                step.AddEvent(new EffectEventRequest
                {
                    EventId = StableIdFactory.Create(
                        "event",
                        node.EffectId,
                        EffectTypeId,
                        JoinIdsWithoutSpaces(locationIds),
                        isOpen ? "open" : "closed"),
                    EventType = "LocationsOpenStateChanged",
                    SourceEffectId = node.EffectId,
                    OwnerNodeId = node.EffectId,
                    ResponseKind = RuleEventResponseKind.Effects,
                    DefinitionVersion = DefinitionVersion,
                    Visibility = "public",
                    SemanticKey = isOpen ? "open" : "closed",
                    Payload = CreateEventPayload(locationIds, changed, isOpen, reasonId)
                });
            }

            string message = changed.Count == 0
                ? "红区资源点开放状态无变化：" + JoinIds(locationIds) + "。"
                : (isOpen ? "红区资源点已开放：" : "红区资源点已关闭：") + JoinIds(changed) + "。";
            step.AddLog(new EffectLogEntrySpec
            {
                PlayerId = node.PlayerId,
                Message = message
            });
            return step;
        }

        private static EffectStepResult Failed(
            EffectExecutionContext context,
            string failureCode,
            string diagnostic,
            IList<string> locationIds,
            IList<string> unchanged,
            bool isOpen)
        {
            var step = EffectStepResult.Failed(failureCode, CreateResult(isOpen, new List<string>(), unchanged));
            step.AddLog(new EffectLogEntrySpec
            {
                PlayerId = context.Node.PlayerId,
                Message = "红区资源点开放状态提交失败：" + (diagnostic ?? failureCode) + "。"
            });
            return step;
        }

        private static string ValidateSpec(EffectSpec spec)
        {
            if (spec == null || spec.NormalizedArguments == null ||
                spec.NormalizedArguments.Kind != NormalizedValueKind.Object)
            {
                return "SetLocationsOpen 的参数必须是对象。";
            }

            if (spec.NormalizedArguments.Properties == null)
            {
                return "SetLocationsOpen 的对象字段无效。";
            }

            for (int i = 0; i < spec.NormalizedArguments.Properties.Count; i++)
            {
                NormalizedValueEntry entry = spec.NormalizedArguments.Properties[i];
                if (entry == null ||
                    (entry.Name != "locations" && entry.Name != "isOpen" && entry.Name != "reasonId"))
                {
                    return "SetLocationsOpen 参数包含未登记字段。";
                }
            }

            NormalizedValue locations;
            if (!TryGetProperty(spec.NormalizedArguments, "locations", out locations) ||
                locations == null || locations.Kind != NormalizedValueKind.Array)
            {
                return "SetLocationsOpen 必须提供 locations 数组。";
            }

            if (locations.Items == null)
            {
                return "SetLocationsOpen 的 locations 数组无效。";
            }

            for (int i = 0; i < locations.Items.Count; i++)
            {
                NormalizedValue item = locations.Items[i];
                if (item == null || item.Kind != NormalizedValueKind.StableReference ||
                    item.ReferenceType != "location" || string.IsNullOrEmpty(item.ReferenceId))
                {
                    return "locations 必须只包含 location 稳定引用。";
                }
            }

            NormalizedValue isOpen;
            if (!TryGetProperty(spec.NormalizedArguments, "isOpen", out isOpen) ||
                isOpen == null || isOpen.Kind != NormalizedValueKind.Boolean)
            {
                return "SetLocationsOpen 必须提供 isOpen 布尔值。";
            }

            NormalizedValue reason;
            if (TryGetProperty(spec.NormalizedArguments, "reasonId", out reason) &&
                (reason == null || reason.Kind != NormalizedValueKind.String || string.IsNullOrWhiteSpace(reason.StringValue)))
            {
                return "reasonId 必须是非空字符串。";
            }

            return string.Empty;
        }

        private static bool TryReadArguments(
            NormalizedValue arguments,
            out List<string> locationIds,
            out bool isOpen,
            out string reasonId,
            out string diagnostic)
        {
            locationIds = new List<string>();
            isOpen = false;
            reasonId = "rule.unspecified";
            diagnostic = string.Empty;

            NormalizedValue locations;
            NormalizedValue open;
            if (!TryGetProperty(arguments, "locations", out locations) ||
                !TryGetProperty(arguments, "isOpen", out open) ||
                locations == null || locations.Kind != NormalizedValueKind.Array ||
                locations.Items == null || open == null || open.Kind != NormalizedValueKind.Boolean)
            {
                diagnostic = "SetLocationsOpen 参数结构无效。";
                return false;
            }

            isOpen = open.BooleanValue;
            for (int i = 0; i < locations.Items.Count; i++)
            {
                NormalizedValue item = locations.Items[i];
                if (item == null || item.Kind != NormalizedValueKind.StableReference ||
                    item.ReferenceType != "location" || string.IsNullOrEmpty(item.ReferenceId))
                {
                    diagnostic = "locations 必须只包含 location 稳定引用。";
                    return false;
                }

                if (!locationIds.Contains(item.ReferenceId)) locationIds.Add(item.ReferenceId);
            }

            SortDistinct(locationIds);
            NormalizedValue reason;
            if (TryGetProperty(arguments, "reasonId", out reason) && reason != null &&
                reason.Kind == NormalizedValueKind.String && !string.IsNullOrWhiteSpace(reason.StringValue))
            {
                reasonId = reason.StringValue;
            }

            return true;
        }

        private static NormalizedValue CreateResult(bool isOpen, IList<string> changed, IList<string> unchanged)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry
                {
                    Name = "changedLocationIds",
                    Value = CreateStringArray(changed)
                },
                new NormalizedValueEntry
                {
                    Name = "isOpen",
                    Value = NormalizedValue.CreateBoolean(isOpen)
                },
                new NormalizedValueEntry
                {
                    Name = "unchangedLocationIds",
                    Value = CreateStringArray(unchanged)
                }
            });
        }

        private static NormalizedValue CreateEventPayload(
            IList<string> locationIds,
            IList<string> changed,
            bool isOpen,
            string reasonId)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry
                {
                    Name = "changedLocationIds",
                    Value = CreateStringArray(changed)
                },
                new NormalizedValueEntry
                {
                    Name = "isOpen",
                    Value = NormalizedValue.CreateBoolean(isOpen)
                },
                new NormalizedValueEntry
                {
                    Name = "locationIds",
                    Value = CreateStringArray(locationIds)
                },
                new NormalizedValueEntry
                {
                    Name = "reasonId",
                    Value = NormalizedValue.CreateString(reasonId ?? "rule.unspecified")
                }
            });
        }

        private static NormalizedValue CreateStringArray(IList<string> values)
        {
            var result = new List<NormalizedValue>();
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    result.Add(NormalizedValue.CreateString(values[i] ?? string.Empty));
                }
            }

            return NormalizedValue.CreateArray(result);
        }

        private static bool TryGetProperty(NormalizedValue value, string name, out NormalizedValue result)
        {
            if (value != null && value.Kind == NormalizedValueKind.Object && value.Properties != null)
            {
                for (int i = 0; i < value.Properties.Count; i++)
                {
                    NormalizedValueEntry entry = value.Properties[i];
                    if (entry != null && entry.Name == name)
                    {
                        result = entry.Value;
                        return true;
                    }
                }
            }

            result = null;
            return false;
        }

        private static void SortDistinct(List<string> values)
        {
            values.Sort(StringComparer.Ordinal.Compare);
            for (int i = values.Count - 1; i > 0; i--)
            {
                if (string.Equals(values[i], values[i - 1], StringComparison.Ordinal)) values.RemoveAt(i);
            }
        }

        private static string JoinIds(IList<string> values)
        {
            return values == null || values.Count == 0 ? "（空集合）" : JoinIdsWithoutSpaces(values).Replace(",", ", ");
        }

        private static string JoinIdsWithoutSpaces(IList<string> values)
        {
            if (values == null || values.Count == 0) return string.Empty;
            var copy = new string[values.Count];
            for (int i = 0; i < values.Count; i++) copy[i] = values[i] ?? string.Empty;
            return string.Join(",", copy);
        }
    }
}
