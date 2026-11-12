# SVNAssist — Copilot Instructions

## Messaggi di commit (letti anche dal backend: CommitInstructions.cs, max 4000 caratteri per file, quindi tienili in alto)

- Scrivi in italiano, modo imperativo o passato breve, nessun emoji.
- Prima riga: massimo 72 caratteri, senza punto finale, che dice *cosa* cambia.
- Se serve, riga vuota e poche righe di dettaglio sul *perché*.
- Cita ogni file aggiunto, eliminato o rinominato, non solo quelli modificati.
- Non inventare ciò che non è nel diff.

## Cos'è

Estensione per Visual Studio 2026 che integra Subversion (`svn.exe` via CLI) e genera i messaggi di commit con AI
(Copilot, Claude Code, Claude API, OpenAI-compatibile/GitHub Models). Pannello "SVN Changes" in stile Git Changes
(sezione Staged, albero con tastiera, diff nell'editor di VS), finestra Log, prerequisiti guidati. Il codice deve essere chiaro e commentato.

## Architettura (3 progetti, JSON-RPC su stdio)

| Progetto | Runtime | Ruolo |
|---|---|---|
| `src/SVNAssist.Host` | net472, VSSDK in-proc | Package, comandi `.vsct`, Options (`DialogPage`), WebView2, tema VS, diff nell'editor, terminale |
| `src/SVNAssist.Backend` | net10, exe separato | Tutta la logica: store (`ChangesStore`, `CommitStore`, `LogStore`, `PrereqStore`), `SvnService`, provider AI |
| `src/SVNAssist.UI.Web` | HTML/CSS/JS | UI unica, condivisa con la demo `docs/demo` (non ha stato di dominio: riceve snapshot, invia comandi) |

`src/Shared/RpcContract.cs` definisce il contratto host ⇄ backend. Il protocollo UI ⇄ host è in `docs/ui-protocol.md`
(schemi in `src/SVNAssist.UI.Web/contract/`). Non usare VisualStudio.Extensibility/Remote UI né SharpSVN: sono la vecchia architettura.

## Regole

- C# 12; async con `CancellationToken` ovunque sia I/O; `HttpClient` singleton; niente chiavi/endpoint/path hardcoded (passano da `SettingsService`).
- Una interfaccia per file. I comandi `svn` partono con `--targets`/peg revision corretti e mai con input non validato.
- Tutto ciò che arriva dalla UI è non fidato: valida `name`, `args` e rivalida le chiavi di riga contro `svn status`.
- UI: niente script/stile inline né CDN (CSP stretta), colori solo da `css/tokens.css` (WCAG AA verificato), testo dei dati sempre con `esc()`.
- Dopo modifiche a `UI.Web` rigenera la demo: `node tools/build-demo.mjs`.
- Testi per l'utente in italiano; commit in italiano.

## Test

- `node --test "tests/ui-web/*.test.mjs"` — contrasto, schemi, render, tastiera, CSP.
- `dotnet test tests/SVNAssist.Tests -c Release` — store, snapshot vs schemi, backend con repository `file://`.
- Prova reale: F5 su `SVNAssist.Host` (istanza sperimentale); note in `HANDOFF.md` e `docs/host.md`.
