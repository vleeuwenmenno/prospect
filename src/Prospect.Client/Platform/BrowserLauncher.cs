using System.ComponentModel;
using System.Diagnostics;

namespace Prospect.Client.Platform;

/// <summary>
/// Opens a page in the player's own browser with the system's standard opener: the shell's URL handler on
/// Windows, <c>open</c> on macOS, <c>xdg-open</c> elsewhere. The address is passed as one argument, never through a
/// shell command line. Called only for an explicit request from the player (Sign in with Discord).
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>Opens the page; false when the address is not one this game opens or no opener could start.</summary>
    public static bool Open(Uri page)
    {
        if (!IsSafe(page))
            return false;
        var address = page.AbsoluteUri;
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                // ShellExecute hands an http(s) address to the default browser; nothing else is parsed.
                start = new ProcessStartInfo(address) { UseShellExecute = true };
            }
            else
            {
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(address);
            }

            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for an absolute https address, or http to this machine, without a user name or fragment and written
    /// with nothing a command line or the opener could read as anything else.
    /// </summary>
    public static bool IsSafe(Uri page)
    {
        if (!page.IsAbsoluteUri || page.UserInfo.Length > 0 || page.Fragment.Length > 0)
            return false;
        if (page.Scheme != Uri.UriSchemeHttps && !(page.Scheme == Uri.UriSchemeHttp && page.IsLoopback))
            return false;
        foreach (var c in page.AbsoluteUri)
        {
            if (c is <= ' ' or > '~' or '"' or '\'' or '`' or '\\' or '^' or '|' or '<' or '>')
                return false;
        }

        return true;
    }
}
