using Prospect.Engine.Net;
using Prospect.Game.Server;

namespace Prospect.Game.Client;

/// <summary>
/// One play session as the player sees it: a client, plus the server too when the game is local. A local game is a
/// full server in this process joined to the client by an in-process link, so both kinds run the same simulation.
/// </summary>
public sealed class GameSession : IDisposable
{
    private readonly ServerHost? _server;
    private readonly LoopbackLink? _link;

    private GameSession(ClientSession client, ServerHost? server, LoopbackLink? link, string title)
    {
        Client = client;
        _server = server;
        _link = link;
        Title = title;
    }

    public ClientSession Client { get; }

    /// <summary>True when this process also runs the server.</summary>
    public bool IsLocal => _server is not null;

    /// <summary>What the player is connected to, for display.</summary>
    public string Title { get; }

    /// <summary>Status of the in-process server, or null for a remote session.</summary>
    public ServerStatus? LocalServerStatus => _server?.Status;

    /// <summary>The first exception thrown by any of the session's threads, if one died.</summary>
    public Exception? Fault => Client.Fault ?? _server?.Fault;

    /// <summary>Starts a server in this process and joins it without touching the network.</summary>
    public static GameSession StartLocal(ServerConfig config, string playerName)
    {
        var link = new LoopbackLink();
        var server = new ServerHost(config, link.Server);
        var client = new ClientSession(link.Client, playerName);
        server.Start();
        client.Start();
        return new GameSession(client, server, link, config.Name);
    }

    /// <summary>Starts connecting to a server over the network.</summary>
    public static GameSession Join(string host, ushort port, string playerName)
    {
        var transport = RakNetTransport.Connect(host, port);
        var client = new ClientSession(transport, playerName);
        client.Start();
        return new GameSession(client, null, null, $"{host}:{port}");
    }

    public void Dispose()
    {
        // Threads first, then the memory they were using.
        Client.Dispose();
        _server?.Dispose();
        _link?.Dispose();
    }
}
