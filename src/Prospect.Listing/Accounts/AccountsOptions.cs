namespace Prospect.Listing.Accounts;

/// <summary>Configuration for the optional central accounts service.</summary>
public sealed class AccountsOptions
{
    /// <summary>Whether account storage and sign-in are available.</summary>
    public bool Enabled { get; set; }
    /// <summary>Fixed external origin, independent of incoming Host and forwarding headers.</summary>
    public string PublicUrl { get; set; } = "https://prospect.mvl.sh";
    /// <summary>Discord application client id.</summary>
    public string ClientId { get; set; } = "";
    /// <summary>Discord application secret; never written to logs.</summary>
    public string ClientSecret { get; set; } = "";
    /// <summary>Durable SQLite file, whose directory must already exist.</summary>
    public string DatabasePath { get; set; } = "/data/accounts.db";
    /// <summary>Maximum live native sign-in attempts.</summary>
    public int MaxAttempts { get; set; } = 1000;
    /// <summary>Maximum attempts per originating address group.</summary>
    public int AttemptsPerAddress { get; set; } = 5;
    /// <summary>Maximum concurrent Discord exchanges, with no waiting queue.</summary>
    public int DiscordConcurrency { get; set; } = 8;
    /// <summary>Total timeout for one Discord exchange.</summary>
    public int DiscordTimeoutSeconds { get; set; } = 15;
    /// <summary>Accepted friends per account.</summary>
    public int MaxFriends { get; set; } = 200;
    /// <summary>Outgoing pending requests per account.</summary>
    public int MaxOutgoing { get; set; } = 50;
    /// <summary>Incoming pending requests per account.</summary>
    public int MaxIncoming { get; set; } = 200;
    /// <summary>Pending requests expire after this many days.</summary>
    public int PendingDays { get; set; } = 30;
    /// <summary>Seconds a game ticket can be redeemed after it is issued.</summary>
    public int TicketSeconds { get; set; } = 30;
    /// <summary>Unredeemed game tickets the whole service holds at once.</summary>
    public int MaxTickets { get; set; } = 10000;
    /// <summary>Unredeemed game tickets one account holds at once.</summary>
    public int TicketsPerAccount { get; set; } = 4;
    internal string Callback => PublicUrl.TrimEnd('/') + "/v1/accounts/discord/callback";

    internal void Validate()
    {
        if (!Enabled) return;
        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri)
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.Host is "127.0.0.1" or "[::1]"
                && (PublicUrl.StartsWith("http://127.0.0.1", StringComparison.Ordinal) || PublicUrl.StartsWith("http://[::1]", StringComparison.Ordinal)))
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/"
            || string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret)
            || string.IsNullOrWhiteSpace(DatabasePath))
            throw new InvalidOperationException("Invalid Accounts origin, credentials or database configuration.");
        if (MaxAttempts is < 1 or > 100000 || AttemptsPerAddress < 1 || AttemptsPerAddress > MaxAttempts
            || DiscordConcurrency is < 1 or > 64 || DiscordTimeoutSeconds is < 1 or > 60
            || MaxFriends is < 1 or > 200 || MaxOutgoing is < 1 or > 50 || MaxIncoming is < 1 or > 1000
            || PendingDays is < 1 or > 365
            || TicketSeconds is < 1 or > 300 || MaxTickets is < 1 or > 1000000
            || TicketsPerAccount < 1 || TicketsPerAccount > MaxTickets)
            throw new InvalidOperationException("Invalid Accounts limits.");
    }
}
