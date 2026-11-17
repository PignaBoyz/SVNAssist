# Bug 13 — merge a passaggi: prestazioni e scelta

Richiesta: applicare le revisioni una alla volta, con avanzamento reale e arresto **dopo** la revisione in conflitto, senza rendere lento un
"Merge tutto" di centinaia di revisioni (420 nel repository dell'utente).

## Misure (svn 1.14.3, Linux, working copy di 3000 file, 420 revisioni sul ramo, ognuna tocca un file)

`file://` ha latenza zero e **non è rappresentativo**: sulla rete il costo è dominato dai giri al server. Per questo le misure sono ripetute su `svn://`
con un proxy che aggiunge 25 ms per direzione (50 ms di round trip; tools in `tools/bench-merge`). Un server http con autenticazione
integrata di Windows avrà più giri e un handshake per processo: i numeri sotto sono un ordine di grandezza, non una previsione.

| Strategia | `file://` | `svn://` 50 ms RTT |
|---|---|---|
| un processo svn per revisione (`-c N`, 100 processi) | 16 ms/rev (6,7 s per 420) | 962 ms/rev (misurato su 40 rev; 420 ≈ **6,7 min**) |
| un processo, lista `-c 4,5,6,...` (una passata per revisione) | 8 ms/rev | 557 ms/rev (misurato su 40 rev; 420 ≈ **3,9 min**) |
| un solo intervallo `-c 4-423` (senza arresto al conflitto) | 72 ms (100 rev) | 1,0 s fissi + 37 ms/rev: 1 rev 1,0 s, 25 rev 2,3 s, 100 rev 6,2 s, 420 rev **16,5 s** |
| **scelta**: dry-run a finestre + intervalli da 25 | 0,67 s (17 passaggi + 3 dry-run) | **61,6 s** |
| **scelta**, intervalli da 50 (default) | | **54,3 s** (9 passaggi + 3 dry-run) |
| intervalli da 50, dry-run unico | | 46,6 s |
| intervalli da 100, dry-run unico | | 42,6 s |

Il costo fisso di un passaggio svn è ~1 s a 50 ms di round trip (≈19 giri); un intervallo costa poi ~37 ms per revisione. Per questo una chiamata per revisione è
da escludere (25 volte più lenta dell'intervallo), e anche la lista in un solo processo (14 volte).

## Perché non basta un intervallo

Un intervallo `-c a-b` è **una passata**: svn non si ferma a metà (il conflitto compare una volta, sul risultato netto) e lo stato non dice *quale* revisione
lo causa. Con la lista `-c a,b,c` svn applica una passata per revisione, si ferma alla prima in conflitto con E155015 (`while merging r5:6` = la r6) e ha già
registrato in `svn:mergeinfo` anche la revisione in conflitto: `svn mergeinfo --show-revs eligible` elenca esattamente le rimaste (provato, vedi test).
Serve quindi sapere **in anticipo** dove spezzare.

## Scelta adottata (`MergeRunner`)

1. **Raggruppamento per adiacenza nell'elenco da unire, non per numero di revisione.** Nel repository vero i numeri sono a salti (commit di altri rami in mezzo):
   il raggruppamento per numeri consecutivi (quello della versione precedente) lasciava quasi ogni revisione da sola, cioè in pratica una chiamata per revisione.
   Un intervallo `-c a-b` comprende i numeri in mezzo che non toccano il ramo: svn li salta (verificato con commit di altri rami in mezzo e con un ramo
   sincronizzato da trunk: stessi file di un merge automatico, mergeinfo diverso solo nella forma `3-14` / `5-14`).
2. **Un dry-run per finestra di 200 revisioni** (un solo passaggio svn, costa quanto il merge vero della finestra). Senza conflitti previsti la finestra si applica a
   **intervalli da 50 revisioni** (un passaggio ciascuno: l'avanzamento si muove ogni ~3 s a 50 ms di round trip). Con conflitti previsti, le revisioni che toccano i file in
   conflitto (da `svn log -v`, in cache) si applicano **ciascuna da sola** e le altre a intervalli: l'arresto è esattamente dopo la revisione in conflitto.
3. Se il dry-run non è utilizzabile (E155015) o non si riconoscono i file, il gruppo si applica con **una passata per revisione in un processo solo** (`-c a,b,c`): è svn a fermarsi, alla
   revisione giusta.
4. Dopo ogni passaggio si ferma se ci sono conflitti. **Limite noto**: un conflitto non previsto dal dry-run (di albero o di proprietà, o diverso dal previsto) dentro un intervallo ferma alla *fine dell'intervallo*;
   il messaggio lo dice ("conflitto nel gruppo r..-r..") invece di indicare una revisione che non sa.
5. Nessun aggiornamento di Changes/revisioni tra un passaggio e l'altro: una volta sola, allo stop o alla fine. Le notifiche restano limitate a ~150 ms (`NotifyLine`),
   il passo corrente è coalescente (`SetStep`).

Risultato per "Merge tutto" di 420 revisioni a 50 ms di round trip: ~54 s contro ~6,7 min di una chiamata per revisione e ~16 s dell'intervallo unico che non si ferma al conflitto.
Con un conflitto alla 3ª revisione il costo è un dry-run della finestra + 1-2 passaggi.

## Annullamento e processo terminato

"Annulla" nella finestra di avanzamento **non uccide svn**: chiede di fermarsi al termine del passaggio in corso (di norma pochi secondi), così non resta nulla a metà. Se il
passaggio non finisce entro 30 s si termina a forza. Un passaggio terminato a forza (o VS chiuso) può aver scritto i file senza registrare `mergeinfo`: rieseguirlo riapplica i
testi senza conflitti (diff3 riconosce le modifiche identiche) ma un file **aggiunto** da quel passaggio compare come conflitto ad albero ("local file add, incoming file add"):
provato con svn 1.14. L'avviso del banner lo dice ("Usa la mia" e segna come risolto) e il passaggio in corso resta annotato nello stato su disco.

## Cosa non è stato misurato

Server http(s) reale con autenticazione Windows, repository con tree conflict per rinomina, working copy con `svn:externals`. I tempi di Windows (avvio del processo svn più lento) vanno
nella stessa direzione: più passaggi costano di più, quindi gli intervalli lunghi convengono ancora.
