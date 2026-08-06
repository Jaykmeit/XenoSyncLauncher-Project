using System.Diagnostics;
using Microsoft.Win32;

namespace XenoSyncLauncher.Services;

/// Detects whether the local Steam client is running and has an active
/// logged-in user, instead of asking for Steam credentials directly inside
/// XenoSync Launcher. DepotDownloader-backed features (and, in the future,
/// any Auto-Update check that needs Steam) should call
/// <see cref="IsRunningAndLoggedIn"/> before proceeding, and use
/// <see cref="LaunchSteamClient"/> to prompt the user to sign in if not.
///
/// TODO: DepotDownloader authenticates against Steam independently of the
/// running Steam client — it does not literally reuse the client's session
/// token. This check assumes a login key has already been cached for the
/// configured Steam account (e.g. by running DepotDownloader once manually
/// with -remember-password) so it can log in silently once a Steam session
/// is detected locally. If DepotDownloader still prompts for a password with
/// no cached key available, see DepotDownloaderService's handling of that case.
public class SteamSessionService
{
    public bool IsRunningAndLoggedIn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key is null) return false;

            var activeUser = key.GetValue("ActiveUser");
            return activeUser is int id && id != 0;
        }
        catch
        {
            return false;
        }
    }

    /// Launches (or brings to front) the Steam client via its steam:// URI
    /// handler. If the user isn't logged in, Steam will show its own sign-in
    /// screen. Requires Steam to be installed and registered as a URI handler,
    /// which is the case for any standard Steam installation.
    public void LaunchSteamClient()
    {
        try
        {
            Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
        }
        catch
        {
            // TODO: fall back to launching Steam.exe directly using the path
            // stored at HKEY_CURRENT_USER\Software\Valve\Steam "SteamExe" /
            // "SteamPath", in case the steam:// protocol isn't registered.
        }
    }
}
