using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using SVNAssist.Models;

namespace SVNAssist.ToolWindows;

/// <summary>Tipo di riga nell'elenco appiattito del pannello.</summary>
internal enum ChangeRowKind
{
    /// <summary>Intestazione di sezione (Conflitti / Modifiche / Non versionati).</summary>
    Section,

    /// <summary>Cartella dell'albero.</summary>
    Folder,

    /// <summary>File (o cartella non versionata) modificato.</summary>
    File,
}

/// <summary>
/// Una riga della lista appiattita (sezione, cartella o file). L'albero è reso con un'unica ListBox:
/// l'indentazione e il chevron sono precalcolati qui perché Remote UI non ha convertitori.
/// </summary>
[DataContract]
internal sealed class ChangeRowViewModel : NotifyPropertyChangedObject
{
    private bool? _isChecked = false;
    private bool _canAdd, _canDelete, _canRevert, _canResolve, _canIgnore, _canDiff;
    private string _chevron = string.Empty;
    private string _name = string.Empty;
    private string _detail = string.Empty;
    private string _toolTip = string.Empty;
    private string _statusText = string.Empty;
    private string _statusColor = "Gray";
    private double _indentWidth;

    public ChangeRowViewModel(ChangeRowKind kind, string key, ChangeSection section, IReadOnlyList<ChangeEntry> entries)
    {
        Kind = kind;
        Key = key;
        Section = section;
        Entries = entries;
    }

    // ── Dati interni (non serializzati) ──────────────────────────────

    /// <summary>Tipo di riga.</summary>
    internal ChangeRowKind Kind { get; }

    /// <summary>Chiave stabile (serve a riusare la riga tra un refresh e l'altro e a ricordare lo stato espanso).</summary>
    internal string Key { get; }

    /// <summary>Sezione della riga.</summary>
    internal ChangeSection Section { get; }

    /// <summary>Elementi rappresentati: uno per i file, tutti i discendenti per cartelle e sezioni.</summary>
    internal IReadOnlyList<ChangeEntry> Entries { get; private set; }

    /// <summary>Il file, per le righe file.</summary>
    internal ChangeEntry? Entry => Kind == ChangeRowKind.File ? Entries[0] : null;

    /// <summary>Percorso completo della cartella (solo righe cartella).</summary>
    internal string FolderFullPath { get; set; } = string.Empty;

    /// <summary>Percorso relativo della cartella (solo righe cartella).</summary>
    internal string FolderRelativePath { get; set; } = string.Empty;

    /// <summary>Richiesta di toggle dalla checkbox: il ViewModel decide il nuovo stato.</summary>
    internal Action<ChangeRowViewModel>? ToggleRequested { get; set; }

    // ── Proprietà bindabili ──────────────────────────────────────────

    /// <summary>Riga di sezione.</summary>
    [DataMember]
    public bool IsSection => Kind == ChangeRowKind.Section;

    /// <summary>Riga di cartella.</summary>
    [DataMember]
    public bool IsFolder => Kind == ChangeRowKind.Folder;

    /// <summary>Riga di file.</summary>
    [DataMember]
    public bool IsFile => Kind == ChangeRowKind.File;

    /// <summary>Il file può essere aggiunto a SVN (non versionato) / la cartella/sezione contiene elementi non versionati.</summary>
    [DataMember]
    public bool CanAdd
    {
        get => _canAdd;
        set => SetProperty(ref _canAdd, value);
    }

    /// <summary>L'elemento può essere cancellato da SVN (solo file versionati).</summary>
    [DataMember]
    public bool CanDelete
    {
        get => _canDelete;
        set => SetProperty(ref _canDelete, value);
    }

    /// <summary>L'elemento (o il suo contenuto) può essere ripristinato.</summary>
    [DataMember]
    public bool CanRevert
    {
        get => _canRevert;
        set => SetProperty(ref _canRevert, value);
    }

    /// <summary>Il file è in conflitto.</summary>
    [DataMember]
    public bool CanResolve
    {
        get => _canResolve;
        set => SetProperty(ref _canResolve, value);
    }

    /// <summary>L'elemento (o il suo contenuto non versionato) può essere aggiunto alla ignore list.</summary>
    [DataMember]
    public bool CanIgnore
    {
        get => _canIgnore;
        set => SetProperty(ref _canIgnore, value);
    }

    /// <summary>È disponibile il diff (file versionati / cartelle con modifiche).</summary>
    [DataMember]
    public bool CanDiff
    {
        get => _canDiff;
        set => SetProperty(ref _canDiff, value);
    }

    /// <summary>Spazio a sinistra (px) per l'indentazione dell'albero.</summary>
    [DataMember]
    public double IndentWidth
    {
        get => _indentWidth;
        set => SetProperty(ref _indentWidth, value);
    }

    /// <summary>Chevron ▸ / ▾ (vuoto per i file).</summary>
    [DataMember]
    public string Chevron
    {
        get => _chevron;
        set => SetProperty(ref _chevron, value);
    }

    /// <summary>
    /// Stato della checkbox: <c>true</c>, <c>false</c> o <c>null</c> (parziale, solo gruppi).
    /// Il click non imposta direttamente il valore: chiede al ViewModel di invertire la selezione.
    /// </summary>
    [DataMember]
    public bool? IsChecked
    {
        get => _isChecked;
        set => ToggleRequested?.Invoke(this);
    }

    /// <summary>Lettera di stato SVN (M, A, D, ?, ...). Vuota per sezioni e cartelle.</summary>
    [DataMember]
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>Colore (nome WPF) della lettera di stato.</summary>
    [DataMember]
    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    /// <summary>Nome mostrato (file, cartella o titolo della sezione con conteggio).</summary>
    [DataMember]
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>Testo secondario in grigio: cartella (vista Lista) o conteggio (cartelle).</summary>
    [DataMember]
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    /// <summary>Tooltip (percorso completo e descrizione dello stato).</summary>
    [DataMember]
    public string ToolTip
    {
        get => _toolTip;
        set => SetProperty(ref _toolTip, value);
    }

    /// <summary>
    /// Aggiorna la checkbox dal ViewModel. Con <paramref name="force"/> notifica anche se il valore
    /// non è cambiato: serve a riallineare la UI dopo un click che ha ciclato lo stato tri-state in locale.
    /// </summary>
    internal void ApplyChecked(bool? state, bool force = false)
    {
        if (_isChecked == state && !force)
            return;

        _isChecked = state;
        RaiseNotifyPropertyChangedEvent(nameof(IsChecked));
    }

    /// <summary>Valore corrente della checkbox (per i test).</summary>
    internal bool? CheckedState => _isChecked;

    /// <summary>Copia i campi di presentazione da una riga equivalente appena costruita.</summary>
    internal void CopyPresentationFrom(ChangeRowViewModel other)
    {
        Entries = other.Entries;
        FolderFullPath = other.FolderFullPath;
        FolderRelativePath = other.FolderRelativePath;
        IndentWidth = other.IndentWidth;
        Chevron = other.Chevron;
        StatusText = other.StatusText;
        StatusColor = other.StatusColor;
        Name = other.Name;
        Detail = other.Detail;
        ToolTip = other.ToolTip;
        CanAdd = other.CanAdd;
        CanDelete = other.CanDelete;
        CanRevert = other.CanRevert;
        CanResolve = other.CanResolve;
        CanIgnore = other.CanIgnore;
        CanDiff = other.CanDiff;
    }
}

/// <summary>Come mostrare gli stati SVN: lettera, descrizione, colore.</summary>
internal static class StatusPresentation
{
    /// <summary>Lettera abbreviata (come nell'output di <c>svn status</c>).</summary>
    public static string ShortCode(SvnStatusKind status) => status switch
    {
        SvnStatusKind.Modified => "M",
        SvnStatusKind.Added => "A",
        SvnStatusKind.Deleted => "D",
        SvnStatusKind.Conflicted => "C",
        SvnStatusKind.Unversioned => "?",
        SvnStatusKind.Missing => "!",
        SvnStatusKind.Replaced => "R",
        SvnStatusKind.Obstructed => "~",
        SvnStatusKind.Incomplete => "!",
        _ => "·",
    };

    /// <summary>Descrizione in italiano dello stato.</summary>
    public static string DisplayName(SvnStatusKind status, bool isTreeConflict) => status switch
    {
        SvnStatusKind.Conflicted => isTreeConflict ? "Conflitto (albero)" : "Conflitto",
        SvnStatusKind.Modified => "Modificato",
        SvnStatusKind.Added => "Aggiunto",
        SvnStatusKind.Deleted => "Eliminato",
        SvnStatusKind.Unversioned => "Non versionato",
        SvnStatusKind.Missing => "Mancante",
        SvnStatusKind.Replaced => "Sostituito",
        SvnStatusKind.Obstructed => "Ostruito",
        SvnStatusKind.Incomplete => "Incompleto",
        SvnStatusKind.Normal => "Normale",
        _ => "Sconosciuto",
    };

    /// <summary>Colore (nome WPF) leggibile sia in tema chiaro che scuro.</summary>
    public static string Color(SvnStatusKind status) => status switch
    {
        SvnStatusKind.Modified => "CornflowerBlue",
        SvnStatusKind.Added => "MediumSeaGreen",
        SvnStatusKind.Deleted => "IndianRed",
        SvnStatusKind.Conflicted => "Crimson",
        SvnStatusKind.Missing or SvnStatusKind.Obstructed or SvnStatusKind.Incomplete => "DarkOrange",
        SvnStatusKind.Replaced => "MediumPurple",
        _ => "Gray",
    };
}
