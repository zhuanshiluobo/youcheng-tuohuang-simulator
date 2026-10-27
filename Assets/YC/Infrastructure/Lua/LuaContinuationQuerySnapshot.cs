using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Enterprises;
using YC.Domain.Items;
using YC.Domain.Movement;
using YC.Domain.State;

namespace YC.Infrastructure.Lua
{
    internal static partial class LuaQuerySnapshotBuilder
    {
        internal static void AddContinuationFacts(GameState state, LuaPlayerSnapshot snapshot, int playerId, int viewer,
            EnterpriseDefinitions definitions)
        {
            var player = state.FindPlayer(playerId);
            if (state.FindPlayer(viewer) == null) return;
            var query = new EnterpriseStateQuery(definitions);
            var specials = new List<LuaSnapshotValue>();
            var candidates = new List<LuaSnapshotValue>();
            foreach (var value in definitions == null ? Array.Empty<EnterpriseSpecialCandidate>() : query.GetSpecialCandidates(state, viewer, playerId, 5, null))
            {
                var enterprise = state.ItemZones.Items.Single(i => i.ItemId == value.EnterpriseItemId);
                int level = definitions.GetSpecialAbilityLevel(enterprise.DefinitionId, value.SpecialId);
                var entry = O(E("id", S(playerId + ":" + value.SpecialId)), E("candidateId", S(value.CandidateId)), E("specialId", S(value.SpecialId)),
                    E("enterpriseItemId", S(value.EnterpriseItemId)), E("unlockedByPlayer", S(Player(playerId))), E("level", N(level)));
                candidates.Add(entry);
                if (level <= value.CurrentLevel) specials.Add(entry);
            }
            // 原已解锁集合不变；公开卡面全集供Lua按本次文字规则生成显式候选集合。
            var fields = new List<LuaSnapshotEntry> { E("enterpriseSpecials", A(specials)), E("enterpriseSpecialCandidates", A(candidates)) };
            // 合法非动作轮没有当前动作轮序号；非法轮号仍由原函数严格拒绝。
            if (state.Round < 1 || state.ActionRound != 0)
                fields.Add(E("actionRoundSequence", N(CharacterLifecycleService.GetActionRoundSequence(state))));
            if (playerId == viewer)
            {
                fields.Add(E("bindings", A(state.FacilityCharacterBindings.Where(b => b.Active && b.ControllerPlayerId == playerId)
                    .Select(b => O(E("bindingId", S(b.BindingId)), E("characterItemId", S(b.CharacterItemId)), E("facilityItemId", S(b.FacilityItemId)))))));
                fields.Add(E("coveredCards", A(state.CharacterCoverSlots.Where(c => c.PlayerId == playerId && c.CardInstanceId.Length > 0)
                    .Select(c => O(E("cardItemId", S(c.CardInstanceId)), E("sourceEffectId", S(c.SourceEffectId)), E("reusable", B(c.Reusable)))))));
                fields.Add(E("styleDeclarations", A(player.DeclaredCityStyles.Select(d => O(E("markerItemId", S(d.InfluenceMarkerId)),
                    E("specialActionId", S(d.UnlockedSpecialActionId)), E("remainingUses", N(d.RemainingSpecialActionUses)),
                    E("usedThisRound", B(player.UsedSpecialActionIdsThisRound.Contains(d.UnlockedSpecialActionId))))))));
            }
            snapshot.Facts = O(fields.ToArray());
        }

        private static void AddInstanceContinuationFacts(GameState state, string itemId,
            IDictionary<string, LuaSnapshotValue> fields, HashSet<string> visible)
        {
            // 不增可见实例，只给本次已经授权的实例补同样可见的关系目标。
            fields["relations"] = A(state.ItemZones.Relations.Where(r => r.SourceItemId == itemId && visible.Contains(r.TargetItemId))
                .Select(r => O(E("relationTypeId", S(r.RelationTypeId)), E("targetItemId", S(r.TargetItemId)))));
            var item = state.ItemZones.Items.Single(i => i.ItemId == itemId);
            var entrepreneur = state.EntrepreneurSetup?.Players.SingleOrDefault(p => p.AbilityItemId == itemId || p.RoundGroups.Any(g => g.Candidates.Any(c => c.EntityItemId == itemId)));
            if (entrepreneur != null)
            {
                fields["entrepreneurAbilityItemId"] = S(entrepreneur.AbilityItemId);
                fields["entrepreneurDefinitionId"] = S(entrepreneur.DefinitionId);
                fields["roundChoices"] = A(entrepreneur.RoundGroups.OrderBy(g => g.RoundNumber).Select(g => O(
                    E("groupId", S(g.GroupId)), E("roundNumber", N(g.RoundNumber)), E("selectedCandidateId", S(g.SelectedCandidateId)), E("activeFaceId", S(g.ActiveFaceId)),
                    E("candidates", A(g.Candidates.OrderBy(c => c.DisplayOrder).Select(c => O(E("candidateId", S(c.CandidateId)), E("entityItemId", S(c.EntityItemId)), E("definitionId", S(c.DefinitionId)), E("faceId", S(c.FaceId)), E("displayOrder", N(c.DisplayOrder)))))))));
            }
            if (item.PhysicalKind != ItemPhysicalKind.Token) return;
            if (item.TypeId == "core.die") return;
            fields["locationId"] = S(TokenMovementProfileRegistry.GetLocationId(state, itemId));
            if (!state.ItemZones.Relations.Any(r => r.SourceItemId == itemId && r.RelationTypeId == "core.persistent_token_source")) return;
            fields["adjacentDestinations"] = Strings(new TokenMovementProfileRegistry().GetDestinationLocationIds(state, itemId, "core.adjacent_location_token_move"));
        }
    }
}
