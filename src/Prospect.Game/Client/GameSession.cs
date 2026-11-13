using Prospect.Engine.Net;
using Prospect.Game.Lobby;
using Prospect.Game.Persistence;
using Prospect.Game.Server;
using Prospect.Game.World;

namespace Prospect.Game.Client;

/// <summary>
/// One play session as the player sees it: a client, plus the server too when the game is local. A local game is a
/// full server in this process joined to the client by an in-process link, so both kinds run the same simulation.
/// </summary>
public sealed class GameSession : IDisposable
{
    private readonly ServerHost? _server;
    private readonly LoopbackLink? _link;
    private bool _closeRequested;

    private GameSession(ClientSession client, ServerHost? server, LoopbackLink? link, string title)
    {
        Client = client;
        _server = server;
        _link = link;
        Title = title;
    }

    public ClientSession Client { get; }

    /// <summary>Explicitly asks to link the private saved local character to this signed-in account. No UI is wired yet.</summary>
    public bool RequestLocalClaim(AccountClient account) => !Volatile.Read(ref _closeRequested) && (_server?.RequestLocalClaim(account) ?? false);

    /// <summary>Proof and durable claim outcome, independently of save/close status. Safe from any thread.</summary>
    public LocalClaimState LocalClaim => _server?.LocalClaim ?? LocalClaimState.None;

    /// <summary>True when this process also runs the server.</summary>
    public bool IsLocal => _server is not null;

    /// <summary>What the player is connected to, for display.</summary>
    public string Title { get; }

    /// <summary>Status of the in-process server, or null for a remote session.</summary>
    public ServerStatus? LocalServerStatus => _server?.Status;

    /// <summary>
    /// Where a hosted public game stands with the listing server: listed, still trying, or off for a game that
    /// does not announce itself and for a remote session.
    /// </summary>
    public AnnounceState LocalAnnounce => _server?.Announce ?? AnnounceState.Off;

    /// <summary>The address a remote session was joined at, so it can be joined again with a password; null for a local game.</summary>
    public string? RemoteHost { get; private init; }

    /// <summary>The port a remote session was joined at; zero for a local game.</summary>
    public ushort RemotePort { get; private init; }

    /// <summary>The first exception thrown by any of the session's threads, if one died.</summary>
    public Exception? Fault => Client.Fault ?? _server?.Fault;

    /// <summary>True when this process runs the server and the server keeps its world in a store.</summary>
    public bool IsPersistent => _server?.IsPersistent ?? false;

    /// <summary>Where the local world's saves stand; off for a remote session and for a game that is not saved. Safe from any thread.</summary>
    public SaveStatus Save => _server?.Save ?? SaveStatus.Off;

    /// <summary>
    /// Where closing the session stands. A remote session has nothing of its own to save: it is closed the
    /// moment <see cref="BeginClose"/> is called. Safe from any thread; takes no memory.
    /// </summary>
    public CloseStatus Close =>
        _server?.Close
        ?? (Volatile.Read(ref _closeRequested)
            ? new CloseStatus(ClosePhase.Completed, FinalCommitOutcome.NotPersisted, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, null)
            : CloseStatus.Running);

    /// <summary>
    /// Asks the session to close, and returns at once. A local saved world is frozen at the end of its current
    /// interval and saved as it is then, before the client is disconnected: leaving must not be what the save
    /// records. Call it instead of disposing straight away, keep drawing the session while <see cref="Close"/>
    /// is not completed, then stop reading it and call <see cref="Dispose"/>. Look at
    /// <see cref="CloseStatus.FinalCommit"/> first: a final save that failed is not a clean exit, and the player
    /// should be told. Calling it again changes nothing.
    /// </summary>
    public void BeginClose()
    {
        Volatile.Write(ref _closeRequested, true);
        _server?.BeginClose();
    }

    /// <summary>
    /// Starts a server in this process and joins it over the in-process link. A private game touches no
    /// network. An unlisted or public one is the same server with a UDP listener beside the link, so other
    /// players can join the host's game; the host is not asked for the game's own password. Throws
    /// <see cref="IOException"/> when the UDP port cannot be opened and <see cref="InvalidDataException"/> for a
    /// name or password that cannot be used.
    /// </summary>
    public static GameSession StartLocal(ServerConfig config, GameContent content, string playerName)
    {
        var link = new LoopbackLink();
        ServerHost server;
        try
        {
            server = new ServerHost(config, content, link.Server);
        }
        catch
        {
            link.Dispose();
            throw;
        }

        // From here the server holds what it was given (a saved world's store too): a failure must dispose it.
        ClientSession? client = null;
        try
        {
            client = new ClientSession(link.Client, content, playerName);
            server.Start();
            client.Start();
        }
        catch
        {
            client?.Dispose();
            server.Dispose();
            link.Dispose();
            throw;
        }

        return new GameSession(client, server, link, server.Name);
    }

    /// <summary>
    /// Starts connecting to a server over the network. <paramref name="password"/> is for a server that has
    /// one; without it such a server ends the session with <see cref="SessionEndReason.PasswordRequired"/>.
    /// </summary>
    public static GameSession Join(string host, ushort port, GameContent content, string playerName, string? password = null)
    {
        var transport = RakNetTransport.Connect(host, port);
        var client = new ClientSession(transport, content, playerName, password);
        client.Start();
        return new GameSession(client, null, null, $"{host}:{port}") { RemoteHost = host, RemotePort = port };
    }

    /// <summary>
    /// Joins a game the player picked from the server browser. This is the first moment anything is sent to
    /// the listed address.
    /// </summary>
    public static GameSession Join(ListedGame game, GameContent content, string playerName, string? password = null)
    {
        // The list is not trusted: only an address the browser itself would have accepted is ever dialled.
        if (!ServerBrowser.IsJoinableAddress(game.Address, allowPrivateAddresses: true) || game.Port == 0)
            throw new IOException("That server's address is not one this game can join.");
        return Join(game.Address, game.Port, content, playerName, password);
    }

    /// <summary>
    /// Stops the session's threads and frees what they used. This saves nothing: for a saved world call
    /// <see cref="BeginClose"/> first and wait for <see cref="Close"/>. Disposing a saved world that was not
    /// closed loses what was not saved yet, never more: the owner's hulls stay in the world when the client
    /// disconnects, so the last completed save is whole whichever side stops first.
    /// </summary>
    public void Dispose()
    {
        // Threads first, then the memory they were using.
        Client.Dispose();
        _server?.Dispose();
        _link?.Dispose();
    }
}
