using System.Diagnostics;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;

namespace Prospect.Client;

/// <summary>Headless end-to-end social scripting through the same bridge used by the interface.</summary>
internal sealed class SocialScript
{
    public int Seconds;
    public string Name = "Prospector";
    public string? Say, PartySay, Invite;
    public bool Accept;

    public int Run(AppOptions options)
    {
        if (Say is { Length: > 256 } || PartySay is { Length: > 256 })
            throw new InvalidDataException("A scripted chat line must fit 256 UTF-16 units.");
        var content = GameContent.Load();
        using var session = options.JoinHost is { } host
            ? GameSession.Join(host, options.JoinPort, content, Name, options.Password)
            : GameSession.StartLocal(new ServerConfig(), content, Name);
        var clock = Stopwatch.StartNew();
        uint accepted = 0;
        var active = false;
        while (clock.Elapsed.TotalSeconds < Seconds)
        {
            var snapshot = session.Client.AcquireSnapshot();
            if (snapshot.State == SessionState.Ended) return 1;
            if (snapshot.State == SessionState.Active)
            {
                active = true;
                var input = session.Client.Input;
                if (Say is not null && input.SendChat(ChatChannel.Everyone, Say)) Say = null;
                if (PartySay is not null && snapshot.Social.PartyCount > 1 && input.SendChat(ChatChannel.Party, PartySay)) PartySay = null;
                if (Accept && snapshot.Social.Invitation.Player != 0 && accepted != snapshot.Social.Invitation.Player)
                { accepted = snapshot.Social.Invitation.Player; input.AcceptInvitation(); }
                if (snapshot.Social.Invitation.Player == 0) accepted = 0;
                if (Invite is not null)
                    for (var i = 0; i < snapshot.Social.RosterCount; i++)
                    {
                        ref var player = ref snapshot.Social.Roster[i];
                        if (player.Player != snapshot.PlayerId && player.Value != 0 && ((ReadOnlySpan<char>)player.Text[..player.Length]).SequenceEqual(Invite))
                        { input.Invite(player.Player); Invite = null; break; }
                    }
            }
            Thread.Sleep(20);
        }
        var final = session.Client.AcquireSnapshot();
        Console.WriteLine($"Social script: player {final.PlayerId}, roster {final.Social.RosterCount}, chat {final.Social.ChatCount}, party {final.Social.PartyCount}, refusal {final.Social.LastRefusal}");
        return active && session.Fault is null ? 0 : 1;
    }
}
