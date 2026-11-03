using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    public static partial class ItemMovementV4
    {
        internal static bool MainCoverOperation(NormalizedValue args)=>CoverOperation(args)&&Text(Value(args,"cover"),"mode")=="main";
        public static EffectSpec MainCoverSpec(GameState state,int player,string itemId,string sourceSlot)
        {
            var source=Object("zoneId",NormalizedValue.CreateString(FindZone(state,"hand",player).ZoneId));
            if(sourceSlot.Length>0){source.Properties.Add(new NormalizedValueEntry{Name="slotId",Value=NormalizedValue.CreateString(sourceSlot)});source.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));}
            var spec=EffectSpec.Create(TypeId,Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),"operation",NormalizedValue.CreateString("cover_character"),
                "selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(new[]{itemId}),"expectedSource",source),
                "cover",Object("mode",NormalizedValue.CreateString("main"),"acceptedTypeId",NormalizedValue.CreateString("core.character_card"),"reusable",NormalizedValue.CreateBoolean(false),"blockedActionRoundSequence",NormalizedValue.CreateInteger(0))),player);
            return spec;
        }
    }
}
