using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    public sealed class LuaEffectCompilationContext
    {
        public string SourceId { get; set; } = string.Empty;
        public string ContentInstanceId { get; set; } = string.Empty;
        public string AbilityId { get; set; } = string.Empty;
        public string CompletionHandlerId { get; set; } = string.Empty;
        public string StableKeyPrefix { get; set; } = string.Empty;
        public int PlayerId { get; set; } = -1;
    }

    public sealed class LuaEffectCompilationFault
    {
        public LuaEffectCompilationFault(
            LuaFailureCode code,
            string diagnostic,
            int effectIndex = -1)
        {
            Code = code;
            Diagnostic = diagnostic ?? string.Empty;
            EffectIndex = effectIndex;
        }

        public LuaFailureCode Code { get; }
        public string Diagnostic { get; }
        public int EffectIndex { get; }
    }

    public sealed class LuaEffectTypeRegistration
    {
        public LuaEffectTypeRegistration(
            string effectTypeId,
            Func<LuaEffectSpec, string> validator,
            Func<LuaEffectSpec, NormalizedValue> argumentBuilder)
        {
            if (string.IsNullOrEmpty(effectTypeId)) throw new ArgumentException("Effect 类型 ID 不能为空。", nameof(effectTypeId));
            if (argumentBuilder == null) throw new ArgumentNullException(nameof(argumentBuilder));
            EffectTypeId = effectTypeId;
            Validator = validator;
            ArgumentBuilder = argumentBuilder;
        }

        public string EffectTypeId { get; }
        public Func<LuaEffectSpec, string> Validator { get; }
        public Func<LuaEffectSpec, NormalizedValue> ArgumentBuilder { get; }
    }

    /// <summary>
    /// 运行时白名单。它是宿主适配层的索引，不会进入 GameState 或快照。
    /// </summary>
    public sealed class LuaEffectTypeCatalog
    {
        private readonly Dictionary<string, LuaEffectTypeRegistration> registrations =
            new Dictionary<string, LuaEffectTypeRegistration>(StringComparer.Ordinal);

        public LuaEffectTypeCatalog()
        {
            Register(new LuaEffectTypeRegistration(
                EffectTypeIds.Condition,
                spec => spec == null || spec.NestedEffectGroups == null || spec.NestedEffectGroups.Length < 2
                    ? "Condition 必须提供 leftEffects 和 rightEffects。" : string.Empty,
                spec => spec.RawArguments == null
                    ? NormalizedValue.CreateObject(new List<NormalizedValueEntry>())
                    : spec.RawArguments.Clone()));
            Register(new LuaEffectTypeRegistration(
                "effect.resource.gain",
                spec => IsResourceType(spec.ResourceTypeId) ? string.Empty : "资源类型未登记。",
                spec => CreateArguments(spec, true)));
            Register(new LuaEffectTypeRegistration(
                "effect.resource.pay",
                spec => IsResourceType(spec.ResourceTypeId) ? string.Empty : "资源类型未登记。",
                spec => CreateArguments(spec, true)));
            Register(new LuaEffectTypeRegistration(
                "effect.score.gain",
                null,
                spec => CreateArguments(spec, false)));
            Register(new LuaEffectTypeRegistration(
                SetLocationsOpenEffectExecutor.EffectTypeId,
                ValidateLocationsOpen,
                CreateLocationsOpenArguments));
            Register(new LuaEffectTypeRegistration(
                CityMoveEffectTypeIds.Move,
                ValidateRawObject,
                CreateRawArguments));
            Register(new LuaEffectTypeRegistration(
                InfluenceEffectTypeIds.PlaceInfluence,
                ValidateInfluence,
                CreateInfluenceArguments));
            Register(new LuaEffectTypeRegistration(
                InfluenceEffectTypeIds.RemoveInfluence,
                ValidateInfluence,
                CreateInfluenceArguments));
            Register(new LuaEffectTypeRegistration(
                InfluenceEffectTypeIds.MoveInfluence,
                ValidateInfluence,
                CreateInfluenceArguments));
            Register(new LuaEffectTypeRegistration(
                InfluenceEffectTypeIds.ReplaceInfluence,
                ValidateInfluence,
                CreateInfluenceArguments));
            Register(new LuaEffectTypeRegistration(
                CityStyleSpecialActionEffectTypeIds.OperatePlayerMarker,
                ValidateCityStyleMarker,
                CreateCityStyleArguments));
            Register(new LuaEffectTypeRegistration(
                EventCardEffectTypeIds.Resolve,
                ValidateRawObject,
                CreateRawArguments));
            Register(new LuaEffectTypeRegistration(
                CharacterAbilityEffectExecutor.AbilityEffectTypeId,
                ValidateCharacterAbility,
                CreateCharacterAbilityArguments));
            Register(new LuaEffectTypeRegistration(
                CharacterCoverEffectExecutor.EffectTypeId,
                ValidateRawObject,
                CreateRawArguments));

            string[] genericTypes =
            {
                ContentPrimitiveEffectExecutor.SelectInfluence,
                CityStyleSpecialActionEffectTypeIds.GrantMainActions,
                FacilitySelectionEffectExecutor.TypeId,
                ExplorationSelectionEffectExecutor.TypeId,
                ContentPrimitiveEffectExecutor.OverrideNextStartPlayer,
                ContentPrimitiveEffectExecutor.ActivateFacilityEntry,
                ContentPrimitiveEffectExecutor.ChooseResources,
                LuaDomainEffectTypeIds.Choice,
                LuaDomainEffectTypeIds.Repeat,
                LuaDomainEffectTypeIds.RollDice,
                LuaDomainEffectTypeIds.ResourceDiscount,
                LuaDomainEffectTypeIds.ResourceSell,
                LuaDomainEffectTypeIds.LoseScore,
                LuaDomainEffectTypeIds.PlaceRoad,
                LuaDomainEffectTypeIds.OperateToken,
                LuaDomainEffectTypeIds.ShuffleFacilityDeck,
                LuaDomainEffectTypeIds.MoveFacilityCard,
                LuaDomainEffectTypeIds.RotateFacilityCard,
                LuaDomainEffectTypeIds.RevealCharacterCard,
                LuaDomainEffectTypeIds.SetCharacterDoubleUseRule,
                LuaDomainEffectTypeIds.MoveCharacterCard,
                LuaDomainEffectTypeIds.DispatchOperator,
                LuaDomainEffectTypeIds.UpgradeEnterprise,
                LuaDomainEffectTypeIds.ActivateEnterpriseSpecial,
                LuaDomainEffectTypeIds.SwitchDepartment,
                LuaDomainEffectTypeIds.ExecuteMainAction,
                LuaDomainEffectTypeIds.OpenPlayerTaskGroup,
                LuaDomainEffectTypeIds.MainActionDeploy,
                LuaDomainEffectTypeIds.MainActionDispatch,
                LuaDomainEffectTypeIds.MainActionBuild,
                LuaDomainEffectTypeIds.MainActionMoveCity,
                LuaDomainEffectTypeIds.MainActionSpecial,
                LuaDomainEffectTypeIds.DeclareCityStyle,
                FacilityEntryEffectTypeIds.Build,
                ExplorationEffectTypeIds.Explore
            };
            for (int i = 0; i < genericTypes.Length; i++)
            {
                Register(new LuaEffectTypeRegistration(genericTypes[i], ValidateRawObject, CreateRawArguments));
            }
        }

        public void Register(LuaEffectTypeRegistration registration)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (registrations.ContainsKey(registration.EffectTypeId))
            {
                throw new InvalidOperationException("Lua Effect 类型重复注册：" + registration.EffectTypeId);
            }

            registrations.Add(registration.EffectTypeId, registration);
        }

        public bool TryGet(string effectTypeId, out LuaEffectTypeRegistration registration)
        {
            return registrations.TryGetValue(effectTypeId ?? string.Empty, out registration);
        }

        private static string ValidateRawObject(LuaEffectSpec spec)
        {
            return spec == null || spec.RawArguments == null || spec.RawArguments.Kind != NormalizedValueKind.Object
                ? "Lua Effect 参数必须是对象。" : string.Empty;
        }

        private static NormalizedValue CreateRawArguments(LuaEffectSpec spec)
        {
            return spec.RawArguments == null
                ? NormalizedValue.CreateObject(new List<NormalizedValueEntry>())
                : spec.RawArguments.Clone();
        }

        private static NormalizedValue CreateArguments(LuaEffectSpec spec, bool includeResource)
        {
            var entries = new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry
                {
                    Name = "amount",
                    Value = NormalizedValue.CreateInteger(spec.Amount)
                },
                new NormalizedValueEntry
                {
                    Name = "executingPlayer",
                    Value = NormalizedValue.CreateStableReference("player", spec.RecipientPlayerId)
                }
            };

            if (includeResource)
            {
                entries.Add(new NormalizedValueEntry
                {
                    Name = "resourceType",
                    Value = NormalizedValue.CreateString(NormalizeResourceType(spec.ResourceTypeId))
                });
            }

            if (!string.IsNullOrEmpty(spec.ReasonId))
            {
                entries.Add(new NormalizedValueEntry
                {
                    Name = "reasonId",
                    Value = NormalizedValue.CreateString(spec.ReasonId)
                });
            }

            return NormalizedValue.CreateObject(entries);
        }

        private static bool IsResourceType(string resourceType)
        {
            return resourceType == ResourceType.Originium.ToString() ||
                   resourceType == ResourceType.OriginiumShard.ToString() ||
                   resourceType == ResourceType.Iron.ToString() ||
                   resourceType == ResourceType.PureOriginium.ToString() ||
                   resourceType == ResourceType.GoldVoucher.ToString() ||
                   resourceType == "originium" || resourceType == "originium_shard" || resourceType == "iron" ||
                   resourceType == "pure_originium" || resourceType == "gold_voucher";
        }

        private static string NormalizeResourceType(string resourceType)
        {
            string value = (resourceType ?? string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            if (value == "originium") return ResourceType.Originium.ToString();
            if (value == "originiumshard") return ResourceType.OriginiumShard.ToString();
            if (value == "iron") return ResourceType.Iron.ToString();
            if (value == "pureoriginium") return ResourceType.PureOriginium.ToString();
            if (value == "goldvoucher") return ResourceType.GoldVoucher.ToString();
            return resourceType ?? string.Empty;
        }

        private static string ValidateFacilityEntry(LuaEffectSpec spec)
        {
            return spec == null || string.IsNullOrWhiteSpace(spec.Operation)
                ? "FacilityEntry 必须提供 operation。"
                : string.Empty;
        }

        private static NormalizedValue CreateFacilityEntryArguments(LuaEffectSpec spec)
        {
            var entries = new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "operation", Value = NormalizedValue.CreateString(spec.Operation) },
                new NormalizedValueEntry { Name = "candidateSetId", Value = NormalizedValue.CreateString(spec.CandidateSetId) },
                new NormalizedValueEntry { Name = "candidateResolutionId", Value = NormalizedValue.CreateString(spec.CandidateSetId) },
                new NormalizedValueEntry { Name = "candidateSetVersion", Value = NormalizedValue.CreateInteger(spec.CandidateSetVersion) },
                new NormalizedValueEntry { Name = "candidateIds", Value = CreateCandidateArray(spec.CandidateIds) },
                new NormalizedValueEntry { Name = "minSelections", Value = NormalizedValue.CreateInteger(spec.MinSelections) },
                new NormalizedValueEntry { Name = "maxSelections", Value = NormalizedValue.CreateInteger(spec.MaxSelections) },
                new NormalizedValueEntry { Name = "promptKey", Value = NormalizedValue.CreateString(spec.PromptKey) },
                new NormalizedValueEntry { Name = "amount", Value = NormalizedValue.CreateInteger(spec.Amount) },
                new NormalizedValueEntry { Name = "resourceType", Value = NormalizedValue.CreateString(spec.ResourceTypeId) },
                new NormalizedValueEntry { Name = "markerId", Value = NormalizedValue.CreateString(spec.MarkerId) },
                new NormalizedValueEntry { Name = "sourceSlotIndex", Value = NormalizedValue.CreateInteger(spec.SourceSlotIndex) }
            };
            if (!string.IsNullOrEmpty(spec.ExecutingPlayerId))
            {
                entries.Add(new NormalizedValueEntry
                {
                    Name = "executingPlayer",
                    Value = NormalizedValue.CreateStableReference("player", spec.ExecutingPlayerId)
                });
            }
            if (!string.IsNullOrEmpty(spec.FacilityId))
            {
                entries.Add(new NormalizedValueEntry { Name = "facilityId", Value = NormalizedValue.CreateString(spec.FacilityId) });
            }
            if (!string.IsNullOrEmpty(spec.FacilityInstanceId))
            {
                entries.Add(new NormalizedValueEntry { Name = "facilityInstanceId", Value = NormalizedValue.CreateStableReference("facility", spec.FacilityInstanceId) });
            }
            return NormalizedValue.CreateObject(entries);
        }

        private static string ValidateLocationsOpen(LuaEffectSpec spec)
        {
            if (spec == null || !spec.HasIsOpen || spec.LocationIds == null)
            {
                return "SetLocationsOpen 必须同时提供 locations 和 isOpen。";
            }

            if (spec.LocationIds.Count > SetLocationsOpenEffectExecutor.MaxLocationCount)
            {
                return "SetLocationsOpen 一次最多只能设置 " +
                       SetLocationsOpenEffectExecutor.MaxLocationCount + " 个地块。";
            }

            for (int i = 0; i < spec.LocationIds.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(spec.LocationIds[i]))
                {
                    return "SetLocationsOpen 的地块 ID 不能为空。";
                }
            }

            return string.Empty;
        }

        private static NormalizedValue CreateLocationsOpenArguments(LuaEffectSpec spec)
        {
            var locationValues = new List<NormalizedValue>();
            var locationIds = new List<string>(spec.LocationIds ?? Array.Empty<string>());
            locationIds.Sort(StringComparer.Ordinal);
            for (int i = locationIds.Count - 1; i > 0; i--)
            {
                if (string.Equals(locationIds[i], locationIds[i - 1], StringComparison.Ordinal))
                {
                    locationIds.RemoveAt(i);
                }
            }

            for (int i = 0; i < locationIds.Count; i++)
            {
                locationValues.Add(NormalizedValue.CreateStableReference("location", locationIds[i]));
            }

            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry
                {
                    Name = "isOpen",
                    Value = NormalizedValue.CreateBoolean(spec.IsOpen)
                },
                new NormalizedValueEntry
                {
                    Name = "locations",
                    Value = NormalizedValue.CreateArray(locationValues)
                },
                new NormalizedValueEntry
                {
                    Name = "reasonId",
                    Value = NormalizedValue.CreateString(
                        string.IsNullOrWhiteSpace(spec.ReasonId) ? "rule.unspecified" : spec.ReasonId)
                }
            });
        }

        private static string ValidateCharacterAbility(LuaEffectSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.AbilityId) ||
                string.IsNullOrEmpty(spec.CardInstanceId) || string.IsNullOrEmpty(spec.Mode))
            {
                return "CharacterAbility 必须同时提供 abilityId、cardInstanceId 和 mode。";
            }

            return CharacterAbilityCatalog.IsRegistered(spec.AbilityId)
                ? string.Empty
                : "CharacterAbility 未登记：" + spec.AbilityId;
        }

        private static NormalizedValue CreateCharacterAbilityArguments(LuaEffectSpec spec)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry
                {
                    Name = "abilityId",
                    Value = NormalizedValue.CreateString(spec.AbilityId)
                },
                new NormalizedValueEntry
                {
                    Name = "cardInstanceId",
                    Value = NormalizedValue.CreateStableReference("character_card", spec.CardInstanceId)
                },
                new NormalizedValueEntry
                {
                    Name = "mode",
                    Value = NormalizedValue.CreateString(spec.Mode)
                },
                new NormalizedValueEntry
                {
                    Name = "effectOrder",
                    Value = NormalizedValue.CreateString(spec.EffectOrder)
                }
            });
        }

        private static string ValidateInfluence(LuaEffectSpec spec)
        {
            if (spec == null || string.IsNullOrWhiteSpace(spec.ExecutingPlayerId)) return "影响力 Effect 必须提供 executingPlayer。";
            if (string.IsNullOrWhiteSpace(spec.CauseKind)) return "影响力 Effect 必须提供 causeKind。";
            if (spec.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence)
            {
                return string.IsNullOrWhiteSpace(spec.InfluenceSourceId)
                    ? "PlaceInfluence 必须提供 influenceSource。"
                    : string.Empty;
            }

            if (spec.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence ||
                spec.EffectTypeId == InfluenceEffectTypeIds.MoveInfluence)
            {
                if (spec.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence &&
                    string.IsNullOrWhiteSpace(spec.TargetInfluenceId) &&
                    (spec.CandidateIds == null || spec.CandidateIds.Count == 0))
                {
                    return "RemoveInfluence 必须提供 targetInfluence 或 candidateScope。";
                }
                if (spec.EffectTypeId == InfluenceEffectTypeIds.MoveInfluence &&
                    string.IsNullOrWhiteSpace(spec.TargetInfluenceId)) return "影响力 Effect 必须提供 targetInfluence。";
                return spec.EffectTypeId == InfluenceEffectTypeIds.MoveInfluence && string.IsNullOrWhiteSpace(spec.TargetSlotId)
                    ? "MoveInfluence 必须提供 targetSlotId。"
                    : string.Empty;
            }

            if (string.IsNullOrWhiteSpace(spec.TargetInfluenceId) ||
                string.IsNullOrWhiteSpace(spec.ReplacementSourceId) ||
                string.IsNullOrWhiteSpace(spec.ReplacementOwnerId))
            {
                return "ReplaceInfluence 必须提供 targetInfluence、replacementSource 和 replacementOwner。";
            }

            return spec.PlacementFailurePolicy == "keep_removal"
                ? string.Empty
                : "ReplaceInfluence 只允许 placementFailurePolicy=keep_removal。";
        }

        private static NormalizedValue CreateInfluenceArguments(LuaEffectSpec spec)
        {
            var entries = new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry
                {
                    Name = "causeKind",
                    Value = NormalizedValue.CreateString(spec.CauseKind)
                },
                new NormalizedValueEntry
                {
                    Name = "executingPlayer",
                    Value = NormalizedValue.CreateStableReference("player", spec.ExecutingPlayerId)
                }
            };

            if (spec.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence)
            {
                entries.Add(new NormalizedValueEntry { Name = "influenceSource", Value = CreateSource(spec.InfluenceSourceId, spec.ExecutingPlayerId) });
                if (spec.HasCandidateScope)
                    entries.Add(new NormalizedValueEntry { Name = "candidateScope", Value = CreateCandidateArray(spec.CandidateIds) });
                entries.Add(new NormalizedValueEntry { Name = "ownerSubject", Value = CreateSubject(string.IsNullOrEmpty(spec.OwnerSubjectId) ? spec.ExecutingPlayerId : spec.OwnerSubjectId) });
                if (!string.IsNullOrWhiteSpace(spec.TargetSlotId))
                {
                    entries.Add(new NormalizedValueEntry { Name = "targetSlotId", Value = NormalizedValue.CreateStableReference("slot", spec.TargetSlotId) });
                }
            }
            else if (spec.EffectTypeId == InfluenceEffectTypeIds.RemoveInfluence)
            {
                entries.Add(new NormalizedValueEntry { Name = "destination", Value = NormalizedValue.CreateString(string.IsNullOrEmpty(spec.Destination) ? "player_supply" : spec.Destination) });
                entries.Add(new NormalizedValueEntry { Name = "reasonId", Value = NormalizedValue.CreateString(string.IsNullOrEmpty(spec.ReasonId) ? "rule.unspecified" : spec.ReasonId) });
                if (!string.IsNullOrWhiteSpace(spec.TargetInfluenceId))
                {
                    entries.Add(new NormalizedValueEntry { Name = "targetInfluence", Value = NormalizedValue.CreateStableReference("influence", spec.TargetInfluenceId) });
                }
                else
                {
                    var candidateValues = new List<NormalizedValue>();
                    for (int i = 0; spec.CandidateIds != null && i < spec.CandidateIds.Count; i++)
                    {
                        candidateValues.Add(NormalizedValue.CreateStableReference("influence", spec.CandidateIds[i]));
                    }
                    entries.Add(new NormalizedValueEntry { Name = "candidateScope", Value = NormalizedValue.CreateArray(candidateValues) });
                }
            }
            else if (spec.EffectTypeId == InfluenceEffectTypeIds.MoveInfluence)
            {
                entries.Add(new NormalizedValueEntry { Name = "targetInfluence", Value = NormalizedValue.CreateStableReference("influence", spec.TargetInfluenceId) });
                entries.Add(new NormalizedValueEntry { Name = "targetSlotId", Value = NormalizedValue.CreateStableReference("slot", spec.TargetSlotId) });
            }
            else
            {
                entries.Add(new NormalizedValueEntry { Name = "placementFailurePolicy", Value = NormalizedValue.CreateString("keep_removal") });
                entries.Add(new NormalizedValueEntry { Name = "replacementOwner", Value = CreateSubject(spec.ReplacementOwnerId) });
                entries.Add(new NormalizedValueEntry { Name = "replacementSource", Value = CreateSource(spec.ReplacementSourceId, spec.ReplacementOwnerId) });
                entries.Add(new NormalizedValueEntry { Name = "targetInfluence", Value = NormalizedValue.CreateStableReference("influence", spec.TargetInfluenceId) });
            }

            return NormalizedValue.CreateObject(entries);
        }

        private static NormalizedValue CreateSubject(string playerId)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "instanceId", Value = NormalizedValue.CreateString(playerId ?? string.Empty) },
                new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(ParsePlayerId(playerId)) },
                new NormalizedValueEntry { Name = "subjectType", Value = NormalizedValue.CreateString("player") }
            });
        }

        private static NormalizedValue CreateSource(string sourceId, string playerId)
        {
            return NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "kind", Value = NormalizedValue.CreateString("player_supply") },
                new NormalizedValueEntry { Name = "sourceId", Value = NormalizedValue.CreateString(sourceId ?? string.Empty) },
                new NormalizedValueEntry { Name = "subject", Value = CreateSubject(playerId) }
            });
        }

        private static int ParsePlayerId(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return -1;
            var value = playerId.StartsWith("p", StringComparison.OrdinalIgnoreCase) ? playerId.Substring(1) : playerId;
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : -1;
        }

        private static string ValidateCityStyleStep(LuaEffectSpec spec)
        {
            if (spec == null || string.IsNullOrWhiteSpace(spec.Operation) ||
                string.IsNullOrWhiteSpace(spec.ExecutingPlayerId)) return "特殊行动 ScriptStep 必须提供 operation 和 executingPlayer。";
            if (spec.MinSelections < 0 || spec.MaxSelections < spec.MinSelections) return "特殊行动选择数量约束无效。";
            return string.Empty;
        }

        private static string ValidateCityStyleMarker(LuaEffectSpec spec)
        {
            return spec == null || string.IsNullOrWhiteSpace(spec.Operation)
                ? "OperatePlayerMarker 必须提供 operation。" : string.Empty;
        }

        private static string ValidateCityStyleGrant(LuaEffectSpec spec)
        {
            return spec == null || spec.Amount <= 0 ? "GrantMainActions 必须提供正 amount。" : string.Empty;
        }

        private static NormalizedValue CreateCityStyleArguments(LuaEffectSpec spec)
        {
            var entries = new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "operation", Value = NormalizedValue.CreateString(spec.Operation) },
                new NormalizedValueEntry { Name = "executingPlayer", Value = NormalizedValue.CreateStableReference("player", spec.ExecutingPlayerId) },
                new NormalizedValueEntry { Name = "candidateSetId", Value = NormalizedValue.CreateString(spec.CandidateSetId) },
                new NormalizedValueEntry { Name = "candidateResolutionId", Value = NormalizedValue.CreateString(spec.CandidateSetId) },
                new NormalizedValueEntry { Name = "candidateSetVersion", Value = NormalizedValue.CreateInteger(spec.CandidateSetVersion) },
                new NormalizedValueEntry { Name = "candidateIds", Value = CreateCandidateArray(spec.CandidateIds) },
                new NormalizedValueEntry { Name = "minSelections", Value = NormalizedValue.CreateInteger(spec.MinSelections) },
                new NormalizedValueEntry { Name = "maxSelections", Value = NormalizedValue.CreateInteger(spec.MaxSelections) },
                new NormalizedValueEntry { Name = "promptKey", Value = NormalizedValue.CreateString(spec.PromptKey) },
                new NormalizedValueEntry { Name = "amount", Value = NormalizedValue.CreateInteger(spec.Amount) },
                new NormalizedValueEntry { Name = "resourceType", Value = NormalizedValue.CreateString(spec.ResourceTypeId) },
                new NormalizedValueEntry { Name = "markerId", Value = NormalizedValue.CreateString(spec.MarkerId) }
            };
            return NormalizedValue.CreateObject(entries);
        }

        private static NormalizedValue CreateCandidateArray(IReadOnlyList<string> candidateIds)
        {
            var values = new List<NormalizedValue>();
            if (candidateIds != null)
            {
                for (int i = 0; i < candidateIds.Count; i++)
                    values.Add(NormalizedValue.CreateStableReference("candidate", candidateIds[i] ?? string.Empty));
            }
            return NormalizedValue.CreateArray(values);
        }
    }

    public sealed class LuaEffectSpecCompiler
    {
        private readonly LuaEffectTypeCatalog catalog;

        public LuaEffectSpecCompiler(LuaEffectTypeCatalog catalog = null)
        {
            this.catalog = catalog ?? new LuaEffectTypeCatalog();
        }

        public LuaEffectTypeCatalog Catalog { get { return catalog; } }

        public bool TryCompile(
            LuaInvocationResult invocation,
            LuaEffectCompilationContext context,
            out List<EffectSpec> specs,
            out LuaEffectCompilationFault fault)
        {
            specs = new List<EffectSpec>();
            fault = null;
            if (invocation == null || !invocation.IsSuccess)
            {
                fault = new LuaEffectCompilationFault(
                    invocation == null ? LuaFailureCode.InvalidDefinition : invocation.FailureCode,
                    invocation == null ? "Lua invocation 为空。" : invocation.Diagnostic);
                return false;
            }

            if (context == null)
            {
                fault = new LuaEffectCompilationFault(LuaFailureCode.InvalidDefinition, "EffectSpec 编译上下文为空。");
                return false;
            }

            IReadOnlyList<LuaEffectSpec> source = invocation.Effects ?? Array.Empty<LuaEffectSpec>();
            var compiled = new List<EffectSpec>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                LuaEffectSpec sourceSpec = source[i];
                LuaEffectTypeRegistration registration;
                if (sourceSpec == null || !catalog.TryGet(sourceSpec.EffectTypeId, out registration))
                {
                    fault = new LuaEffectCompilationFault(
                        LuaFailureCode.UnknownEffectType,
                        "Lua EffectSpec 类型未登记。",
                        i);
                    return false;
                }

                string validation = registration.Validator == null ? string.Empty : registration.Validator(sourceSpec);
                if (!string.IsNullOrEmpty(validation))
                {
                    fault = new LuaEffectCompilationFault(LuaFailureCode.InvalidEffectSpec, validation, i);
                    return false;
                }

                NormalizedValue arguments;
                try
                {
                    arguments = registration.ArgumentBuilder(sourceSpec);
                }
                catch (Exception exception)
                {
                    fault = new LuaEffectCompilationFault(
                        LuaFailureCode.InvalidEffectSpec,
                        Sanitize(exception.Message),
                        i);
                    return false;
                }

                string reason = string.Empty;
                if (arguments == null || !arguments.TryValidate(out reason))
                {
                    fault = new LuaEffectCompilationFault(LuaFailureCode.InvalidEffectSpec, reason, i);
                    return false;
                }

                var spec = EffectSpec.Create(
                    sourceSpec.EffectTypeId,
                    arguments,
                    context.PlayerId);
                // 脚本版本属于内容/handler 收据；节点执行版本由正式 Registry 的
                // EffectRegistration 决定，避免同一通用 Effect 因内容版本不同而失配。
                spec.DefinitionVersion = string.Empty;
                spec.SourceId = string.IsNullOrEmpty(context.SourceId) ? invocation.ContentId : context.SourceId;
                spec.StableKey = BuildStableKey(context.StableKeyPrefix, i);
                spec.CompletionHandlerId = context.CompletionHandlerId ?? string.Empty;
                spec.ContinuationContentId = invocation.ContentId ?? string.Empty;
                spec.ContinuationContentInstanceId = context.ContentInstanceId ?? string.Empty;
                spec.ContinuationAbilityId = string.IsNullOrEmpty(context.AbilityId)
                    ? invocation.HandlerId
                    : context.AbilityId;
                spec.ContinuationDefinitionVersion = invocation.DefinitionVersion ?? string.Empty;
                spec.ContinuationContentHash = invocation.ContentHash ?? string.Empty;

                ApplyExecutionOptions(spec);
                if (!TryCompileGroups(sourceSpec, spec, context.PlayerId, 0, out fault)) return false;
                compiled.Add(spec);
            }

            specs = compiled;
            return true;
        }

        // 嵌套结构递归编译；continuation 仅属于显式绑定的顶层节点，不能复制到每个支付子节点。
        private bool TryCompileGroups(LuaEffectSpec source, EffectSpec target, int playerId, int depth,
            out LuaEffectCompilationFault fault)
        {
            fault = null;
            if (depth > 16)
            {
                fault = new LuaEffectCompilationFault(LuaFailureCode.InvalidEffectSpec, "Effect 嵌套层数超过 16。");
                return false;
            }
            if (source.NestedEffectGroups == null) return true;
            for (int group = 0; group < source.NestedEffectGroups.Length; group++)
            {
                var children = new List<EffectSpec>();
                var sources = source.NestedEffectGroups[group];
                for (int i = 0; sources != null && i < sources.Count; i++)
                {
                    var child = sources[i];
                    LuaEffectTypeRegistration registration;
                    if (child == null || !catalog.TryGet(child.EffectTypeId, out registration))
                    {
                        fault = new LuaEffectCompilationFault(LuaFailureCode.UnknownEffectType, "嵌套 Effect 类型未登记。");
                        return false;
                    }
                    try
                    {
                        string error = registration.Validator == null ? string.Empty : registration.Validator(child);
                        if (!string.IsNullOrEmpty(error)) throw new ArgumentException(error);
                        var args = registration.ArgumentBuilder(child);
                        if (args == null || !args.TryValidate(out error)) throw new ArgumentException(error);
                        var compiled = EffectSpec.Create(child.EffectTypeId, args, playerId);
                        compiled.DefinitionVersion = string.Empty;
                        compiled.SourceId = target.SourceId;
                        compiled.StableKey = BuildStableKey(target.StableKey + ":group:" + group, i);
                        ApplyExecutionOptions(compiled);
                        if (!TryCompileGroups(child, compiled, playerId, depth + 1, out fault)) return false;
                        children.Add(compiled);
                    }
                    catch (Exception exception)
                    {
                        fault = new LuaEffectCompilationFault(LuaFailureCode.InvalidEffectSpec, Sanitize(exception.Message));
                        return false;
                    }
                }
                target.NestedEffectGroups.Add(children);
            }
            return true;
        }

        private static void ApplyExecutionOptions(EffectSpec spec)
        {
            var args = spec.NormalizedArguments;
            if (args == null || args.Properties == null) return;
            foreach (var entry in args.Properties)
            {
                if (entry == null || entry.Value == null) continue;
                if (entry.Name == "allowDecline" && entry.Value.Kind == NormalizedValueKind.Boolean)
                    spec.AllowDecline = entry.Value.BooleanValue;
                if (entry.Name == "decisionPlayer")
                {
                    int id;
                    string value = entry.Value.Kind == NormalizedValueKind.StableReference
                        ? entry.Value.ReferenceId : entry.Value.StringValue;
                    if (int.TryParse(value, out id)) spec.DecisionPlayerId = id;
                }
                if (entry.Name == "declinePromptKey" && entry.Value.Kind == NormalizedValueKind.String)
                    spec.DeclinePromptKey = entry.Value.StringValue;
            }
        }

        private static string BuildStableKey(string prefix, int index)
        {
            string normalized = prefix ?? string.Empty;
            return normalized + (normalized.Length == 0 ? string.Empty : ":") + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Sanitize(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "EffectSpec 编译失败。";
            string oneLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return oneLine.Length <= 512 ? oneLine : oneLine.Substring(0, 512);
        }
    }
}
