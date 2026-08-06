namespace XenoSyncLauncher.Models;

/// Static metadata for one boolean flag in xv2patcher.ini that XenoSync Launcher exposes as a toggle.
public class PatcherFlagDefinition
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
}
