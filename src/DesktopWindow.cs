using System.Text;

namespace Artho.Transcreve;

internal sealed class DesktopWindow : Form
{
    private static readonly Color Canvas = Color.FromArgb(15, 19, 28);
    private static readonly Color Surface = Color.FromArgb(24, 31, 44);
    private static readonly Color Ink = Color.FromArgb(238, 242, 251);
    private static readonly Color Muted = Color.FromArgb(164, 177, 202);
    private static readonly Color Accent = Color.FromArgb(135, 184, 255);
    private readonly RichTextBox transcript = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false,
        BackColor = Surface, ForeColor = Ink, Font = new Font("Segoe UI", 12),
        AccessibleName = "Transcrição"
    };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Accent, AutoEllipsis = true };
    private readonly Button setup = MakeButton("Baixar motor", true);
    private readonly Button stop = MakeButton("Parar captura");
    private readonly Button copy = MakeButton("Copiar texto");
    private readonly Button export = MakeButton("Salvar TXT");
    private readonly Button clear = MakeButton("Limpar");
    private readonly ComboBox language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, AccessibleName = "Idioma da fala" };
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 1000 };
    private readonly List<(DateTime Time, string Text)> entries = new();
    private readonly WhisperEngine engine;
    private readonly Bridge bridge;
    private bool closing;

    public DesktopWindow(WhisperEngine engine, string code)
    {
        this.engine = engine;
        bridge = new Bridge(engine, code);
        bridge.Status = message => OnUi(() => SetStatus(message));
        bridge.Transcript = (time, text) => OnUi(() => Append(time, text));
        Text = "Artho Transcreve";
        ClientSize = new Size(830, 570);
        MinimumSize = new Size(700, 465);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Canvas;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(24, 15, 24, 15) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 49, 30, 53, 47 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Artho Transcreve", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 26, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(new Label { Text = "Áudio da aba do TikTok em texto, direto no Windows.", Dock = DockStyle.Fill, ForeColor = Muted }, 0, 1);

        var pairing = MakeButton("Copiar código");
        pairing.Click += (_, _) => CopyToClipboard(code);
        language.Items.AddRange(["Português", "Detectar idioma", "Inglês", "Espanhol"]);
        language.SelectedIndex = 0;
        language.SelectedIndexChanged += (_, _) => bridge.Language = new[] { "pt", "auto", "en", "es" }[language.SelectedIndex];
        language.Margin = new Padding(0, 10, 8, 0);
        var pairingRow = Row(pairing, language, setup);
        layout.Controls.Add(pairingRow, 0, 2);
        var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusRow.Controls.Add(status, 0, 0);
        statusRow.Controls.Add(stop, 1, 0);
        layout.Controls.Add(statusRow, 0, 3);

        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(14), Margin = new Padding(0, 5, 0, 4) };
        panel.Controls.Add(transcript);
        layout.Controls.Add(panel, 0, 4);
        layout.Controls.Add(Row(copy, export, clear), 0, 5);
        layout.Controls.Add(new Label
        {
            Text = "Processamento local • A transcrição fica nesta janela até você salvar.",
            ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        }, 0, 6);

        setup.Visible = !engine.Ready;
        setup.Enabled = false;
        stop.Enabled = false;
        pairing.Enabled = false;
        ResetTranscript();
        SetStatus("Iniciando conexão local…");
        setup.Click += async (_, _) => await SetupAsync();
        stop.Click += (_, _) => bridge.Cancel("Captura encerrada no app.");
        copy.Click += (_, _) => CopyToClipboard(GetText());
        export.Click += (_, _) => ExportText();
        clear.Click += (_, _) => { entries.Clear(); ResetTranscript(); };
        heartbeat.Tick += (_, _) => bridge.Expire();
        Shown += async (_, _) =>
        {
            try
            {
                await bridge.StartAsync(lifetime.Token);
                if (closing) return;
                heartbeat.Start();
                pairing.Enabled = true;
                setup.Enabled = true;
                SetStatus(engine.Ready ? "Pronto. Inicie a transcrição pela extensão no TikTok." : "Baixe o motor para começar. Cerca de 60 MB, uma única vez.");
            }
            catch (OperationCanceledException) when (closing) { }
            catch (Exception)
            {
                if (!closing) SetStatus("A porta local 48721 está indisponível. Feche outro app que esteja usando essa porta.");
            }
        };
        FormClosing += async (_, eventArgs) =>
        {
            if (closing) return;
            eventArgs.Cancel = true;
            closing = true;
            Enabled = false;
            heartbeat.Stop();
            lifetime.Cancel();
            try { await bridge.CloseAsync(); }
            finally { BeginInvoke(Close); }
        };
    }

    private async Task SetupAsync()
    {
        setup.Enabled = false;
        try
        {
            await engine.InstallAsync(new Progress<string>(message => SetStatus(message)), lifetime.Token);
            setup.Visible = false;
            SetStatus("Motor pronto. Inicie a transcrição pela extensão no TikTok.");
        }
        catch (Exception error) when (!closing)
        {
            SetStatus("Falha no download. Verifique a conexão e clique em Baixar motor.");
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception) when (closing) { }
        finally { if (!closing) setup.Enabled = true; }
    }

    private void SetStatus(string message)
    {
        if (closing) return;
        status.Text = message;
        stop.Enabled = bridge.Capturing;
        language.Enabled = !bridge.Capturing;
    }

    private void Append(DateTime time, string text)
    {
        if (entries.Count == 0) transcript.Clear();
        entries.Add((time, text));
        transcript.SelectionStart = transcript.TextLength;
        transcript.SelectionColor = Accent;
        transcript.AppendText($"[{time:HH:mm:ss}]  ");
        transcript.SelectionColor = Ink;
        transcript.AppendText(text + Environment.NewLine + Environment.NewLine);
        transcript.ScrollToCaret();
        copy.Enabled = export.Enabled = clear.Enabled = true;
    }

    private void ResetTranscript()
    {
        transcript.Text = "A transcrição vai aparecer aqui durante a reprodução.";
        copy.Enabled = export.Enabled = clear.Enabled = false;
    }

    private string GetText() => string.Join(Environment.NewLine,
        entries.Select(item => $"[{item.Time:HH:mm:ss}] {item.Text}"));

    private void CopyToClipboard(string value)
    {
        try { Clipboard.SetText(value); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "A área de transferência está ocupada. Tente novamente.", Text);
        }
    }

    private void ExportText()
    {
        using var dialog = new SaveFileDialog { Filter = "Texto UTF-8 (*.txt)|*.txt", FileName = $"TikTok-{DateTime.Now:yyyy-MM-dd-HHmm}.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, GetText() + Environment.NewLine, Encoding.UTF8); }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OnUi(Action action)
    {
        if (IsHandleCreated && !IsDisposed && !closing)
            BeginInvoke(() => { if (!closing) action(); });
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Button MakeButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, Padding = new Padding(12, 0, 12, 0),
            MinimumSize = new Size(0, 34), Margin = new Padding(0, 6, 8, 0),
            BackColor = primary ? Accent : Surface, ForeColor = primary ? Canvas : Ink,
            FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(56, 72, 99);
        return button;
    }
}
