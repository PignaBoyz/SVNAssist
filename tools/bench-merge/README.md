# Misure del merge a passaggi (bug 13)

Richiedono svn e svnadmin (Linux/macOS/Git Bash). `setup.sh` crea un repository `file://` con un trunk da 3000 file e un ramo con 420 revisioni.
`measure-file.sh N` confronta un processo per revisione, una lista in un processo e un intervallo; per la rete: avviare `svnserve -d -r <repo> --listen-port 3690`,
`python3 lagproxy.py 25 3691 3690` (25 ms per direzione = 50 ms di round trip), fare `svn relocate svn://127.0.0.1:3691/trunk` in una copia della working copy e lanciare `measure-lag.sh` / `measure-range.sh`.
Risultati e conclusioni: `docs/feedback/0.4.0/bug13-prestazioni.md`.
