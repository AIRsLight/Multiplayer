using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.Client.Factions;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client;

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

        MultifactionLetterTargetFactionPatch.PushIncidentMap(map);

        if (IncidentCanRunForCurrentFaction(map))
            return true;

        Log.Warning($"[Multiplayer] Blocked incident for wrong multifaction map. Faction={Faction.OfPlayer}, map={map}, parentFaction={map.ParentFaction}");
        __result = false;
        return false;
    }

    static void Finalizer(IncidentParms parms)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction || parms.target is not Map)
            return;

        MultifactionLetterTargetFactionPatch.PopIncidentMap();
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

[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), typeof(Letter), typeof(string), typeof(int), typeof(bool))]
static class MultifactionLetterTargetFactionPatch
{
    private static readonly Stack<Map> incidentMaps = new();

    public static void PushIncidentMap(Map map) => incidentMaps.Push(map);

    public static void PopIncidentMap()
    {
        if (incidentMaps.Count > 0)
            incidentMaps.Pop();
    }

    [HarmonyPriority(Priority.First)]
    static void Prefix(Letter let, ref bool __state)
    {
        if (Multiplayer.Client == null || !Multiplayer.GameComp.multifaction)
            return;

        var faction = FactionForLetter(let);
        if (faction == null)
            return;

        FactionContext.Push(faction);
        __state = true;
    }

    static void Finalizer(bool __state)
    {
        if (__state)
            FactionContext.Pop();
    }

    private static Faction FactionForLetter(Letter letter)
    {
        var faction = FactionForTargets(letter?.lookTargets);
        if (faction != null)
            return faction;

        if (incidentMaps.Count > 0 &&
            incidentMaps.Peek()?.ParentFaction is { IsPlayer: true } incidentFaction)
            return incidentFaction;

        var contextMap = Multiplayer.MapContext;
        if (contextMap?.ParentFaction is { IsPlayer: true } mapFaction)
            return mapFaction;

        return null;
    }

    private static Faction FactionForTargets(LookTargets targets)
    {
        if (targets?.targets == null)
            return null;

        foreach (var target in targets.targets)
        {
            var faction = FactionForTarget(target);
            if (faction != null)
                return faction;
        }

        return null;
    }

    private static Faction FactionForTarget(GlobalTargetInfo target)
    {
        if (target.Map?.ParentFaction is { IsPlayer: true } mapFaction)
            return mapFaction;

        if (target.WorldObject?.Faction is { IsPlayer: true } worldObjectFaction)
            return worldObjectFaction;

        if (target.Tile.Valid)
        {
            var mapParent = Find.WorldObjects.MapParentAt(target.Tile);
            if (mapParent?.Faction is { IsPlayer: true } mapParentFaction)
                return mapParentFaction;

            var caravan = Find.WorldObjects.PlayerControlledCaravanAt(target.Tile);
            if (caravan?.Faction is { IsPlayer: true } caravanFaction)
                return caravanFaction;

            if (TileFactionContext.GetFactionForTile(target.Tile) is { IsPlayer: true } tileFaction)
                return tileFaction;
        }

        return null;
    }
}
