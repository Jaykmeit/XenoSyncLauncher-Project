namespace XenoSyncLauncher.Models;

/// <summary>
/// The three groups mods are organized into in the UI. Which mods exist and
/// which category each belongs to is curated by the launcher's maintainer
/// (via the hosted mods catalog), not chosen by end users - this keeps every
/// player's modded experience consistent, which matters for online play
/// between friends.
/// </summary>
public enum ModCategory
{
    /// <summary>Whatever the Revamp installer itself bundles. Always a single locked entry - XenoSync Launcher doesn't enumerate Revamp's internal contents.</summary>
    RevampCore,

    /// <summary>Additional mods the maintainer has marked mandatory. Always enabled, checkbox locked.</summary>
    XenoSyncCore,

    /// <summary>Additional mods the maintainer has made available, that the user may freely enable/disable.</summary>
    Optional
}
