using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.State;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;

namespace YC.Infrastructure.Lua
{
    /// <summary>
    /// 最小 Lua 宿主验证器：Lua 只生成可校验的 EffectSpec，不能直接接触 Unity、领域对象或可写状态。
    /// </summary>
    public sealed partial class MoonSharpLuaRuntimeHost
    {
        // 只记录宿主创建的只读代理；归一化不能执行来自脚本的任意元方法。
        // 弱键避免静态注册表延长 Lua VM 的生命周期。
        private static readonly ConditionalWeakTable<Table, Table> ReadOnlySnapshotTables =
            new ConditionalWeakTable<Table, Table>();

        private static Table SnapshotValues(Table table)
        {
            Table values;
            return ReadOnlySnapshotTables.TryGetValue(table, out values) ? values : table;
        }

        private const string EffectSpecKind = "effect_spec";
        private const string GainResourceEffectType = "effect.resource.gain";
        private const string PayResourceEffectType = "effect.resource.pay";
        private const string GainScoreEffectType = "effect.score.gain";
        private const string SetLocationsOpenEffectType = "effect.map.set_locations_open";
        private const string PlaceInfluenceEffectType = InfluenceEffectTypeIds.PlaceInfluence;
        private const string RemoveInfluenceEffectType = InfluenceEffectTypeIds.RemoveInfluence;
        private const string MoveInfluenceEffectType = InfluenceEffectTypeIds.MoveInfluence;
        private const string ReplaceInfluenceEffectType = InfluenceEffectTypeIds.ReplaceInfluence;
        private const string CityStyleMarkerEffectType = CityStyleSpecialActionEffectTypeIds.OperatePlayerMarker;
        private const string MoveCityEffectType = CityMoveEffectTypeIds.Move;
        private const string RevealEventCardEffectType = EventCardEffectTypeIds.Resolve;
        private const string ResolveCardEffectType = CharacterAbilityEffectExecutor.AbilityEffectTypeId;
        private const string CoverCardEffectType = CharacterCoverEffectExecutor.EffectTypeId;
        private const int MaxSetLocationsOpenCount = 16;

        // 不启用 Math 是为了连同 math.random/randomseed 一起排除；确定性数值运算仍由 Lua 语言本身提供。
        private const CoreModules SandboxedModules =
            CoreModules.GlobalConsts |
            CoreModules.TableIterators |
            CoreModules.String |
            CoreModules.Table |
            CoreModules.Basic |
            CoreModules.Bit32;

        private readonly LuaExecutionBudget _budget;

        public MoonSharpLuaRuntimeHost(LuaExecutionBudget budget = null)
        {
            _budget = budget ?? LuaExecutionBudget.Default;
        }

        public LuaInvocationResult Invoke(
            LuaScriptDefinition definition,
            LuaInvocationContext context)
        {
            LuaFailureCode validationCode;
            string validationDiagnostic;
            if (!TryValidateDefinition(definition, context, out validationCode, out validationDiagnostic))
            {
                return LuaInvocationResult.Failure(definition, validationCode, validationDiagnostic);
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            ExecutionGuard guard = new ExecutionGuard(_budget.MaxInstructions, _budget.MaxMilliseconds, stopwatch);

            try
            {
                // 只允许显式注册的回调；本宿主不注册任何 UserData，因此 Lua 永远拿不到 CLR 对象。
                UserData.RegistrationPolicy = InteropRegistrationPolicy.Default;

                Script script = new Script(SandboxedModules);
                script.Options.DebugPrint = _ => { };
                InstallForbiddenGlobals(script, guard);

                bool settlementResponse = context.ResponseKind == "settlementOffers" || context.ResponseKind == "settlementAvailability";
                if (!settlementResponse)
                {
                    Table gameData = CreateGameData(script, context);
                    Table playerData = CreatePlayerData(script, context);
                    InstallReadApis(script, gameData, playerData, context, guard);
                }
                if (context.ResponseKind != "settlementAvailability") InstallEffectConstructors(script, guard);
                InstallTextConstructors(script, guard);
                if (context.ResponseKind != "settlementAvailability") InstallSettlementConstructors(script, guard);

                DynValue chunk = script.LoadString(definition.Source, null, definition.ContentId);
                Coroutine chunkCoroutine = script.CreateCoroutine(chunk).Coroutine;
                DynValue handler = ResumeWithBudget(chunkCoroutine, Array.Empty<DynValue>(), guard);
                if (!string.IsNullOrEmpty(context.EntryPoint)) handler = handler.Type == DataType.Table ? handler.Table.Get(context.EntryPoint) : script.Globals.Get(context.EntryPoint);
                if (handler.Type != DataType.Function)
                {
                    return LuaInvocationResult.Failure(
                        definition,
                        LuaFailureCode.InvalidHandler,
                        "Lua 内容必须返回一个 handler(ctx) 函数。");
                }

                Coroutine handlerCoroutine = script.CreateCoroutine(handler).Coroutine;
                DynValue contextValue = settlementResponse ? CreateSettlementContext(script, context, guard) : CreateInvocationContext(script, context, guard);
                DynValue rawResult = ResumeWithBudget(handlerCoroutine, new[] { contextValue }, guard);
                if (settlementResponse) return NormalizeSettlementResponse(definition, context, rawResult);

                LuaFailureCode resultCode;
                string resultDiagnostic;
                List<LuaEffectSpec> effects;
                List<LuaCandidatePatch> candidatePatches;
                if (string.Equals(context.ResponseKind, "candidatePatches", StringComparison.Ordinal))
                {
                    if (!TryNormalizeCandidatePatches(rawResult, out candidatePatches, out resultCode, out resultDiagnostic))
                    {
                        return LuaInvocationResult.Failure(definition, resultCode, resultDiagnostic);
                    }

                    return LuaInvocationResult.Success(
                        definition,
                        Array.Empty<LuaEffectSpec>(),
                        candidatePatches);
                }

                if (!TryNormalizeResult(rawResult, context, out effects, out resultCode, out resultDiagnostic))
                {
                    return LuaInvocationResult.Failure(definition, resultCode, resultDiagnostic);
                }

                return LuaInvocationResult.Success(definition, effects);
            }
            catch (LuaLimitException exception)
            {
                return LuaInvocationResult.Failure(definition, exception.Code, exception.Message);
            }
            catch (ScriptRuntimeException exception)
            {
                return LuaInvocationResult.Failure(
                    definition,
                    MapScriptRuntimeFailure(exception.Message),
                    SanitizeDiagnostic(exception.Message));
            }
            catch (SyntaxErrorException exception)
            {
                return LuaInvocationResult.Failure(
                    definition,
                    LuaFailureCode.ScriptError,
                    SanitizeDiagnostic(exception.Message));
            }
            catch (InterpreterException exception)
            {
                return LuaInvocationResult.Failure(
                    definition,
                    LuaFailureCode.ScriptError,
                    SanitizeDiagnostic(exception.Message));
            }
            catch (Exception exception)
            {
                // 未预期异常不能变成宿主崩溃，也不能把堆栈或脚本正文回传给网络层。
                return LuaInvocationResult.Failure(
                    definition,
                    LuaFailureCode.ScriptError,
                    SanitizeDiagnostic(exception.Message));
            }
        }

        private bool TryValidateDefinition(
            LuaScriptDefinition definition,
            LuaInvocationContext context,
            out LuaFailureCode code,
            out string diagnostic)
        {
            code = LuaFailureCode.None;
            diagnostic = string.Empty;

            if (definition == null || context == null)
            {
                code = LuaFailureCode.InvalidDefinition;
                diagnostic = "Lua 内容定义或调用上下文为空。";
                return false;
            }

            if (context.FacadeSnapshot.StateRevision.HasValue && context.FacadeSnapshot.StateRevision.Value != context.StateRevision)
            {
                code = LuaFailureCode.VersionMismatch;
                diagnostic = "nmc_query_revision_mismatch";
                return false;
            }

            if (context.ResponseKind != "effects" && context.ResponseKind != "candidatePatches" &&
                context.ResponseKind != "settlementOffers" && context.ResponseKind != "settlementAvailability")
            {
                code = LuaFailureCode.InvalidDefinition;
                diagnostic = "未知 Lua 返回合同。";
                return false;
            }

            if (!IsStableIdentifier(definition.ContentId) ||
                !IsStableIdentifier(definition.AbilityId) ||
                !IsStableIdentifier(definition.HandlerId) ||
                !IsStableIdentifier(definition.DefinitionVersion) ||
                !IsStableIdentifier(context.EventId) ||
                !IsStableIdentifier(context.EventType) ||
                !IsStableIdentifier(context.PlayerId))
            {
                code = LuaFailureCode.InvalidDefinition;
                diagnostic = "Lua 内容或调用上下文包含空的稳定标识。";
                return false;
            }

            if (definition.Source == null)
            {
                code = LuaFailureCode.InvalidDefinition;
                diagnostic = "Lua 内容正文为空。";
                return false;
            }

            if (Encoding.UTF8.GetByteCount(definition.Source) > _budget.MaxScriptLength)
            {
                code = LuaFailureCode.ScriptTooLarge;
                diagnostic = "Lua 内容超过脚本长度预算。";
                return false;
            }

            string actualHash = LuaContentHasher.ComputeSha256(definition.Source);
            if (!string.Equals(actualHash, definition.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                code = LuaFailureCode.ContentHashMismatch;
                diagnostic = "Lua 内容哈希与登记值不一致。";
                return false;
            }

            if (!string.Equals(definition.DefinitionVersion, context.DefinitionVersion, StringComparison.Ordinal))
            {
                code = LuaFailureCode.VersionMismatch;
                diagnostic = "Lua 内容版本与调用上下文版本不一致。";
                return false;
            }

            return true;
        }

        private static bool IsStableIdentifier(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
        }

        private static DynValue ResumeWithBudget(
            Coroutine coroutine,
            DynValue[] args,
            ExecutionGuard guard)
        {
            guard.ThrowIfTimeExceeded();
            coroutine.AutoYieldCounter = guard.MaxInstructions;
            DynValue result = args.Length == 0 ? coroutine.Resume() : coroutine.Resume(args);
            guard.ThrowIfTimeExceeded();

            if (result.Type == DataType.YieldRequest)
            {
                throw new LuaLimitException(
                    LuaFailureCode.InstructionBudgetExceeded,
                    "Lua handler 超过指令预算。");
            }

            return result;
        }

        private static void InstallReadApis(
            Script script,
            Table gameData,
            Table playerData,
            LuaInvocationContext context,
            ExecutionGuard guard)
        {
            Table gameDataApi = new Table(script);
            gameDataApi.Set("Get", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                if (args.Count != 0)
                {
                    throw new ScriptRuntimeException("nmc_invalid_game_data_arguments");
                }

                return DynValue.NewTable(gameData);
            }, "GameData.Get"));
            gameDataApi.Set("GetRound", DynValue.NewCallback((_, args) =>
                ReadInteger(args, context.FacadeSnapshot.Round, "GameData.GetRound", guard), "GameData.GetRound"));
            gameDataApi.Set("GetActionRound", DynValue.NewCallback((_, args) =>
                ReadInteger(args, context.FacadeSnapshot.ActionRound, "GameData.GetActionRound", guard), "GameData.GetActionRound"));
            gameDataApi.Set("GetPhase", DynValue.NewCallback((_, args) =>
                ReadString(args, context.FacadeSnapshot.PhaseId, "GameData.GetPhase", guard), "GameData.GetPhase"));
            gameDataApi.Set("GetPlayers", DynValue.NewCallback((_, args) =>
            {
                EnsureNoArguments(args, "GameData.GetPlayers");
                guard.ThrowIfTimeExceeded();
                var values = new List<DynValue>();
                for (int i = 0; i < context.FacadeSnapshot.Players.Count; i++)
                {
                    values.Add(CreatePlayerReference(script, context.FacadeSnapshot.Players[i].PlayerId, guard));
                }

                return CreateReadOnlyArray(script, values, "nmc_read_only_snapshot");
            }, "GameData.GetPlayers"));
            gameDataApi.Set("GetTurnOrder", DynValue.NewCallback((_, args) =>
                CreatePlayerReferenceArray(script, args, context.FacadeSnapshot.TurnOrder, "GameData.GetTurnOrder", guard), "GameData.GetTurnOrder"));
            gameDataApi.Set("GetStartPlayer", DynValue.NewCallback((_, args) =>
                ReadPlayerReference(script, args, context.FacadeSnapshot.StartPlayerId, "GameData.GetStartPlayer", guard), "GameData.GetStartPlayer"));
            gameDataApi.Set("GetCurrentPlayer", DynValue.NewCallback((_, args) =>
                ReadPlayerReference(script, args, context.FacadeSnapshot.CurrentPlayerId, "GameData.GetCurrentPlayer", guard), "GameData.GetCurrentPlayer"));
            gameDataApi.Set("GetEnabledDlcIds", DynValue.NewCallback((_, args) =>
            {
                if (!context.FacadeSnapshot.EnabledDlcIdsAvailable && context.FacadeSnapshot.EnabledDlcIds.Count == 0)
                    throw new ScriptRuntimeException("nmc_query_snapshot_unavailable:GameData.GetEnabledDlcIds");
                return CreateStringArrayResult(script, args, context.FacadeSnapshot.EnabledDlcIds, "GameData.GetEnabledDlcIds", guard);
            }, "GameData.GetEnabledDlcIds"));
            gameDataApi.Set("IsDlcEnabled", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                if (args.Count != 1 || args[0].Type != DataType.String) throw new ScriptRuntimeException("nmc_invalid_game_data_arguments");
                if (!context.FacadeSnapshot.EnabledDlcIdsAvailable && context.FacadeSnapshot.EnabledDlcIds.Count == 0)
                    throw new ScriptRuntimeException("nmc_query_snapshot_unavailable:GameData.IsDlcEnabled");
                return DynValue.NewBoolean(context.FacadeSnapshot.EnabledDlcIds.Contains(args[0].String));
            }, "GameData.IsDlcEnabled"));
            script.Globals.Set("GameData", DynValue.NewTable(CreateReadOnlyProxy(script, gameDataApi, "nmc_read_only_snapshot")));

            Table playerDataApi = new Table(script);
            playerDataApi.Set("Get", DynValue.NewCallback((_, args) =>
            {
                LuaPlayerSnapshot snapshot = ResolvePlayer(context, args, "PlayerData.Get", false, guard);

                return DynValue.NewTable(CreatePlayerSnapshot(
                    script,
                    snapshot,
                    snapshot.PlayerId == context.PlayerId || context.AllowOtherPlayerReads,
                    guard));
            }, "PlayerData.Get"));
            playerDataApi.Set("GetScore", DynValue.NewCallback((_, args) =>
                GetPlayerInteger(script, context, args, snapshot => snapshot.Score, "PlayerData.GetScore", guard), "PlayerData.GetScore"));
            playerDataApi.Set("GetResources", DynValue.NewCallback((_, args) =>
                GetPlayerResources(script, context, args, guard), "PlayerData.GetResources"));
            playerDataApi.Set("GetCityLocation", DynValue.NewCallback((_, args) =>
                GetPlayerString(context, args, snapshot => snapshot.CityLocationId, "PlayerData.GetCityLocation", guard), "PlayerData.GetCityLocation"));
            playerDataApi.Set("GetHand", DynValue.NewCallback((_, args) =>
                GetPlayerStringArray(script, context, args, snapshot => snapshot.HandCardIds, "PlayerData.GetHand", true, guard), "PlayerData.GetHand"));
            playerDataApi.Set("GetDiscard", DynValue.NewCallback((_, args) =>
                GetPlayerStringArray(script, context, args, snapshot => snapshot.DiscardCardIds, "PlayerData.GetDiscard", true, guard), "PlayerData.GetDiscard"));
            playerDataApi.Set("GetBuiltFacilities", DynValue.NewCallback((_, args) =>
            {
                LuaPlayerSnapshot snapshot = ResolvePlayer(context, args, "PlayerData.GetBuiltFacilities", false, guard);
                return SnapshotToDynValue(script, LuaSnapshotValue.Array(snapshot.BuiltFacilities), guard, "nmc_read_only_snapshot");
            }, "PlayerData.GetBuiltFacilities"));
            script.Globals.Set("PlayerData", DynValue.NewTable(CreateReadOnlyProxy(script, playerDataApi, "nmc_read_only_snapshot")));

            InstallGlobalApi(script, context, guard);
        }

        private static Table CreateGameData(Script script, LuaInvocationContext context)
        {
            Table values = new Table(script);
            values.Set("eventId", DynValue.NewString(context.EventId));
            values.Set("eventType", DynValue.NewString(context.EventType));
            values.Set("stateRevision", DynValue.NewNumber(context.StateRevision));
            values.Set("playerCount", DynValue.NewNumber(context.PlayerCount));
            values.Set("definitionVersion", DynValue.NewString(context.DefinitionVersion));
            values.Set("gameId", DynValue.NewString(context.FacadeSnapshot.GameId ?? string.Empty));
            values.Set("phaseId", DynValue.NewString(context.FacadeSnapshot.PhaseId ?? string.Empty));
            values.Set("round", DynValue.NewNumber(context.FacadeSnapshot.Round));
            values.Set("maxRounds", DynValue.NewNumber(context.FacadeSnapshot.MaxRounds));
            values.Set("actionRound", DynValue.NewNumber(context.FacadeSnapshot.ActionRound));
            values.Set("startPlayerId", DynValue.NewString(context.FacadeSnapshot.StartPlayerId ?? string.Empty));
            values.Set("currentPlayerId", DynValue.NewString(context.FacadeSnapshot.CurrentPlayerId ?? string.Empty));
            values.Set("contentVersion", DynValue.NewString(context.FacadeSnapshot.ContentVersion ?? string.Empty));
            return CreateReadOnlyProxy(script, values, "nmc_read_only_snapshot");
        }

        private static Table CreatePlayerData(Script script, LuaInvocationContext context)
        {
            Table values = new Table(script);
            values.Set("playerId", DynValue.NewString(context.PlayerId));
            values.Set("score", DynValue.NewNumber(context.PlayerScore));
            values.Set("goldVoucherCount", DynValue.NewNumber(context.GoldVoucherCount));
            values.Set("availableGoldVoucherCount", DynValue.NewNumber(context.AvailableGoldVoucherCount));
            values.Set("reservedGoldVoucherCount", DynValue.NewNumber(context.ReservedGoldVoucherCount));
            return CreateReadOnlyProxy(script, values, "nmc_read_only_snapshot");
        }

        private static void InstallGlobalApi(
            Script script,
            LuaInvocationContext context,
            ExecutionGuard guard)
        {
            Table map = new Table(script);
            map.Set("GetMap", DynValue.NewCallback((_, args) =>
            {
                EnsureNoArguments(args, "Global.Map.GetMap");
                guard.ThrowIfTimeExceeded();
                QueryCollection(context.FacadeSnapshot.GlobalMap, "locations", "Global.Map.GetMap");
                return SnapshotToDynValue(script, context.FacadeSnapshot.GlobalMap, guard, "nmc_read_only_snapshot");
            }, "Global.Map.GetMap"));
            map.Set("GetLocation", DynValue.NewCallback((_, args) =>
                FindSnapshotItem(script, context.FacadeSnapshot.GlobalMap, args, "locations", "Global.Map.GetLocation", guard), "Global.Map.GetLocation"));
            map.Set("GetRoute", DynValue.NewCallback((_, args) =>
                FindSnapshotItem(script, context.FacadeSnapshot.GlobalMap, args, "routes", "Global.Map.GetRoute", guard, "routeId"), "Global.Map.GetRoute"));
            map.Set("GetRegion", DynValue.NewCallback((_, args) =>
                FindSnapshotItem(script, context.FacadeSnapshot.GlobalMap, args, "regions", "Global.Map.GetRegion", guard, "regionId"), "Global.Map.GetRegion"));
            map.Set("GetConnectedLocations", DynValue.NewCallback((_, args) =>
                FindSnapshotItem(script, context.FacadeSnapshot.GlobalMap, args, "connectedLocations", "Global.Map.GetConnectedLocations", guard, "locationId", true), "Global.Map.GetConnectedLocations"));
            map.Set("GetRoutesBetween", DynValue.NewCallback((_, args) =>
                QueryRoutesBetween(script, context.FacadeSnapshot.GlobalMap, args, guard), "Global.Map.GetRoutesBetween"));
            map.Set("GetInfluenceSlot", DynValue.NewCallback((_, args) =>
                FindSnapshotItem(script, context.FacadeSnapshot.GlobalMap, args, "influenceSlots", "Global.Map.GetInfluenceSlot", guard, "slotId"), "Global.Map.GetInfluenceSlot"));
            map.Set("GetResourcePoint", DynValue.NewCallback((_, args) =>
                FindSnapshotItem(script, context.FacadeSnapshot.GlobalMap, args, "resourcePoints", "Global.Map.GetResourcePoint", guard), "Global.Map.GetResourcePoint"));
            map.Set("GetCitiesAt", DynValue.NewCallback((_, args) =>
                FindSnapshotItem(script, context.FacadeSnapshot.GlobalMap, args, "citiesAt", "Global.Map.GetCitiesAt", guard, "locationId", true), "Global.Map.GetCitiesAt"));
            map.Set("IsRoad", DynValue.NewCallback((_, args) =>
                ReadSnapshotBoolean(context.FacadeSnapshot.GlobalMap, args, "roads", "Global.Map.IsRoad", guard), "Global.Map.IsRoad"));
            map.Set("FindInfluences", DynValue.NewCallback((_, args) =>
                ReadSnapshotCollection(script, context.FacadeSnapshot.GlobalMap, args, "influences", "Global.Map.FindInfluences", guard), "Global.Map.FindInfluences"));

            Table content = new Table(script);
            content.Set("GetDefinition", DynValue.NewCallback((_, args) =>
                FindSnapshotItem(script, context.FacadeSnapshot.GlobalContent, args, "definitions", "Global.Content.GetDefinition", guard, "id"), "Global.Content.GetDefinition"));
            content.Set("GetInstance", DynValue.NewCallback((_, args) =>
                FindSnapshotItem(script, context.FacadeSnapshot.GlobalContent, args, "instances", "Global.Content.GetInstance", guard, "id"), "Global.Content.GetInstance"));
            content.Set("FindZones", DynValue.NewCallback((_, args) =>
                ReadSnapshotCollection(script, context.FacadeSnapshot.GlobalContent, args, "zones", "Global.Content.FindZones", guard), "Global.Content.FindZones"));
            content.Set("FindInstances", DynValue.NewCallback((_, args) =>
                ReadSnapshotCollection(script, context.FacadeSnapshot.GlobalContent, args, "instances", "Global.Content.FindInstances", guard), "Global.Content.FindInstances"));

            Table global = new Table(script);
            global.Set("Map", DynValue.NewTable(CreateReadOnlyProxy(script, map, "nmc_read_only_snapshot")));
            global.Set("Content", DynValue.NewTable(CreateReadOnlyProxy(script, content, "nmc_read_only_snapshot")));
            script.Globals.Set("Global", DynValue.NewTable(CreateReadOnlyProxy(script, global, "nmc_read_only_snapshot")));
        }

        private static DynValue ReadInteger(
            CallbackArguments args,
            int value,
            string apiName,
            ExecutionGuard guard)
        {
            EnsureNoArguments(args, apiName);
            guard.ThrowIfTimeExceeded();
            return DynValue.NewNumber(value);
        }

        private static DynValue ReadString(
            CallbackArguments args,
            string value,
            string apiName,
            ExecutionGuard guard)
        {
            EnsureNoArguments(args, apiName);
            guard.ThrowIfTimeExceeded();
            return string.IsNullOrEmpty(value) ? DynValue.Nil : DynValue.NewString(value);
        }

        private static void EnsureNoArguments(CallbackArguments args, string apiName)
        {
            if (args.Count != 0) throw new ScriptRuntimeException("nmc_invalid_api_arguments:" + apiName);
        }

        private static DynValue CreateStringArrayResult(
            Script script,
            CallbackArguments args,
            IList<string> values,
            string apiName,
            ExecutionGuard guard)
        {
            EnsureNoArguments(args, apiName);
            guard.ThrowIfTimeExceeded();
            var result = new List<DynValue>();
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++) result.Add(DynValue.NewString(values[i] ?? string.Empty));
            }

            return CreateReadOnlyArray(script, result, "nmc_read_only_snapshot");
        }

        private static DynValue CreatePlayerReferenceArray(
            Script script,
            CallbackArguments args,
            IList<string> values,
            string apiName,
            ExecutionGuard guard)
        {
            EnsureNoArguments(args, apiName);
            guard.ThrowIfTimeExceeded();
            var result = new List<DynValue>();
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    result.Add(CreatePlayerReference(script, values[i], guard));
                }
            }

            return CreateReadOnlyArray(script, result, "nmc_read_only_snapshot");
        }

        private static DynValue ReadPlayerReference(
            Script script,
            CallbackArguments args,
            string playerId,
            string apiName,
            ExecutionGuard guard)
        {
            EnsureNoArguments(args, apiName);
            guard.ThrowIfTimeExceeded();
            return string.IsNullOrEmpty(playerId)
                ? DynValue.Nil
                : CreatePlayerReference(script, playerId, guard);
        }

        private static DynValue CreatePlayerReference(Script script, string playerId, ExecutionGuard guard)
        {
            guard.ThrowIfTimeExceeded();
            Table values = new Table(script);
            values.Set("playerId", DynValue.NewString(playerId));
            Table proxy = CreateReadOnlyProxy(script, values, "nmc_read_only_snapshot");
            // The stable reference is intentionally materialized on the proxy itself.
            // Lua can still only read it, while host-side normalization can inspect it
            // without invoking MoonSharp's __index metamethod.
            proxy.Set("playerId", DynValue.NewString(playerId));
            return DynValue.NewTable(proxy);
        }

        private static DynValue GetPlayerInteger(
            Script script,
            LuaInvocationContext context,
            CallbackArguments args,
            Func<LuaPlayerSnapshot, int> selector,
            string apiName,
            ExecutionGuard guard)
        {
            LuaPlayerSnapshot snapshot = ResolvePlayer(context, args, apiName, false, guard);
            return DynValue.NewNumber(selector(snapshot));
        }

        private static DynValue GetPlayerString(
            LuaInvocationContext context,
            CallbackArguments args,
            Func<LuaPlayerSnapshot, string> selector,
            string apiName,
            ExecutionGuard guard)
        {
            LuaPlayerSnapshot snapshot = ResolvePlayer(context, args, apiName, false, guard);
            string value = selector(snapshot);
            return string.IsNullOrEmpty(value) ? DynValue.Nil : DynValue.NewString(value);
        }

        private static DynValue GetPlayerStringArray(
            Script script,
            LuaInvocationContext context,
            CallbackArguments args,
            Func<LuaPlayerSnapshot, IList<string>> selector,
            string apiName,
            bool privateData,
            ExecutionGuard guard)
        {
            LuaPlayerSnapshot snapshot = ResolvePlayer(context, args, apiName, false, guard);
            var values = new List<DynValue>();
            if (privateData && snapshot.PlayerId != context.PlayerId && !context.AllowOtherPlayerReads)
            {
                return CreateReadOnlyArray(script, values, "nmc_read_only_snapshot");
            }

            IList<string> source = selector(snapshot);
            if (source != null)
            {
                for (int i = 0; i < source.Count; i++) values.Add(DynValue.NewString(source[i] ?? string.Empty));
            }

            return CreateReadOnlyArray(script, values, "nmc_read_only_snapshot");
        }

        private static DynValue GetPlayerResources(
            Script script,
            LuaInvocationContext context,
            CallbackArguments args,
            ExecutionGuard guard)
        {
            LuaPlayerSnapshot snapshot = ResolvePlayer(context, args, "PlayerData.GetResources", false, guard);
            return SnapshotToDynValue(
                script,
                LuaSnapshotValue.Object(new[]
                {
                    new LuaSnapshotEntry("goldVoucher", LuaSnapshotValue.Integer(snapshot.GoldVoucherCount)),
                    new LuaSnapshotEntry("goldVoucherCount", LuaSnapshotValue.Integer(snapshot.GoldVoucherCount)),
                    new LuaSnapshotEntry("availableGoldVoucherCount", LuaSnapshotValue.Integer(snapshot.AvailableGoldVoucherCount)),
                    new LuaSnapshotEntry("reservedGoldVoucherCount", LuaSnapshotValue.Integer(snapshot.ReservedGoldVoucherCount)),
                    new LuaSnapshotEntry("originium", LuaSnapshotValue.Integer(snapshot.Originium)),
                    new LuaSnapshotEntry("originiumShard", LuaSnapshotValue.Integer(snapshot.OriginiumShard)),
                    new LuaSnapshotEntry("iron", LuaSnapshotValue.Integer(snapshot.Iron)),
                    new LuaSnapshotEntry("pureOriginium", LuaSnapshotValue.Integer(snapshot.PureOriginium))
                }),
                guard,
                "nmc_read_only_snapshot");
        }

        private static LuaPlayerSnapshot ResolvePlayer(
            LuaInvocationContext context,
            CallbackArguments args,
            string apiName,
            bool privateData,
            ExecutionGuard guard)
        {
            guard.ThrowIfTimeExceeded();
            string playerId;
            bool stable;
            LuaFailureCode code;
            string diagnostic;
            if (args.Count != 1 || !TryReadPlayerReference(args[0], "player", out playerId, out stable, out code, out diagnostic))
            {
                throw new ScriptRuntimeException("nmc_invalid_player_data_arguments:" + apiName);
            }

            LuaPlayerSnapshot snapshot = FindPlayer(context.FacadeSnapshot, playerId);
            if (snapshot == null || (privateData && !context.AllowOtherPlayerReads && snapshot.PlayerId != context.PlayerId))
            {
                throw new ScriptRuntimeException("nmc_forbidden_player_snapshot");
            }

            return snapshot;
        }

        private static LuaPlayerSnapshot FindPlayer(LuaFacadeSnapshot snapshot, string playerId)
        {
            if (snapshot == null || snapshot.Players == null) return null;
            for (int i = 0; i < snapshot.Players.Count; i++)
            {
                if (snapshot.Players[i] != null && snapshot.Players[i].PlayerId == playerId) return snapshot.Players[i];
            }

            return null;
        }

        private static Table CreatePlayerSnapshot(
            Script script,
            LuaPlayerSnapshot snapshot,
            bool includePrivateData,
            ExecutionGuard guard)
        {
            Table values = new Table(script);
            values.Set("playerId", DynValue.NewString(snapshot.PlayerId ?? string.Empty));
            values.Set("score", DynValue.NewNumber(snapshot.Score));
            values.Set("goldVoucherCount", DynValue.NewNumber(snapshot.GoldVoucherCount));
            values.Set("availableGoldVoucherCount", DynValue.NewNumber(snapshot.AvailableGoldVoucherCount));
            values.Set("reservedGoldVoucherCount", DynValue.NewNumber(snapshot.ReservedGoldVoucherCount));
            values.Set("cityLocationId", string.IsNullOrEmpty(snapshot.CityLocationId)
                ? DynValue.Nil
                : DynValue.NewString(snapshot.CityLocationId));
            foreach (var entry in snapshot.Facts.Properties) values.Set(entry.Name, SnapshotToDynValue(script, entry.Value, guard, "nmc_read_only_snapshot"));
            values.Set("handCount", DynValue.NewNumber(snapshot.HandCardIds == null ? 0 : snapshot.HandCardIds.Count));
            values.Set("discardCount", DynValue.NewNumber(snapshot.DiscardCardIds == null ? 0 : snapshot.DiscardCardIds.Count));
            values.Set("hand", includePrivateData
                ? GetStringArraySnapshot(script, snapshot.HandCardIds, guard)
                : CreateReadOnlyArray(script, new List<DynValue>(), "nmc_read_only_snapshot"));
            values.Set("discard", includePrivateData
                ? GetStringArraySnapshot(script, snapshot.DiscardCardIds, guard)
                : CreateReadOnlyArray(script, new List<DynValue>(), "nmc_read_only_snapshot"));
            return CreateReadOnlyProxy(script, values, "nmc_read_only_snapshot");
        }

        private static DynValue GetStringArraySnapshot(
            Script script,
            IList<string> values,
            ExecutionGuard guard)
        {
            var result = new List<DynValue>();
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    guard.ThrowIfTimeExceeded();
                    result.Add(DynValue.NewString(values[i] ?? string.Empty));
                }
            }

            return CreateReadOnlyArray(script, result, "nmc_read_only_snapshot");
        }

        private static DynValue FindSnapshotItem(
            Script script,
            LuaSnapshotValue root,
            CallbackArguments args,
            string collectionName,
            string apiName,
            ExecutionGuard guard,
            string idField = "locationId",
            bool emptyOnMissing = false)
        {
            guard.ThrowIfTimeExceeded();
            QueryStringArguments(args, 1, apiName);
            LuaSnapshotValue collection = QueryCollection(root, collectionName, apiName);
            if (collection.Kind == LuaSnapshotValueKind.Object)
            {
                LuaSnapshotValue value = collection.Get(args[0].String);
                return value.Kind == LuaSnapshotValueKind.Null
                    ? (emptyOnMissing ? CreateReadOnlyArray(script, new List<DynValue>(), "nmc_read_only_snapshot") : DynValue.Nil)
                    : SnapshotToDynValue(script, value, guard, "nmc_read_only_snapshot");
            }

            if (collection.Kind == LuaSnapshotValueKind.Array)
            {
                for (int i = 0; i < collection.Items.Count; i++)
                {
                    LuaSnapshotValue item = collection.Items[i];
                    LuaSnapshotValue itemId = item == null ? LuaSnapshotValue.Null : item.Get(idField);
                    if (item != null && item.Kind == LuaSnapshotValueKind.Object &&
                        itemId.Kind == LuaSnapshotValueKind.String &&
                        itemId.StringValue == args[0].String)
                    {
                        return SnapshotToDynValue(script, item, guard, "nmc_read_only_snapshot");
                    }
                }
            }

            return emptyOnMissing ? CreateReadOnlyArray(script, new List<DynValue>(), "nmc_read_only_snapshot") : DynValue.Nil;
        }

        private static DynValue ReadSnapshotCollection(
            Script script,
            LuaSnapshotValue root,
            CallbackArguments args,
            string collectionName,
            string apiName,
            ExecutionGuard guard)
        {
            return QueryFilteredCollection(script, root, args, collectionName, apiName, guard);
        }

        private static DynValue ReadSnapshotBoolean(
            LuaSnapshotValue root,
            CallbackArguments args,
            string collectionName,
            string apiName,
            ExecutionGuard guard)
        {
            guard.ThrowIfTimeExceeded();
            QueryStringArguments(args, 1, apiName);
            LuaSnapshotValue value = QueryCollection(root, collectionName, apiName).Get(args[0].String);
            return value.Kind == LuaSnapshotValueKind.Boolean ? DynValue.NewBoolean(value.BooleanValue) : DynValue.NewBoolean(false);
        }

        private static DynValue CreateReadOnlyArray(Script script, IList<DynValue> values, string writeError)
        {
            Table table = new Table(script);
            for (int i = 0; i < values.Count; i++) table.Set(i + 1, values[i]);
            return DynValue.NewTable(CreateReadOnlyProxy(script, table, writeError));
        }

        private static DynValue SnapshotToDynValue(
            Script script,
            LuaSnapshotValue value,
            ExecutionGuard guard,
            string writeError)
        {
            if (value == null || value.Kind == LuaSnapshotValueKind.Null) return DynValue.Nil;
            guard.ThrowIfTimeExceeded();
            switch (value.Kind)
            {
                case LuaSnapshotValueKind.Boolean:
                    return DynValue.NewBoolean(value.BooleanValue);
                case LuaSnapshotValueKind.Integer:
                    return DynValue.NewNumber(value.IntegerValue);
                case LuaSnapshotValueKind.String:
                    return DynValue.NewString(value.StringValue ?? string.Empty);
                case LuaSnapshotValueKind.Array:
                    var items = new List<DynValue>();
                    for (int i = 0; i < value.Items.Count; i++) items.Add(SnapshotToDynValue(script, value.Items[i], guard, writeError));
                    return CreateReadOnlyArray(script, items, writeError);
                case LuaSnapshotValueKind.Object:
                    Table table = new Table(script);
                    for (int i = 0; i < value.Properties.Count; i++)
                    {
                        table.Set(value.Properties[i].Name, SnapshotToDynValue(script, value.Properties[i].Value, guard, writeError));
                    }

                    return DynValue.NewTable(CreateReadOnlyProxy(script, table, writeError));
                default:
                    return DynValue.Nil;
            }
        }

        private static DynValue CreateInvocationContext(
            Script script,
            LuaInvocationContext context,
            ExecutionGuard guard)
        {
            Table values = new Table(script);
            values.Set("eventId", DynValue.NewString(context.EventId));
            values.Set("eventType", DynValue.NewString(context.EventType));
            values.Set("playerId", DynValue.NewString(context.PlayerId));
            values.Set("goldVoucherCount", DynValue.NewNumber(context.GoldVoucherCount));
            values.Set("availableGoldVoucherCount", DynValue.NewNumber(context.AvailableGoldVoucherCount));
            values.Set("reservedGoldVoucherCount", DynValue.NewNumber(context.ReservedGoldVoucherCount));
            values.Set("stateRevision", DynValue.NewNumber(context.StateRevision));
            values.Set("definitionVersion", DynValue.NewString(context.DefinitionVersion));
            values.Set("payload", SnapshotToDynValue(
                script,
                context.FacadeSnapshot.EventPayload,
                guard,
                "nmc_read_only_snapshot"));
            return DynValue.NewTable(CreateReadOnlyProxy(script, values, "nmc_read_only_context"));
        }

        private static Table CreateReadOnlyProxy(Script script, Table values, string writeError)
        {
            Table proxy = new Table(script);
            Table meta = new Table(script);
            meta.Set("__index", DynValue.NewCallback((_, args) =>
            {
                if (args.Count < 2)
                {
                    return DynValue.Nil;
                }

                return values.Get(args[1]);
            }, "read_only_index"));
            meta.Set("__len", DynValue.NewCallback((_, args) =>
            {
                return DynValue.NewNumber(values.Length);
            }, "read_only_length"));
            meta.Set("__ipairs", DynValue.NewCallback((_, args) =>
            {
                return DynValue.NewTuple(
                    DynValue.NewCallback((__, iteratorArgs) =>
                    {
                        if (iteratorArgs.Count < 2 || iteratorArgs[1].Type != DataType.Number)
                        {
                            return DynValue.Nil;
                        }

                        int index = ((int)iteratorArgs[1].Number) + 1;
                        DynValue value = values.Get(index);
                        return value.Type == DataType.Nil
                            ? DynValue.Nil
                            : DynValue.NewTuple(DynValue.NewNumber(index), value);
                    }, "read_only_next_i"),
                    DynValue.NewTable(proxy),
                    DynValue.NewNumber(0));
            }, "read_only_ipairs"));
            meta.Set("__pairs", DynValue.NewCallback((_, args) =>
            {
                var entries = values.Pairs.ToArray(); int index = 0;
                return DynValue.NewTuple(DynValue.NewCallback((__, iteratorArgs) => {
                    if (index >= entries.Length) return DynValue.Nil;
                    var pair = entries[index++]; return DynValue.NewTuple(pair.Key,pair.Value);
                }, "read_only_next"), DynValue.NewTable(proxy), DynValue.Nil);
            }, "read_only_pairs"));
            meta.Set("__newindex", DynValue.NewCallback((_, args) =>
            {
                throw new ScriptRuntimeException(writeError);
            }, "read_only_newindex"));
            proxy.MetaTable = meta;
            ReadOnlySnapshotTables.Add(proxy, values);
            return proxy;
        }

        public static IReadOnlyCollection<string> SupportedEffectConstructors => CreateRegisteredEffectConstructors().Keys.Concat(new[] { "WithPresentation", "WithContext" }).ToList().AsReadOnly();
        public static IReadOnlyCollection<string> SupportedFlowConstructors => Array.AsReadOnly(new[] { "DispatchOperator", "ReplaceHandWithOperator", "UpgradeDistinctEnterprises", "UpgradeThenActivateEnterpriseSpecial", "ActivateAllEnterpriseSpecials", "UpgradeEnterpriseForAllPlayers", "ClearRouteAndPlaceRoad", "AcquireReward" });
        private static Dictionary<string, string> CreateRegisteredEffectConstructors()
        {
            var registered = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Condition", EffectTypeIds.Condition },
                { "Choice", LuaDomainEffectTypeIds.Choice },
                { "Collect", ResourceCollectionEffectExecutor.TypeId },
                { "Repeat", LuaDomainEffectTypeIds.Repeat },
                { "MoveItem", ItemTransferEffectExecutor.TypeId },
                { "ShuffleDeck", DeckShuffleEffectExecutor.TypeId },
                { "ShuffleFacilityDeck", LuaDomainEffectTypeIds.ShuffleFacilityDeck },
                { "RefreshFacilitySupply", ItemTransferEffectExecutor.RefreshTypeId },
                { "RefillFacilitySlot", ItemTransferEffectExecutor.RefillTypeId },
                { "LoseResource", ResourceLossEffectExecutor.TypeId },
                { "LoadCardMarkers", ContentPrimitiveEffectExecutor.LoadMarkers },
                { "PayCardMarkers", ContentPrimitiveEffectExecutor.PayMarkers },
                { "StoreRemovedInfluence", ContentPrimitiveEffectExecutor.StoreRemoved },
                { "SelectInfluence", ContentPrimitiveEffectExecutor.SelectInfluence },
                { "GrantMainActions", CityStyleSpecialActionEffectTypeIds.GrantMainActions },
                { "RetireFacility", ContentLifecycleEffectExecutor.RetireFacilityTypeId },
                { "ReturnCityStyleMarker", ContentLifecycleEffectExecutor.ReturnMarkerTypeId },
                { "ChooseBuild", FacilitySelectionEffectExecutor.TypeId },
                { "ChooseExplore", ExplorationSelectionEffectExecutor.TypeId },
                { "OverrideNextStartPlayer", ContentPrimitiveEffectExecutor.OverrideNextStartPlayer },
                { "ActivateFacilityEntry", ContentPrimitiveEffectExecutor.ActivateFacilityEntry },
                { "ChooseResources", ContentPrimitiveEffectExecutor.ChooseResources },
                { "SellResource", LuaDomainEffectTypeIds.ResourceSell },
                { "MoveFacilityCard", LuaDomainEffectTypeIds.MoveFacilityCard },
                { "MoveCharacterCard", LuaDomainEffectTypeIds.MoveCharacterCard },
                { "GainResource", GainResourceEffectType },
                { "PayResource", PayResourceEffectType },
                { "GainScore", GainScoreEffectType },
                { "LoseScore", LuaDomainEffectTypeIds.LoseScore },
                { "UpgradeEnterprise", LuaDomainEffectTypeIds.UpgradeEnterprise },
                { "ChangeEnterpriseLevel", YC.Domain.Enterprises.EnterpriseEffectExecutor.ChangeLevelTypeId },
                { "ActivateEnterpriseSpecial", LuaDomainEffectTypeIds.ActivateEnterpriseSpecial },
                { "SwitchDepartment", LuaDomainEffectTypeIds.SwitchDepartment },
                { "MoveCity", MoveCityEffectType },
                { "PlaceInfluence", PlaceInfluenceEffectType },
                { "RemoveInfluence", RemoveInfluenceEffectType },
                { "MoveInfluence", MoveInfluenceEffectType },
                { "ReplaceInfluence", ReplaceInfluenceEffectType },
                { "PlaceRoad", LuaDomainEffectTypeIds.PlaceRoad },
                { "OperateToken", LuaDomainEffectTypeIds.OperateToken },
                { "SetLocationsOpen", SetLocationsOpenEffectType },
                { "OperatePlayerMarker", CityStyleMarkerEffectType },
                { "RevealEventCard", RevealEventCardEffectType },
                { "CoverCharacterCard", CoverCardEffectType },
                { "SetCharacterDoubleUseRule", LuaDomainEffectTypeIds.SetCharacterDoubleUseRule },
                { "TransferCharacterCard", CharacterLifecycleService.TransferTypeId },
                { "ConsumeCharacterDoubleUseSource", CharacterLifecycleService.ConsumeSourceTypeId },
                { "PaymentSubstitution", ResourcePaymentSubstitutionEffectExecutor.TypeId },
                { "Build", FacilityEntryEffectTypeIds.Build },
                { "Explore", ExplorationEffectTypeIds.Explore },
                { "ExecuteMainAction", LuaDomainEffectTypeIds.ExecuteMainAction },
                { "RollDice", LuaDomainEffectTypeIds.RollDice },
            };
            foreach (var constructor in CharacterContentEffectExecutor.Constructors) registered.Add(constructor.Key, CharacterContentEffectExecutor.Prefix + constructor.Value);
            foreach (var constructor in ContentTransactionEffectExecutor.Constructors) registered.Add(constructor.Key, ContentTransactionEffectExecutor.Prefix + constructor.Value);
            return registered;
        }
        private static void InstallEffectConstructors(Script script, ExecutionGuard guard)
        {
            Table effect = new Table(script);
            // 未完成宿主结算的规划 API 不对 Lua 暴露；持久化旧节点仍由 Registry 明确 fail-stop。
            var constructors = CreateRegisteredEffectConstructors();

            foreach (KeyValuePair<string, string> constructor in constructors)
            {
                string constructorName = constructor.Key;
                string effectTypeId = constructor.Value;
                effect.Set(constructorName, DynValue.NewCallback((_, args) =>
                {
                    guard.ThrowIfTimeExceeded();
                    return CreateEffectSpec(script, effectTypeId, args);
                }, "Effect." + constructorName));
            }
            InstallPresentationDecorator(script, effect, guard);
            script.Globals.Set("Effect", DynValue.NewTable(CreateReadOnlyProxy(script, effect, "nmc_read_only_effect_api")));

            Table flow = new Table(script);
            flow.Set("DispatchOperator", DynValue.NewCallback((_, args) => { guard.ThrowIfTimeExceeded(); return CreateEffectSpec(script, OperatorDispatchFlow.TypeId, args); }, "Flow.DispatchOperator"));
            flow.Set("ReplaceHandWithOperator", DynValue.NewCallback((_, args) => { guard.ThrowIfTimeExceeded(); return CreateEffectSpec(script, OperatorDispatchFlow.ReplaceTypeId, args); }, "Flow.ReplaceHandWithOperator"));
            flow.Set("UpgradeDistinctEnterprises", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                return CreateEffectSpec(script, YC.Domain.Enterprises.EnterpriseDistinctUpgradeFlow.TypeId, args);
            }, "Flow.UpgradeDistinctEnterprises"));
            flow.Set("UpgradeThenActivateEnterpriseSpecial", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                return CreateEffectSpec(script, YC.Domain.Enterprises.EnterpriseSpecialSelectionExecutor.UpgradeThenActivateTypeId, args);
            }, "Flow.UpgradeThenActivateEnterpriseSpecial"));
            flow.Set("ActivateAllEnterpriseSpecials", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                return CreateEffectSpec(script, YC.Domain.Enterprises.EnterpriseSpecialSelectionExecutor.ActivateAllTypeId, args);
            }, "Flow.ActivateAllEnterpriseSpecials"));
            flow.Set("UpgradeEnterpriseForAllPlayers", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                return CreateEffectSpec(script, YC.Domain.Enterprises.EnterpriseEffectExecutor.UpgradeAllPlayersTypeId, args);
            }, "Flow.UpgradeEnterpriseForAllPlayers"));
            foreach (var entry in new Dictionary<string, string> { { "AcquireReward", YC.Domain.Influence.RewardAcquisitionFlow.TypeId }, { "ClearRouteAndPlaceRoad", ContentPrimitiveEffectExecutor.ClearRouteRoad } })
            {
                string name = entry.Key, type = entry.Value;
                flow.Set(name, DynValue.NewCallback((_, args) => { guard.ThrowIfTimeExceeded(); return CreateEffectSpec(script, type, args); }, "Flow." + name));
            }
            script.Globals.Set("Flow", DynValue.NewTable(CreateReadOnlyProxy(script, flow, "enterprise_read_only_flow_api")));

            Table candidate = new Table(script);
            string[] patchOperations = { "Add", "Remove", "Intersect" };
            for (int i = 0; i < patchOperations.Length; i++)
            {
                string operation = patchOperations[i];
                candidate.Set(operation, DynValue.NewCallback((_, args) =>
                {
                    guard.ThrowIfTimeExceeded();
                    return CreateCandidatePatchSpec(script, operation, args);
                }, "Candidate." + operation));
            }
            script.Globals.Set("Candidate", DynValue.NewTable(CreateReadOnlyProxy(script, candidate, "nmc_read_only_candidate_api")));
        }

        private static DynValue CreateCandidatePatchSpec(Script script, string operation, CallbackArguments args)
        {
            if (args.Count != 1 || args[0].Type != DataType.Table)
                throw new ScriptRuntimeException("nmc_invalid_candidate_constructor_arguments");
            Table input = args[0].Table;
            foreach (TablePair pair in input.Pairs)
            {
                if (pair.Key.Type != DataType.String ||
                    (pair.Key.String != "ids" && pair.Key.String != "reasonCode"))
                    throw new ScriptRuntimeException("nmc_unexpected_candidate_field");
            }
            Table result = new Table(script);
            result.Set("kind", DynValue.NewString("candidate_patch"));
            result.Set("operation", DynValue.NewString(operation));
            result.Set("ids", input.Get("ids"));
            result.Set("reasonCode", input.Get("reasonCode"));
            return DynValue.NewTable(result);
        }

        private static DynValue CreateEffectSpec(Script script, string effectTypeId, CallbackArguments args)
        {
            if (args.Count != 1 || args[0].Type != DataType.Table)
            {
                throw new ScriptRuntimeException("nmc_invalid_effect_constructor_arguments");
            }

            Table input = args[0].Table;
            HashSet<string> allowedFields = CreateAllowedFields(effectTypeId);
            foreach (TablePair pair in input.Pairs)
            {
                if (pair.Key.Type != DataType.String || !allowedFields.Contains(pair.Key.String))
                {
                    throw new ScriptRuntimeException("nmc_unexpected_effect_field");
                }
            }

            Table spec = new Table(script);
            spec.Set("kind", DynValue.NewString(EffectSpecKind));
            spec.Set("effectTypeId", DynValue.NewString(effectTypeId));
            foreach (TablePair pair in input.Pairs)
            {
                spec.Set(pair.Key.String, pair.Value);
            }
            return DynValue.NewTable(spec);
        }

        private static HashSet<string> CreateAllowedFields(string effectTypeId)
        {
            var fields = CreateBaseAllowedFields(effectTypeId);
            if (effectTypeId == LuaDomainEffectTypeIds.Choice)
                fields.UnionWith(new[] { "resultContext", "amountRange", "completionHandlerId" });
            if (effectTypeId == ResourceLossEffectExecutor.TypeId) fields.Add("completionHandlerId");
            if (effectTypeId == EffectTypeIds.Condition) fields.Add("ruleCondition");
            if (effectTypeId == PayResourceEffectType) fields.Add("allowSubstitution");
            return fields;
        }

        private static HashSet<string> CreateBaseAllowedFields(string effectTypeId)
        {
            if (effectTypeId == ResourceLossEffectExecutor.TypeId) return new HashSet<string>(new[] { "apiVersion", "player", "targetPlayer", "resourceType", "amount" }, StringComparer.Ordinal);
            if (effectTypeId == CharacterLifecycleService.ConsumeSourceTypeId) return new HashSet<string>(new[] { "apiVersion", "player", "useId", "sourceItemId" }, StringComparer.Ordinal);
            if (effectTypeId == ResourcePaymentSubstitutionEffectExecutor.TypeId) return new HashSet<string>(new[] { "apiVersion", "player", "paymentOwnerEffectId", "sourceZoneId", "substitutionBudget" }, StringComparer.Ordinal);
            if (CharacterContentEffectExecutor.IsPublic(effectTypeId)) return new HashSet<string>(CharacterContentEffectExecutor.Fields(effectTypeId), StringComparer.Ordinal);
            if (ContentTransactionEffectExecutor.IsType(effectTypeId)) return new HashSet<string>(ContentTransactionEffectExecutor.Fields(effectTypeId), StringComparer.Ordinal);
            if (effectTypeId == LuaDomainEffectTypeIds.RollDice) return new HashSet<string>(new[] { "apiVersion", "player", "count", "sides" }, StringComparer.Ordinal);
            if (effectTypeId == ResourceCollectionEffectExecutor.TypeId) return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "scope" };
            if (effectTypeId == OperatorDispatchFlow.TypeId) return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "drawCount", "excludedReturnItemIds" };
            if (effectTypeId == OperatorDispatchFlow.ReplaceTypeId) return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player" };
            if (effectTypeId == CharacterLifecycleService.TransferTypeId) return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "cardInstanceId", "recipientPlayer" };
            if (effectTypeId == LuaDomainEffectTypeIds.SetCharacterDoubleUseRule) return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "scope", "useId", "sourceItemId", "expiresActionRoundSequence" };
            if (effectTypeId == LuaDomainEffectTypeIds.PlaceRoad) return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "routeId" };
            if (effectTypeId == LuaDomainEffectTypeIds.OperateToken) return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "tokenItemId", "operation", "destinationLocationId", "operationProfileId" };
            if (YC.Domain.Enterprises.EnterpriseEffectExecutor.IsEnterpriseType(effectTypeId))
                return new HashSet<string>(YC.Domain.Enterprises.EnterpriseEffectExecutor.AllowedFields(effectTypeId), StringComparer.Ordinal);
            if (effectTypeId == LuaDomainEffectTypeIds.Repeat)
                return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "count", "effects", "allowEarlyStop" };
            if (effectTypeId == DeckShuffleEffectExecutor.TypeId || effectTypeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck)
                return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "deckZone" };
            if (effectTypeId == ItemTransferEffectExecutor.TypeId || effectTypeId == LuaDomainEffectTypeIds.MoveFacilityCard || effectTypeId == LuaDomainEffectTypeIds.MoveCharacterCard)
                return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "itemRefs", "expectedSource", "destination", "insertion", "operationProfileId", "requiredCount", "carrierPolicy", "sourceZone", "destinationZone", "interactionType", "promptKey" };
            if (effectTypeId == "effect.flow.choice")
                return new HashSet<string>(StringComparer.Ordinal) { "player", "options", "minSelections", "maxSelections", "promptKey", "branches", "interactionType" };
            if (effectTypeId == SetLocationsOpenEffectType)
                return new HashSet<string>(StringComparer.Ordinal) { "locations", "isOpen", "reasonId" };
            if (ContentPrimitiveEffectExecutor.ContentFlowTypeIds.Contains(effectTypeId)) return new HashSet<string>(ContentPrimitiveEffectExecutor.ContentFlowFields(effectTypeId), StringComparer.Ordinal);
            if (effectTypeId == ItemTransferEffectExecutor.RefillTypeId || effectTypeId == ItemTransferEffectExecutor.RefreshTypeId) return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "slotZoneId" };
            if (effectTypeId == CityStyleSpecialActionEffectTypeIds.GrantMainActions)
                return new HashSet<string>(StringComparer.Ordinal) { "player", "amount", "lockCharacterCard", "allowedActionTypes" };
            if (ContentLifecycleEffectExecutor.PublicTypeIds.Contains(effectTypeId)) return new HashSet<string>(ContentLifecycleEffectExecutor.Fields(effectTypeId), StringComparer.Ordinal);
            if (effectTypeId == FacilitySelectionEffectExecutor.TypeId)
                return new HashSet<string>(StringComparer.Ordinal) { "player", "sourceZone", "effectId", "paymentMode", "allowDecline", "maxGoldVoucherCost", "includeStoredFacilities", "excludeRainbow" };
            if (effectTypeId == ContentPrimitiveEffectExecutor.OverrideNextStartPlayer)
                return new HashSet<string>(StringComparer.Ordinal) { "player", "targetPlayer", "scope" };
            if (effectTypeId == ExplorationSelectionEffectExecutor.TypeId)
                return new HashSet<string>(StringComparer.Ordinal) { "player" };
            if (effectTypeId == ContentPrimitiveEffectExecutor.ActivateFacilityEntry)
                return new HashSet<string>(StringComparer.Ordinal) { "player", "instanceId" };
            if (effectTypeId == ContentPrimitiveEffectExecutor.ChooseResources)
                return new HashSet<string>(StringComparer.Ordinal) { "player", "amount" };
            if (effectTypeId == LuaDomainEffectTypeIds.ResourceSell)
                return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "targetPlayer", "resourceType", "saleMode", "unitPrice", "pricePolicy", "interactionType", "promptKey" };
            if (effectTypeId == LuaDomainEffectTypeIds.MoveFacilityCard || effectTypeId == LuaDomainEffectTypeIds.MoveCharacterCard)
                return new HashSet<string>(StringComparer.Ordinal) { "player", "sourceZone", "destinationZone", "interactionType", "promptKey" };
            if (effectTypeId == ContentPrimitiveEffectExecutor.SelectInfluence)
                return new HashSet<string>(StringComparer.Ordinal) { "player", "operation", "ownerFilter", "subjectFilter", "excludedSubjectKeys", "count", "requiredCount", "distinctPolicy", "interactionType", "promptKey", "nextPromptKey", "candidateScope", "requireCompleteSelection" };
            if (effectTypeId == MoveCityEffectType)
                return new HashSet<string>(StringComparer.Ordinal) { "player", "targetPolicy", "targetLocationId", "movementMode", "candidateScope", "costPolicy", "clearInfluencePolicy", "sourceInfluencePolicy", "targetLocation", "waiveBaseCost", "consumeMainAction", "allowDecline", "decisionPlayer", "declinePromptKey" };
            if (effectTypeId == RevealEventCardEffectType)
                return new HashSet<string>(StringComparer.Ordinal) { "player", "eventDeck", "placeResourcePointIndicator", "resourcePoint", "postResolveDestination", "allowedOptions", "eventColor", "targetLocation", "targetLocationId", "primaryInfluenceSlot" };
            if (effectTypeId == ResolveCardEffectType)
                return new HashSet<string>(StringComparer.Ordinal) { "player", "card", "allowedModes", "doubleUseRule", "orderPolicy", "abilityId", "cardInstanceId", "mode", "effectOrder" };
            if (effectTypeId == CoverCardEffectType)
                return new HashSet<string>(StringComparer.Ordinal) { "apiVersion", "player", "cardInstanceId", "coverMode", "blockedActionRoundSequence", "acceptedTypeId", "reusable", "candidateSource", "count", "recycleDiscardWhenEmpty", "playerId", "coverIndex", "mainNodeId" };
            if (IsInfluenceEffectType(effectTypeId)) return CreateInfluenceAllowedFields(effectTypeId);
            if (effectTypeId == CityStyleMarkerEffectType)
                return new HashSet<string>(StringComparer.Ordinal) { "markerOwner", "operation", "destinationZone", "sourceZone", "count", "targetSlot", "executingPlayer", "markerId" };
            if (effectTypeId == GainResourceEffectType || effectTypeId == PayResourceEffectType || effectTypeId == GainScoreEffectType)
                return new HashSet<string>(StringComparer.Ordinal) { "recipient", "payer", "player", "resourceType", "resourceId", "cost", "amount", "reasonId", "source" };

            // 未逐项连接的首批稳定 API 仍使用同一组可审计字段；不再接受旧的
            // 设施/城市样式专用 operation 或隐藏目标字段。
            return new HashSet<string>(StringComparer.Ordinal)
            {
                "player", "recipient", "payer", "operatorDeck", "eventDeck", "deck", "card", "enterprise", "cityStyle",
                "destination", "source", "sourceZone", "destinationZone", "count", "amount", "minimumScore", "resourceType", "resourceId",
                "cost", "discount", "priceTable", "allowedResourceTypes", "maximumAmounts", "roller", "expression", "resultPurpose",
                "candidateScope", "candidateSource", "candidateIds", "ids", "route", "roadSource", "targetLocationId", "targetSlotId",
                "targetInfluence", "influenceSource", "ownerSubject", "replacementSource", "replacementOwner", "causeKind",
                "placementFailurePolicy", "failurePolicy", "rotation", "usageMeaning", "allowedModes", "doubleUseRule", "orderPolicy",
                "drawCount", "declineReward", "replacementScope", "levels", "rewardOrder", "department", "candidateDepartments",
                "targetPlayer", "scope", "duration", "remainingUses", "allowedActionTypes", "executionMode", "consumeBudget",
                "facilityId", "facilityInstanceId", "sourceSlotIndex", "sourceId",
                "characterUsePolicy", "facilityScope", "slotScope", "locationScope", "routeScope", "free", "tasks", "groupKey",
                "completionPolicy", "revealPolicy", "allowPerPlayerDecline", "minSelections", "maxSelections", "allowDecline",
                "decisionPlayer", "declinePromptKey", "unavailablePolicy", "leftEffects", "rightEffects", "body", "maxCount", "minCount",
                "allowEarlyStop", "isOpen", "locations", "reasonId", "markerOwner", "markerId", "targetSlot", "candidateSetId",
                "candidateSetVersion", "promptKey", "postResolveDestination", "placeResourcePointIndicator", "resourcePoint", "allowedOptions",
                "cardInstanceId", "abilityId", "mode", "effectOrder", "cityBoardSlotIndex", "paymentMode", "reserveForFree", "branches", "options", "interactionType"
            };
        }

        private static HashSet<string> CreateInfluenceAllowedFields(string effectTypeId)
        {
            if (effectTypeId == PlaceInfluenceEffectType)
            {
                return new HashSet<string>(StringComparer.Ordinal)
                {
                    "subjectFilter", "excludedSubjectKeys", "executingPlayer", "influenceSource", "ownerSubject", "targetSlotId", "candidateScope", "causeKind"
                };
            }

            if (effectTypeId == RemoveInfluenceEffectType)
            {
                return new HashSet<string>(StringComparer.Ordinal)
                {
                    "subjectFilter", "excludedSubjectKeys", "executingPlayer", "targetInfluence", "candidateScope", "destination", "reasonId", "causeKind"
                };
            }

            if (effectTypeId == MoveInfluenceEffectType)
            {
                return new HashSet<string>(StringComparer.Ordinal)
                {
                    "subjectFilter", "excludedSubjectKeys", "executingPlayer", "targetInfluence", "targetSlotId", "causeKind"
                };
            }

            return new HashSet<string>(StringComparer.Ordinal)
            {
                "subjectFilter", "excludedSubjectKeys", "executingPlayer", "targetInfluence", "replacementSource", "replacementOwner",
                "placementFailurePolicy", "causeKind"
            };
        }

        private static bool IsInfluenceEffectType(string effectTypeId)
        {
            return effectTypeId == PlaceInfluenceEffectType ||
                   effectTypeId == RemoveInfluenceEffectType ||
                   effectTypeId == MoveInfluenceEffectType ||
                   effectTypeId == ReplaceInfluenceEffectType;
        }

        private static void InstallForbiddenGlobals(Script script, ExecutionGuard guard)
        {
            string[] forbiddenTables =
            {
                "os", "io", "debug", "package", "coroutine", "math", "CS", "System", "UnityEngine"
            };

            for (int i = 0; i < forbiddenTables.Length; i++)
            {
                string name = forbiddenTables[i];
                script.Globals.Set(name, DynValue.NewTable(CreateForbiddenProxy(script, name, guard)));
            }

            string[] forbiddenFunctions = { "require", "load", "loadfile", "dofile" };
            for (int i = 0; i < forbiddenFunctions.Length; i++)
            {
                string name = forbiddenFunctions[i];
                script.Globals.Set(name, DynValue.NewCallback((_, args) =>
                {
                    guard.ThrowIfTimeExceeded();
                    throw new ScriptRuntimeException("nmc_forbidden_api:" + name);
                }, name));
            }
        }

        private static Table CreateForbiddenProxy(Script script, string name, ExecutionGuard guard)
        {
            Table proxy = new Table(script);
            Table meta = new Table(script);
            meta.Set("__index", DynValue.NewCallback((_, args) =>
            {
                guard.ThrowIfTimeExceeded();
                throw new ScriptRuntimeException("nmc_forbidden_api:" + name);
            }, "forbidden_api_index"));
            meta.Set("__newindex", DynValue.NewCallback((_, args) =>
            {
                throw new ScriptRuntimeException("nmc_forbidden_api:" + name);
            }, "forbidden_api_newindex"));
            proxy.MetaTable = meta;
            return proxy;
        }

        private bool TryNormalizeCandidatePatches(
            DynValue rawResult,
            out List<LuaCandidatePatch> patches,
            out LuaFailureCode code,
            out string diagnostic)
        {
            patches = new List<LuaCandidatePatch>();
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (rawResult.Type != DataType.Table)
            {
                code = LuaFailureCode.InvalidReturnShape;
                diagnostic = "Candidate handler 必须返回连续的 CandidatePatch 数组。";
                return false;
            }

            int length = rawResult.Table.Length;
            if (length > _budget.MaxEffectSpecs)
            {
                code = LuaFailureCode.ReturnLimitExceeded;
                diagnostic = "CandidatePatch 数量超过预算。";
                return false;
            }

            for (int i = 1; i <= length; i++)
            {
                DynValue value = rawResult.Table.Get(i);
                if (value.Type != DataType.Table)
                {
                    code = LuaFailureCode.InvalidReturnShape;
                    diagnostic = "CandidatePatch 数组包含非对象项。";
                    return false;
                }

                Table table = value.Table;
                string operation;
                string reason;
                if (!TryReadRequiredString(table, "kind", out reason, out code, out diagnostic) || reason != "candidate_patch" ||
                    !TryReadRequiredString(table, "operation", out operation, out code, out diagnostic) ||
                    !TryReadRequiredString(table, "reasonCode", out reason, out code, out diagnostic))
                    return false;
                if (operation != "Add" && operation != "Remove" && operation != "Intersect")
                {
                    code = LuaFailureCode.InvalidEffectSpec;
                    diagnostic = "CandidatePatch operation 未登记。";
                    return false;
                }

                List<string> ids;
                if (!TryReadStringArray(table, "ids", out ids, out code, out diagnostic) || ids.Count == 0)
                {
                    if (code == LuaFailureCode.None)
                    {
                        code = LuaFailureCode.InvalidEffectSpec;
                        diagnostic = "CandidatePatch 必须包含非空 ids。";
                    }
                    return false;
                }
                patches.Add(new LuaCandidatePatch(operation, ids, reason));
            }
            return true;
        }

        private bool TryNormalizeResult(
            DynValue rawResult,
            LuaInvocationContext context,
            out List<LuaEffectSpec> effects,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effects = new List<LuaEffectSpec>();
            code = LuaFailureCode.None;
            diagnostic = string.Empty;

            if (rawResult.Type != DataType.Table)
            {
                code = LuaFailureCode.InvalidReturnShape;
                diagnostic = "Lua handler 必须返回连续的 EffectSpec 数组。";
                return false;
            }

            LuaFailureCode inspectCode;
            if (!InspectTables(rawResult, 0, new HashSet<Table>(TableReferenceComparer.Instance), out inspectCode, out diagnostic))
            {
                code = inspectCode;
                return false;
            }

            Table result = rawResult.Table;
            int length = result.Length;
            if (length > _budget.MaxEffectSpecs)
            {
                code = LuaFailureCode.ReturnLimitExceeded;
                diagnostic = "Lua handler 返回的 EffectSpec 数量超过预算。";
                return false;
            }

            HashSet<int> numericKeys = new HashSet<int>();
            foreach (TablePair pair in result.Pairs)
            {
                if (pair.Key.Type != DataType.Number ||
                    double.IsNaN(pair.Key.Number) ||
                    double.IsInfinity(pair.Key.Number) ||
                    Math.Truncate(pair.Key.Number) != pair.Key.Number ||
                    pair.Key.Number < 1 ||
                    pair.Key.Number > _budget.MaxEffectSpecs)
                {
                    code = pair.Key.Type == DataType.Number && pair.Key.Number > _budget.MaxEffectSpecs
                        ? LuaFailureCode.ReturnLimitExceeded
                        : LuaFailureCode.InvalidReturnShape;
                    diagnostic = "Lua handler 返回的 EffectSpec 数组必须只包含从 1 开始的连续整数键。";
                    return false;
                }

                numericKeys.Add((int)pair.Key.Number);
            }

            if (numericKeys.Count != length)
            {
                code = LuaFailureCode.InvalidReturnShape;
                diagnostic = "Lua handler 返回的 EffectSpec 数组存在空洞或非数组字段。";
                return false;
            }

            for (int i = 1; i <= length; i++)
            {
                DynValue value = result.Get(i);
                LuaEffectSpec effect;
                LuaFailureCode effectCode;
                string effectDiagnostic;
                if (!TryNormalizeEffect(value, context, out effect, out effectCode, out effectDiagnostic))
                {
                    code = effectCode;
                    diagnostic = effectDiagnostic;
                    return false;
                }

                effects.Add(effect);
            }

            return true;
        }

        private bool InspectTables(
            DynValue value,
            int depth,
            HashSet<Table> visited,
            out LuaFailureCode code,
            out string diagnostic)
        {
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (value.Type != DataType.Table)
            {
                return true;
            }

            if (depth >= _budget.MaxTableDepth)
            {
                code = LuaFailureCode.TableDepthExceeded;
                diagnostic = "Lua 返回值嵌套深度超过预算。";
                return false;
            }

            var inspectedTable = SnapshotValues(value.Table);
            if (!visited.Add(inspectedTable))
            {
                return true;
            }

            bool effectSpec = inspectedTable.Get("kind").Type == DataType.String &&
                inspectedTable.Get("kind").String == EffectSpecKind &&
                inspectedTable.Get("effectTypeId").Type == DataType.String;
            int entries = 0;
            foreach (TablePair pair in inspectedTable.Pairs)
            {
                entries++;
                if (entries > _budget.MaxTableEntries)
                {
                    code = LuaFailureCode.TableEntryLimitExceeded;
                    diagnostic = "Lua 返回值表项数量超过预算。";
                    return false;
                }

                if (effectSpec && pair.Key.Type == DataType.String && pair.Key.String == "continuationContext")
                {
                    var saved = ReadPresentationValue(pair.Value, 0);
                    if (saved.Kind == NormalizedValueKind.Array && saved.Items.Count == 0) saved = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
                    if (!EffectContinuationContext.IsValid(saved)) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "effect_continuation_context_invalid"; return false; }
                    continue;
                }
                if (effectSpec && pair.Key.Type == DataType.String && pair.Key.String == "presentation")
                {
                    // 显示声明有独立的封闭 DTO 预算，不重复累计结果数组和业务分支的包装层。
                    // 仅识别 EffectSpec 的声明；普通业务对象同名字段仍按原业务预算检查。
                    try
                    {
                        YC.Domain.Presentation.GameplayPresentationNormalizer.FromNormalized<YC.Domain.Presentation.EffectPresentationDefinition>(
                            ReadPresentationValue(pair.Value, 0));
                    }
                    catch (Exception error) when (error is ArgumentException || error is OverflowException || error is ScriptRuntimeException)
                    {
                        code = LuaFailureCode.InvalidEffectSpec;
                        diagnostic = SanitizeDiagnostic(error.Message);
                        return false;
                    }
                    continue;
                }

                if (!InspectTables(pair.Key, depth + 1, visited, out code, out diagnostic) ||
                    !InspectTables(pair.Value, depth + 1, visited, out code, out diagnostic))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryNormalizeBusinessEffect(
            DynValue value,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            if (value.Type == DataType.Table && !value.Table.Get("completionHandlerId").IsNil())
            {
                effect = null;
                string type = ReadString(value.Table, "effectTypeId", "");
                if (!TryReadRequiredString(value.Table, "completionHandlerId", out string handler, out code, out diagnostic) || string.IsNullOrWhiteSpace(handler))
                { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "完成处理器必须为非空字符串。"; return false; }
                var business = new Table(value.Table.OwnerScript);
                foreach (var pair in value.Table.Pairs)
                    if (pair.Key.Type != DataType.String || pair.Key.String != "completionHandlerId") business.Set(pair.Key, pair.Value);
                if (!TryNormalizeEffectCore(DynValue.NewTable(business), context, out effect, out code, out diagnostic)) return false;
                effect.CompletionHandlerId = handler;
                return true;
            }
            if (TryNormalizeEffectCore(value, context, out effect, out code, out diagnostic)) return true;
            if (code != LuaFailureCode.MissingField || value.Type != DataType.Table || value.Table.Get("kind").String != EffectSpecKind) return false;
            string typeId = value.Table.Get("effectTypeId").String;
            if (typeId != GainResourceEffectType && typeId != PayResourceEffectType && typeId != GainScoreEffectType && typeId != EffectTypeIds.Condition && !IsGenericEffectType(typeId)) return false;
            // 只把明确缺字段转换成可保存的跳过节点；未知 schema、权限、错误类型和非法目标仍失败。
            string missingDiagnostic = diagnostic;
            var missing = new[] { "amount", "recipient", "payer", "player", "resourceId", "resourceType", "leftEffects", "rightEffects", "cardId", "targetSlotId" }
                .Where(field => missingDiagnostic.Contains(field)).ToList();
            if (missing.Count == 0) missing.Add("requiredField");
            effect = LuaEffectSpec.FromArguments(ContentPrimitiveEffectExecutor.IncompleteEffect, ItemTransferEffectExecutor.Object(
                "originalEffectTypeId", NormalizedValue.CreateString(typeId), "missingFieldNames", ItemTransferEffectExecutor.Strings(missing)));
            code = LuaFailureCode.None; diagnostic = ""; return true;
        }

        private static bool TryNormalizeEffectCore(
            DynValue value,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (value.Type != DataType.Table)
            {
                code = LuaFailureCode.InvalidEffectSpec;
                diagnostic = "EffectSpec 必须是 Lua table。";
                return false;
            }

            Table table = value.Table;
            string effectTypeId;
            if (!TryReadRequiredString(table, "effectTypeId", out effectTypeId, out code, out diagnostic))
            {
                return false;
            }

            HashSet<string> allowedFields = CreateAllowedFields(effectTypeId);
            allowedFields.Add("kind");
            allowedFields.Add("effectTypeId");

            foreach (TablePair pair in table.Pairs)
            {
                if (pair.Key.Type != DataType.String || !allowedFields.Contains(pair.Key.String))
                {
                    code = LuaFailureCode.UnexpectedField;
                    diagnostic = "EffectSpec 包含未登记字段。";
                    return false;
                }
            }

            string kind;
            if (!TryReadRequiredString(table, "kind", out kind, out code, out diagnostic) || kind != EffectSpecKind)
            {
                if (code == LuaFailureCode.None)
                {
                    code = LuaFailureCode.InvalidEffectSpec;
                    diagnostic = "EffectSpec kind 无效。";
                }

                return false;
            }

            if (effectTypeId == SetLocationsOpenEffectType)
            {
                return TryNormalizeSetLocationsOpenEffect(table, effectTypeId, out effect, out code, out diagnostic);
            }

            if (effectTypeId == ResourceLossEffectExecutor.TypeId)
                return TryNormalizeResourceLoss(table, context, out effect, out code, out diagnostic);

            if (IsInfluenceEffectType(effectTypeId))
            {
                if (!TryNormalizeInfluenceEffect(table, effectTypeId, out effect, out code, out diagnostic)) return false;
                if (!TryReadOptionalString(table, "subjectFilter", out var filter, out code, out diagnostic)) return false;
                if (!string.IsNullOrEmpty(filter) && Array.IndexOf(new[] { "self", "player", "opponent", "any" }, filter) < 0) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "subjectFilter 无效。"; return false; }
                var excluded = new List<string>();
                if (HasField(table, "excludedSubjectKeys") && !TryReadStringArray(table, "excludedSubjectKeys", out excluded, out code, out diagnostic)) return false;
                effect.SubjectFilter = filter ?? string.Empty;
                effect.ExcludedSubjectKeys = excluded;
                return true;
            }

            if (effectTypeId == LuaDomainEffectTypeIds.Choice && HasField(table, "branches"))
                return TryNormalizeBranchChoice(table, context, out effect, out code, out diagnostic);
            if (effectTypeId == LuaDomainEffectTypeIds.Repeat)
            {
                effect = null;
                var groups = new List<IReadOnlyList<LuaEffectSpec>>();
                if (HasField(table, "effects"))
                {
                    if (!TryReadNestedEffectArray(table.Get("effects"), context, "effects", out var body, out code, out diagnostic)) return false;
                    groups.Add(body);
                }
                var arguments = ItemTransferEffectExecutor.Object("apiVersion", NormalizedValue.CreateInteger(ReadInteger(table, "apiVersion", -1)), "allowEarlyStop", NormalizedValue.CreateBoolean(ReadBoolean(table, "allowEarlyStop", false)));
                if (HasField(table, "count"))
                {
                    var count = table.Get("count");
                    if (count.Type != DataType.Number || count.Number != Math.Truncate(count.Number) || count.Number < 0 || count.Number > int.MaxValue) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "Repeat 次数必须是非负有界整数。"; return false; }
                    arguments.Properties.Add(new NormalizedValueEntry { Name = "count", Value = NormalizedValue.CreateInteger((long)count.Number) });
                    arguments.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                }
                if (ReadInteger(table, "apiVersion", -1) != 1 || (HasField(table, "count") && RepeatEffectExecutor.ValidateArguments(arguments).Length > 0) || (HasField(table, "allowEarlyStop") && table.Get("allowEarlyStop").Type != DataType.Boolean)) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "Repeat 参数无效。"; return false; }
                effect = LuaEffectSpec.FromEffectGroups(effectTypeId, arguments, groups.ToArray());
                return true;
            }
            if (effectTypeId == EffectTypeIds.Condition)
            {
                return TryNormalizeConditionEffect(table, context, out effect, out code, out diagnostic);
            }

            if (effectTypeId == MoveCityEffectType)
            {
                return TryNormalizeMoveCityEffect(table, context, out effect, out code, out diagnostic);
            }

            if (effectTypeId == RevealEventCardEffectType)
            {
                return TryNormalizeRevealEventCardEffect(table, context, out effect, out code, out diagnostic);
            }

            if (effectTypeId == ResolveCardEffectType)
            {
                return TryNormalizeResolveCardEffect(table, context, out effect, out code, out diagnostic);
            }

            if (effectTypeId == CoverCardEffectType)
            {
                return TryNormalizeCoverCardEffect(table, context, out effect, out code, out diagnostic);
            }

            if (effectTypeId == CityStyleMarkerEffectType)
            {
                return TryNormalizeMarkerEffect(table, effectTypeId, context, out effect, out code, out diagnostic);
            }

            if (effectTypeId != GainResourceEffectType &&
                effectTypeId != PayResourceEffectType &&
                effectTypeId != GainScoreEffectType &&
                !IsGenericEffectType(effectTypeId))
            {
                code = LuaFailureCode.UnknownEffectType;
                diagnostic = "EffectSpec 的 effectTypeId 未登记。";
                return false;
            }

            if (effectTypeId == PayResourceEffectType && HasField(table, "cost"))
            {
                if (HasField(table, "allowSubstitution") && table.Get("allowSubstitution").Type != DataType.Boolean)
                { code = LuaFailureCode.InvalidFieldType; diagnostic = "替代支付标记必须为布尔值。"; return false; }
                var rawCost = ToNormalizedValue(DynValue.NewTable(table), 0, out code, out diagnostic);
                if (rawCost == null) return false;
                NormalizeContractPlayerFields(rawCost, "player");
                diagnostic = ResourceEffectExecutor.ValidateCompositePayment(rawCost);
                if (diagnostic.Length > 0 || ParsePlayerKey(ReadString(table, "player", "")) != ParsePlayerKey(context.PlayerId))
                { code = LuaFailureCode.InvalidEffectSpec; return false; }
                effect = LuaEffectSpec.FromArguments(effectTypeId, rawCost);
                return true;
            }
            if (IsGenericEffectType(effectTypeId))
            {
                return TryNormalizeGenericEffect(table, effectTypeId, context, out effect, out code, out diagnostic);
            }

            string recipient;
            bool recipientIsStableReference;
            if (!TryReadPlayerReference(table.Get("recipient"), "recipient", out recipient, out recipientIsStableReference, out code, out diagnostic) &&
                !TryReadPlayerReference(table.Get("player"), "player", out recipient, out recipientIsStableReference, out code, out diagnostic) &&
                !TryReadPlayerReference(table.Get("payer"), "payer", out recipient, out recipientIsStableReference, out code, out diagnostic))
            {
                return false;
            }

            if ((!recipientIsStableReference && recipient != context.PlayerId) ||
                !IsKnownPlayerOrCurrent(context, recipient))
            {
                code = LuaFailureCode.InvalidTarget;
                diagnostic = "EffectSpec 引用了当前快照中不存在的玩家。";
                return false;
            }

            int amount;
            if (!TryReadBoundedInteger(table, "amount", 1, 100, out amount, out code, out diagnostic))
            {
                return false;
            }

            string resourceType = null;
            if (effectTypeId == GainResourceEffectType || effectTypeId == PayResourceEffectType)
            {
                if (!TryReadOptionalString(table, "resourceType", out resourceType, out code, out diagnostic) ||
                    string.IsNullOrEmpty(resourceType))
                {
                    if (!TryReadRequiredString(table, "resourceId", out resourceType, out code, out diagnostic)) return false;
                }

                if (!IsRegisteredResourceType(resourceType))
                {
                    code = LuaFailureCode.InvalidEffectSpec;
                    diagnostic = "资源类型未登记。";
                    return false;
                }
            }

            string reasonId;
            if (!TryReadOptionalString(table, "reasonId", out reasonId, out code, out diagnostic))
            {
                return false;
            }

            if (effectTypeId == PayResourceEffectType && HasField(table, "allowSubstitution"))
            {
                if (table.Get("allowSubstitution").Type != DataType.Boolean)
                { code = LuaFailureCode.InvalidFieldType; diagnostic = "替代支付标记必须为布尔值。"; return false; }
                if (!ResourceLossEffectExecutor.TryReadResourceType(NormalizedValue.CreateString(resourceType), out var normalizedType))
                { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "支付资源类型未登记。"; return false; }
                effect = LuaEffectSpec.FromArguments(effectTypeId, ItemTransferEffectExecutor.Object(
                    "player", NormalizedValue.CreateString(ParsePlayerKey(recipient).ToString(CultureInfo.InvariantCulture)),
                    "cost", ItemTransferEffectExecutor.Object(normalizedType.ToString(), NormalizedValue.CreateInteger(amount)),
                    "allowSubstitution", NormalizedValue.CreateBoolean(table.Get("allowSubstitution").Boolean)));
                return true;
            }
            effect = new LuaEffectSpec(effectTypeId, recipient, resourceType, amount, reasonId);
            return true;
        }

        private static bool TryNormalizeResourceLoss(Table table, LuaInvocationContext context, out LuaEffectSpec effect,
            out LuaFailureCode code, out string diagnostic)
        {
            effect = null;
            if (!TryReadBoundedInteger(table, "apiVersion", 1, 1, out _, out code, out diagnostic) ||
                !TryReadBoundedInteger(table, "amount", 0, int.MaxValue, out int amount, out code, out diagnostic) ||
                !TryReadPlayerReference(table.Get("player"), "player", out string player, out _, out code, out diagnostic) ||
                !TryReadPlayerReference(table.Get("targetPlayer"), "targetPlayer", out string target, out _, out code, out diagnostic) ||
                !TryReadRequiredString(table, "resourceType", out string resource, out code, out diagnostic)) return false;
            if (ParsePlayerKey(player) != ParsePlayerKey(context.PlayerId) || ParsePlayerKey(player) < 1 ||
                ParsePlayerKey(target) < 1 || !IsKnownPlayerOrCurrent(context, target))
            { code = LuaFailureCode.InvalidTarget; diagnostic = "失去资源必须由执行者指定真实目标玩家。"; return false; }
            if (!ResourceLossEffectExecutor.TryReadResourceType(NormalizedValue.CreateString(resource), out var resourceType))
            { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "失去资源的资源类型未登记。"; return false; }
            var args = ItemTransferEffectExecutor.Object("apiVersion", NormalizedValue.CreateInteger(1),
                "player", NormalizedValue.CreateStableReference("player", ParsePlayerKey(player).ToString(CultureInfo.InvariantCulture)),
                "targetPlayer", NormalizedValue.CreateStableReference("player", ParsePlayerKey(target).ToString(CultureInfo.InvariantCulture)),
                "resourceType", NormalizedValue.CreateString(resourceType.ToString()), "amount", NormalizedValue.CreateInteger(amount));
            effect = LuaEffectSpec.FromArguments(ResourceLossEffectExecutor.TypeId, args);
            return true;
        }

        private static bool IsRegisteredResourceType(string resourceType)
        {
            string value = (resourceType ?? string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            return value == "originium" || value == "originiumshard" || value == "iron" ||
                   value == "pureoriginium" || value == "goldvoucher";
        }

        private static bool TryNormalizeBranchChoice(Table table, LuaInvocationContext context, out LuaEffectSpec effect,
            out LuaFailureCode code, out string diagnostic)
        {
            effect = null; code = LuaFailureCode.None; diagnostic = "";
            if (HasField(table, "amountRange") || table.Get("options").Type != DataType.Nil || table.Get("minSelections").Type != DataType.Nil || table.Get("maxSelections").Type != DataType.Nil)
            { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "Choice.branches 是单分支选择，不能同时提供 options 或多选数量。"; return false; }
            DynValue branches = table.Get("branches");
            if (branches.Type != DataType.Table || branches.Table.Length < 1 || branches.Table.Length > 16)
            { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "Choice.branches 必须包含 1 至 16 个分支。"; return false; }
            var groups = new List<IReadOnlyList<LuaEffectSpec>>();
            var options = new List<NormalizedValue>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i <= branches.Table.Length; i++)
            {
                var branch = branches.Table.Get(i);
                if (branch.Type != DataType.Table) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "Choice 分支必须为 table。"; return false; }
                string id;
                if (!TryReadRequiredString(branch.Table, "id", out id, out code, out diagnostic)) return false;
                if (!ids.Add(id)) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "Choice 分支 ID 重复。"; return false; }
                List<LuaEffectSpec> effects;
                if (!TryReadNestedEffectArray(branch.Table.Get("effects"), context, "effects", out effects, out code, out diagnostic)) return false;
                options.Add(NormalizedValue.CreateString(id)); groups.Add(effects);
            }
            var arguments = NormalizedValue.CreateObject(new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "player", Value = NormalizedValue.CreateString(ReadString(table, "player", context.PlayerId)) },
                new NormalizedValueEntry { Name = "options", Value = NormalizedValue.CreateArray(options) },
                new NormalizedValueEntry { Name = "promptKey", Value = NormalizedValue.CreateString(ReadString(table, "promptKey", "lua.choice.select")) },
                new NormalizedValueEntry { Name = "interactionType", Value = NormalizedValue.CreateString(ReadString(table, "interactionType", "lua.choice")) }
            });
            if (HasField(table, "resultContext"))
            {
                var resultContext = ToNormalizedValue(table.Get("resultContext"), 0, out code, out diagnostic);
                if (resultContext == null) return false;
                if (resultContext.Kind == NormalizedValueKind.Array && resultContext.Items.Count == 0)
                    resultContext = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
                arguments.Properties.Add(new NormalizedValueEntry { Name = "resultContext", Value = resultContext });
                arguments.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            }
            string argumentError = GenericLuaEffectExecutor.ValidateChoiceArguments(arguments);
            if (argumentError.Length > 0) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = argumentError; return false; }
            effect = LuaEffectSpec.FromEffectGroups(LuaDomainEffectTypeIds.Choice, arguments, groups.ToArray());
            return true;
        }

        private static bool TryNormalizeConditionEffect(
            Table table,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            List<LuaEffectSpec> left;
            List<LuaEffectSpec> right;
            if (HasField(table, "ruleCondition") && table.Get("ruleCondition").Type != DataType.Boolean)
            { code = LuaFailureCode.InvalidFieldType; diagnostic = "规则条件标记必须为布尔值。"; return false; }
            if (!TryReadNestedEffectArray(table.Get("leftEffects"), context, "leftEffects", out left, out code, out diagnostic) ||
                !TryReadNestedEffectArray(table.Get("rightEffects"), context, "rightEffects", out right, out code, out diagnostic))
                return false;

            var entries = new List<NormalizedValueEntry>
            {
                new NormalizedValueEntry { Name = "allowDecline", Value = NormalizedValue.CreateBoolean(ReadBoolean(table, "allowDecline", false)) },
                new NormalizedValueEntry { Name = "ruleCondition", Value = NormalizedValue.CreateBoolean(ReadBoolean(table, "ruleCondition", true)) },
                new NormalizedValueEntry { Name = "decisionPlayer", Value = NormalizedValue.CreateString(ReadString(table, "decisionPlayer", string.Empty)) },
                new NormalizedValueEntry { Name = "declinePromptKey", Value = NormalizedValue.CreateString(ReadString(table, "declinePromptKey", "")) }
            };
            NormalizedValue arguments = NormalizedValue.CreateObject(entries);
            effect = LuaEffectSpec.FromNestedArguments(EffectTypeIds.Condition, arguments, left, right);
            return true;
        }

        private static bool TryReadNestedEffectArray(
            DynValue raw,
            LuaInvocationContext context,
            string field,
            out List<LuaEffectSpec> effects,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effects = new List<LuaEffectSpec>();
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (raw.Type == DataType.Nil || raw.Type == DataType.Void)
            {
                code = LuaFailureCode.MissingField;
                diagnostic = "Effect.Condition 缺少字段：" + field + "。";
                return false;
            }
            if (raw.Type != DataType.Table)
            {
                code = LuaFailureCode.InvalidFieldType;
                diagnostic = "Effect.Condition 的 " + field + " 必须是 EffectSpec 数组。";
                return false;
            }

            int length = raw.Table.Length;
            if (length > 16)
            {
                code = LuaFailureCode.ReturnLimitExceeded;
                diagnostic = "Effect.Condition 的嵌套 Effect 数量超过 16。";
                return false;
            }
            for (int i = 1; i <= length; i++)
            {
                LuaEffectSpec nested = ToNestedSpec(raw.Table.Get(i), context, out code, out diagnostic);
                if (nested == null) return false;
                effects.Add(nested);
            }
            return true;
        }

        private static bool TryNormalizeMoveCityEffect(
            Table table,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            string target = string.Empty;
            if ((HasField(table, "targetLocationId") || HasField(table, "targetLocation")) &&
                !TryReadLocationValue(table.Get("targetLocationId"), "targetLocationId", out target, out code, out diagnostic) &&
                !TryReadLocationValue(table.Get("targetLocation"), "targetLocation", out target, out code, out diagnostic))
                return false;
            bool waive = ReadString(table, "costPolicy", "") == "waived" || ReadBoolean(table, "waiveBaseCost", false);
            bool consume = !HasField(table, "consumeMainAction") || ReadBoolean(table, "consumeMainAction", true);
            effect = LuaEffectSpec.FromArguments(MoveCityEffectType,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "targetLocation", Value = string.IsNullOrEmpty(target) ? NormalizedValue.CreateNull() : NormalizedValue.CreateStableReference("location", target) },
                    new NormalizedValueEntry { Name = "targetPolicy", Value = NormalizedValue.CreateString(ReadString(table, "targetPolicy", "standard")) },
                    new NormalizedValueEntry { Name = "waiveBaseCost", Value = NormalizedValue.CreateBoolean(waive) },
                    new NormalizedValueEntry { Name = "consumeMainAction", Value = NormalizedValue.CreateBoolean(consume) },
                    new NormalizedValueEntry { Name = "clearInfluencePolicy", Value = NormalizedValue.CreateString(ReadString(table, "clearInfluencePolicy", "remove")) },
                    new NormalizedValueEntry { Name = "allowDecline", Value = NormalizedValue.CreateBoolean(ReadBoolean(table, "allowDecline", false)) },
                    new NormalizedValueEntry { Name = "decisionPlayer", Value = NormalizedValue.CreateString(ReadString(table, "decisionPlayer", context.PlayerId)) },
                    new NormalizedValueEntry { Name = "declinePromptKey", Value = NormalizedValue.CreateString(ReadString(table, "declinePromptKey", "")) }
                }));
            return true;
        }

        private static bool TryNormalizeRevealEventCardEffect(
            Table table,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            string target;
            if (!TryReadLocationValue(table.Get("targetLocationId"), "targetLocationId", out target, out code, out diagnostic) &&
                !TryReadLocationValue(table.Get("resourcePoint"), "resourcePoint", out target, out code, out diagnostic))
                return false;
            string color = ReadString(table, "eventColor", "green");
            string slot = TryReadSlotValue(table.Get("primaryInfluenceSlot"));
            effect = LuaEffectSpec.FromArguments(
                RevealEventCardEffectType,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "eventColor", Value = NormalizedValue.CreateString(color) },
                    new NormalizedValueEntry { Name = "targetLocation", Value = NormalizedValue.CreateStableReference("location", target) },
                    new NormalizedValueEntry { Name = "primaryInfluenceSlot", Value = string.IsNullOrEmpty(slot) ? NormalizedValue.CreateNull() : NormalizedValue.CreateStableReference("slot", slot) }
                }));
            return true;
        }

        private static bool TryNormalizeResolveCardEffect(
            Table table,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            string card;
            if (!TryReadStringValue(table.Get("cardInstanceId"), "cardInstanceId", out card, out code, out diagnostic) &&
                !TryReadStringValue(table.Get("card"), "card", out card, out code, out diagnostic)) return false;
            string ability = ReadString(table, "abilityId", card);
            string mode = ReadString(table, "mode", string.Empty);
            if (string.IsNullOrEmpty(mode))
            {
                List<string> allowedModes;
                if (!TryReadStringArray(table, "allowedModes", out allowedModes, out code, out diagnostic)) return false;
                if (allowedModes.Count == 1) mode = allowedModes[0];
            }
            if (string.IsNullOrEmpty(mode)) mode = CharacterEffectModes.Both;
            string order = ReadString(table, "orderPolicy", ReadString(table, "effectOrder", string.Empty));
            effect = new LuaEffectSpec(ResolveCardEffectType, ability, card, mode, order);
            return true;
        }

        private static bool TryNormalizeCoverCardEffect(
            Table table,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            string player = ReadString(table, "playerId", context.PlayerId);
            if (HasField(table, "apiVersion"))
            {
                var raw = ToNormalizedValue(DynValue.NewTable(table), 0, out code, out diagnostic);
                if (raw == null) return false;
                NormalizeContractPlayerFields(raw,"player");
                if (ItemTransferEffectExecutor.Text(raw,"player") != ParsePlayerKey(context.PlayerId).ToString(CultureInfo.InvariantCulture)) { code=LuaFailureCode.InvalidTarget;diagnostic="character_cover_player_invalid";return false; }
                string reason = CharacterCoverEffectExecutor.ValidateArguments(raw);
                if (reason.Length > 0) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = reason; return false; }
                effect = LuaEffectSpec.FromArguments(CoverCardEffectType, raw); return true;
            }
            int coverIndex = ReadInteger(table, "coverIndex", 0);
            string mainNodeId = ReadString(table, "mainNodeId", string.Empty);
            effect = LuaEffectSpec.FromArguments(
                CoverCardEffectType,
                NormalizedValue.CreateObject(new List<NormalizedValueEntry>
                {
                    new NormalizedValueEntry { Name = "playerId", Value = NormalizedValue.CreateInteger(ParsePlayerKey(player)) },
                    new NormalizedValueEntry { Name = "coverIndex", Value = NormalizedValue.CreateInteger(coverIndex) },
                    new NormalizedValueEntry { Name = "mainNodeId", Value = NormalizedValue.CreateString(mainNodeId) }
                }));
            return true;
        }

        private static bool TryNormalizeMarkerEffect(
            Table table,
            string effectTypeId,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            string operation = ReadString(table, "operation", string.Empty);
            string executingPlayer = ReadString(table, "executingPlayer", context.PlayerId);
            List<string> candidates;
            if (!TryReadStringArray(table, "candidateIds", out candidates, out code, out diagnostic))
            {
                effect = null;
                return false;
            }
            int version = ReadInteger(table, "candidateSetVersion", 0);
            int min = ReadInteger(table, "minSelections", 0);
            int max = ReadInteger(table, "maxSelections", 0);
            effect = new LuaEffectSpec(
                effectTypeId,
                operation,
                executingPlayer,
                candidates,
                ReadString(table, "candidateSetId", string.Empty),
                version,
                min,
                max,
                ReadString(table, "promptKey", string.Empty),
                ReadInteger(table, "amount", 0),
                ReadString(table, "resourceType", string.Empty),
                ReadString(table, "markerId", string.Empty));
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            return true;
        }

        private static bool TryReadLocationValue(
            DynValue raw,
            string field,
            out string value,
            out LuaFailureCode code,
            out string diagnostic)
        {
            return TryReadReferenceValue(raw, field, "locationId", out value, out code, out diagnostic);
        }

        private static bool TryReadStringValue(
            DynValue raw,
            string field,
            out string value,
            out LuaFailureCode code,
            out string diagnostic)
        {
            value = string.Empty;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (raw.Type == DataType.String)
            {
                value = raw.String;
            }
            else if (raw.Type == DataType.Table)
            {
                DynValue id = raw.Table.Get("cardInstanceId");
                if (id.Type != DataType.String) id = raw.Table.Get("cardId");
                if (id.Type != DataType.String)
                {
                    code = LuaFailureCode.MissingField;
                    diagnostic = field + " 必须是字符串或稳定卡牌引用。";
                    return false;
                }
                value = id.String;
            }
            else
            {
                code = LuaFailureCode.MissingField;
                diagnostic = field + " 必须是字符串或稳定卡牌引用。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                code = LuaFailureCode.InvalidTarget;
                diagnostic = field + " 不能为空。";
                return false;
            }
            return true;
        }

        private static bool TryReadReferenceValue(
            DynValue raw,
            string field,
            string idField,
            out string value,
            out LuaFailureCode code,
            out string diagnostic)
        {
            value = string.Empty;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (raw.Type == DataType.String)
            {
                value = raw.String;
            }
            else if (raw.Type == DataType.Table)
            {
                DynValue id = raw.Table.Get(idField);
                if (id.Type != DataType.String)
                {
                    code = LuaFailureCode.MissingField;
                    diagnostic = field + " 必须包含 " + idField + "。";
                    return false;
                }
                value = id.String;
            }
            else
            {
                code = LuaFailureCode.MissingField;
                diagnostic = "EffectSpec 缺少字段：" + field + "。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                code = LuaFailureCode.InvalidTarget;
                diagnostic = field + " 不能为空。";
                return false;
            }
            return true;
        }

        private static string TryReadSlotValue(DynValue raw)
        {
            string value;
            LuaFailureCode code;
            string diagnostic;
            return TryReadReferenceValue(raw, "primaryInfluenceSlot", "slotId", out value, out code, out diagnostic) ? value : string.Empty;
        }

        private static bool HasField(Table table, string name)
        {
            DynValue value = table.Get(name);
            return value.Type != DataType.Nil && value.Type != DataType.Void;
        }

        private static string ReadString(Table table, string name, string fallback)
        {
            DynValue value = table.Get(name);
            return value.Type == DataType.String && !string.IsNullOrEmpty(value.String) ? value.String : fallback;
        }

        private static int ReadInteger(Table table, string name, int fallback)
        {
            DynValue value = table.Get(name);
            return value.Type == DataType.Number && !double.IsNaN(value.Number) && !double.IsInfinity(value.Number) && Math.Truncate(value.Number) == value.Number
                ? (int)value.Number : fallback;
        }

        private static bool ReadBoolean(Table table, string name, bool fallback)
        {
            DynValue value = table.Get(name);
            return value.Type == DataType.Boolean ? value.Boolean : fallback;
        }

        private static int ParsePlayerKey(string value)
        {
            string normalized = (value ?? string.Empty).StartsWith("p", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(1) : value;
            int parsed;
            return int.TryParse(normalized, out parsed) ? parsed : -1;
        }

        private static bool IsGenericEffectType(string effectTypeId)
        {
            if (ContentTransactionEffectExecutor.IsType(effectTypeId)) return true;
            if (effectTypeId == CharacterLifecycleService.ConsumeSourceTypeId || effectTypeId == ResourcePaymentSubstitutionEffectExecutor.TypeId) return true;
            if (CharacterContentEffectExecutor.IsPublic(effectTypeId)) return true;
            if (effectTypeId == ResourceCollectionEffectExecutor.TypeId) return true;
            if (effectTypeId == CharacterLifecycleService.TransferTypeId || effectTypeId == OperatorDispatchFlow.ReplaceTypeId) return true;
            if (YC.Domain.Enterprises.EnterpriseEffectExecutor.IsEnterpriseType(effectTypeId)) return true;
            return ContentLifecycleEffectExecutor.PublicTypeIds.Contains(effectTypeId) || ContentPrimitiveEffectExecutor.ContentFlowTypeIds.Contains(effectTypeId) || effectTypeId == ItemTransferEffectExecutor.RefillTypeId || effectTypeId == ItemTransferEffectExecutor.RefreshTypeId || effectTypeId == ItemTransferEffectExecutor.TypeId || effectTypeId == DeckShuffleEffectExecutor.TypeId || effectTypeId == CityStyleSpecialActionEffectTypeIds.GrantMainActions || effectTypeId == FacilitySelectionEffectExecutor.TypeId || effectTypeId == ExplorationSelectionEffectExecutor.TypeId || effectTypeId == ContentPrimitiveEffectExecutor.OverrideNextStartPlayer || effectTypeId == ContentPrimitiveEffectExecutor.ActivateFacilityEntry || effectTypeId == ContentPrimitiveEffectExecutor.ChooseResources || effectTypeId == ContentPrimitiveEffectExecutor.SelectInfluence ||
                   effectTypeId == LuaDomainEffectTypeIds.Choice ||
                   effectTypeId == LuaDomainEffectTypeIds.Repeat ||
                   effectTypeId == LuaDomainEffectTypeIds.RollDice ||
                   effectTypeId == LuaDomainEffectTypeIds.ResourceDiscount ||
                   effectTypeId == LuaDomainEffectTypeIds.ResourceSell ||
                   effectTypeId == LuaDomainEffectTypeIds.LoseScore ||
                   effectTypeId == LuaDomainEffectTypeIds.PlaceRoad ||
                   effectTypeId == LuaDomainEffectTypeIds.OperateToken ||
                   effectTypeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck ||
                   effectTypeId == LuaDomainEffectTypeIds.MoveFacilityCard ||
                   effectTypeId == LuaDomainEffectTypeIds.RotateFacilityCard ||
                   effectTypeId == LuaDomainEffectTypeIds.RevealCharacterCard ||
                   effectTypeId == LuaDomainEffectTypeIds.SetCharacterDoubleUseRule ||
                   effectTypeId == LuaDomainEffectTypeIds.MoveCharacterCard ||
                   effectTypeId == LuaDomainEffectTypeIds.DispatchOperator ||
                   effectTypeId == LuaDomainEffectTypeIds.UpgradeEnterprise ||
                   effectTypeId == LuaDomainEffectTypeIds.ActivateEnterpriseSpecial ||
                   effectTypeId == LuaDomainEffectTypeIds.SwitchDepartment ||
                   effectTypeId == LuaDomainEffectTypeIds.OverrideNextStartPlayer ||
                   effectTypeId == LuaDomainEffectTypeIds.ExecuteMainAction ||
                   effectTypeId == LuaDomainEffectTypeIds.OpenPlayerTaskGroup ||
                   effectTypeId == LuaDomainEffectTypeIds.MainActionDeploy ||
                   effectTypeId == LuaDomainEffectTypeIds.MainActionDispatch ||
                   effectTypeId == LuaDomainEffectTypeIds.MainActionBuild ||
                   effectTypeId == LuaDomainEffectTypeIds.MainActionMoveCity ||
                   effectTypeId == LuaDomainEffectTypeIds.MainActionSpecial ||
                   effectTypeId == LuaDomainEffectTypeIds.DeclareCityStyle ||
                   effectTypeId == FacilityEntryEffectTypeIds.Build ||
                   effectTypeId == ExplorationEffectTypeIds.Explore;
        }

        private static bool IsKnownPlayer(LuaInvocationContext context, string playerId)
        {
            if (context == null || context.FacadeSnapshot == null) return false;
            for (int i = 0; i < context.FacadeSnapshot.Players.Count; i++)
                if (context.FacadeSnapshot.Players[i].PlayerId == playerId) return true;
            return false;
        }

        private static bool IsKnownPlayerOrCurrent(LuaInvocationContext context, string playerId)
        {
            return context != null && playerId == context.PlayerId || IsKnownPlayer(context, playerId);
        }

        private static bool TryReadPlayerReference(
            DynValue raw,
            string field,
            out string value,
            out bool isStableReference,
            out LuaFailureCode code,
            out string diagnostic)
        {
            value = string.Empty;
            isStableReference = false;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (raw.Type == DataType.String)
            {
                value = raw.String;
            }
            else if (raw.Type == DataType.Table)
            {
                isStableReference = true;
                foreach (TablePair pair in raw.Table.Pairs)
                {
                    if (pair.Key.Type != DataType.String || pair.Key.String != "playerId")
                    {
                        code = LuaFailureCode.UnexpectedField;
                        diagnostic = field + " 稳定引用包含未登记字段。";
                        return false;
                    }
                }

                DynValue id = raw.Table.Get("playerId");
                if (id.Type != DataType.String)
                {
                    code = id.Type == DataType.Nil || id.Type == DataType.Void
                        ? LuaFailureCode.MissingField : LuaFailureCode.InvalidFieldType;
                    diagnostic = field + " 必须包含 playerId 字符串。";
                    return false;
                }

                value = id.String;
            }
            else
            {
                code = raw.Type == DataType.Nil || raw.Type == DataType.Void
                    ? LuaFailureCode.MissingField : LuaFailureCode.InvalidFieldType;
                diagnostic = "EffectSpec 缺少或错误的玩家引用：" + field + "。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                code = LuaFailureCode.InvalidTarget;
                diagnostic = field + " 不能为空。";
                return false;
            }

            return true;
        }

        private static bool TryNormalizeGenericEffect(
            Table table,
            string effectTypeId,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (effectTypeId == CityStyleSpecialActionEffectTypeIds.GrantMainActions || effectTypeId == FacilitySelectionEffectExecutor.TypeId || effectTypeId == ExplorationSelectionEffectExecutor.TypeId || effectTypeId == ContentPrimitiveEffectExecutor.OverrideNextStartPlayer || effectTypeId == ContentPrimitiveEffectExecutor.ActivateFacilityEntry || effectTypeId == ContentPrimitiveEffectExecutor.ChooseResources || effectTypeId == ContentPrimitiveEffectExecutor.SelectInfluence || effectTypeId == LuaDomainEffectTypeIds.ResourceSell ||
                effectTypeId == LuaDomainEffectTypeIds.MoveFacilityCard || effectTypeId == LuaDomainEffectTypeIds.MoveCharacterCard)
            {
                string player = ReadString(table, "player", context.PlayerId);
                if (player != context.PlayerId) { code = LuaFailureCode.InvalidTarget; diagnostic = "此内容原语只能操作执行玩家。"; return false; }
            }
            NormalizedValue raw = ToNormalizedValue(DynValue.NewTable(table), 0, out code, out diagnostic);
            if (raw == null) return false;
            if (effectTypeId == LuaDomainEffectTypeIds.Choice)
            {
                var contextEntry = raw.Properties.Find(entry => entry.Name == "resultContext");
                if (contextEntry?.Value.Kind == NormalizedValueKind.Array && contextEntry.Value.Items.Count == 0)
                    contextEntry.Value = NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
                var amountRange = ItemTransferEffectExecutor.Value(raw, "amountRange");
                if (amountRange != null)
                {
                    if (HasField(table, "options") || HasField(table, "branches") || HasField(table, "minSelections") || HasField(table, "maxSelections"))
                    { code = LuaFailureCode.InvalidEffectSpec; diagnostic = "数量Choice不能同时提供候选或分支。"; return false; }
                    if (amountRange.Kind != NormalizedValueKind.Object)
                    { code = LuaFailureCode.InvalidFieldType; diagnostic = "数量范围必须为对象。"; return false; }
                    var rangeTable = table.Get("amountRange").Table;
                    if (HasField(rangeTable, "sourcePlayer"))
                    {
                        if (!TryReadPlayerReference(rangeTable.Get("sourcePlayer"), "sourcePlayer", out string source, out _, out code, out diagnostic)) return false;
                        if (ParsePlayerKey(source) < 1 || !IsKnownPlayerOrCurrent(context, source))
                        { code = LuaFailureCode.InvalidTarget; diagnostic = "数量范围来源玩家不存在。"; return false; }
                        amountRange.Properties.Find(entry => entry.Name == "sourcePlayer").Value =
                            NormalizedValue.CreateStableReference("player", ParsePlayerKey(source).ToString(CultureInfo.InvariantCulture));
                    }
                    string actor = context.PlayerId;
                    if (HasField(table, "player") && !TryReadPlayerReference(table.Get("player"), "player", out actor, out _, out code, out diagnostic)) return false;
                    if (ParsePlayerKey(actor) != ParsePlayerKey(context.PlayerId) || ParsePlayerKey(actor) < 1)
                    { code = LuaFailureCode.InvalidTarget; diagnostic = "数量选择必须由执行玩家回答。"; return false; }
                    var actorEntry = raw.Properties.Find(entry => entry.Name == "player");
                    var actorValue = NormalizedValue.CreateStableReference("player", ParsePlayerKey(actor).ToString(CultureInfo.InvariantCulture));
                    if (actorEntry == null) raw.Properties.Add(new NormalizedValueEntry { Name = "player", Value = actorValue });
                    else actorEntry.Value = actorValue;
                    raw.Properties.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                }
                string choiceError = GenericLuaEffectExecutor.ValidateChoiceArguments(raw);
                if (choiceError.Length > 0) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = choiceError; return false; }
            }
            if (ContentTransactionEffectExecutor.IsType(effectTypeId))
            {
                NormalizeContractPlayerFields(raw, "player", "targetPlayer");
                string transactionError = ContentTransactionEffectExecutor.ValidateArguments(effectTypeId, raw);
                if (transactionError.Length > 0 || ParsePlayerKey(ReadString(table, "player", "")) != ParsePlayerKey(context.PlayerId))
                { code = LuaFailureCode.InvalidEffectSpec; diagnostic = transactionError.Length > 0 ? transactionError : "invalid_player"; return false; }
            }
            if (CharacterContentEffectExecutor.IsPublic(effectTypeId) || effectTypeId == LuaDomainEffectTypeIds.RollDice)
            {
                NormalizeContractPlayerFields(raw, "player");
                string characterContentError = effectTypeId == LuaDomainEffectTypeIds.RollDice ? DiceRollEffectExecutor.ValidateArguments(raw) : CharacterContentEffectExecutor.ValidateArguments(effectTypeId, raw);
                if (characterContentError.Length > 0 || ParsePlayerKey(ReadString(table, "player", "")) != ParsePlayerKey(context.PlayerId))
                { code = LuaFailureCode.InvalidEffectSpec; diagnostic = characterContentError.Length > 0 ? characterContentError : "invalid_player"; return false; }
            }
            if (ContentLifecycleEffectExecutor.PublicTypeIds.Contains(effectTypeId))
            {
                NormalizeContractPlayerFields(raw, "player");
                string lifecycleError = ContentLifecycleEffectExecutor.ValidateArguments(effectTypeId, raw);
                if (lifecycleError.Length > 0 || ParsePlayerKey(ReadString(table, "player", "")) != ParsePlayerKey(context.PlayerId))
                { code = LuaFailureCode.InvalidEffectSpec; diagnostic = lifecycleError.Length > 0 ? lifecycleError : "lifecycle_permission_denied"; return false; }
            }
            var rainbow = ItemTransferEffectExecutor.Value(raw, "excludeRainbow");
            if (effectTypeId == FacilitySelectionEffectExecutor.TypeId && rainbow != null && rainbow.Kind != NormalizedValueKind.Boolean)
            { code = LuaFailureCode.InvalidFieldType; diagnostic = "非彩设施限制必须为布尔值。"; return false; }
            if (ContentPrimitiveEffectExecutor.ContentFlowTypeIds.Contains(effectTypeId) || effectTypeId == ItemTransferEffectExecutor.RefillTypeId || effectTypeId == ItemTransferEffectExecutor.RefreshTypeId || effectTypeId == LuaDomainEffectTypeIds.ResourceSell && ItemTransferEffectExecutor.Value(raw, "apiVersion") != null)
            {
                NormalizeContractPlayerFields(raw, "player", "sourcePlayer", "targetPlayer");
                string error = ContentPrimitiveEffectExecutor.ContentFlowTypeIds.Contains(effectTypeId) ? ContentPrimitiveEffectExecutor.ValidateContentFlow(effectTypeId, raw) : ItemTransferEffectExecutor.Value(raw, "apiVersion") == null ? "supply_arguments_incomplete" : ItemTransferEffectExecutor.ValidateSupplyArguments(effectTypeId, raw);
                if (effectTypeId == LuaDomainEffectTypeIds.ResourceSell) error = ContentPrimitiveEffectExecutor.ValidateSaleArguments(raw);
                if (error.Length > 0) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = error; return false; }
                if (ParsePlayerKey(ReadString(table, "player", "")) != ParsePlayerKey(context.PlayerId)) { code = LuaFailureCode.InvalidTarget; diagnostic = "能力组合只能由执行玩家提交。"; return false; }
                foreach (string field in new[] { "sourcePlayer", "targetPlayer" })
                { var value = ItemTransferEffectExecutor.Value(raw, field); if (value != null && !IsKnownPlayerOrCurrent(context, "p" + value.StringValue)) { code = LuaFailureCode.InvalidTarget; diagnostic = "玩家引用不存在。"; return false; } }
            }
            var stored = ItemTransferEffectExecutor.Value(raw, "includeStoredFacilities");
            if (effectTypeId == FacilitySelectionEffectExecutor.TypeId && stored != null && stored.Kind != NormalizedValueKind.Boolean) { code = LuaFailureCode.InvalidFieldType; diagnostic = "牌下设施候选配置必须为布尔值。"; return false; }
            var limit = ItemTransferEffectExecutor.Value(raw, "maxGoldVoucherCost");
            if (effectTypeId == FacilitySelectionEffectExecutor.TypeId && limit != null && (limit.Kind != NormalizedValueKind.Integer || limit.IntegerValue < 0)) { code = LuaFailureCode.InvalidFieldType; diagnostic = "设施费用上限必须为非负整数。"; return false; }
            if (YC.Domain.Enterprises.EnterpriseEffectExecutor.IsEnterpriseType(effectTypeId) ||
                effectTypeId == CharacterLifecycleService.TransferTypeId || effectTypeId == LuaDomainEffectTypeIds.SetCharacterDoubleUseRule)
                NormalizeContractPlayerFields(raw, "player", "unlockedByPlayer", "recipientPlayer");
            if (YC.Domain.Enterprises.EnterpriseEffectExecutor.IsEnterpriseType(effectTypeId))
            {
                foreach (string field in new[] { "player", "unlockedByPlayer" })
                {
                    var value = ItemTransferEffectExecutor.Value(raw, field);
                    if (value == null) continue;
                    if (value.Kind != NormalizedValueKind.String || !int.TryParse(value.StringValue, NumberStyles.None, CultureInfo.InvariantCulture, out int target) || target < 1 ||
                        !IsKnownPlayerOrCurrent(context, value.StringValue) && !IsKnownPlayerOrCurrent(context, "p" + value.StringValue))
                    { code = LuaFailureCode.InvalidTarget; diagnostic = "企业操作引用了本局不存在的玩家。"; return false; }
                    if (field == "player" && target != ParsePlayerKey(context.PlayerId))
                    { code = LuaFailureCode.InvalidTarget; diagnostic = "普通企业操作只能由当前执行玩家提交；全员升级须使用授权Flow。"; return false; }
                }
            }
            if (effectTypeId == ResourceCollectionEffectExecutor.TypeId)
            {
                var playerField = raw.Properties.Find(p => p.Name == "player");
                if (playerField != null && playerField.Value.Kind == NormalizedValueKind.String)
                    playerField.Value = NormalizedValue.CreateString(ParsePlayerKey(playerField.Value.StringValue).ToString(CultureInfo.InvariantCulture));
                string collectionError = ResourceCollectionEffectExecutor.ValidateArguments(raw);
                if (collectionError.Length > 0 || ReadString(table, "player", "") != context.PlayerId) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = collectionError.Length > 0 ? collectionError : "invalid_player"; return false; }
            }
            if (effectTypeId == LuaDomainEffectTypeIds.PlaceRoad || effectTypeId == LuaDomainEffectTypeIds.OperateToken)
            {
                NormalizeContractPlayerFields(raw, "player");
                string mapError = YC.Domain.Movement.MapOperationEffectExecutor.ValidateArguments(effectTypeId, raw);
                if (mapError.Length > 0) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = mapError; return false; }
                if (ParsePlayerKey(ReadString(table, "player", "")) != ParsePlayerKey(context.PlayerId)) { code = LuaFailureCode.InvalidTarget; diagnostic = "地图操作只能由执行玩家提交。"; return false; }
            }
            if (effectTypeId == OperatorDispatchFlow.TypeId || effectTypeId == OperatorDispatchFlow.ReplaceTypeId)
            {
                NormalizeContractPlayerFields(raw, "player");
                if (ParsePlayerKey(ReadString(table, "player", "")) != ParsePlayerKey(context.PlayerId)) { code = LuaFailureCode.InvalidTarget; diagnostic = "特派和直接换牌只能由执行玩家提交。"; return false; }
            }
            if (effectTypeId == CharacterLifecycleService.ConsumeSourceTypeId || effectTypeId == ResourcePaymentSubstitutionEffectExecutor.TypeId)
            {
                NormalizeContractPlayerFields(raw, "player");
                if (ParsePlayerKey(ReadString(table, "player", "")) != ParsePlayerKey(context.PlayerId))
                { code = LuaFailureCode.InvalidTarget; diagnostic = "结算受控构造只能引用当前执行玩家。"; return false; }
                string controlledError = effectTypeId == CharacterLifecycleService.ConsumeSourceTypeId
                    ? SettlementHostProtocol.ValidateConsumeSourceArguments(raw) : ResourcePaymentSubstitutionEffectExecutor.ValidateArguments(raw);
                if (controlledError.Length > 0) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = controlledError; return false; }
            }
            string characterError = effectTypeId == OperatorDispatchFlow.TypeId || effectTypeId == OperatorDispatchFlow.ReplaceTypeId ? OperatorDispatchFlow.ValidateArguments(effectTypeId, raw) :
                effectTypeId == CharacterLifecycleService.TransferTypeId || effectTypeId == LuaDomainEffectTypeIds.SetCharacterDoubleUseRule ? CharacterLifecycleService.ValidateArguments(effectTypeId, raw) : "";
            if (characterError.Length > 0) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = characterError; return false; }
            if ((effectTypeId == ItemTransferEffectExecutor.TypeId || effectTypeId == LuaDomainEffectTypeIds.MoveFacilityCard || effectTypeId == LuaDomainEffectTypeIds.MoveCharacterCard || effectTypeId == DeckShuffleEffectExecutor.TypeId || effectTypeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck) && ItemTransferEffectExecutor.Value(raw, "apiVersion") != null)
            {
                NormalizeContractPlayerFields(raw, "player");
                if (ItemTransferEffectExecutor.Value(raw, "player") != null && ParsePlayerKey(ReadString(table, "player", "")) != ParsePlayerKey(context.PlayerId))
                { code = LuaFailureCode.InvalidTarget; diagnostic = "物品转移和洗牌只能由执行玩家提交。"; return false; }
            }
            string contractError = effectTypeId == ItemTransferEffectExecutor.TypeId || effectTypeId == LuaDomainEffectTypeIds.MoveFacilityCard || effectTypeId == LuaDomainEffectTypeIds.MoveCharacterCard ? ItemTransferEffectExecutor.ValidateArguments(effectTypeId, raw) : effectTypeId == DeckShuffleEffectExecutor.TypeId || effectTypeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck ? DeckShuffleEffectExecutor.ValidateArguments(effectTypeId, raw) : "";
            if ((effectTypeId == ItemTransferEffectExecutor.TypeId || effectTypeId == LuaDomainEffectTypeIds.MoveFacilityCard || effectTypeId == LuaDomainEffectTypeIds.MoveCharacterCard) && ItemTransferEffectExecutor.MissingTransferFields(effectTypeId, raw).Count > 0 ||
                (effectTypeId == DeckShuffleEffectExecutor.TypeId || effectTypeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck) && DeckShuffleEffectExecutor.MissingFields(effectTypeId, raw).Count > 0) contractError = "";
            if (contractError.Length > 0) { code = LuaFailureCode.InvalidEffectSpec; diagnostic = contractError; return false; }
            effect = LuaEffectSpec.FromArguments(effectTypeId, raw);
            return true;
        }

        private static void NormalizeContractPlayerFields(NormalizedValue arguments, params string[] fields)
        {
            foreach (var entry in arguments.Properties)
                if (fields.Contains(entry.Name) && entry.Value.Kind == NormalizedValueKind.String)
                    entry.Value = NormalizedValue.CreateString(ParsePlayerKey(entry.Value.StringValue).ToString(CultureInfo.InvariantCulture));
        }

        private static LuaEffectSpec ToNestedSpec(
            DynValue raw,
            LuaInvocationContext context,
            out LuaFailureCode code,
            out string diagnostic)
        {
            LuaEffectSpec result;
            if (!TryNormalizeEffect(raw, context, out result, out code, out diagnostic)) return null;
            return result;
        }

        private static NormalizedValue ToNormalizedValue(
            DynValue value,
            int depth,
            out LuaFailureCode code,
            out string diagnostic)
        {
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (depth >= NormalizedValue.MaxDepth)
            {
                code = LuaFailureCode.TableDepthExceeded;
                diagnostic = "Effect 参数嵌套深度超过预算。";
                return null;
            }

            switch (value.Type)
            {
                case DataType.Nil:
                case DataType.Void:
                    return NormalizedValue.CreateNull();
                case DataType.Boolean:
                    return NormalizedValue.CreateBoolean(value.Boolean);
                case DataType.Number:
                    if (double.IsNaN(value.Number) || double.IsInfinity(value.Number) || Math.Truncate(value.Number) != value.Number)
                    {
                        code = LuaFailureCode.InvalidFieldType;
                        diagnostic = "Effect 参数中的数字必须是有限整数。";
                        return null;
                    }
                    return NormalizedValue.CreateInteger((long)value.Number);
                case DataType.String:
                    return NormalizedValue.CreateString(value.String ?? string.Empty);
                case DataType.Table:
                    Table snapshotTable = SnapshotValues(value.Table);
                    var entries = new List<NormalizedValueEntry>();
                    var items = new List<NormalizedValue>();
                    bool array = true;
                    int maxIndex = 0;
                    foreach (TablePair pair in snapshotTable.Pairs)
                    {
                        if (pair.Key.Type != DataType.Number || pair.Key.Number < 1 || Math.Truncate(pair.Key.Number) != pair.Key.Number)
                        {
                            array = false;
                            break;
                        }
                        maxIndex = Math.Max(maxIndex, (int)pair.Key.Number);
                    }
                    if (array && snapshotTable.Length == maxIndex)
                    {
                        for (int i = 1; i <= maxIndex; i++)
                        {
                            NormalizedValue item = ToNormalizedValue(snapshotTable.Get(i), depth + 1, out code, out diagnostic);
                            if (item == null) return null;
                            items.Add(item);
                        }
                        return NormalizedValue.CreateArray(items);
                    }

                    foreach (TablePair pair in snapshotTable.Pairs)
                    {
                        if (pair.Key.Type != DataType.String)
                        {
                            code = LuaFailureCode.InvalidReturnShape;
                            diagnostic = "Effect 参数对象只能包含字符串字段。";
                            return null;
                        }
                        NormalizedValue item = ToNormalizedValue(pair.Value, depth + 1, out code, out diagnostic);
                        if (item == null) return null;
                        entries.Add(new NormalizedValueEntry { Name = pair.Key.String, Value = item });
                    }
                    return NormalizedValue.CreateObject(entries);
                default:
                    code = LuaFailureCode.InvalidFieldType;
                    diagnostic = "Effect 参数包含不支持的值类型。";
                    return null;
            }
        }

        private static bool TryNormalizeFacilityEntryEffect(
            Table table,
            string effectTypeId,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            string operation;
            string executingPlayer;
            string candidateSetId;
            string promptKey;
            string resourceType;
            string markerId;
            string facilityId;
            string facilityInstanceId;
            if (!TryReadRequiredString(table, "operation", out operation, out code, out diagnostic) ||
                !TryReadOptionalString(table, "executingPlayer", out executingPlayer, out code, out diagnostic) ||
                !TryReadOptionalString(table, "candidateSetId", out candidateSetId, out code, out diagnostic) ||
                !TryReadOptionalString(table, "promptKey", out promptKey, out code, out diagnostic) ||
                !TryReadOptionalString(table, "resourceType", out resourceType, out code, out diagnostic) ||
                !TryReadOptionalString(table, "markerId", out markerId, out code, out diagnostic) ||
                !TryReadOptionalString(table, "facilityId", out facilityId, out code, out diagnostic) ||
                !TryReadOptionalString(table, "facilityInstanceId", out facilityInstanceId, out code, out diagnostic))
            {
                return false;
            }

            if (string.IsNullOrEmpty(executingPlayer)) executingPlayer = context.PlayerId;
            int candidateSetVersion;
            int minSelections;
            int maxSelections;
            int amount;
            int sourceSlotIndex;
            if (!TryReadOptionalBoundedInteger(table, "candidateSetVersion", 0, 1000000, out candidateSetVersion, out code, out diagnostic) ||
                !TryReadOptionalBoundedInteger(table, "minSelections", 0, 128, out minSelections, out code, out diagnostic) ||
                !TryReadOptionalBoundedInteger(table, "maxSelections", 0, 128, out maxSelections, out code, out diagnostic) ||
                !TryReadOptionalBoundedInteger(table, "amount", 0, 1000000, out amount, out code, out diagnostic) ||
                !TryReadOptionalBoundedInteger(table, "sourceSlotIndex", -1, 128, out sourceSlotIndex, out code, out diagnostic))
            {
                return false;
            }

            List<string> candidates;
            if (!TryReadStringArray(table, "candidateIds", out candidates, out code, out diagnostic)) return false;
            if (maxSelections < minSelections)
            {
                code = LuaFailureCode.InvalidEffectSpec;
                diagnostic = "设施入场 Effect 的 maxSelections 不能小于 minSelections。";
                return false;
            }

            effect = new LuaEffectSpec(
                effectTypeId, operation, executingPlayer, candidates, candidateSetId,
                candidateSetVersion, minSelections, maxSelections, promptKey, amount,
                resourceType, markerId, facilityId, facilityInstanceId, sourceSlotIndex);
            return true;
        }

        private static bool TryNormalizeCityStyleEffect(
            Table table,
            string effectTypeId,
            LuaInvocationContext context,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            string operation;
            string executingPlayer;
            string candidateSetId;
            string promptKey;
            string resourceType;
            string markerId;
            if (!TryReadRequiredString(table, "operation", out operation, out code, out diagnostic) ||
                !TryReadOptionalString(table, "executingPlayer", out executingPlayer, out code, out diagnostic) ||
                !TryReadOptionalString(table, "candidateSetId", out candidateSetId, out code, out diagnostic) ||
                !TryReadOptionalString(table, "promptKey", out promptKey, out code, out diagnostic) ||
                !TryReadOptionalString(table, "resourceType", out resourceType, out code, out diagnostic) ||
                !TryReadOptionalString(table, "markerId", out markerId, out code, out diagnostic)) return false;
            if (string.IsNullOrEmpty(executingPlayer)) executingPlayer = context.PlayerId;
            if (string.IsNullOrEmpty(candidateSetId)) candidateSetId = string.Empty;
            int candidateSetVersion;
            int minSelections;
            int maxSelections;
            int amount;
            if (!TryReadOptionalBoundedInteger(table, "candidateSetVersion", 0, 1000000, out candidateSetVersion, out code, out diagnostic) ||
                !TryReadOptionalBoundedInteger(table, "minSelections", 0, 128, out minSelections, out code, out diagnostic) ||
                !TryReadOptionalBoundedInteger(table, "maxSelections", 0, 128, out maxSelections, out code, out diagnostic) ||
                !TryReadOptionalBoundedInteger(table, "amount", 0, 1000000, out amount, out code, out diagnostic)) return false;
            List<string> candidates;
            if (!TryReadStringArray(table, "candidateIds", out candidates, out code, out diagnostic)) return false;
            if (maxSelections < minSelections)
            {
                code = LuaFailureCode.InvalidEffectSpec;
                diagnostic = "特殊行动 Effect 的 maxSelections 不能小于 minSelections。";
                return false;
            }
            effect = new LuaEffectSpec(
                effectTypeId,
                operation,
                executingPlayer,
                candidates,
                candidateSetId,
                candidateSetVersion,
                minSelections,
                maxSelections,
                promptKey,
                amount,
                resourceType,
                markerId);
            return true;
        }

        private static bool TryNormalizeCharacterAbilityEffect(
            Table table,
            string effectTypeId,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            string abilityId;
            string cardInstanceId;
            string mode;
            string effectOrder;
            if (!TryReadRequiredString(table, "abilityId", out abilityId, out code, out diagnostic) ||
                !TryReadRequiredString(table, "cardInstanceId", out cardInstanceId, out code, out diagnostic) ||
                !TryReadRequiredString(table, "mode", out mode, out code, out diagnostic) ||
                !TryReadOptionalString(table, "effectOrder", out effectOrder, out code, out diagnostic))
            {
                return false;
            }

            if (!string.Equals(mode, CharacterEffectModes.Strategy, StringComparison.Ordinal) &&
                !string.Equals(mode, CharacterEffectModes.Tactic, StringComparison.Ordinal) &&
                !string.Equals(mode, CharacterEffectModes.Both, StringComparison.Ordinal))
            {
                code = LuaFailureCode.InvalidEffectSpec;
                diagnostic = "CharacterAbility 的 mode 无效。";
                return false;
            }

            effect = new LuaEffectSpec(effectTypeId, abilityId, cardInstanceId, mode, effectOrder);
            return true;
        }

        private static bool TryNormalizeInfluenceEffect(
            Table table,
            string effectTypeId,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            string executingPlayerId;
            if (!TryReadInfluenceReference(table.Get("executingPlayer"), "player", "executingPlayer", out executingPlayerId, out code, out diagnostic)) return false;

            string targetInfluenceId;
            string targetSlotId;
            string sourceId;
            string ownerSubjectId;
            string replacementSourceId;
            string replacementOwnerId;
            string reasonId;
            string destination;
            string causeKind;
            string policy;

            if (effectTypeId == PlaceInfluenceEffectType)
            {
                DynValue target = table.Get("targetSlotId");
                if (target.Type == DataType.Nil || target.Type == DataType.Void)
                {
                    targetSlotId = string.Empty;
                }
                else if (!TryReadInfluenceReference(target, "slot", "targetSlotId", out targetSlotId, out code, out diagnostic))
                {
                    return false;
                }

                if (!TryReadInfluenceSource(table.Get("influenceSource"), executingPlayerId, out sourceId, out code, out diagnostic) ||
                    !TryReadInfluenceSubject(table.Get("ownerSubject"), executingPlayerId, out ownerSubjectId, out code, out diagnostic)) return false;
                causeKind = ReadOptionalCause(table, InfluenceCauseKinds.Direct, out code, out diagnostic);
                if (code != LuaFailureCode.None) return false;
                List<string> scope = null;
                if (HasField(table, "candidateScope") && !TryReadStringArray(table, "candidateScope", out scope, out code, out diagnostic)) return false;
                effect = new LuaEffectSpec(effectTypeId, executingPlayerId, string.Empty, targetSlotId, sourceId, ownerSubjectId, string.Empty, string.Empty, causeKind, string.Empty, string.Empty, string.Empty, scope);
                return true;
            }

            if (effectTypeId == RemoveInfluenceEffectType)
            {
                DynValue target = table.Get("targetInfluence");
                if (target.Type == DataType.Nil || target.Type == DataType.Void)
                {
                    List<string> candidateIds;
                    if (!TryReadStringArray(table, "candidateScope", out candidateIds, out code, out diagnostic) || candidateIds.Count == 0)
                    {
                        if (code == LuaFailureCode.None)
                        {
                            code = LuaFailureCode.MissingField;
                            diagnostic = "RemoveInfluence 必须提供 targetInfluence 或非空 candidateScope。";
                        }
                        return false;
                    }

                    if (!TryReadOptionalString(table, "destination", out destination, out code, out diagnostic)) return false;
                    if (string.IsNullOrEmpty(destination)) destination = "player_supply";
                    if (destination != "player_supply")
                    {
                        code = LuaFailureCode.InvalidEffectSpec;
                        diagnostic = "RemoveInfluence 第一版只支持 player_supply 去向。";
                        return false;
                    }
                    if (!TryReadOptionalString(table, "reasonId", out reasonId, out code, out diagnostic)) return false;
                    causeKind = ReadOptionalCause(table, InfluenceCauseKinds.Direct, out code, out diagnostic);
                    if (code != LuaFailureCode.None) return false;
                    effect = new LuaEffectSpec(
                        effectTypeId,
                        executingPlayerId,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        causeKind,
                        string.Empty,
                        destination,
                        reasonId,
                        candidateIds);
                    return true;
                }

                if (!TryReadInfluenceReference(target, "influence", "targetInfluence", out targetInfluenceId, out code, out diagnostic)) return false;
                if (!TryReadOptionalString(table, "destination", out destination, out code, out diagnostic)) return false;
                if (string.IsNullOrEmpty(destination)) destination = "player_supply";
                if (destination != "player_supply")
                {
                    code = LuaFailureCode.InvalidEffectSpec;
                    diagnostic = "RemoveInfluence 第一版只支持 player_supply 去向。";
                    return false;
                }
                if (!TryReadOptionalString(table, "reasonId", out reasonId, out code, out diagnostic)) return false;
                causeKind = ReadOptionalCause(table, InfluenceCauseKinds.Direct, out code, out diagnostic);
                if (code != LuaFailureCode.None) return false;
                effect = new LuaEffectSpec(effectTypeId, executingPlayerId, targetInfluenceId, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, causeKind, string.Empty, destination, reasonId);
                return true;
            }

            if (!TryReadInfluenceReference(table.Get("targetInfluence"), "influence", "targetInfluence", out targetInfluenceId, out code, out diagnostic)) return false;

            if (effectTypeId == MoveInfluenceEffectType)
            {
                var destinationSlot = table.Get("targetSlotId");
                if (destinationSlot.IsNil()) targetSlotId = string.Empty;
                else if (!TryReadInfluenceReference(destinationSlot,"slot","targetSlotId",out targetSlotId,out code,out diagnostic)) return false;
                causeKind = ReadOptionalCause(table, InfluenceCauseKinds.Direct, out code, out diagnostic);
                if (code != LuaFailureCode.None) return false;
                effect = new LuaEffectSpec(effectTypeId, executingPlayerId, targetInfluenceId, targetSlotId, string.Empty, string.Empty, string.Empty, string.Empty, causeKind, string.Empty, string.Empty, string.Empty);
                return true;
            }

            if (!TryReadInfluenceSource(table.Get("replacementSource"), executingPlayerId, out replacementSourceId, out code, out diagnostic) ||
                !TryReadInfluenceSubject(table.Get("replacementOwner"), executingPlayerId, out replacementOwnerId, out code, out diagnostic)) return false;
            if (!TryReadOptionalString(table, "placementFailurePolicy", out policy, out code, out diagnostic)) return false;
            if (string.IsNullOrEmpty(policy)) policy = "keep_removal";
            if (policy != "keep_removal")
            {
                code = LuaFailureCode.InvalidEffectSpec;
                diagnostic = "ReplaceInfluence 只允许 placementFailurePolicy=keep_removal。";
                return false;
            }
            causeKind = ReadOptionalCause(table, InfluenceCauseKinds.Replace, out code, out diagnostic);
            if (code != LuaFailureCode.None) return false;
            effect = new LuaEffectSpec(effectTypeId, executingPlayerId, targetInfluenceId, string.Empty, string.Empty, string.Empty, replacementSourceId, replacementOwnerId, causeKind, policy, string.Empty, string.Empty);
            return true;
        }

        private static string ReadOptionalCause(Table table, string fallback, out LuaFailureCode code, out string diagnostic)
        {
            string value;
            if (!TryReadOptionalString(table, "causeKind", out value, out code, out diagnostic)) return string.Empty;
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static bool TryReadInfluenceReference(
            DynValue raw,
            string referenceType,
            string field,
            out string value,
            out LuaFailureCode code,
            out string diagnostic)
        {
            value = string.Empty;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (raw.Type == DataType.String)
            {
                value = raw.String;
            }
            else if (raw.Type == DataType.Table)
            {
                string expectedField = referenceType == "player" ? "playerId" : referenceType == "influence" ? "influenceId" : "slotId";
                foreach (TablePair pair in raw.Table.Pairs)
                {
                    if (pair.Key.Type != DataType.String || pair.Key.String != expectedField)
                    {
                        code = LuaFailureCode.UnexpectedField;
                        diagnostic = field + " 稳定引用包含未登记字段。";
                        return false;
                    }
                }

                DynValue nested = raw.Table.Get(expectedField);
                if (nested.Type != DataType.String)
                {
                    code = nested.Type == DataType.Nil || nested.Type == DataType.Void ? LuaFailureCode.MissingField : LuaFailureCode.InvalidFieldType;
                    diagnostic = field + " 稳定引用字段类型无效。";
                    return false;
                }

                value = nested.String;
            }
            else
            {
                code = raw.Type == DataType.Nil || raw.Type == DataType.Void ? LuaFailureCode.MissingField : LuaFailureCode.InvalidFieldType;
                diagnostic = "EffectSpec 缺少或错误的稳定引用：" + field + "。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                code = LuaFailureCode.InvalidTarget;
                diagnostic = field + " 稳定引用不能为空。";
                return false;
            }

            return true;
        }

        private static bool TryReadInfluenceSubject(
            DynValue raw,
            string fallbackPlayerId,
            out string subjectId,
            out LuaFailureCode code,
            out string diagnostic)
        {
            subjectId = string.Empty;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (raw.Type == DataType.String)
            {
                subjectId = raw.String;
            }
            else if (raw.Type == DataType.Table)
            {
                foreach (TablePair pair in raw.Table.Pairs)
                {
                    if (pair.Key.Type != DataType.String ||
                        (pair.Key.String != "kind" && pair.Key.String != "companyId" && pair.Key.String != "subjectType" && pair.Key.String != "instanceId" && pair.Key.String != "definitionId" && pair.Key.String != "playerId"))
                    {
                        code = LuaFailureCode.UnexpectedField;
                        diagnostic = "ownerSubject 包含未登记字段。";
                        return false;
                    }
                }

                DynValue instance = raw.Table.Get("instanceId");
                DynValue player = raw.Table.Get("playerId");
                string kind = ReadString(raw.Table, "kind", ReadString(raw.Table, "subjectType", "player"));
                if (kind == "company")
                {
                    string companyId = ReadString(raw.Table, "companyId", ReadString(raw.Table, "definitionId", instance.Type == DataType.String ? instance.String : ""));
                    if (companyId != "stonehoove_security") { code = LuaFailureCode.InvalidTarget; diagnostic = "公司主体引用无效。"; return false; }
                    subjectId = "company:" + companyId;
                    return true;
                }
                if (kind != "player" || player.Type != DataType.String && instance.Type != DataType.String) { code = LuaFailureCode.InvalidTarget; diagnostic = "玩家主体必须有明确身份。"; return false; }
                subjectId = instance.Type == DataType.String ? instance.String : player.Type == DataType.String ? player.String : fallbackPlayerId;
            }
            else
            {
                code = LuaFailureCode.MissingField;
                diagnostic = "ownerSubject 必须是稳定主体引用。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(subjectId))
            {
                code = LuaFailureCode.InvalidTarget;
                diagnostic = "ownerSubject 不能为空。";
                return false;
            }

            return true;
        }

        private static bool TryReadInfluenceSource(
            DynValue raw,
            string fallbackPlayerId,
            out string sourceId,
            out LuaFailureCode code,
            out string diagnostic)
        {
            sourceId = string.Empty;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (raw.Type == DataType.String)
            {
                sourceId = raw.String;
            }
            else if (raw.Type == DataType.Table)
            {
                foreach (TablePair pair in raw.Table.Pairs)
                {
                    if (pair.Key.Type != DataType.String ||
                        (pair.Key.String != "kind" && pair.Key.String != "sourceId" && pair.Key.String != "subject"))
                    {
                        code = LuaFailureCode.UnexpectedField;
                        diagnostic = "influenceSource 包含未登记字段。";
                        return false;
                    }
                }

                DynValue kind = raw.Table.Get("kind");
                if (kind.Type != DataType.String || kind.String != "player_supply" && kind.String != "company_supply")
                {
                    code = LuaFailureCode.InvalidEffectSpec;
                    diagnostic = "influenceSource 只支持 player_supply 或 company_supply。";
                    return false;
                }

                DynValue id = raw.Table.Get("sourceId");
                if (id.Type != DataType.String)
                {
                    code = LuaFailureCode.MissingField;
                    diagnostic = "influenceSource 必须提供 sourceId。";
                    return false;
                }

                sourceId = id.String;
            }
            else
            {
                code = LuaFailureCode.MissingField;
                diagnostic = "influenceSource 必须是稳定来源引用。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourceId))
            {
                code = LuaFailureCode.InvalidTarget;
                diagnostic = "influenceSource 的 sourceId 不能为空。";
                return false;
            }

            return true;
        }

        private static bool TryNormalizeSetLocationsOpenEffect(
            Table table,
            string effectTypeId,
            out LuaEffectSpec effect,
            out LuaFailureCode code,
            out string diagnostic)
        {
            effect = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;

            DynValue rawLocations = table.Get("locations");
            if (rawLocations.Type == DataType.Nil || rawLocations.Type == DataType.Void)
            {
                code = LuaFailureCode.MissingField;
                diagnostic = "EffectSpec 缺少字段：locations。";
                return false;
            }

            if (rawLocations.Type != DataType.Table)
            {
                code = LuaFailureCode.InvalidFieldType;
                diagnostic = "EffectSpec 字段类型无效：locations。";
                return false;
            }

            Table locations = rawLocations.Table;
            int length = locations.Length;
            if (length > MaxSetLocationsOpenCount)
            {
                code = LuaFailureCode.OutOfBounds;
                diagnostic = "EffectSpec 的 locations 数量超过 16。";
                return false;
            }

            var numericKeys = new HashSet<int>();
            foreach (TablePair pair in locations.Pairs)
            {
                if (pair.Key.Type != DataType.Number ||
                    double.IsNaN(pair.Key.Number) ||
                    double.IsInfinity(pair.Key.Number) ||
                    Math.Truncate(pair.Key.Number) != pair.Key.Number ||
                    pair.Key.Number < 1 || pair.Key.Number > MaxSetLocationsOpenCount)
                {
                    code = LuaFailureCode.InvalidReturnShape;
                    diagnostic = "locations 必须是从 1 开始的连续 LocationRef 数组。";
                    return false;
                }

                numericKeys.Add((int)pair.Key.Number);
            }

            if (numericKeys.Count != length)
            {
                code = LuaFailureCode.InvalidReturnShape;
                diagnostic = "locations 数组存在空洞或非数组字段。";
                return false;
            }

            var locationIds = new List<string>();
            for (int i = 1; i <= length; i++)
            {
                string locationId;
                if (!TryReadLocationReference(locations.Get(i), out locationId, out code, out diagnostic))
                {
                    return false;
                }

                if (!locationIds.Contains(locationId)) locationIds.Add(locationId);
            }

            locationIds.Sort(StringComparer.Ordinal);
            bool isOpen;
            if (!TryReadRequiredBoolean(table, "isOpen", out isOpen, out code, out diagnostic))
            {
                return false;
            }

            string reasonId;
            if (!TryReadOptionalString(table, "reasonId", out reasonId, out code, out diagnostic))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(reasonId)) reasonId = "rule.unspecified";
            effect = new LuaEffectSpec(effectTypeId, locationIds, isOpen, reasonId);
            return true;
        }

        private static bool TryReadLocationReference(
            DynValue raw,
            out string locationId,
            out LuaFailureCode code,
            out string diagnostic)
        {
            locationId = null;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            if (raw.Type == DataType.String)
            {
                locationId = raw.String;
            }
            else if (raw.Type == DataType.Table)
            {
                foreach (TablePair pair in raw.Table.Pairs)
                {
                    if (pair.Key.Type != DataType.String || pair.Key.String != "locationId")
                    {
                        code = LuaFailureCode.UnexpectedField;
                        diagnostic = "LocationRef 只允许包含 locationId 字段。";
                        return false;
                    }
                }

                DynValue id = raw.Table.Get("locationId");
                if (id.Type != DataType.String)
                {
                    code = id.Type == DataType.Nil || id.Type == DataType.Void
                        ? LuaFailureCode.MissingField
                        : LuaFailureCode.InvalidFieldType;
                    diagnostic = "LocationRef 的 locationId 必须是字符串。";
                    return false;
                }

                locationId = id.String;
            }
            else
            {
                code = LuaFailureCode.InvalidFieldType;
                diagnostic = "locations 只能包含字符串或 LocationRef。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(locationId))
            {
                code = LuaFailureCode.InvalidTarget;
                diagnostic = "LocationRef 的 locationId 不能为空。";
                return false;
            }

            return true;
        }

        private static bool TryReadRequiredBoolean(
            Table table,
            string field,
            out bool value,
            out LuaFailureCode code,
            out string diagnostic)
        {
            DynValue raw = table.Get(field);
            if (raw.Type == DataType.Nil || raw.Type == DataType.Void)
            {
                value = false;
                code = LuaFailureCode.MissingField;
                diagnostic = "EffectSpec 缺少字段：" + field + "。";
                return false;
            }

            if (raw.Type != DataType.Boolean)
            {
                value = false;
                code = LuaFailureCode.InvalidFieldType;
                diagnostic = "EffectSpec 字段必须是布尔值：" + field + "。";
                return false;
            }

            value = raw.Boolean;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            return true;
        }

        private static bool TryReadRequiredString(
            Table table,
            string field,
            out string value,
            out LuaFailureCode code,
            out string diagnostic)
        {
            DynValue raw = table.Get(field);
            if (raw.Type == DataType.Nil || raw.Type == DataType.Void)
            {
                value = null;
                code = LuaFailureCode.MissingField;
                diagnostic = "EffectSpec 缺少字段：" + field + "。";
                return false;
            }

            if (raw.Type != DataType.String)
            {
                value = null;
                code = LuaFailureCode.InvalidFieldType;
                diagnostic = "EffectSpec 字段类型无效：" + field + "。";
                return false;
            }

            value = raw.String;
            if (string.IsNullOrWhiteSpace(value))
            {
                code = LuaFailureCode.InvalidEffectSpec;
                diagnostic = "EffectSpec 字段不能为空：" + field + "。";
                return false;
            }

            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            return true;
        }

        private static bool TryReadOptionalString(
            Table table,
            string field,
            out string value,
            out LuaFailureCode code,
            out string diagnostic)
        {
            DynValue raw = table.Get(field);
            if (raw.Type == DataType.Nil || raw.Type == DataType.Void)
            {
                value = null;
                code = LuaFailureCode.None;
                diagnostic = string.Empty;
                return true;
            }

            if (raw.Type != DataType.String)
            {
                value = null;
                code = LuaFailureCode.InvalidFieldType;
                diagnostic = "EffectSpec 字段类型无效：" + field + "。";
                return false;
            }

            value = raw.String;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            return true;
        }

        private static bool TryReadBoundedInteger(
            Table table,
            string field,
            int minimum,
            int maximum,
            out int value,
            out LuaFailureCode code,
            out string diagnostic)
        {
            DynValue raw = table.Get(field);
            if (raw.Type == DataType.Nil || raw.Type == DataType.Void)
            {
                value = 0;
                code = LuaFailureCode.MissingField;
                diagnostic = "EffectSpec 缺少字段：" + field + "。";
                return false;
            }

            if (raw.Type != DataType.Number ||
                double.IsNaN(raw.Number) ||
                double.IsInfinity(raw.Number) ||
                Math.Truncate(raw.Number) != raw.Number)
            {
                value = 0;
                code = LuaFailureCode.InvalidFieldType;
                diagnostic = "EffectSpec 字段必须是有限整数：" + field + "。";
                return false;
            }

            if (raw.Number < minimum || raw.Number > maximum)
            {
                value = 0;
                code = LuaFailureCode.OutOfBounds;
                diagnostic = string.Format(
                    CultureInfo.InvariantCulture,
                    "EffectSpec 字段超出范围：{0}，允许 {1}..{2}。",
                    field,
                    minimum,
                    maximum);
                return false;
            }

            value = (int)raw.Number;
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            return true;
        }

        private static bool TryReadOptionalBoundedInteger(
            Table table,
            string field,
            int minimum,
            int maximum,
            out int value,
            out LuaFailureCode code,
            out string diagnostic)
        {
            DynValue raw = table.Get(field);
            if (raw.Type == DataType.Nil || raw.Type == DataType.Void)
            {
                value = 0;
                code = LuaFailureCode.None;
                diagnostic = string.Empty;
                return true;
            }
            return TryReadBoundedInteger(table, field, minimum, maximum, out value, out code, out diagnostic);
        }

        private static bool TryReadStringArray(
            Table table,
            string field,
            out List<string> values,
            out LuaFailureCode code,
            out string diagnostic)
        {
            values = new List<string>();
            DynValue raw = table.Get(field);
            if (raw.Type == DataType.Nil || raw.Type == DataType.Void)
            {
                code = LuaFailureCode.None;
                diagnostic = string.Empty;
                return true;
            }
            if (raw.Type != DataType.Table)
            {
                code = LuaFailureCode.InvalidFieldType;
                diagnostic = "EffectSpec 字段必须是字符串数组：" + field + "。";
                return false;
            }

            int pairCount = 0;
            foreach (TablePair pair in raw.Table.Pairs)
            {
                if (pair.Key.Type != DataType.Number || pair.Value.Type != DataType.String)
                {
                    code = LuaFailureCode.InvalidFieldType;
                    diagnostic = "EffectSpec 字符串数组包含非法项：" + field + "。";
                    return false;
                }
                values.Add(pair.Value.String ?? string.Empty);
                pairCount++;
            }

            // 事件 payload 的数组来自只读快照代理：代理自身没有直接键，
            // 数值项通过 __index/__ipairs 暴露给 Lua。C# 侧不能只遍历 Pairs，
            // 否则候选数组会被误读为空。
            if (pairCount == 0)
            {
                for (int index = 1; index <= 128; index++)
                {
                    DynValue item = ReadIndexedTableValue(raw.Table, index);
                    if (item.Type == DataType.Nil || item.Type == DataType.Void) break;
                    if (item.Type != DataType.String)
                    {
                        code = LuaFailureCode.InvalidFieldType;
                        diagnostic = "EffectSpec 字符串数组包含非法项：" + field + "。";
                        return false;
                    }

                    values.Add(item.String ?? string.Empty);
                }

                DynValue overflow = ReadIndexedTableValue(raw.Table, 129);
                if (overflow.Type != DataType.Nil && overflow.Type != DataType.Void)
                {
                    code = LuaFailureCode.OutOfBounds;
                    diagnostic = "EffectSpec 字符串数组超过 128 项：" + field + "。";
                    return false;
                }
            }

            values.Sort(StringComparer.Ordinal);
            code = LuaFailureCode.None;
            diagnostic = string.Empty;
            return true;
        }

        private static DynValue ReadIndexedTableValue(Table table, int index)
        {
            DynValue value = table.Get(index);
            if (value.Type != DataType.Nil && value.Type != DataType.Void) return value;
            if (table.MetaTable == null || table.OwnerScript == null) return value;

            DynValue indexer = table.MetaTable.Get("__index");
            if (indexer.Type != DataType.Function && indexer.Type != DataType.ClrFunction) return value;
            return table.OwnerScript.Call(
                indexer,
                DynValue.NewTable(table),
                DynValue.NewNumber(index));
        }

        private static LuaFailureCode MapScriptRuntimeFailure(string message)
        {
            if (message != null && message.IndexOf("nmc_query_unknown_filter:", StringComparison.Ordinal) >= 0) return LuaFailureCode.UnexpectedField;
            if (message != null && message.IndexOf("nmc_query_invalid_arguments:", StringComparison.Ordinal) >= 0) return LuaFailureCode.InvalidFieldType;
            if (message != null && message.IndexOf("nmc_query_filter_limit:", StringComparison.Ordinal) >= 0) return LuaFailureCode.OutOfBounds;
            if (message != null && message.IndexOf("nmc_query_result_limit:", StringComparison.Ordinal) >= 0) return LuaFailureCode.ReturnLimitExceeded;
            if (message != null && message.IndexOf("nmc_query_snapshot_unavailable:", StringComparison.Ordinal) >= 0) return LuaFailureCode.InvalidDefinition;
            if (message == null)
            {
                return LuaFailureCode.ScriptError;
            }

            if (message.IndexOf("nmc_forbidden_api:", StringComparison.Ordinal) >= 0)
            {
                return LuaFailureCode.ForbiddenApi;
            }

            if (message.IndexOf("nmc_unexpected_effect_field", StringComparison.Ordinal) >= 0)
            {
                return LuaFailureCode.UnexpectedField;
            }

            if (message.IndexOf("nmc_invalid_effect_constructor_arguments", StringComparison.Ordinal) >= 0)
            {
                return LuaFailureCode.InvalidEffectSpec;
            }

            if (message.IndexOf("nmc_read_only_context", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("nmc_read_only_snapshot", StringComparison.Ordinal) >= 0)
            {
                return LuaFailureCode.ReadOnlySnapshot;
            }

            if (message.IndexOf("nmc_forbidden_player_snapshot", StringComparison.Ordinal) >= 0)
            {
                return LuaFailureCode.ForbiddenStateAccess;
            }

            if (message.IndexOf("nmc_time_budget_exceeded", StringComparison.Ordinal) >= 0)
            {
                return LuaFailureCode.TimeBudgetExceeded;
            }

            return LuaFailureCode.ScriptError;
        }

        private static string SanitizeDiagnostic(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "Lua 执行失败。";
            }

            string oneLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return oneLine.Length <= 512 ? oneLine : oneLine.Substring(0, 512);
        }

        private sealed class ExecutionGuard
        {
            private readonly long _maxInstructions;
            private readonly Stopwatch _stopwatch;
            private readonly int _maxMilliseconds;

            public ExecutionGuard(long maxInstructions, int maxMilliseconds, Stopwatch stopwatch)
            {
                _maxInstructions = maxInstructions;
                _maxMilliseconds = maxMilliseconds;
                _stopwatch = stopwatch;
            }

            public long MaxInstructions => _maxInstructions;

            public void ThrowIfTimeExceeded()
            {
                if (_stopwatch.ElapsedMilliseconds > _maxMilliseconds)
                {
                    throw new ScriptRuntimeException("nmc_time_budget_exceeded");
                }
            }
        }

        private sealed class LuaLimitException : Exception
        {
            public LuaLimitException(LuaFailureCode code, string message)
                : base(message)
            {
                Code = code;
            }

            public LuaFailureCode Code { get; }
        }

        private sealed class TableReferenceComparer : IEqualityComparer<Table>
        {
            public static TableReferenceComparer Instance { get; } = new TableReferenceComparer();

            public bool Equals(Table x, Table y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(Table obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }
    }

}
