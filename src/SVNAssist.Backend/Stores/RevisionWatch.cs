using SVNAssist.Services;

namespace SVNAssist.Stores;

/// <summary>Stato dell'indicatore "r1234" nella barra di stato: <c>current</c> (verde), <c>behind</c> (arancio), <c>unknown</c> (neutro).</summary>
internal static class RevisionIndicator
{
    /// <summary>
    /// Confronta la revisione della working copy con l'ultima del ramo sul server (<c>last-changed-revision</c> dell'URL):
    /// il server avanti = <c>behind</c>. Qualcosa di sconosciuto = <c>unknown</c>, mai un falso verde.
    /// shortcut: dopo un nostro commit la working copy e' "a revisioni miste"; si usa la revisione del commit come minimo,
    /// quindi un commit altrui arrivato prima del nostro non si vede fino al prossimo SVN Update.
    /// </summary>
    internal static string Compute(long? workingCopyRev, long? headRev)
        => workingCopyRev is null or <= 0 || headRev is null ? "unknown" : headRev > workingCopyRev ? "behind" : "current";
}

/// <summary>
/// Controllo periodico e leggero di "qualcun altro ha committato?": una sola <c>svn info --show-item last-changed-revision --non-interactive URL</c>
/// ogni N minuti (impostazione, 0 = spento). Sola lettura, mai interattivo: un errore di rete o di autenticazione non apre il login
/// e non mostra nulla, rende solo l'indicatore neutro (<see cref="Error"/>). Non parte se c'e' un'operazione dell'utente in corso
/// e due controlli non si sovrappongono.
/// </summary>
internal sealed class HeadWatcher
{
    /// <summary>Dopo un controllo saltato perche' occupati si riprova presto, non fra N minuti.</summary>
    internal static readonly TimeSpan RetryBusy = TimeSpan.FromSeconds(15);

    /// <summary>A controllo spento si rilegge l'impostazione ogni tanto (accenderlo non richiede di riaprire la finestra).</summary>
    internal static readonly TimeSpan PollDisabled = TimeSpan.FromMinutes(1);

    private readonly Func<CancellationToken, Task<long?>> _fetch;
    private readonly Func<bool> _busy;
    private readonly Func<CancellationToken, Task<int>> _minutes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action _changed;
    private readonly Func<DateTime> _now;
    private DateTime? _last;
    private int _interval;
    private volatile bool _active = true;
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private int _checking;
    private readonly object _sync = new();
    private long? _head;
    private string? _error;

    /// <param name="fetch">Revisione piu' recente del ramo (<c>null</c> = non applicabile, es. working copy non ancora letta).</param>
    /// <param name="busy">Un'operazione dell'utente e' in corso: il controllo non parte.</param>
    /// <param name="minutes">Intervallo in minuti dalle impostazioni (0 = spento).</param>
    /// <param name="changed">L'esito e' cambiato (si rimanda lo snapshot).</param>
    /// <param name="delay">Attesa (iniettabile nei test; default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>).</param>
    public HeadWatcher(Func<CancellationToken, Task<long?>> fetch, Func<bool> busy, Func<CancellationToken, Task<int>> minutes, Action changed,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _fetch = fetch;
        _busy = busy;
        _minutes = minutes;
        _changed = changed;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Ultima revisione nota del ramo sul server (<c>null</c> = sconosciuta).</summary>
    internal long? Head { get { lock (_sync) return _head; } }

    /// <summary>Perche' l'esito e' sconosciuto (errore di rete o di autenticazione), oppure <c>null</c>.</summary>
    internal string? Error { get { lock (_sync) return _error; } }

    /// <summary>L'ultimo giro ha trovato l'impostazione a 0 (controllo spento).</summary>
    internal bool Disabled { get; private set; }

    /// <summary>
    /// Visual Studio e' (o non e') l'applicazione attiva: in secondo piano non si controlla; al ritorno in primo piano si controlla
    /// subito se l'ultimo controllo e' piu' vecchio dell'intervallo.
    /// </summary>
    internal void SetActive(bool active)
    {
        _active = active;
        if (active && _interval > 0 && (_last is null || _now() - _last >= TimeSpan.FromMinutes(_interval)))
            Nudge();
    }

    /// <summary>Dimentica l'esito (working copy o ramo cambiati).</summary>
    internal void Reset()
    {
        Set(null, null);
    }

    /// <summary>Chiede un controllo appena possibile (dopo uno switch o all'apertura di un'altra working copy).</summary>
    internal void Nudge()
    {
        try
        {
            _nudge.Release();
        }
        catch (SemaphoreFullException)
        {
            // gia' richiesto
        }
    }

    private void Set(long? head, string? error)
    {
        lock (_sync)
            (_head, _error) = (head, error);
    }

    /// <summary>Un controllo. <c>false</c> = saltato (occupati o gia' in corso); un errore non si propaga: diventa <see cref="Error"/>.</summary>
    internal async Task<bool> CheckAsync(CancellationToken ct)
    {
        if (!_active || _busy() || Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
            return false;
        try
        {
            var head = await _fetch(ct);
            Set(head, null);
            _last = _now();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Set(null, ex.Message);
            BackendLog.Info("Controllo nuove revisioni non riuscito: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }

        _changed();
        return true;
    }

    /// <summary>Ciclo: controlla, aspetta N minuti (o un <see cref="Nudge"/>), ripete. Termina con <paramref name="ct"/>.</summary>
    internal async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int minutes;
            try
            {
                minutes = Math.Clamp(await _minutes(ct), 0, 24 * 60);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                minutes = 0;
            }

            TimeSpan wait;
            if (minutes <= 0)
            {
                var was = Disabled;
                Disabled = true;
                if (!was || Head is not null || Error is not null)
                {
                    Reset();
                    _changed();
                }

                wait = PollDisabled;
            }
            else
            {
                Disabled = false;
                _interval = minutes;
                wait = !_active ? PollDisabled : await CheckAsync(ct) ? TimeSpan.FromMinutes(minutes) : RetryBusy;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sleep = _delay(wait, cts.Token);
            var nudged = _nudge.WaitAsync(cts.Token);
            await Task.WhenAny(sleep, nudged);
            cts.Cancel();
            try
            {
                await Task.WhenAll(sleep, nudged);
            }
            catch (OperationCanceledException)
            {
                // l'altra attesa e' stata annullata: normale
            }
        }
    }
}
