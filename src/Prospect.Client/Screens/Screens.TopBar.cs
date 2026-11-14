using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Lobby;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private enum BarTab : byte
    {
        /// <summary>The main menu: only out of a game, where it is the way back to the menu.</summary>
        Play,
        Fleet,
        Map,
        Charts,
        Inventory,
        Player,
        Social,
        Settings,
    }

    private static readonly BarTab[] SessionTabs = [BarTab.Fleet, BarTab.Map, BarTab.Charts, BarTab.Inventory, BarTab.Player, BarTab.Social, BarTab.Settings];
    private static readonly BarTab[] MenuTabs = [BarTab.Play, BarTab.Social, BarTab.Settings];
    private readonly float[] _tabLabels = new float[8];
    private readonly float[] _tabLeft = new float[8];
    private readonly float[] _tabWidth = new float[8];

    /// <summary>True when the bar's tabs are those of a game: the game screen, or Settings opened from it.</summary>
    private bool BarInGame => _screen == Screen.Session || (_screen == Screen.Settings && _settingsReturn == Screen.Session);

    private static ReadOnlySpan<byte> TabLabel(BarTab tab) => tab switch
    {
        BarTab.Play => "PLAY"u8,
        BarTab.Fleet => "FLEET"u8,
        BarTab.Map => "MAP"u8,
        BarTab.Charts => "CHARTS"u8,
        BarTab.Inventory => "INVENTORY"u8,
        BarTab.Player => "PLAYER"u8,
        BarTab.Social => "SOCIAL"u8,
        _ => "SETTINGS"u8,
    };

    /// <summary>The tab lit for where the player is: one at a time.</summary>
    private BarTab ActiveTab()
    {
        if (_screen == Screen.Settings)
            return BarTab.Settings;
        if (_screen == Screen.Account)
            return BarTab.Social;
        if (_screen != Screen.Session)
            return BarTab.Play;
        if (_mapOpen)
            return BarTab.Map;
        // In interface mode the tab of the window in front is lit; with none in front the social windows' is.
        if (InterfaceModeOn)
            return _focusedPane switch
            {
                SocialPane.Charts => BarTab.Charts,
                SocialPane.Inventory => BarTab.Inventory,
                SocialPane.Player => BarTab.Player,
                _ => BarTab.Social,
            };
        return _app.World.Mode == ViewMode.Command ? BarTab.Fleet : BarTab.Play;
    }

    /// <summary>
    /// The navigation bar along the top. In a game: Fleet (the command view, again to return to the pilot view), Map,
    /// Charts, Inventory, Player (windows that open in interface mode), Social and Settings; out of one: Play (the main
    /// menu), Social (Friends) and Settings. No tab starts or leaves a game.
    /// At the right, the Discord account's display name as the account service gives it, or the way to sign in.
    /// While flight has the pointer it is drawn dimmed: the interface cannot see a captured pointer at all.
    /// </summary>
    private void DrawTopBar(SessionSnapshot? snapshot)
    {
        var display = _ui.DisplaySize;
        var scale = Kit.Scale;
        var tabs = BarInGame ? SessionTabs : MenuTabs;
        var labels = _tabLabels.AsSpan(0, tabs.Length);
        for (var i = 0; i < tabs.Length; i++)
            labels[i] = Kit.Measure(_ui.SansMedium, Theme.FontSmall, TabLabel(tabs[i])).X;

        var view = _app.Accounts.View;
        var account = AccountLabel(view, out var accountTone);
        var accountWant = Kit.Measure(_ui.SansMedium, Theme.FontSmall, account).X + Kit.Px(Theme.Space3) * 2;
        var layout = TopBar.Layout(display, scale, labels, accountWant, _tabLeft, _tabWidth);

        TopBar.Begin(display, scale, _mapOpen && _screen == Screen.Session ? _mapPointer : null);
        var previousFade = Kit.Fade;
        Kit.Fade = _app.PointerCaptured ? 0.6f : 1f;
        var draw = ImGui.GetWindowDrawList();
        var height = layout.Height;
        draw.AddRectFilled(float2.Zero, new float2(display.X, height), Kit.Color(Theme.Panel, Hud.FillAlpha));
        draw.AddLine(new float2(0, height - 0.5f), new float2(display.X, height - 0.5f), Kit.Color(Theme.Line));

        // The brand mark: an amber chevron, the shape of the game's cut corners.
        var mark = new float2(MathF.Round(layout.BrandWidth * 0.5f), MathF.Round(height * 0.5f));
        var size = Kit.Px(7);
        draw.AddTriangle(mark + new float2(0, -size), mark + new float2(size, size * 0.8f), mark + new float2(-size, size * 0.8f), Kit.Color(Theme.Accent), Kit.Px(2));

        var active = ActiveTab();
        var news = (snapshot is { Social.Invitation.Player: not 0 } && BarInGame) || (view.State == AccountState.SignedIn && view.Incoming.Length > 0);
        for (var i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            var min = new float2(_tabLeft[i], 0);
            var tabSize = new float2(_tabWidth[i], height);
            ImGui.PushID(i);
            var pressed = TopBar.Tab("##tab"u8, min, tabSize);
            ImGui.PopID();
            var hot = Kit.Animate(ImGui.GetItemID(), ImGui.IsItemHovered() ? 1f : 0f);
            var lit = tab == active;
            var label = TabLabel(tab);
            var textHeight = Kit.Measure(_ui.SansMedium, Theme.FontSmall, label).Y;
            var text = new float2(min.X + (tabSize.X - labels[i]) * 0.5f, MathF.Round((height - textHeight) * 0.5f));
            Kit.Text(draw, _ui.SansMedium, Theme.FontSmall, text, lit ? Theme.Text : Kit.Mix(Theme.TextDim, Theme.Text, hot), label);
            if (lit)
                draw.AddRectFilled(new float2(text.X, height - Kit.Px(2)), new float2(text.X + labels[i], height), Kit.Color(Theme.Accent));
            if (tab == BarTab.Social && news)
                draw.AddCircleFilled(new float2(text.X + labels[i] + Kit.Px(5), text.Y + Kit.Px(2)), Kit.Px(3), Kit.Color(Theme.Accent), 12);
            if (pressed)
                ChooseTab(tab, snapshot);
        }

        if (layout.AccountWidth > 0)
        {
            var min = new float2(layout.AccountLeft, 0);
            var pressed = TopBar.Tab("##account"u8, min, new float2(layout.AccountWidth, height));
            var hot = Kit.Animate(ImGui.GetItemID(), ImGui.IsItemHovered() ? 1f : 0f);
            if (hot > 0.01f)
                draw.AddRectFilled(min, min + new float2(layout.AccountWidth, height - 1), Kit.Color(Theme.PanelRaised, 0.9f * hot));
            var textHeight = Kit.Measure(_ui.SansMedium, Theme.FontSmall, "Ag"u8).Y;
            // A name from the account service: drawn as text only, cut to the room.
            Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, new float2(min.X + Kit.Px(Theme.Space3), MathF.Round((height - textHeight) * 0.5f)),
                Kit.Mix(accountTone, Theme.Text, hot * 0.5f), account, Math.Max(0f, layout.AccountWidth - Kit.Px(Theme.Space3) * 2));
            if (pressed)
                ChooseAccount(view);
        }

        Kit.Fade = previousFade;
        TopBar.End();
    }

    /// <summary>What the account area says: the display name, or what to do about signing in.</summary>
    private static ReadOnlySpan<byte> AccountLabel(AccountView view, out uint tone)
    {
        switch (view.State)
        {
            case AccountState.SignedIn:
                tone = Theme.Text;
                return view.NameUtf8.Length > 0 ? view.NameUtf8 : "SIGNED IN"u8;
            case AccountState.SignedOut:
                tone = Theme.Accent;
                return "SIGN IN WITH DISCORD"u8;
            case AccountState.Off:
                tone = Theme.TextMute;
                return "ACCOUNTS OFF"u8;
            default:
                tone = Theme.TextDim;
                return "SIGNING IN..."u8;
        }
    }

    /// <summary>
    /// The account area's press: signed out it starts the sign-in, as the Friends window's button does; otherwise
    /// it opens Friends, where the account, Sign out and a sign-in that is waiting (Cancel, Copy link) are.
    /// </summary>
    private void ChooseAccount(AccountView view)
    {
        if (view.State == AccountState.SignedOut)
        {
            if (!view.Busy)
                _app.Accounts.SignIn();
            return;
        }

        if (BarInGame)
        {
            if (_screen != Screen.Session)
                Show(Screen.Session);
            _mapOpen = false;
            OpenFriends();
        }
        else
        {
            Show(Screen.Account);
        }
    }

    /// <summary>What a tab does. In a game every tab stays in the game; out of one, none starts one.</summary>
    private void ChooseTab(BarTab tab, SessionSnapshot? snapshot)
    {
        if (!BarInGame)
        {
            switch (tab)
            {
                case BarTab.Play:
                    Show(Screen.MainMenu);
                    break;
                case BarTab.Social:
                    if (_screen != Screen.Account)
                        Show(Screen.Account);
                    break;
                case BarTab.Settings:
                    if (_screen != Screen.Settings)
                        OpenSettings(_screen);
                    break;
            }

            return;
        }

        if (tab == BarTab.Settings)
        {
            if (_screen != Screen.Settings)
                OpenSettings(Screen.Session);
            return;
        }

        if (_screen != Screen.Session)
            Show(Screen.Session);
        var world = _app.World;
        switch (tab)
        {
            case BarTab.Fleet:
                _mapOpen = false;
                if (InterfaceModeOn)
                    CloseWorkspace();
                // The tab that is lit is the way back: pressing it again returns to the pilot view.
                world.SetMode(world.Mode == ViewMode.Command ? ViewMode.Pilot : ViewMode.Command);
                break;
            case BarTab.Map:
                if (SocialSurfaceOpen)
                    CloseWorkspace();
                if (!_mapOpen && world.Galaxy is not null)
                    OpenMap();
                break;
            case BarTab.Charts:
                _mapOpen = false;
                OpenWindow(SocialPane.Charts, true);
                break;
            case BarTab.Inventory:
                _mapOpen = false;
                OpenWindow(SocialPane.Inventory, true);
                break;
            case BarTab.Player:
                _mapOpen = false;
                OpenWindow(SocialPane.Player, true);
                break;
            case BarTab.Social:
                _mapOpen = false;
                if (InterfaceModeOn && _focusedPane is not (SocialPane.Charts or SocialPane.Inventory or SocialPane.Player))
                    CloseWorkspace();
                else
                {
                    OpenWorkspace(snapshot);
                    // The Social tab lights for the window in front, so bring the social ones forward from the player's own.
                    if (_focusedPane is SocialPane.Charts or SocialPane.Inventory or SocialPane.Player)
                        Raise(SocialPane.Navigator);
                }

                break;
        }
    }
}
