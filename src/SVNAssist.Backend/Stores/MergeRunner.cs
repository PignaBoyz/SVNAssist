using SVNAssist.Models;
using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>
/// Un passaggio di <c>svn merge</c>: <see cref="Revs"/> consecutive nell'elenco da unire. <see cref="Separate"/> = una passata svn per revisione dentro un solo processo
/// (<c>-c 5,6,7</c>: svn si ferma da solo alla prima in conflitto e dice quale); altrimenti un solo intervallo <c>-c 5-7</c> (una passata, un giro al server).
/// </summary>
internal sealed record MergeStep(IReadOnlyList<long> Revs, bool Separate = false)
{
    internal long First => Revs[0];

    internal long Last => Revs[^1];

    internal IReadOnlyList<(long First, long Last)> Ranges => Separate ? Revs.Select(r => (r, r)).ToList() : [(First, Last)];
}

/// <summary>Esito di <see cref="MergeRunner.RunAsync"/>. Un merge che arriva in fondo senza conflitti ha <see cref="Stopped"/> falso.</summary>
internal sealed class MergeRunResult
{
    /// <summary>Revisioni applicate da questa esecuzione, in ordine. Quella in conflitto (se c'e') e' tra queste: svn la registra come unita e i conflitti restano da risolvere.</summary>
    internal List<long> Applied { get; } = [];

    /// <summary>Si e' fermato per un conflitto: <see cref="ConflictRev"/> e' la revisione esatta, oppure (conflitto non previsto dentro un intervallo) <see cref="ConflictRange"/>.</summary>
    internal bool Stopped { get; set; }

    internal long? ConflictRev { get; set; }

    internal (long First, long Last)? ConflictRange { get; set; }

    /// <summary>L'utente ha chiesto di fermarsi: si e' interrotto tra due passaggi, senza lasciare nulla a meta'.</summary>
    internal bool StoppedByUser { get; set; }

    /// <summary>Errore di svn che non e' un conflitto (rete, permessi...): lo stato reale lo dice mergeinfo.</summary>
    internal Exception? Error { get; set; }

    internal List<MergeItem> Items { get; } = [];

    internal int Conflicts { get; set; }

    internal int Skipped { get; set; }

    internal MergeOutput Output => new(Items, Conflicts, Skipped);
}

/// <summary>
/// Esegue un merge di molte revisioni <b>fermandosi dopo la revisione in conflitto</b> senza costare una chiamata a svn per revisione.
/// <list type="number">
/// <item>Le revisioni scelte si raggruppano per <i>adiacenza nell'elenco da unire</i> (nessuna revisione da unire ma non scelta in mezzo), non per numero consecutivo:
/// in un repository con altri rami i numeri sono a salti e il raggruppamento per numero lascerebbe ogni revisione da sola.</item>
/// <item>Di ogni finestra (<see cref="DryRunWindow"/> revisioni) si fa un dry-run unico: se non prevede conflitti, la finestra si applica a gruppi di <see cref="ChunkSize"/>
/// revisioni (un intervallo per gruppo); se ne prevede, le revisioni che toccano i file in conflitto si applicano ciascuna da sola e le altre a gruppi.</item>
/// <item>Se il dry-run non e' utilizzabile (E155015) o non si sa chi tocca i file in conflitto, il gruppo si applica con una passata per revisione in un solo processo:
/// e' svn a fermarsi, alla revisione giusta.</item>
/// <item>Dopo ogni passaggio si ferma se ci sono conflitti. Un conflitto non previsto dal dry-run dentro un intervallo ferma alla fine dell'intervallo (<see cref="MergeRunResult.ConflictRange"/>).</item>
/// </list>
/// Misure e scelta: docs/feedback/0.4.0/bug13-prestazioni.md.
/// </summary>
internal sealed class MergeRunner
{
    /// <summary>Revisioni per intervallo applicato in un colpo. 1 = una chiamata a svn per revisione (esatto, lento).</summary>
    internal const int DefaultChunkSize = 50, DefaultDryRunWindow = 200;

    internal static int ChunkSize { get; set; } = DefaultChunkSize;

    /// <summary>Revisioni per dry-run (un dry-run costa quanto il merge vero dello stesso intervallo: a finestre, cosi' il primo avanzamento arriva presto).</summary>
    internal static int DryRunWindow { get; set; } = DefaultDryRunWindow;

    private readonly ISvnService _svn;
    private readonly string _wc, _url;
    private readonly Func<IReadOnlyList<long>, CancellationToken, Task<IReadOnlyDictionary<long, IReadOnlySet<string>>>> _touched;

    /// <param name="touched">File toccati da ogni revisione, relativi alla cartella del merge (da <c>svn log -v</c>, in cache): serve a trovare chi tocca i file in conflitto.</param>
    internal MergeRunner(ISvnService svn, string workingCopy, string sourceUrl,
        Func<IReadOnlyList<long>, CancellationToken, Task<IReadOnlyDictionary<long, IReadOnlySet<string>>>> touched)
    {
        _svn = svn;
        _wc = workingCopy;
        _url = sourceUrl;
        _touched = touched;
    }

    /// <summary>Un passaggio sta per partire (per l'avanzamento e per segnare "passo in corso" nello stato salvato).</summary>
    internal Action<MergeStep>? StepStarting { get; set; }

    /// <summary>Un passaggio e' finito: le revisioni appena applicate (per salvare lo stato).</summary>
    internal Action<IReadOnlyList<long>>? StepDone { get; set; }

    /// <summary>Fase che non e' un passaggio (es. dry-run di controllo).</summary>
    internal Action<string>? Phase { get; set; }

    /// <summary>L'utente ha chiesto di fermarsi: si controlla tra un passaggio e l'altro.</summary>
    internal Func<bool>? StopRequested { get; set; }

    /// <summary>Il passaggio in corso (null tra un passaggio e l'altro): se il processo e' terminato a forza e' quello che puo' essere rimasto a meta'.</summary>
    internal MergeStep? InFlight { get; private set; }

    /// <summary>Gruppi di revisioni scelte adiacenti nell'elenco <paramref name="eligible"/> (crescente): nessuna revisione da unire ma non scelta in mezzo.</summary>
    internal static List<List<long>> Groups(IEnumerable<long> chosen, IEnumerable<long> eligible)
    {
        var pick = chosen.ToHashSet();
        var all = eligible.Concat(pick).Distinct().Order();
        var groups = new List<List<long>>();
        List<long>? cur = null;
        foreach (var r in all)
        {
            if (pick.Contains(r))
            {
                (cur ??= []).Add(r);
                continue;
            }

            if (cur is not null)
                groups.Add(cur);
            cur = null;
        }

        if (cur is not null)
            groups.Add(cur);
        return groups;
    }

    internal static IEnumerable<List<long>> Slices(IReadOnlyList<long> revs, int size)
    {
        size = Math.Max(1, size);
        for (var i = 0; i < revs.Count; i += size)
            yield return revs.Skip(i).Take(size).ToList();
    }

    /// <summary>Passaggi per una finestra: <paramref name="suspects"/> (revisioni che toccano file con conflitto previsto) da sole, le altre a gruppi.</summary>
    internal static List<MergeStep> PlanSteps(IReadOnlyList<long> window, ISet<long> suspects, bool separate = false)
    {
        var steps = new List<MergeStep>();
        var run = new List<long>();
        void Flush()
        {
            steps.AddRange(Slices(run, ChunkSize).Select(c => new MergeStep(c, separate && c.Count > 1)));
            run.Clear();
        }

        foreach (var r in window)
        {
            if (suspects.Contains(r))
            {
                Flush();
                steps.Add(new MergeStep([r]));
            }
            else
                run.Add(r);
        }

        Flush();
        return steps;
    }

    /// <summary>Applica <paramref name="revs"/> (crescenti, quelle ancora da unire) fino in fondo o al primo conflitto.</summary>
    internal async Task<MergeRunResult> RunAsync(IReadOnlyList<long> revs, IReadOnlyList<long> eligible, CancellationToken ct)
    {
        var result = new MergeRunResult();
        foreach (var group in Groups(revs, eligible))
        {
            foreach (var window in Slices(group, DryRunWindow))
            {
                if (StopRequested?.Invoke() == true)
                {
                    result.StoppedByUser = true;
                    return result;
                }

                var steps = await PlanAsync(window, ct);
                foreach (var step in steps)
                {
                    if (StopRequested?.Invoke() == true)
                    {
                        result.StoppedByUser = true;
                        return result;
                    }

                    if (!await ExecuteAsync(step, result, ct))
                        return result;
                }
            }
        }

        return result;
    }

    private async Task<List<MergeStep>> PlanAsync(IReadOnlyList<long> window, CancellationToken ct)
    {
        if (window.Count == 1)
            return [new MergeStep(window)];
        Phase?.Invoke(Loc.T("Controllo i conflitti previsti (r{0}-r{1})...", window[0], window[^1]));
        MergeOutput? dry = null;
        try
        {
            // Il dry-run non deve riempire l'elenco di avanzamento: le sue righe non sono modifiche.
            dry = await SvnProgress.RunAsync(_ => { }, () => _svn.MergeRangesAsync(_wc, _url, [(window[0], window[^1])], dryRun: true, ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && SvnErrors.IsMergeStoppedByConflict(ex.Message))
        {
            BackendLog.Info("merge: dry-run fermato da E155015, passate separate: " + ex.Message.Split('\n')[0]);
        }

        if (dry is not null && dry.Conflicts == 0 && !dry.Items.Any(i => i.Conflicted))
            return PlanSteps(window, new HashSet<long>());
        var conflicts = dry is null ? new HashSet<string>() : MergeFiles.ConflictPaths(dry);
        HashSet<long> suspects = [];
        if (conflicts.Count > 0)
        {
            var touched = await _touched(window, ct);
            foreach (var r in window)
                if (touched.TryGetValue(r, out var files) && files.Any(f => conflicts.Contains(Normalize(f))))
                    suspects.Add(r);
        }

        BackendLog.Info($"merge: conflitti previsti in {conflicts.Count} percorsi, {suspects.Count} revisioni sospette su {window.Count}");
        // Nessun sospetto noto (dry-run non utilizzabile, percorsi non riconosciuti): una passata per revisione, e' svn a fermarsi.
        return suspects.Count > 0 ? PlanSteps(window, suspects) : PlanSteps(window, new HashSet<long>(), separate: true);
    }

    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        return p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
    }

    /// <summary>Esegue il passaggio; <c>false</c> = fermarsi (conflitto, errore).</summary>
    private async Task<bool> ExecuteAsync(MergeStep step, MergeRunResult result, CancellationToken ct)
    {
        StepStarting?.Invoke(step);
        InFlight = step;
        MergeOutput? output = null;
        long? stoppedAt = null;
        try
        {
            output = await _svn.MergeRangesAsync(_wc, _url, step.Ranges, dryRun: false, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && SvnErrors.IsMergeStoppedByConflict(ex.Message))
        {
            // Un passaggio di una lista ha prodotto conflitti e svn si e' fermato li': le precedenti sono applicate, quella pure, le successive no.
            stoppedAt = SvnErrors.ConflictingRevision(ex.Message) ?? step.Last;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            result.Error = ex;
            return false; // InFlight resta: puo' essere rimasto a meta'
        }

        InFlight = null;
        if (output is not null)
        {
            result.Items.AddRange(output.Items);
            result.Conflicts += output.Conflicts;
            result.Skipped += output.Skipped;
        }

        var applied = stoppedAt is { } end ? step.Revs.Where(r => r <= end).ToList() : step.Revs;
        result.Applied.AddRange(applied);
        StepDone?.Invoke(applied);
        if (stoppedAt is { } at)
        {
            result.Stopped = true;
            result.ConflictRev = at;
            return false;
        }

        if (output is { Conflicts: > 0 })
        {
            result.Stopped = true;
            if (step.Revs.Count == 1 || step.Separate)
                result.ConflictRev = step.Last;   // un passaggio per revisione: l'ultimo (dopo il quale non e' stato fatto altro) e' quello in conflitto
            else
                result.ConflictRange = (step.First, step.Last);
            return false;
        }

        return true;
    }
}

/// <summary>Intestazioni di <c>svn merge</c> (<c>--- Merging r6 into '.':</c>, <c>--- Merging r4 through r10 into '.':</c>): a che revisione e' arrivato.</summary>
internal static partial class MergeHeader
{
    internal static bool TryParse(string line, out long first, out long last)
    {
        first = last = 0;
        var m = Header().Match(line);
        if (!m.Success)
            return false;
        first = long.Parse(m.Groups["a"].Value, System.Globalization.CultureInfo.InvariantCulture);
        last = m.Groups["b"].Success ? long.Parse(m.Groups["b"].Value, System.Globalization.CultureInfo.InvariantCulture) : first;
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^--- Merging r(?<a>\d+)(?: through r(?<b>\d+))? into ")]
    private static partial System.Text.RegularExpressions.Regex Header();
}
