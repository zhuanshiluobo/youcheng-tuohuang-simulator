using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YC.Domain.Rules;

namespace YC.Infrastructure.Lua
{
    public static class EntrepreneurContentCatalog
    {
        public static void ValidateInstalled(IEnumerable<ExternalContentDefinition> installed)
        {
            var all = installed.ToDictionary(d => d.DefinitionId, StringComparer.Ordinal);
            foreach (var entrepreneur in Read(all.Values))
            {
                foreach (var group in entrepreneur.RoundGroups)
                foreach (var candidate in group.Candidates)
                    if (!all.TryGetValue(candidate.EntityDefinitionId, out var entity) || entity.ContentType != "entrepreneur_round" ||
                        entity.ItemTypeId != "entrepreneur.round_card" || entity.Data["faces"]?.Any(f => (string)f["candidateId"] == candidate.CandidateId && (string)f["faceId"] == candidate.FaceId && (string)f["artwork"] == candidate.Artwork) != true)
                        throw new InvalidDataException("entrepreneur_candidate_reference_invalid:" + group.GroupId + ":" + candidate.CandidateId);
                foreach (var component in entrepreneur.Components)
                    if (component.DefinitionId != "core.player_marker" && (!all.TryGetValue(component.DefinitionId, out var definition) || definition.ItemTypeId != component.TypeId))
                        throw new InvalidDataException("entrepreneur_component_reference_invalid:" + component.EntryId);
            }
        }
        public static List<EntrepreneurDefinition> Read(IEnumerable<ExternalContentDefinition> definitions)
        {
            var result = definitions.Where(d => d.ContentType == "entrepreneur_ability" && d.Data["entrepreneur"] != null)
                .Select(d => d.Data["entrepreneur"].ToObject<EntrepreneurDefinition>()).ToList();
            EntrepreneurContent.ValidateDefinitions(result); return result;
        }
        public static void ValidateReady(ExternalContentPack pack, GameSetupRecipe recipe)
        {
            var definitions = pack.ActiveDefinitions; var entrepreneurs = Read(definitions);
            RuleModeReadiness.ValidateContent(new GameContentDefinitions { Entrepreneurs = entrepreneurs,
                AbilitySources = AbilitySourceLuaCatalog.CreateDeclarations(pack), Awards = ContentLifecycleLuaCatalog.CreateAwards(pack) }, recipe);
            var handlers = definitions.SelectMany(d => d.AbilityHandlers).ToDictionary(h => h.HandlerId, StringComparer.Ordinal);
            foreach (string handler in entrepreneurs.SelectMany(d => d.SetupHandlerRefs.Concat(d.RoundGroups.SelectMany(g => g.Candidates).SelectMany(f => f.ImmediateHandlerRefs))))
                if (!handlers.ContainsKey(handler)) throw new InvalidOperationException("entrepreneur_handler_unavailable:" + handler);
            foreach (var definition in definitions.Where(d => d.Data["requiredEntryHandlerRef"] != null))
                if (!handlers.ContainsKey((string)definition.Data["requiredEntryHandlerRef"]) || string.IsNullOrEmpty((string)definition.Data["effectScript"]))
                    throw new InvalidOperationException("entrepreneur_entry_handler_unavailable:" + definition.DefinitionId);
            foreach (var group in entrepreneurs.SelectMany(d => d.RoundGroups))
                if (group.PreviewCandidateId.Length == 0) throw new InvalidOperationException("diy_preview_candidate_missing:" + group.GroupId);
            foreach (var d in entrepreneurs)
            {
                foreach (string handler in d.SetupHandlerRefs)
                    if (definitions.SelectMany(c => c.AbilitySources).Count(s => s.DefinitionId == d.DefinitionId && s.Kind == "setup" && s.HandlerRef == handler) != 1)
                        throw new InvalidOperationException("entrepreneur_setup_source_missing_or_ambiguous:" + handler);
                foreach (var face in d.RoundGroups.SelectMany(g => g.Candidates))
                foreach (string handler in face.ImmediateHandlerRefs)
                    if (definitions.SelectMany(c => c.AbilitySources).Count(s => s.DefinitionId == face.EntityDefinitionId && s.ActiveFaceId == face.FaceId && s.Kind == "setup" && s.HandlerRef == handler && s.AuthorizationPolicy == "active_selection") != 1)
                        throw new InvalidOperationException("entrepreneur_immediate_source_missing_or_ambiguous:" + handler);
            }
        }
    }
}
