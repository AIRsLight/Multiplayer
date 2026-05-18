using System.Reflection;
using System.Linq;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Factions;

public static class MultifactionRouting
{
    public static Faction ResolveLetterRecipient(Letter letter, Map incidentMap, out string reason)
    {
        var faction = FactionForTargets(letter?.lookTargets, out reason);
        if (faction != null)
            return faction;

        if (incidentMap?.ParentFaction is { IsPlayer: true } incidentFaction)
        {
            reason = "incident-map";
            return incidentFaction;
        }

        var contextMap = Multiplayer.MapContext;
        if (contextMap?.ParentFaction is { IsPlayer: true } contextFaction)
        {
            reason = "map-context";
            return contextFaction;
        }

        reason = "unresolved";
        return null;
    }

    public static Faction ResolveDecisionOwner(Letter letter, out string reason)
    {
        switch (letter)
        {
            case ChoiceLetter_AcceptJoiner joiner:
                return FactionForMap(MapForAcceptJoiner(joiner), "accept-joiner-map", out reason);

            case ChoiceLetter_AcceptVisitors visitors:
                return FactionForPawnMap(visitors.pawns?.FirstOrDefault(p => p?.MapHeld != null), "accept-visitors-pawn-map", out reason);

            case ChoiceLetter_AcceptCreepJoiner creepJoiner:
                if (creepJoiner.speaker?.Faction is { IsPlayer: true } speakerFaction)
                {
                    reason = "creep-joiner-speaker";
                    return speakerFaction;
                }

                return FactionForPawnMap(creepJoiner.pawn, "creep-joiner-pawn-map", out reason);

            default:
                reason = "not-choice-owner";
                return null;
        }
    }

    public static void TraceIncident(IncidentParms parms, string phase)
    {
        if (!ShouldTrace)
            return;

        var map = parms?.target as Map;
        MpLog.Log("[MultifactionRoute] incident " +
            $"phase={phase} " +
            $"def={parms?.target?.GetType().Name}/{parms} " +
            $"target={TargetInfo(parms?.target)} " +
            $"contextFaction={FactionInfo(Faction.OfPlayer)} " +
            $"mapOwner={FactionInfo(map?.ParentFaction)} " +
            $"mapContext={MapInfo(Multiplayer.MapContext)}");
    }

    public static void TraceLetter(Letter letter, Map incidentMap, Faction recipient, string recipientReason)
    {
        if (!ShouldTrace)
            return;

        var decisionOwner = ResolveDecisionOwner(letter, out var decisionReason);
        MpLog.Log("[MultifactionRoute] letter " +
            $"type={letter?.GetType().Name ?? "null"} " +
            $"def={letter?.def?.defName ?? "null"} " +
            $"recipient={FactionInfo(recipient)} " +
            $"recipientReason={recipientReason} " +
            $"decisionOwner={FactionInfo(decisionOwner)} " +
            $"decisionReason={decisionReason} " +
            $"relatedFaction={FactionInfo(letter?.relatedFaction)} " +
            $"incidentMap={MapInfo(incidentMap)} " +
            $"mapContext={MapInfo(Multiplayer.MapContext)} " +
            $"targets={TargetsInfo(letter?.lookTargets)}");
    }

    public static void TraceCommand(ScheduledCommand cmd, Map map, string phase)
    {
        if (!ShouldTrace)
            return;

        MpLog.Log("[MultifactionRoute] command " +
            $"phase={phase} " +
            $"type={cmd.type} " +
            $"map={MapInfo(map)} " +
            $"cmdMap={cmd.mapId} " +
            $"cmdFaction={FactionInfo(CommandFaction(cmd))} " +
            $"cmdPlayer={cmd.playerId} " +
            $"issuedBySelf={IsIssuedBySelf(cmd)} " +
            $"contextFaction={FactionInfo(Faction.OfPlayer)} " +
            $"mapContext={MapInfo(Multiplayer.MapContext)}");
    }

    public static void TraceDecisionSync(object target, object[] args, string action)
    {
        if (!ShouldTrace)
            return;

        var letter = LetterFromSyncTarget(target);
        var decisionOwner = ResolveDecisionOwner(letter, out var decisionReason);
        var executingFaction = Faction.OfPlayer;
        var message = "[MultifactionRoute] decision " +
            $"action={action} " +
            $"target={target?.GetType().Name ?? "null"} " +
            $"type={letter?.GetType().Name ?? "null"} " +
            $"def={letter?.def?.defName ?? "null"} " +
            $"decisionOwner={FactionInfo(decisionOwner)} " +
            $"decisionReason={decisionReason} " +
            $"executingFaction={FactionInfo(executingFaction)} " +
            $"relatedFaction={FactionInfo(letter?.relatedFaction)} " +
            $"mapContext={MapInfo(Multiplayer.MapContext)} " +
            $"args={args?.Length ?? 0} " +
            $"targets={TargetsInfo(letter?.lookTargets)}";

        MpLog.Log(message);

        if (decisionOwner != null && executingFaction is { IsPlayer: true } && decisionOwner != executingFaction)
            MpLog.Warn("[MultifactionRoute] decision owner mismatch " + message);
    }

    public static void WarnUnresolvedLetter(Letter letter)
    {
        if (!ShouldTrace)
            return;

        MpLog.Warn("[MultifactionRoute] unresolved letter route " +
            $"type={letter?.GetType().Name ?? "null"} " +
            $"def={letter?.def?.defName ?? "null"} " +
            $"relatedFaction={FactionInfo(letter?.relatedFaction)} " +
            $"mapContext={MapInfo(Multiplayer.MapContext)} " +
            $"targets={TargetsInfo(letter?.lookTargets)}");
    }

    private static bool ShouldTrace =>
        Multiplayer.Client != null &&
        Multiplayer.GameComp.multifaction &&
        Multiplayer.ShowDevInfo;

    private static Letter LetterFromSyncTarget(object target)
    {
        if (target is Letter letter)
            return letter;

        if (target == null)
            return null;

        return target.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(f => typeof(Letter).IsAssignableFrom(f.FieldType))
            ?.GetValue(target) as Letter;
    }

    private static Faction FactionForTargets(LookTargets targets, out string reason)
    {
        if (targets?.targets == null)
        {
            reason = "no-targets";
            return null;
        }

        foreach (var target in targets.targets)
        {
            var faction = FactionForTarget(target, out reason);
            if (faction != null)
                return faction;
        }

        reason = "targets-unresolved";
        return null;
    }

    private static Faction FactionForTarget(GlobalTargetInfo target, out string reason)
    {
        if (target.Map?.ParentFaction is { IsPlayer: true } mapFaction)
        {
            reason = "target-map";
            return mapFaction;
        }

        if (target.WorldObject?.Faction is { IsPlayer: true } worldObjectFaction)
        {
            reason = "target-world-object";
            return worldObjectFaction;
        }

        if (target.Tile.Valid)
        {
            var mapParent = Find.WorldObjects.MapParentAt(target.Tile);
            if (mapParent?.Faction is { IsPlayer: true } mapParentFaction)
            {
                reason = "target-tile-map-parent";
                return mapParentFaction;
            }

            var caravan = Find.WorldObjects.PlayerControlledCaravanAt(target.Tile);
            if (caravan?.Faction is { IsPlayer: true } caravanFaction)
            {
                reason = "target-tile-caravan";
                return caravanFaction;
            }

            if (TileFactionContext.GetFactionForTile(target.Tile) is { IsPlayer: true } tileFaction)
            {
                reason = "target-tile-context";
                return tileFaction;
            }
        }

        reason = "target-unresolved";
        return null;
    }

    private static Faction FactionForPawnMap(Pawn pawn, string source, out string reason)
    {
        if (pawn?.Faction is { IsPlayer: true } pawnFaction)
        {
            reason = source + "-pawn-faction";
            return pawnFaction;
        }

        return FactionForMap(pawn?.MapHeld, source, out reason);
    }

    private static Faction FactionForMap(Map map, string source, out string reason)
    {
        if (map?.ParentFaction is { IsPlayer: true } mapFaction)
        {
            reason = source;
            return mapFaction;
        }

        reason = source + "-unresolved";
        return null;
    }

    private static Map MapForAcceptJoiner(ChoiceLetter_AcceptJoiner joiner)
    {
        return joiner.overrideMap ??
            joiner.lookTargets?.PrimaryTarget.Map ??
            Find.AnyPlayerHomeMap;
    }

    private static string TargetsInfo(LookTargets targets)
    {
        if (targets?.targets == null)
            return "none";

        return string.Join(",", targets.targets.Select(TargetInfo));
    }

    private static string TargetInfo(GlobalTargetInfo target)
    {
        if (target.Thing != null)
            return $"thing:{target.Thing.GetType().Name}@{MapInfo(target.Thing.MapHeld)} faction={FactionInfo(target.Thing.Faction)}";

        if (target.WorldObject != null)
            return $"worldObject:{target.WorldObject.GetType().Name} tile={target.WorldObject.Tile.tileId} faction={FactionInfo(target.WorldObject.Faction)}";

        if (target.Map != null)
            return $"cell:{target.Cell}@{MapInfo(target.Map)}";

        if (target.Tile.Valid)
            return $"tile:{target.Tile.tileId}";

        return "invalid";
    }

    private static string TargetInfo(IIncidentTarget target)
    {
        return target switch
        {
            Map map => MapInfo(map),
            Caravan caravan => $"caravan:{caravan.ID} tile={caravan.Tile.tileId} faction={FactionInfo(caravan.Faction)}",
            World => "world",
            null => "null",
            _ => target.ToString()
        };
    }

    private static string MapInfo(Map map)
    {
        if (map == null)
            return "null";

        return $"{map.uniqueID}/{map}";
    }

    private static string FactionInfo(Faction faction)
    {
        if (faction == null)
            return "null";

        return $"{faction.loadID}:{faction.Name}";
    }

    private static Faction CommandFaction(ScheduledCommand cmd)
    {
        if (cmd.factionId == ScheduledCommand.NoFaction)
            return null;

        return Find.FactionManager.GetById(cmd.factionId);
    }

    private static bool IsIssuedBySelf(ScheduledCommand cmd)
    {
        return cmd.playerId == Multiplayer.session?.playerId;
    }
}
