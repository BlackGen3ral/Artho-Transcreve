using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Artho.Transcreve;

internal static class AppUpdate
{
    private const string BaseUrl = "https://raw.githubusercontent.com/BlackGen3ral/Artho-Transcreve/main/";
    private const string Package = "downloads/Artho-Transcreve-Windows.zip";
    private const string AppName = "Artho Transcreve";
    private const string Executable = AppName + ".exe";
    private const long MaximumBytes = 25 * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };
    private static readonly string[] RuntimeFiles = [Executable, AppName + ".dll", AppName + ".deps.json", AppName + ".runtimeconfig.json"];
    private static string StagingRoot => Path.Combine(Path.GetTempPath(), "ArthoTranscreve-updates");
    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version!;
    public static string InstalledApp => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public static string? ExtensionFolder(string appFolder)
    {
        if (!Path.GetFileName(appFolder).Equals("app", StringComparison.OrdinalIgnoreCase))
            return null;
        var directory = Path.Combine(Path.GetDirectoryName(appFolder)!, "extensao");
        return ReadExtensionVersion(directory) != null ? directory : null;
    }

    public static Version? ReadExtensionVersion(string directory)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            if (manifest.RootElement.GetProperty("name").GetString() != "Artho Transcreve para TikTok")
                return null;
            return Version.TryParse(manifest.RootElement.GetProperty("version").GetString(), out var version) ? version : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return null; }
    }

    internal sealed record Pending(Version Version, string Stage);

    public static async Task<Pending?> CheckAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(50));
        var token = deadline.Token;
        using var manifest = JsonDocument.Parse(await Client.GetStringAsync(BaseUrl + "update.json", token));
        var current = manifest.RootElement;
        if (!Version.TryParse(current.GetProperty("version").GetString(), out var version) ||
            version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException("A versão publicada é inválida.");
        version = new Version(version.Major, version.Minor, version.Build, 0);
        if (version <= Current)
            return null;
        var hash = current.GetProperty("sha256").GetString();
        var size = current.GetProperty("size").GetInt64();
        if (hash?.Length != 64 || !hash.All(Uri.IsHexDigit) || size is <= 0 or > MaximumBytes)
            throw new InvalidDataException("O manifesto de atualização é inválido.");

        CleanupOldStages();
        var staging = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var zipPath = Path.Combine(staging, "package.zip");
            await DownloadAsync(zipPath, size, hash, token);
            var payload = Path.Combine(staging, "payload");
            await Task.Run(() => Extract(zipPath, payload, version), token);
            token.ThrowIfCancellationRequested();
            return new Pending(version, staging);
        }
        catch
        {
            Discard(staging);
            throw;
        }
    }

    private static async Task DownloadAsync(string destination, long size, string expectedHash, CancellationToken token)
    {
        using var response = await Client.GetAsync(BaseUrl + Package, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > size)
                throw new InvalidDataException("O tamanho da atualização diverge do manifesto.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (total != size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A verificação de integridade da atualização falhou.");
    }

    internal static void Extract(string zipPath, string payload, Version version)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        long uncompressed = 0;
        foreach (var entry in zip.Entries)
        {
            var components = entry.FullName.Split('/');
            if (components.Length < 2 || components[0] is not ("app" or "extensao") || entry.Name.Length == 0)
                continue;
            if (components.Any(name => name is "" or "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException("Caminho inválido no pacote de atualização.");
            uncompressed += entry.Length;
            if (uncompressed > MaximumBytes)
                throw new InvalidDataException("O pacote de atualização excedeu o tamanho permitido.");
            var target = Path.Combine(payload, Path.Combine(components));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }

        var app = Path.Combine(payload, "app");
        if (RuntimeFiles.Any(file => !File.Exists(Path.Combine(app, file))) ||
            AssemblyName.GetAssemblyName(Path.Combine(app, AppName + ".dll")).Version != version ||
            ReadExtensionVersion(Path.Combine(payload, "extensao"))?.ToString() != version.ToString(3))
            throw new InvalidDataException("O pacote está incompleto ou tem versões diferentes.");
    }

    public static void StartInstaller(Pending pending)
    {
        var helper = Path.Combine(pending.Stage, "installer");
        Directory.CreateDirectory(helper);
        foreach (var name in RuntimeFiles)
            File.Copy(Path.Combine(InstalledApp, name), Path.Combine(helper, name), overwrite: true);
        using var current = Process.GetCurrentProcess();
        StartProcess(Path.Combine(helper, Executable),
            "--apply-update", current.Id.ToString(), current.StartTime.ToUniversalTime().Ticks.ToString(), InstalledApp);
    }

    public static void RunInstaller(string[] args)
    {
        var stage = Path.GetDirectoryName(InstalledApp)!;
        if (!Path.GetDirectoryName(stage)!.Equals(StagingRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(stage), "N", out _) ||
            !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var startTicks) ||
            !Path.IsPathFullyQualified(args[3]))
            throw new InvalidDataException("Instalador de atualização inválido.");
        var app = Path.GetFullPath(args[3]);
        var executable = Path.Combine(app, Executable);
        if (!File.Exists(executable) || AssemblyName.GetAssemblyName(Path.Combine(app, AppName + ".dll")).Version != Current)
            throw new InvalidDataException("A pasta do app mudou antes da instalação.");

        Process? parent;
        try { parent = Process.GetProcessById(pid); }
        catch (ArgumentException) { parent = null; }
        using (parent)
        {
            if (parent != null && !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == startTicks &&
                !parent.WaitForExit(15000))
                throw new TimeoutException("Feche o Artho Transcreve e tente novamente.");
        }

        try
        {
            var extension = ExtensionFolder(app);
            InstallFiles(stage, app, extension, () => StartProcess(executable));
        }
        catch
        {
            StartProcess(executable);
            throw;
        }
    }

    internal static void InstallFiles(string stage, string app, string? extension, Action? restart = null)
    {
        var payload = Path.Combine(stage, "payload");
        var replaced = new Stack<(string Destination, string? Backup)>();
        try
        {
            if (extension != null)
                ApplyFolder(Path.Combine(payload, "extensao"), extension, stage, replaced);
            ApplyFolder(Path.Combine(payload, "app"), app, stage, replaced);
            restart?.Invoke();
        }
        catch
        {
            while (replaced.TryPop(out var item))
            {
                if (item.Backup == null)
                    File.Delete(item.Destination);
                else
                    Replace(item.Backup, item.Destination);
            }
            throw;
        }
    }

    private static void ApplyFolder(string source, string destination, string stage, Stack<(string, string?)> replaced)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            var parent = new DirectoryInfo(Path.GetDirectoryName(target)!);
            for (var directory = parent; directory != null; directory = directory.Parent)
            {
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A pasta de instalação contém um link de diretório.");
                if (directory.FullName.Equals(destination, StringComparison.OrdinalIgnoreCase))
                    break;
            }
            parent.Create();

            string? backup = null;
            if (File.Exists(target))
            {
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A pasta de instalação contém um link de arquivo.");
                backup = Path.Combine(stage, "backup-" + replaced.Count);
                File.Copy(target, backup);
            }
            Replace(file, target);
            replaced.Push((target, backup));
        }
    }

    private static void Replace(string source, string destination)
    {
        var temp = destination + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temp);
            File.Move(temp, destination, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static void StartProcess(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Não foi possível iniciar o Artho Transcreve.");
    }

    private static void CleanupOldStages()
    {
        if (!Directory.Exists(StagingRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(StagingRoot))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) &&
                Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                Discard(directory);
    }

    public static void Discard(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
