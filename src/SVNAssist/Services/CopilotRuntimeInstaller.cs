using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SVNAssist.Services;

/// <summary>
/// Scarica al primo uso il runtime di GitHub Copilot usato da <c>GitHub.Copilot.SDK</c>.
/// </summary>
/// <remarks>
/// Il runtime pesa ~100 MB compressi (~250 MB su disco): includerlo nel VSIX lo renderebbe
/// enorme per tutti, anche per chi non usa Copilot. Lo scarichiamo quindi solo quando l'utente
/// sceglie il provider <c>copilot</c>, dalla release ufficiale GitHub della stessa versione che
/// l'SDK si aspetta, verificandone lo SHA-256, in <c>%LOCALAPPDATA%\SVNAssist\copilot-runtime</c>.
/// </remarks>
internal static class CopilotRuntimeInstaller
{
    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    /// <summary>Versione del runtime richiesta dall'SDK (scritta nell'assembly in fase di build).</summary>
    internal static string RuntimeVersion { get; } =
        typeof(CopilotRuntimeInstaller).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "CopilotCliVersion")?.Value
        ?? throw new InvalidOperationException("Versione del runtime Copilot non trovata nell'assembly.");

    private static string Platform =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win32-arm64" : "win32-x64";

    private static string AssetName => $"github-copilot-{RuntimeVersion}-{Platform}.tgz";

    private static string ReleaseUrl => $"https://github.com/github/copilot-cli/releases/download/v{RuntimeVersion}";

    /// <summary>Cartella di installazione del runtime per la versione corrente.</summary>
    internal static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVNAssist", "copilot-runtime", RuntimeVersion, Platform);

    /// <summary>Eseguibile del runtime (avviato dall'SDK via stdio).</summary>
    internal static string RuntimePath => Path.Combine(InstallDirectory, "copilot-runtime.exe");

    /// <summary><c>true</c> se il runtime è già stato scaricato.</summary>
    internal static bool IsInstalled => File.Exists(RuntimePath);

    /// <summary>Scarica ed estrae il runtime se manca; restituisce il percorso dell'eseguibile.</summary>
    internal static async Task<string> EnsureInstalledAsync(HttpClient http, CancellationToken ct)
    {
        if (IsInstalled)
            return RuntimePath;

        await InstallLock.WaitAsync(ct);
        var stagingRoot = InstallDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (IsInstalled)
                return RuntimePath;

            // Doppio controllo: l'hash scaricato dal release (SHA256SUMS.txt) deve coincidere con quello fissato nel
            // sorgente per questa versione/piattaforma, così un release manomesso non basta a far eseguire un altro binario.
            var expectedHash = ResolveExpectedHash(
                AssetName, await http.GetStringAsync($"{ReleaseUrl}/SHA256SUMS.txt", ct));

            Directory.CreateDirectory(stagingRoot);
            var archivePath = Path.Combine(stagingRoot, "copilot.tgz");
            using (var response = await http.GetAsync($"{ReleaseUrl}/{AssetName}", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                    throw new InvalidOperationException("Il runtime Copilot da scaricare è più grande del previsto: download annullato.");

                await using var file = File.Create(archivePath);
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                await CopyCappedAsync(body, file, MaxDownloadBytes, ct);
            }

            // Mai eseguire un binario scaricato senza averne verificato l'integrità.
            string actualHash;
            await using (var archive = File.OpenRead(archivePath))
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archive, ct));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Runtime Copilot scaricato corrotto (checksum SHA-256 diverso).");

            // TarFile rifiuta le voci che puntano fuori dalla cartella di destinazione.
            var extractDir = Path.Combine(stagingRoot, "runtime");
            Directory.CreateDirectory(extractDir);
            await using (var archive = File.OpenRead(archivePath))
            await using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, extractDir, overwriteFiles: true, ct);

            // L'archivio npm ha tutto sotto "package/". Stesso layout dei build targets dell'SDK:
            // i file nativi di prebuilds/<piattaforma> stanno accanto agli asset della radice.
            var packageDir = Path.Combine(extractDir, "package");
            if (Directory.Exists(packageDir))
                extractDir = packageDir;
            MoveDirectoryContents(Path.Combine(extractDir, "prebuilds", Platform), extractDir);
            RemoveUnneededAssets(extractDir);
            if (!File.Exists(Path.Combine(extractDir, "copilot-runtime.exe")))
                throw new InvalidOperationException("Archivio del runtime Copilot senza copilot-runtime.exe.");

            if (Directory.Exists(InstallDirectory))
                Directory.Delete(InstallDirectory, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(InstallDirectory)!);
            Directory.Move(extractDir, InstallDirectory);

            return RuntimePath;
        }
        finally
        {
            InstallLock.Release();
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); }
            catch (IOException) { /* best-effort: la cartella temporanea verrà ignorata */ }
        }
    }

    /// <summary>Estrae l'hash dell'asset da un file <c>SHA256SUMS.txt</c> ("hash  nome-file").</summary>
    /// <summary>Dimensione massima accettata per il download (l'archivio attuale è ~100 MB).</summary>
    internal const long MaxDownloadBytes = 300L * 1024 * 1024;

    /// <summary>
    /// SHA-256 attesi degli archivi, fissati nel sorgente (da <c>SHA256SUMS.txt</c> del release v1.0.109 di github/copilot-cli).
    /// Aggiornarli insieme alla versione di <c>GitHub.Copilot.SDK</c> (che determina <see cref="RuntimeVersion"/>).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> PinnedHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["github-copilot-1.0.109-win32-x64.tgz"] = "5ba9bfc3104d8e37462e9a5214a3cc88e6e2201b972981b6bdf447ee2f8f49bc",
        ["github-copilot-1.0.109-win32-arm64.tgz"] = "04b2821e5541f1bc6fca29365385921d3ea72c61f05210ebcb004a207a18aa48",
    };

    /// <summary>
    /// Hash da richiedere all'archivio: quello del release, e se per questa versione c'è un pin nel sorgente deve coincidere.
    /// Se la versione cambia e non c'è ancora un pin si ripiega sul solo checksum del release (meno forte: va aggiunto il pin).
    /// </summary>
    internal static string ResolveExpectedHash(string assetName, string sums)
    {
        var releaseHash = ParseChecksum(sums, assetName);
        if (PinnedHashes.TryGetValue(assetName, out var pinned)
            && !string.Equals(pinned, releaseHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Il checksum pubblicato per il runtime Copilot non coincide con quello atteso da SVNAssist: download rifiutato.");

        return pinned ?? releaseHash;
    }

    /// <summary>Copia <paramref name="source"/> in <paramref name="destination"/> fermandosi con un errore oltre <paramref name="maxBytes"/>.</summary>
    internal static async Task CopyCappedAsync(Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException("Il download del runtime Copilot supera il limite consentito: annullato.");

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    internal static string ParseChecksum(string sums, string assetName)
    {
        var matches = sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == assetName)
            .Select(parts => parts[0])
            .ToList();

        return matches.Count == 1 && matches[0].Length == 64
            ? matches[0]
            : throw new InvalidOperationException($"Checksum non trovato per {assetName}.");
    }

    /// <summary>
    /// Parti del pacchetto npm che il runtime non usa (stesso elenco escluso dai build targets dell'SDK):
    /// eliminarle porta lo spazio su disco da ~400 MB a meno della metà.
    /// </summary>
    private static readonly string[] UnneededDirectories =
        ["assets", "foundry-local-sdk", "napi-oop-runtime", "prebuilds", "pvrecorder", "queries", "webview"];

    private static readonly string[] UnneededFilePatterns =
        ["app.js", "changelog.json", "cli-main.js", "copilot", "copilot.exe", "index.js", "LICENSE.md",
         "npm-loader.js", "package.json", "README.md", "sea-loader.js", "SHA256SUMS.txt", "tree-sitter*.wasm", "voice-*.js"];

    private static void RemoveUnneededAssets(string root)
    {
        foreach (var dir in UnneededDirectories.Select(d => Path.Combine(root, d)).Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);

        foreach (var file in UnneededFilePatterns.SelectMany(p => Directory.EnumerateFiles(root, p)))
            File.Delete(file);
    }

    private static void MoveDirectoryContents(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }
    }
}
