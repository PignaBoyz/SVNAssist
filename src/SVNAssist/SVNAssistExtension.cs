using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace SVNAssist;

/// <summary>
/// Entry point dell'estensione SVNAssist.
/// Nel nuovo modello VisualStudio.Extensibility, questa classe sostituisce AsyncPackage.
/// </summary>
/// <remarks>
/// Differenze rispetto al vecchio modello (VSSDK):
/// - Eredita da <see cref="Extension"/> invece di <c>AsyncPackage</c>
/// - Non serve registrare comandi manualmente — il source generator li scopre
///   tramite l'attributo <c>[VisualStudioContribution]</c>
/// - Non servono GUID — l'identità è definita in <see cref="ExtensionConfiguration"/>
/// - L'estensione gira in un processo separato (out-of-process), non dentro VS,
///   quindi un crash dell'estensione non fa crashare Visual Studio
/// </remarks>
[VisualStudioContribution]
internal class SVNAssistExtension : Extension
{
    /// <inheritdoc />
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "SVNAssist",
            version: ExtensionAssemblyVersion,
            publisherName: "SVNAssist",
            displayName: "SVNAssist",
            description: "Integrazione SVN per Visual Studio con generazione automatica commit message tramite AI"),
    };

    /// <summary>
    /// Registra i servizi dell'estensione nel container DI.
    /// Chiamato una sola volta all'avvio dell'estensione.
    /// </summary>
    /// <remarks>
    /// <c>AddSettingsObservers()</c> registra gli observer generati dal source generator
    /// per le impostazioni definite in <see cref="Services.SettingDefinitions"/>.
    /// Nel vecchio modello VSSDK, la registrazione delle impostazioni non era necessaria —
    /// si leggevano direttamente dal registro di VS tramite <c>IVsWritableSettingsStore</c>.
    /// </remarks>
    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
        serviceCollection.AddSettingsObservers();
    }
}
