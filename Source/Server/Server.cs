using System.Net;
using System.Diagnostics;
using Multiplayer.Common;
using Multiplayer.Common.Util;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

const string settingsFile = "settings.toml";
const string stopCmd = "stop";
const string saveFile = "save.zip";
const string restartDelayArg = "--bootstrap-restart-delay-ms=";

var startupDelay = GetBootstrapRestartDelay();
if (startupDelay > 0)
{
    ServerLog.Log($"Bootstrap restart: waiting {startupDelay}ms for previous server to shut down.");
    Thread.Sleep(startupDelay);
}

var settings = new ServerSettings
{
    direct = true,
    lan = false
};

var settingsPresent = File.Exists(settingsFile);
if (settingsPresent)
    settings = TomlSettings.Load(settingsFile);
else
    ServerLog.Log($"Bootstrap mode: '{settingsFile}' not found. Waiting for a client to upload it.");
ServerLog.detailEnabled = settings.debugMode;
ServerLog.verboseEnabled = settings.debugMode;

if (settings.steam) ServerLog.Error("Steam is not supported in standalone server.");
if (settings.arbiter) ServerLog.Error("Arbiter is not supported in standalone server.");

var server = MultiplayerServer.instance = new MultiplayerServer(settings)
{
    running = true,
    IsStandaloneServer = true,
};
server.OnBootstrapCompleted = StartReplacementServer;

var persistence = new StandalonePersistence(AppContext.BaseDirectory);
server.persistence = persistence;

// Cleanup leftover temp files from any previous interrupted writes
persistence.CleanupTempFiles();

var consoleSource = new ConsoleSource();

var bootstrap = !settingsPresent;

if (!bootstrap && persistence.HasValidState())
{
    // Prefer loading from the Saved/ directory (structured persistence)
    var info = persistence.LoadInto(server);
    if (info != null)
    {
        server.settings.gameName = info.name;
        server.worldData.hostFactionId = info.playerFaction;
        var spectatorFaction = info.spectatorFaction;
        if (server.settings.multifaction && spectatorFaction == 0)
            ServerLog.Error("Multifaction is enabled but the save doesn't contain spectator faction id.");
        server.worldData.spectatorFactionId = spectatorFaction;
    }
    ServerLog.Log("Loaded state from Saved/ directory.");
}
else if (!bootstrap && File.Exists(saveFile))
{
    // Seed the Saved/ directory from save.zip, then load from it
    ServerLog.Log($"Seeding Saved/ directory from {saveFile}...");
    persistence.SeedFromSaveZip(saveFile);
    var info = persistence.LoadInto(server);
    if (info != null)
    {
        server.settings.gameName = info.name;
        server.worldData.hostFactionId = info.playerFaction;
        var spectatorFaction = info.spectatorFaction;
        if (server.settings.multifaction && spectatorFaction == 0)
            ServerLog.Error("Multifaction is enabled but the save doesn't contain spectator faction id.");
        server.worldData.spectatorFactionId = spectatorFaction;
    }
}
else
{
    bootstrap = true;
    ServerLog.Log($"Bootstrap mode: neither Saved/ directory nor '{saveFile}' found.");
    ServerLog.Log("Waiting for a client to upload world data.");
}

server.BootstrapMode = bootstrap;

if (settings.direct) {
    var badEndpoint = settings.TryParseEndpoints(out var endpoints);
    if (badEndpoint != null)
    {
        ServerLog.Error($"Failed to parse endpoint: {badEndpoint}");
        return;
    }

    if (!LiteNetManager.Create(server, endpoints, out var liteNet))
    {
        ServerLog.Error("Failed to start net manager");
        return;
    }
    server.netManagers.Add(liteNet);
}

if (settings.lan)
{
    if (!IPAddress.TryParse(settings.lanAddress, out var ipAddr))
    {
        ServerLog.Error($"Failed to parse lan address: {settings.lanAddress}");
        return;
    }

    var lan = LiteNetLanManager.Create(server, ipAddr);
    if (lan == null)
    {
        ServerLog.Error("Failed to start lan manager");
        return;
    }
    server.netManagers.Add(lan);
}

var serverThread = new Thread(server.Run) { Name = "Server thread" };
serverThread.Start();

new Thread(ReadConsoleCommands) { Name = "Server console thread", IsBackground = true }.Start();
serverThread.Join();

void ReadConsoleCommands()
{
    while (server.running)
    {
        var cmd = Console.ReadLine();
        if (cmd == null)
            return;

        server.Enqueue(() => server.HandleChatCmd(consoleSource, cmd));

        if (cmd == stopCmd)
        {
            server.running = false;
            return;
        }
    }
}

int GetBootstrapRestartDelay()
{
    foreach (var arg in Environment.GetCommandLineArgs())
        if (arg.StartsWith(restartDelayArg, StringComparison.Ordinal) &&
            int.TryParse(arg[restartDelayArg.Length..], out var delay))
            return Math.Max(0, delay);

    return 0;
}

void StartReplacementServer()
{
    try
    {
        var currentArgs = Environment.GetCommandLineArgs();
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            processPath = currentArgs.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(processPath))
        {
            ServerLog.Error("Bootstrap restart failed: current process path is unknown.");
            return;
        }

        var isDotnetHost = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false
        };

        var firstArg = isDotnetHost ? 0 : 1;
        for (var i = firstArg; i < currentArgs.Length; i++)
        {
            if (!currentArgs[i].StartsWith(restartDelayArg, StringComparison.Ordinal))
                startInfo.ArgumentList.Add(currentArgs[i]);
        }

        startInfo.ArgumentList.Add($"{restartDelayArg}1000");

        if (Process.Start(startInfo) == null)
            ServerLog.Error("Bootstrap restart failed: Process.Start returned null.");
        else
            ServerLog.Log("Bootstrap restart: replacement server process started.");
    }
    catch (Exception e)
    {
        ServerLog.Error($"Bootstrap restart failed: {e}");
    }
}

class ConsoleSource : IChatSource
{
    public void SendMsg(string msg)
    {
        ServerLog.Log(msg);
    }
}
