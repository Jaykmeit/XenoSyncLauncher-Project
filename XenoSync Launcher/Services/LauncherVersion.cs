namespace XenoSyncLauncher.Services;

/// <summary>
/// The launcher's own current version. Compared (simple string equality,
/// same convention as XV2Patcher/Revamp version tracking elsewhere in this
/// codebase - not real semver parsing) against the latest GitHub release tag
/// by SelfUpdateService to decide whether a self-update is available.
/// Bump this manually with each release.
/// </summary>
public static class LauncherVersion
{
    public const string Current = "0.1";
}