# Collaudo 0.6.1

Prima: VS chiuso, PowerShell in `C:\dev\SVNAssist`. Stessa procedura della 0.6.0 (`.\tools\collaudo-mirato.ps1 -Build`, poi senza opzioni). Tutti i passi sono **nuovi e mai provati in VS**. Segna ✅ ok / ❌ + cosa hai visto.

## Parte A — Options moderne

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 1 | Settings (o Tools > Options) > SVNAssist > link **General**. | Si apre la finestra Options con la pagina web di SVNAssist (4 schede: Commit message AI, Modelli e chiavi, Generale, Scorciatoie), non più la griglia. Il tema segue VS. | |
| 2 | Cambia **Provider AI**, accendi **Mostra modello e costo**, chiudi con OK e riapri. | I valori restano. Le modifiche si applicano subito (nessun Annulla). | |
| 3 | Cambia **Lingua dell'interfaccia** in English. | La pagina e le finestre di SVNAssist passano in inglese subito. Rimettila su Automatica. | |
| 4 | Incolla una chiave in **API key Anthropic**, **Salva**. | Compare "Impostata"; la chiave non si rilegge mai nella pagina. **Cancella** la toglie. | |
| 5 | Cambia una scorciatoia (es. Alt+F9). | Vale subito nell'editor (da verificare). | |

## Parte B — Cosa è entrato (SVN Update)

Opzione **Spiega le revisioni in arrivo** accesa; fai committare a un'altra copia una modifica vera (es. togliere una riga).

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 6 | SVN Update > **Genera riassunto AI**. | Il riassunto dice cosa cambia nel codice, con l'autore, senza elencare file. | |
| 7 | Rifai con un file `.env` nella revisione. | Il contenuto del `.env` non compare nel riassunto. | |
| 8 | Guarda il testo sotto il riassunto. | Dice che vanno messaggi di log e modifiche (diff). | |

## Parte C — Crediti Copilot

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 9 | Provider Copilot, **Mostra modello e costo** acceso, **Genera con AI**. | Riga di stato con modello, token e **crediti AI**. | |
| 10 | Confronta i crediti con quelli che mostra GitHub per la stessa richiesta. | Coincidono (verifica la conversione nano-AIU / 1.000.000.000). Se non coincidono, tieni `backend-*.log` (riga "Copilot: n chiamate"). | |
