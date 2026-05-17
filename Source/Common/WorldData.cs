using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Multiplayer.Common;

public class WorldData
{
    public int hostFactionId;
    public int spectatorFactionId;
    public byte[]? savedGame; // Compressed game save
    public byte[]? sessionData; // Compressed semi persistent data
    public Dictionary<int, byte[]> mapData = new(); // Map id to compressed map data

    public Dictionary<int, List<byte[]>> mapCmds = new(); // Map id to serialized cmds list
    public Dictionary<int, List<byte[]>>? tmpMapCmds;
    public int lastJoinPointAtTick = -1;

    public List<byte[]> syncInfos = new();

    public StandaloneWorldSnapshotState standaloneWorldSnapshot = new();
    public Dictionary<int, StandaloneMapSnapshotState> standaloneMapSnapshots = new();

    public static readonly TimeSpan JoinPointTimeout = TimeSpan.FromSeconds(120);

    private TaskCompletionSource<bool>? dataSource;
    private int joinPointStartedAtNetTick;
    private string joinPointIssuer = "";
    private int? joinPointIssuerPlayerId;

    public bool CreatingJoinPoint => tmpMapCmds != null;

    public MultiplayerServer Server { get; }

    public WorldData(MultiplayerServer server)
    {
        Server = server;
    }

    private int CurrentJoinPointTick => Server.IsStandaloneServer ? Server.gameTimer : Server.workTicks;

    public bool TryStartJoinPointCreation(bool force = false, ServerPlayer? sourcePlayer = null)
    {
        int currentTick = CurrentJoinPointTick;

        if (!force && lastJoinPointAtTick >= 0 && currentTick - lastJoinPointAtTick < 30)
        {
            ServerLog.Detail($"Join point skipped: cooldown active at tick={currentTick}, last={lastJoinPointAtTick}, standalone={Server.IsStandaloneServer}");
            return false;
        }

        if (CreatingJoinPoint && IsJoinPointTimedOut)
        {
            AbortJoinPointCreation($"timed out after {JoinPointTimeout.TotalSeconds:0}s before starting a new join point");
        }

        if (CreatingJoinPoint)
        {
            ServerLog.Detail(
                $"Join point skipped: already creating one, issuer={joinPointIssuer}, " +
                $"elapsedNetTicks={Server.NetTimer - joinPointStartedAtNetTick}");
            return false;
        }

        var issuingPlayer = sourcePlayer;
        if (Server.IsStandaloneServer && issuingPlayer?.IsPlaying != true)
        {
            issuingPlayer = Server.PlayingPlayers.FirstOrDefault(player => player.IsHost)
                ?? Server.PlayingPlayers.FirstOrDefault();

            if (issuingPlayer == null)
            {
                ServerLog.Detail("Join point skipped: no playing player available for standalone creation");
                return false;
            }
        }

        joinPointStartedAtNetTick = Server.NetTimer;
        joinPointIssuer = issuingPlayer != null ? $"{issuingPlayer.Username}#{issuingPlayer.id}" : "server";
        joinPointIssuerPlayerId = issuingPlayer?.id;

        ServerLog.Detail(
            $"Join point started at tick={currentTick}, force={force}, standalone={Server.IsStandaloneServer}, " +
            $"issuer={joinPointIssuer}");
        Server.SendChat("Creating a join point...");

        Server.commands.Send(CommandType.CreateJoinPoint, ScheduledCommand.NoFaction, ScheduledCommand.Global, Array.Empty<byte>(),
            sourcePlayer: Server.IsStandaloneServer ? issuingPlayer : null);
        tmpMapCmds = new Dictionary<int, List<byte[]>>();
        dataSource = new TaskCompletionSource<bool>();

        return true;
    }

    public bool IsJoinPointTimedOut =>
        CreatingJoinPoint && Server.NetTimer - joinPointStartedAtNetTick >= JoinPointTimeout.TotalSeconds * MultiplayerServer.NetTicksPerSecond;

    public bool IsJoinPointIssuer(ServerPlayer player) =>
        CreatingJoinPoint && joinPointIssuerPlayerId == player.id;

    public void EndJoinPointCreation()
    {
        int currentTick = CurrentJoinPointTick;
        ServerLog.Detail($"Join point completed at tick={currentTick}, standalone={Server.IsStandaloneServer}");
        mapCmds = tmpMapCmds!;
        tmpMapCmds = null;
        lastJoinPointAtTick = currentTick;

        if (Server.IsStandaloneServer && Server.persistence != null)
        {
            try
            {
                Server.persistence.WriteJoinPoint(this, currentTick);
            }
            catch (Exception e)
            {
                ServerLog.Error($"Failed to persist standalone join point at tick={currentTick}: {e}");
            }
        }

        dataSource?.SetResult(true);
        dataSource = null;
        joinPointIssuer = "";
        joinPointIssuerPlayerId = null;
    }

    public void AbortJoinPointCreation(string reason = "")
    {
        if (!CreatingJoinPoint)
            return;

        ServerLog.Log(
            $"Join point aborted{(string.IsNullOrEmpty(reason) ? "" : $": {reason}")}, " +
            $"issuer={joinPointIssuer}, elapsedNetTicks={Server.NetTimer - joinPointStartedAtNetTick}");

        tmpMapCmds = null;
        dataSource?.SetResult(false);
        dataSource = null;
        joinPointIssuer = "";
        joinPointIssuerPlayerId = null;
    }

    public Task<bool> WaitJoinPoint()
    {
        return dataSource?.Task ?? Task.FromResult(true);
    }

    public bool TryAcceptStandaloneWorldSnapshot(ServerPlayer player, int tick, byte[] worldSnapshot,
        byte[] sessionSnapshot, byte[] expectedHash)
    {
        if (tick < standaloneWorldSnapshot.tick)
            return false;

        var actualHash = ComputeHash(worldSnapshot, sessionSnapshot);
        if (expectedHash.Length > 0 && !actualHash.AsSpan().SequenceEqual(expectedHash))
            return false;

        savedGame = worldSnapshot;
        sessionData = sessionSnapshot;
        standaloneWorldSnapshot = new StandaloneWorldSnapshotState
        {
            tick = tick,
            producerPlayerId = player.id,
            producerUsername = player.Username,
            sha256Hash = actualHash
        };

        // Persist to disk
        Server.persistence?.WriteWorldSnapshot(worldSnapshot, sessionSnapshot, tick);

        return true;
    }

    public bool TryAcceptStandaloneMapSnapshot(ServerPlayer player, int mapId, int tick,
        byte[] mapSnapshot, byte[] expectedHash)
    {
        if (mapId < 0)
            return false;

        var snapshotState = standaloneMapSnapshots.GetOrAddNew(mapId);
        if (tick < snapshotState.tick)
            return false;

        var actualHash = ComputeHash(mapSnapshot);
        if (expectedHash.Length > 0 && !actualHash.AsSpan().SequenceEqual(expectedHash))
            return false;

        mapData[mapId] = mapSnapshot;
        snapshotState.tick = tick;
        snapshotState.producerPlayerId = player.id;
        snapshotState.producerUsername = player.Username;
        snapshotState.sha256Hash = actualHash;
        standaloneMapSnapshots[mapId] = snapshotState;

        // Persist to disk
        Server.persistence?.WriteMapSnapshot(mapId, mapSnapshot);

        return true;
    }

    private static byte[] ComputeHash(params byte[][] payloads)
    {
        using var hasher = SHA256.Create();
        foreach (var payload in payloads)
        {
            hasher.TransformBlock(payload, 0, payload.Length, null, 0);
        }

        hasher.TransformFinalBlock([], 0, 0);
        return hasher.Hash;
    }
}

public struct StandaloneWorldSnapshotState
{
    public StandaloneWorldSnapshotState() { }
    public int tick;
    public int producerPlayerId;
    public string producerUsername = "";
    public byte[] sha256Hash = Array.Empty<byte>();
}

public struct StandaloneMapSnapshotState
{
    public StandaloneMapSnapshotState() { }
    public int tick;
    public int producerPlayerId;
    public string producerUsername = "";
    public byte[] sha256Hash = Array.Empty<byte>();
}
