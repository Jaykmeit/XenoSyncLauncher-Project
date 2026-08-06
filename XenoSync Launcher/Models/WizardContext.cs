namespace XenoSyncLauncher.Models;

/// <summary>
/// Tipo de instalación elegido en la página de selección.
/// </summary>
public enum InstallationType
{
    NotSelected,

    /// <summary>
    /// Tipo 1: el Xenoverse 2 Modded se instala ENCIMA del directorio Vanilla.
    /// El directorio Vanilla pasa a convertirse en el directorio Modded.
    /// </summary>
    OverVanilla,

    /// <summary>
    /// Tipo 2: el Xenoverse 2 Modded se instala en un directorio distinto/ajeno
    /// al Vanilla. El directorio Vanilla es opcional (solo se usa como origen
    /// de copia / referencia de versión).
    /// </summary>
    SeparateDirectory
}

/// <summary>
/// Resultado de la fase de evaluación de versiones (Vanilla vs. Revamp).
/// </summary>
public enum EvaluationAction
{
    /// <summary>No hay que hacer nada: la versión ya coincide con la soportada por Revamp.</summary>
    NoActionRequired,

    /// <summary>La versión instalada no coincide con la soportada por Revamp: hace falta downgrade vía DepotDownloader.</summary>
    DowngradeRequired,

    /// <summary>No existe instalación Vanilla: hay que descargar el manifest correspondiente a la versión de Revamp desde cero.</summary>
    FreshDownloadRequired
}

/// <summary>
/// Contexto compartido entre todas las páginas del Wizard. Se pasa por
/// referencia (misma instancia) a cada página para que puedan leer y
/// escribir el estado acumulado de la instalación.
/// </summary>
public class WizardContext
{
    public InstallationType InstallType { get; set; } = InstallationType.NotSelected;

    /// <summary>Ruta del Xenoverse 2 Vanilla (obligatoria en Tipo 1, opcional en Tipo 2).</summary>
    public string? VanillaPath { get; set; }

    /// <summary>Ruta destino del Xenoverse 2 Modded.
    /// En Tipo 1 es la MISMA ruta que VanillaPath (se convierte in-place).
    /// En Tipo 2 es una ruta independiente elegida por el usuario.</summary>
    public string? ModdedPath { get; set; }

    /// <summary>El usuario ha confirmado el aviso de conflictos de login / auto-update de Steam (solo Tipo 1).</summary>
    public bool ConflictWarningAcknowledged { get; set; }

    /// <summary>Resultado calculado durante la fase de evaluación.</summary>
    public EvaluationAction? EvaluationResult { get; set; }

    public VersionInfo? DetectedVanillaVersion { get; set; }
    public VersionInfo? RevampSupportedVersion { get; set; }

    /// <summary>True cuando la fase de evaluación ha terminado sin errores y se puede pasar al resumen.</summary>
    public bool EvaluationCompleted { get; set; }

    /// <summary>
    /// Set (and offered to the user) only when EvaluationResult is
    /// NoActionRequired and this is a separate-directory install: since the
    /// installed version already matches what Revamp needs, no downgrade is
    /// necessary - just copying Vanilla's files into Modded is enough.
    /// </summary>
    public bool ShouldCopyVanillaToModded { get; set; }

    /// <summary>Path chosen (or auto-installed) for DepotDownloader.exe, set by DepotDownloaderSetupPage.</summary>
    public string? DepotDownloaderPath { get; set; }
}