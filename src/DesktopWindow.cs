using System.Text;

namespace Artho.Transcreve;

internal sealed class DesktopWindow : Form
{
    private static readonly Color Canvas = Color.FromArgb(20, 19, 26);
    private static readonly Color Surface = Color.FromArgb(29, 27, 37);
    private static readonly Color Raised = Color.FromArgb(40, 37, 52);
    private static readonly Color Border = Color.FromArgb(58, 53, 73);
    private static readonly Color Ink = Color.FromArgb(246, 242, 252);
    private static readonly Color Muted = Color.FromArgb(182, 174, 198);
    private static readonly Color Accent = Color.FromArgb(198, 172, 255);
    private static readonly Font TimestampFont = new("Segoe UI", 9, FontStyle.Bold);

    private readonly RichTextBox transcript = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false,
        BackColor = Surface, ForeColor = Ink, Font = new Font("Segoe UI", 12),
        AccessibleName = "Transcrição", ScrollBars = RichTextBoxScrollBars.Vertical
    };
    private readonly Label status = MakeLabel("", 10, Muted);
    private readonly Label state = MakeLabel("Iniciando", 10, Accent, bold: true);
    private readonly Label updateStatus = MakeLabel("", 9, Muted);
    private readonly Label segmentCount = MakeLabel("", 9, Muted);
    private readonly Label levelStatus = MakeLabel("Sem captura", 9, Muted);
    private readonly SignalMeter meter = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 2) };
    private readonly Button setup = MakeButton("Baixar modelo de voz", true);
    private readonly Button begin = MakeButton("Iniciar captura", true);
    private readonly Button stop = MakeButton("Finalizar captura");
    private readonly Button copy = MakeButton("Copiar");
    private readonly Button export = MakeButton("Salvar TXT", true);
    private readonly Button clear = MakeButton("Limpar");
    private readonly ComboBox language = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed,
        Dock = DockStyle.Top, AccessibleName = "Idioma da fala", ItemHeight = 24,
        FlatStyle = FlatStyle.Flat, BackColor = Raised, ForeColor = Ink, Margin = Padding.Empty
    };
    private readonly TableLayoutPanel empty = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 1000 };
    private readonly List<(DateTime Time, string Text)> entries = new();
    private readonly WhisperEngine engine;
    private readonly Bridge bridge;
    private AppUpdate.Pending? pendingUpdate;
    private DateTime nextUpdateCheck;
    private bool checkingUpdate;
    private bool installingEngine;
    private bool applyingUpdate;
    private bool closing;
    private bool ready;

    public DesktopWindow(WhisperEngine engine, string code)
    {
        this.engine = engine;
        bridge = new Bridge(engine, code);
        bridge.Status = message => OnUi(() => SetStatus(message));
        bridge.Transcript = (time, text) => OnUi(() => Append(time, text));
        Text = "Artho Transcreve";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        ClientSize = new Size(1020, 645);
        MinimumSize = new Size(795, 615);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Canvas;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Font;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(28, 20, 28, 14)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(layout);

        var title = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        title.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        title.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        title.Controls.Add(MakeLabel("Artho Transcreve", 29, Ink, bold: true), 0, 0);
        title.Controls.Add(MakeLabel("Transcrição de áudio do TikTok", 11, Muted), 0, 1);
        layout.Controls.Add(title, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 274));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(body, 0, 1);

        var capturePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 13, BackColor = Surface,
            Padding = new Padding(18), Margin = new Padding(0, 0, 18, 0)
        };
        capturePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 33, 24, 54, 26, 19, 24, 27, 37, 43, 43, 43, 43 })
            capturePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        capturePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        capturePanel.Controls.Add(MakeLabel("Captura", 14, Ink, bold: true), 0, 0);
        capturePanel.Controls.Add(state, 0, 1);
        capturePanel.Controls.Add(status, 0, 2);
        capturePanel.Controls.Add(MakeLabel("Nível de áudio", 10, Ink, bold: true), 0, 3);
        capturePanel.Controls.Add(meter, 0, 4);
        capturePanel.Controls.Add(levelStatus, 0, 5);
        capturePanel.Controls.Add(MakeLabel("Idioma da fala", 10, Ink, bold: true), 0, 6);

        language.Items.AddRange(["Português", "Detectar idioma", "Inglês", "Espanhol"]);
        language.SelectedIndex = 0;
        language.SelectedIndexChanged += (_, _) => bridge.Language = new[] { "pt", "auto", "en", "es" }[language.SelectedIndex];
        language.DrawItem += (_, args) =>
        {
            if (args.Index < 0) return;
            using var brush = new SolidBrush((args.State & DrawItemState.Selected) != 0 ? Border : Raised);
            args.Graphics.FillRectangle(brush, args.Bounds);
            var bounds = args.Bounds;
            bounds.Inflate(-6, 0);
            TextRenderer.DrawText(args.Graphics, language.GetItemText(language.Items[args.Index]), language.Font,
                bounds, language.Enabled ? Ink : Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            args.DrawFocusRectangle();
        };
        capturePanel.Controls.Add(language, 0, 7);

        var pairing = MakeButton("Copiar código do app");
        pairing.Click += (_, _) =>
        {
            if (CopyToClipboard(code)) pairing.Text = "Código copiado";
        };
        foreach (var button in new[] { pairing, setup, begin, stop })
        {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 5, 0, 2);
        }
        capturePanel.Controls.Add(pairing, 0, 8);
        capturePanel.Controls.Add(begin, 0, 9);
        capturePanel.Controls.Add(stop, 0, 10);
        capturePanel.Controls.Add(setup, 0, 11);
        body.Controls.Add(capturePanel, 0, 0);

        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Surface,
            Padding = new Padding(22, 15, 22, 12), Margin = Padding.Empty
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        editor.Controls.Add(MakeLabel("Transcrição", 17, Ink, bold: true), 0, 0);
        editor.Controls.Add(segmentCount, 0, 1);
        var textRegion = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1, 10, 1, 10), Margin = Padding.Empty };
        textRegion.Controls.Add(transcript);
        empty.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        empty.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        empty.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        empty.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        empty.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        var emptyTitle = MakeLabel("Nenhum texto ainda", 15, Ink, bold: true);
        var emptyGuide = MakeLabel("Abra o TikTok e clique em Iniciar captura.\nA transcrição aparece aqui.", 10, Muted);
        emptyTitle.TextAlign = emptyGuide.TextAlign = ContentAlignment.MiddleCenter;
        empty.Controls.Add(emptyTitle, 0, 1);
        empty.Controls.Add(emptyGuide, 0, 2);
        textRegion.Controls.Add(empty);
        editor.Controls.Add(textRegion, 0, 2);
        editor.Controls.Add(Row(export, copy, clear), 0, 3);
        body.Controls.Add(editor, 1, 0);

        updateStatus.Text = $"Versão {AppUpdate.Current.ToString(3)} • Atualizações automáticas";
        layout.Controls.Add(updateStatus, 0, 2);

        setup.Visible = !engine.Ready;
        setup.Enabled = false;
        stop.Enabled = false;
        pairing.Enabled = false;
        ResetTranscript();
        SetStatus("Iniciando conexão local…");
        setup.Click += async (_, _) => await SetupAsync();
        begin.Click += (_, _) =>
        {
            if (!bridge.Browser.RequestStart()) return;
            SetStatus(bridge.Browser.Connected ? "Procurando a aba ativa do TikTok…" :
                "Abra a extensão na aba do TikTok para conectar.");
        };
        stop.Click += (_, _) => bridge.Finish();
        copy.Click += (_, _) => CopyToClipboard(GetText());
        export.Click += (_, _) => ExportText();
        clear.Click += (_, _) => { entries.Clear(); ResetTranscript(); };
        heartbeat.Tick += async (_, _) =>
        {
            bridge.Expire();
            if (bridge.Browser.Expire() && !bridge.Capturing)
                SetStatus("Deixe o TikTok na aba ativa do navegador e abra a extensão uma vez antes de tentar.");
            PaintCaptureState();
            await UpdateAsync();
        };
        Shown += async (_, _) =>
        {
            try
            {
                await bridge.StartAsync(lifetime.Token);
                if (closing) return;
                ready = true;
                heartbeat.Start();
                pairing.Enabled = true;
                setup.Enabled = true;
                SetStatus(engine.Ready ? "Abra o TikTok. Autorize a aba ao abrir a extensão pela primeira vez." : "Baixe o modelo de voz para começar. Cerca de 190 MB, uma única vez.");
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
            if (!applyingUpdate && pendingUpdate != null)
                AppUpdate.Discard(pendingUpdate.Stage);
            try { await bridge.CloseAsync(); }
            finally { BeginInvoke(Close); }
        };
    }

    private async Task SetupAsync()
    {
        installingEngine = true;
        setup.Enabled = false;
        try
        {
            await engine.InstallAsync(new Progress<string>(message => SetStatus(message)), lifetime.Token);
            setup.Visible = false;
            SetStatus("Modelo pronto. Abra o TikTok e autorize a aba pela extensão.");
        }
        catch (Exception error) when (!closing)
        {
            SetStatus("Falha no download. Verifique a conexão e tente baixar novamente.");
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception) when (closing) { }
        finally
        {
            installingEngine = false;
            if (!closing) setup.Enabled = true;
        }
    }

    private async Task UpdateAsync()
    {
        if (closing || checkingUpdate || applyingUpdate) return;
        if (pendingUpdate == null && DateTime.UtcNow >= nextUpdateCheck)
        {
            checkingUpdate = true;
            nextUpdateCheck = DateTime.UtcNow.AddMinutes(15);
            try
            {
                var update = await AppUpdate.CheckAsync(lifetime.Token);
                if (closing)
                {
                    if (update != null) AppUpdate.Discard(update.Stage);
                    return;
                }
                pendingUpdate = update;
            }
            catch (Exception)
            {
                if (!closing)
                    updateStatus.Text = $"Versão {AppUpdate.Current.ToString(3)} • Nova tentativa de atualização em 15 min";
            }
            finally { checkingUpdate = false; }
        }

        if (pendingUpdate == null || closing) return;
        if (installingEngine || entries.Count != 0 || !bridge.TryBeginUpdate())
        {
            updateStatus.Text = $"Versão {pendingUpdate.Version.ToString(3)} pronta • Aguardando captura e transcrição livres";
            return;
        }

        try
        {
            AppUpdate.StartInstaller(pendingUpdate);
            applyingUpdate = true;
            Close();
        }
        catch (Exception)
        {
            AppUpdate.Discard(pendingUpdate.Stage);
            pendingUpdate = null;
            bridge.EndUpdate();
            updateStatus.Text = "Falha ao iniciar a atualização. Uma nova tentativa será feita automaticamente.";
        }
    }

    private void SetStatus(string message)
    {
        if (closing) return;
        status.Text = message;
        PaintCaptureState();
    }

    private void PaintCaptureState()
    {
        var active = bridge.Capturing;
        var finishing = bridge.Finishing;
        var level = bridge.AudioLevel;
        var waiting = bridge.Browser.Waiting;
        state.Text = finishing ? "Finalizando" : active ? "Capturando" : waiting ? "Buscando aba" :
            installingEngine ? "Baixando modelo" : !engine.Ready ? "Configuração inicial" :
            bridge.Browser.Connected ? "Extensão conectada" : "Aguardando extensão";
        state.ForeColor = active && !finishing ? Color.FromArgb(146, 226, 195) : Accent;
        stop.Enabled = active && !finishing;
        begin.Enabled = ready && engine.Ready && !active && !waiting;
        language.Enabled = !active;
        meter.SetLevel(level);
        levelStatus.Text = finishing ? "Processando últimos trechos" :
            active ? (level > 0 ? "Áudio recebido" : "Aguardando som da aba") : "Sem captura";
    }

    private void Append(DateTime time, string text)
    {
        empty.Visible = false;
        transcript.Visible = true;
        entries.Add((time, text));
        transcript.SelectionStart = transcript.TextLength;
        transcript.SelectionColor = Accent;
        transcript.SelectionFont = TimestampFont;
        transcript.AppendText($"[{time:HH:mm:ss}]" + Environment.NewLine);
        transcript.SelectionColor = Ink;
        transcript.SelectionFont = transcript.Font;
        transcript.AppendText(text + Environment.NewLine + Environment.NewLine);
        transcript.ScrollToCaret();
        segmentCount.Text = entries.Count == 1 ? "1 trecho reconhecido" : $"{entries.Count} trechos reconhecidos";
        copy.Enabled = export.Enabled = clear.Enabled = true;
    }

    private void ResetTranscript()
    {
        transcript.Clear();
        transcript.Visible = false;
        empty.Visible = true;
        empty.BringToFront();
        segmentCount.Text = "Aguardando áudio";
        copy.Enabled = export.Enabled = clear.Enabled = false;
    }

    private string GetText() => string.Join(Environment.NewLine,
        entries.Select(item => $"[{item.Time:HH:mm:ss}] {item.Text}"));

    private bool CopyToClipboard(string value)
    {
        try { Clipboard.SetText(value); return true; }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "A área de transferência está ocupada. Tente novamente.", Text);
            return false;
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

    private static Label MakeLabel(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text, ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = Padding.Empty
    };

    private sealed class SignalMeter : Control
    {
        private int level;
        public void SetLevel(int value)
        {
            if (value == level) return;
            level = value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            const int bars = 20;
            float width = ClientSize.Width / (float)bars;
            if (width <= 3 || ClientSize.Height <= 2) return;
            using var on = new SolidBrush(Color.FromArgb(146, 226, 195));
            using var off = new SolidBrush(Border);
            for (int i = 0; i < bars; i++)
                e.Graphics.FillRectangle(i * 100 < level * bars ? on : off, i * width, 1, width - 3, ClientSize.Height - 2);
        }
    }

    private static Button MakeButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, Padding = new Padding(12, 0, 12, 0),
            MinimumSize = new Size(0, 34), Margin = new Padding(0, 6, 8, 0),
            BackColor = primary ? Accent : Raised, ForeColor = primary ? Canvas : Ink,
            FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(220, 204, 255) : Border;
        return button;
    }
}
