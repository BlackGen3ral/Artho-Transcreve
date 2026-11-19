using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Artho.Transcreve;

internal sealed class WhisperEngine(string data)
{
    private const string RuntimeUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
    private const string RuntimeHash = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb";
    private const string ModelName = "ggml-small-q5_1.bin";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/87cd18b47b941d2f65d09981dad23bb7d0481c77/" + ModelName;
    private const string ModelHash = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb";
    private const string VadName = "ggml-silero-v5.1.2.bin";
    private const string VadUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/e5614ed76a5dd4b03fad5068c89efcd2617a9d1e/" + VadName;
    private const string VadHash = "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf";
    internal const int PcmRate = 16000;
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(10) };

    private string EngineDirectory => Path.Combine(data, "whisper-b5454");
    private string Executable => Path.Combine(EngineDirectory, "whisper-cli.exe");
    private string Model => Path.Combine(data, ModelName);
    private string Vad => Path.Combine(data, VadName);
    public bool Ready => File.Exists(Executable) && File.Exists(Path.Combine(EngineDirectory, "ggml.dll")) &&
        File.Exists(Model) && File.Exists(Vad);

    public async Task InstallAsync(IProgress<string> progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(data);
        await Task.Run(async () =>
        {
            if (!File.Exists(Executable) || !File.Exists(Path.Combine(EngineDirectory, "ggml.dll")))
            {
                var archive = Path.Combine(data, "engine.download");
                var staging = Path.Combine(data, "whisper-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await DownloadAsync(RuntimeUrl, RuntimeHash, archive, "Motor Whisper", progress, cancellation);
                    Directory.CreateDirectory(staging);
                    using (var zip = ZipFile.OpenRead(archive))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            if (entry.Name.Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase) ||
                                entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                entry.ExtractToFile(Path.Combine(staging, entry.Name));
                        }
                    }
                    if (!File.Exists(Path.Combine(staging, "whisper-cli.exe")) || !File.Exists(Path.Combine(staging, "ggml.dll")))
                        throw new InvalidDataException("Pacote do Whisper incompleto.");
                    Directory.CreateDirectory(EngineDirectory);
                    foreach (var path in Directory.EnumerateFiles(staging))
                        File.Move(path, Path.Combine(EngineDirectory, Path.GetFileName(path)), overwrite: true);
                }
                finally
                {
                    File.Delete(archive);
                    if (Directory.Exists(staging))
                        Directory.Delete(staging, recursive: true);
                }
            }

            await InstallModelAsync(Model, ModelUrl, ModelHash, "modelo Whisper Small", progress, cancellation);
            await InstallModelAsync(Vad, VadUrl, VadHash, "detector de fala", progress, cancellation);
        }, cancellation);
    }

    private static async Task InstallModelAsync(string path, string url, string hash, string label,
        IProgress<string> progress, CancellationToken cancellation)
    {
        if (File.Exists(path)) return;
        var partial = path + ".download";
        try
        {
            await DownloadAsync(url, hash, partial, label, progress, cancellation);
            File.Move(partial, path, overwrite: true);
        }
        finally { File.Delete(partial); }
    }

    private static async Task DownloadAsync(string url, string expected, string path, string label, IProgress<string> progress, CancellationToken cancellation)
    {
        progress.Report($"Baixando {label}…");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        using var response = await Downloads.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var file = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var expectedSize = response.Content.Headers.ContentLength.GetValueOrDefault();
        long total = 0;
        int previouslyReported = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, count), token);
            hash.AppendData(buffer, 0, count);
            total += count;
            if (total > 200_000_000)
                throw new InvalidDataException("O download ultrapassou o tamanho esperado.");
            var percent = expectedSize > 0 ? (int)(100 * total / expectedSize) : 0;
            if (percent >= previouslyReported + 5)
            {
                previouslyReported = percent;
                progress.Report($"Baixando {label}… {percent}%");
            }
        }

        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"A verificação de integridade falhou para {label}. Tente novamente.");
    }

    public async Task<string> TranscribeAsync(byte[] pcm, string language, CancellationToken cancellation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ArthoTranscreve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Keep CLI arguments relative for Windows accounts with accented names.
            var inputPath = Path.Combine(directory, "audio.wav");
            var outputPath = Path.Combine(directory, "texto.txt");
            WriteWave(inputPath, pcm);
            var processInfo = new ProcessStartInfo(Executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            // whisper-cli uses narrow Windows paths. The model is passed via a relative
            // path where possible, and the fallback preserves compatibility across disks.
            var modelPath = Path.GetRelativePath(directory, Model);
            var vadPath = Path.GetRelativePath(directory, Vad);
            foreach (var arg in new[] { "-m", modelPath, "-f", "audio.wav", "-of", "texto",
                         "-l", language, "-t", Math.Clamp(Environment.ProcessorCount / 2, 2, 8).ToString(),
                         "-otxt", "-ng", "-sns", "-nf", "--vad", "--vad-model", vadPath })
                processInfo.ArgumentList.Add(arg);

            using var process = Process.Start(processInfo) ?? throw new InvalidOperationException("Não foi possível abrir o motor Whisper.");
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                cancellation.ThrowIfCancellationRequested();
                throw new TimeoutException("O reconhecimento demorou mais de 60 segundos.");
            }

            var diagnostic = await stderr;
            await stdout;
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                var tail = diagnostic.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                throw new InvalidOperationException($"O motor Whisper falhou ({process.ExitCode}). {tail}".Trim());
            }

            var output = await File.ReadAllLinesAsync(outputPath, cancellation);
            return string.Join(" ", output.Select(line => line.Trim()).Where(line =>
                line.Length > 0 && !(line.StartsWith('[') && line.EndsWith(']')) &&
                !(line.StartsWith('(') && line.EndsWith(')')) && line.Any(char.IsLetterOrDigit)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void WriteWave(string path, byte[] pcm)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(pcm.Length + 36);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(PcmRate);
        writer.Write(PcmRate * sizeof(short));
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }
}
