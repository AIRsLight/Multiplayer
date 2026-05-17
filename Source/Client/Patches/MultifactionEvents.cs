using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch(typeof(Settlement), nameof(Settlement.IncidentTargetTags))]
static class MultifactionSettlementIncidentTargetTagsPatch
{
    static System.Collections.Generic.IEnumerable<IncidentTargetTagDef> Postfix(
        System.Collections.Generic.IEnumerable<IncidentTargetTagDef> tags,
        Settlement __instance)
    {
        foreach (var tag in tags)
        {
            if (ShouldSuppressPlayerHomeTag(__instance, tag))
                continue;

            yield return tag;
        }
    }

    private static bool ShouldSuppressPlayerHomeTag(Settlement settlement, IncidentTargetTagDef tag) =>
        Multiplayer.Client != null &&
        Multiplayer.GameComp.multifaction &&
        tag == IncidentTargetTagDefOf.Map_PlayerHome &&
        settlement.Faction is { IsPlayer: true } &&
        settlement.Faction != Faction.OfPlayer;
}

[HarmonyPatch(typeof(SettlementDefeatUtility), nameof(SettlementDefeatUtility.IsDefeated), typeof(Map), typeof(Faction))]
static class MultifactionSettlementDefeatedPatch
{
    static bool Prefix(Faction faction, ref bool __result)
    {
        if (Multiplayer.Client == null ||
            !Multiplayer.GameComp.multifaction ||
            faction is not { IsPlayer: true } ||
            faction == Faction.OfPlayer)
        {
            return true;
        }

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
static class MultifactionIncidentTargetPatch
{
    [HarmonyPriority(Priority.First)]
    static bool Prefix(IncidentParms parms, ref bool __result)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction || parms.target is not Map map)
            return true;

        if (IncidentCanRunForCurrentFaction(map))
            return true;

        Log.Warning($"[Multiplayer] Blocked incident for wrong multifaction map. Faction={Faction.OfPlayer}, map={map}, parentFaction={map.ParentFaction}");
        __result = false;
        return false;
    }

    private static bool IncidentCanRunForCurrentFaction(Map map)
    {
        var currentFaction = Faction.OfPlayer;
        if (currentFaction is not { IsPlayer: true })
            return true;

        if (map.ParentFaction == currentFaction)
            return true;

        return map.mapPawns?.SpawnedPawnsInFaction(currentFaction).Any(IsIncidentPawn) == true;
    }

    private static bool IsIncidentPawn(Pawn pawn) =>
        pawn != null &&
        !pawn.Destroyed &&
        pawn.Spawned &&
        pawn.Faction == Faction.OfPlayer &&
        pawn.RaceProps.Humanlike &&
        pawn.HostFaction == null;
}
