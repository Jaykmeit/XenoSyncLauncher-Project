using System.IO;

namespace XenoSyncLauncher.Services;

/// Swaps which gamepad DLL is active in "&lt;ModdedPath&gt;/bin". By default the
/// game uses xinput1_3.dll; some controllers work better if dinput8.dll is
/// used instead. Only one of the two lives in bin/ at a time — the other is
/// parked in a "-- alternative dll --" subfolder.
public class DllSwapService
{
    private const string XInputDllName = "xinput1_3.dll";
    private const string DInputDllName = "dinput8.dll";
    private const string AlternativeFolderName = "-- alternative dll --";

    public void ApplyDInput(string moddedPath) => Swap(moddedPath, activate: DInputDllName, park: XInputDllName);

    public void ApplyXInput(string moddedPath) => Swap(moddedPath, activate: XInputDllName, park: DInputDllName);

    private static void Swap(string moddedPath, string activate, string park)
    {
        var binDir = Path.Combine(moddedPath, "bin");
        var altDir = Path.Combine(binDir, AlternativeFolderName);
        Directory.CreateDirectory(altDir);

        // Park whichever DLL is currently active but shouldn't be anymore.
        var parkSource = Path.Combine(binDir, park);
        var parkDestination = Path.Combine(altDir, park);
        if (File.Exists(parkSource))
        {
            if (File.Exists(parkDestination)) File.Delete(parkDestination);
            File.Move(parkSource, parkDestination);
        }

        // Bring in the one that should be active now.
        var activateSource = Path.Combine(altDir, activate);
        var activateDestination = Path.Combine(binDir, activate);
        if (File.Exists(activateSource))
        {
            if (File.Exists(activateDestination)) File.Delete(activateDestination);
            File.Move(activateSource, activateDestination);
        }
    }
}
