using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Artho.Transcreve;

internal sealed class Bridge(WhisperEngine engine, string code)
{
    private const int ChunkSeconds = 10;
    private const int MaxPcmBytes = ChunkSeconds * WhisperEngine.PcmRate * sizeof(short);

    private sealed class Capture(string language, bool supportsFinish)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Language { get; } = language;
        public DateTime Start { get; } = DateTime.Now;
        public long Heartbeat { get; set; } = Environment.TickCount64;
        public bool SupportsFinish { get; } = supportsFinish;
        public long FinishRequestedAt { get; set; }
        public int Level { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private readonly object gate = new();
    private readonly SemaphoreSlim processor = new(1);
    private Capture? active;
    private bool updating;
    private WebApplication? server;
    public string Language { get; set; } = "pt";
    public Action<string>? Status { get; set; }
    public Action<DateTime, string>? Transcript { get; set; }
    public bool Capturing { get { lock (gate) return active != null; } }
    public bool Finishing { get { lock (gate) return active?.FinishRequestedAt > 0; } }
    public int AudioLevel { get { lock (gate) return active?.Level ?? 0; } }

    public void Finish()
    {
        lock (gate)
        {
            if (active == null || active.FinishRequestedAt > 0) return;
            if (!active.SupportsFinish)
            {
                Cancel("Captura encerrada no app.", active);
                return;
            }
            active.FinishRequestedAt = Environment.TickCount64;
            active.Level = 0;
            Status?.Invoke("Finalizando os trechos recebidos…");
        }
    }

    public bool TryBeginUpdate()
    {
        lock (gate)
        {
            if (active != null || updating) return false;
            updating = true;
            return true;
        }
    }

    public void EndUpdate()
    {
        lock (gate) updating = false;
    }

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, 48721);
            options.Limits.MaxRequestBodySize = MaxPcmBytes;
        });
        var app = builder.Build();
        server = app;
        app.Use(async (ctx, next) =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(origin, @"^chrome-extension://[a-p]{32}$"))
                {
                    ctx.Response.StatusCode = 403;
                    return;
                }
                ctx.Response.Headers.AccessControlAllowOrigin = origin;
                ctx.Response.Headers.Vary = "Origin";
                if (HttpMethods.IsOptions(ctx.Request.Method))
                {
                    ctx.Response.Headers.AccessControlAllowMethods = "POST, OPTIONS";
                    ctx.Response.Headers.AccessControlAllowHeaders = "X-Artho-Code, X-Artho-Session, X-Artho-Offset, X-Artho-Capture, X-Artho-Level, Content-Type";
                    ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
                    ctx.Response.StatusCode = 204;
                    return;
                }
            }
            var supplied = ctx.Request.Headers["X-Artho-Code"].ToString().ToUpperInvariant();
            if (supplied.Length != code.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(code)))
            {
                await Reply("Código incorreto. Copie o código no app do PC.", 401).ExecuteAsync(ctx);
                return;
            }
            await next(ctx);
        });
        app.MapPost("/start", (Delegate)StartCapture);
        app.MapPost("/version", () => Results.Json(new
        {
            extensionVersion = AppUpdate.ExtensionFolder(AppUpdate.InstalledApp) is { } folder ?
                AppUpdate.ReadExtensionVersion(folder)?.ToString() : null
        }));
        app.MapPost("/ping", (Delegate)HeartbeatResult);
        app.MapPost("/stop", (HttpContext ctx) =>
        {
            var capture = Find(ctx);
            if (capture != null)
                Cancel("Captura encerrada. Abra o TikTok e inicie pela extensão.", capture);
            return Results.NoContent();
        });
        app.MapPost("/audio", (Delegate)ReceiveAsync);
        await app.StartAsync(cancellation);
    }

    private IResult StartCapture(HttpContext ctx)
    {
        Expire();
        lock (gate)
        {
            if (updating)
                return Reply("O app está instalando uma atualização. Tente novamente em instantes.", 503);
            if (!engine.Ready)
                return Reply("Abra o app e clique em Baixar modelo de voz para preparar a transcrição.", 503);
            if (active != null)
                return Reply("Já existe uma captura ativa neste PC.", 409);
            active = new Capture(Language, ctx.Request.Headers["X-Artho-Capture"] == "2");
            Status?.Invoke("Captura ativa. Dê play no vídeo para transcrever.");
            return Results.Json(new { session = active.Id, chunkSeconds = ChunkSeconds });
        }
    }

    private IResult HeartbeatResult(HttpContext ctx)
    {
        lock (gate)
        {
            var capture = Find(ctx);
            if (capture == null) return Stopped();
            if (!capture.SupportsFinish) return Results.NoContent();
            var finish = capture.FinishRequestedAt > 0;
            capture.Level = !finish && int.TryParse(ctx.Request.Headers["X-Artho-Level"], out var level)
                ? Math.Clamp(level, 0, 100) : 0;
            return Results.Json(new { finish });
        }
    }

    private Capture? Find(HttpContext ctx)
    {
        lock (gate)
        {
            if (active?.Id != ctx.Request.Headers["X-Artho-Session"].ToString())
                return null;
            if (active != null)
                active.Heartbeat = Environment.TickCount64;
            return active;
        }
    }

    private async Task<IResult> ReceiveAsync(HttpContext ctx)
    {
        var capture = Find(ctx);
        if (capture == null)
            return Stopped();
        if (ctx.Request.ContentLength is not (>= 2 and <= MaxPcmBytes) ||
            ctx.Request.ContentLength % 2 != 0 ||
            !double.TryParse(ctx.Request.Headers["X-Artho-Offset"], System.Globalization.CultureInfo.InvariantCulture, out var offset) ||
            !double.IsFinite(offset) || offset < 0 || offset > 31_536_000)
            return Reply("Trecho de áudio inválido.", 400);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, capture.Cancellation.Token);
        var token = cancellation.Token;
        try
        {
            await processor.WaitAsync(token);
            try
            {
                var audio = new byte[(int)ctx.Request.ContentLength.Value];
                await ctx.Request.Body.ReadExactlyAsync(audio, token);
                var text = await engine.TranscribeAsync(audio, capture.Language, token);
                lock (gate)
                    if (ReferenceEquals(active, capture) && text.Length > 0)
                        Transcript?.Invoke(capture.Start.AddSeconds(offset), text);
                return Results.NoContent();
            }
            finally { processor.Release(); }
        }
        catch (OperationCanceledException) { return Stopped(); }
        catch (Exception error)
        {
            var message = $"Falha na transcrição. {error.Message}";
            Cancel(message, capture);
            return Reply(message, 500);
        }
    }

    public void Expire()
    {
        lock (gate)
        {
            if (active == null) return;
            var now = Environment.TickCount64;
            if (now - active.Heartbeat > 15_000)
                Cancel("Conexão com a extensão encerrada. Inicie outra captura no TikTok.", active);
            else if (active.FinishRequestedAt > 0 && now - active.FinishRequestedAt > 90_000)
                Cancel("A finalização demorou demais. O texto já reconhecido continua na janela.", active);
        }
    }

    public void Cancel(string message) => Cancel(message, null);

    private void Cancel(string message, Capture? expected)
    {
        lock (gate)
        {
            if (active == null || (expected != null && !ReferenceEquals(expected, active)))
                return;
            var capture = active;
            active = null;
            capture.Cancellation.Cancel();
            Status?.Invoke(message);
        }
    }

    public async Task CloseAsync()
    {
        Cancel("");
        if (server != null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await server.StopAsync(deadline.Token);
            await server.DisposeAsync();
        }
    }

    private static IResult Reply(string error, int status) => Results.Json(new { error }, statusCode: status);
    private static IResult Stopped() => Reply("Captura encerrada no app do PC.", 409);
}
