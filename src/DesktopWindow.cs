using System.Text;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;

namespace Artho.Transcreve;

internal sealed class DesktopWindow : Form
{
    private static readonly Color Canvas = Color.FromArgb(18, 17, 24);
    private static readonly Color Surface = Color.FromArgb(27, 25, 35);
    private static readonly Color Raised = Color.FromArgb(38, 34, 51);
    private static readonly Color Border = Color.FromArgb(60, 53, 78);
    private static readonly Color Ink = Color.FromArgb(246, 242, 252);
    private static readonly Color Muted = Color.FromArgb(182, 174, 198);
    private static readonly Color Accent = Color.FromArgb(198, 172, 255);
    private static readonly Font TimestampFont = new("Segoe UI", 9, FontStyle.Bold);

    private readonly RichTextBox transcript = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DetectUrls = false,
        BackColor = Surface, ForeColor = Ink, Font = new Font("Segoe UI", 12),
        AccessibleName = "Transcrição", ScrollBars = RichTextBoxScrollBars.Vertical
    };
    private readonly Label status = MakeLabel("", 10, Muted);
    private readonly CaptureBadge state = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 7, 0, 7) };
    private readonly Label updateStatus = MakeLabel("", 9, Muted);
    private readonly Label feedback = MakeLabel("", 9, Muted);
    private readonly Label editorHint = MakeLabel("Edite o texto aqui antes de copiar ou salvar.", 9, Muted);
    private readonly SignalMeter meter = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 2) };
    private readonly Button setup = MakeButton("Baixar modelo de voz", true);
    private readonly Button begin = MakeButton("Iniciar captura", true);
    private readonly Button stop = MakeButton("Finalizar captura", true);
    private readonly Button copy = MakeButton("Copiar");
    private readonly Button export = MakeButton("Salvar TXT");
    private readonly Button clear = MakeButton("Limpar");
    private readonly ComboBox language = MakeSelect("Idioma da fala", ["Português", "Automático", "Inglês", "Espanhol"]);
    private readonly ComboBox quality = MakeSelect("Precisão do reconhecimento", ["Padrão", "Alta precisão"]);
    private readonly TableLayoutPanel empty = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 1000 };
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
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(825, 615);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Canvas;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Font;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(26, 16, 26, 10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 138));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(layout);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, 16) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(new PictureBox
        {
            Image = Icon?.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 12, 8), TabStop = false
        }, 0, 0);
        var brand = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 1, 0, 2) };
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        brand.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        brand.Controls.Add(MakeLabel("Artho Transcreve", 24, Ink, bold: true), 0, 0);
        brand.Controls.Add(MakeLabel("Transcrição de áudio do TikTok", 10, Muted), 0, 1);
        header.Controls.Add(brand, 1, 0);
        header.Controls.Add(state, 2, 0);

        var pairing = MakeButton("Copiar código");
        pairing.AccessibleName = "Copiar código para conectar a extensão";
        pairing.Dock = DockStyle.Fill;
        pairing.Margin = new Padding(10, 14, 0, 14);
        pairing.Click += (_, _) =>
        {
            if (CopyToClipboard(code)) feedback.Text = "Código da extensão copiado.";
        };
        header.Controls.Add(pairing, 3, 0);
        layout.Controls.Add(header, 0, 0);

        var capturePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3, Margin = Padding.Empty, BackColor = Surface };
        capturePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        capturePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        capturePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        capturePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
        capturePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        capturePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        capturePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        capturePanel.Controls.Add(MakeLabel("Nível de áudio", 9, Muted, bold: true), 0, 0);
        capturePanel.Controls.Add(MakeLabel("Idioma da fala", 9, Muted, bold: true), 1, 0);
        capturePanel.Controls.Add(MakeLabel("Precisão", 9, Muted, bold: true), 2, 0);
        capturePanel.Controls.Add(meter, 0, 1);
        foreach (var select in new[] { language, quality })
            select.Margin = new Padding(0, 4, 14, 0);
        capturePanel.Controls.Add(language, 1, 1);
        capturePanel.Controls.Add(quality, 2, 1);

        var captureAction = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0) };
        foreach (var button in new[] { setup, begin, stop })
        {
            button.Dock = DockStyle.Fill;
            button.Margin = Padding.Empty;
            captureAction.Controls.Add(button);
        }
        capturePanel.Controls.Add(captureAction, 3, 1);
        status.Margin = new Padding(0, 4, 0, 0);
        capturePanel.Controls.Add(status, 0, 2);
        capturePanel.SetColumnSpan(status, 4);
        layout.Controls.Add(MakeCard(capturePanel, new Padding(0, 0, 0, 14), new Padding(18, 11, 18, 8)), 0, 1);

        bridge.Mode = engine.PreferredMode();
        quality.SelectedIndex = (int)bridge.Mode;
        language.SelectedIndexChanged += (_, _) => bridge.Language = new[] { "pt", "auto", "en", "es" }[language.SelectedIndex];
        quality.SelectedIndexChanged += (_, _) =>
        {
            bridge.Mode = (RecognitionMode)quality.SelectedIndex;
            engine.SaveMode(bridge.Mode);
            SetStatus(ReadyMessage());
        };

        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Surface, Margin = Padding.Empty
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 284));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.Controls.Add(MakeLabel("Transcrição", 17, Ink, bold: true), 0, 0);
        editor.Controls.Add(editorHint, 0, 1);
        clear.AutoSize = copy.AutoSize = export.AutoSize = false;
        clear.Size = copy.Size = new Size(78, 36);
        export.Size = new Size(104, 36);
        var actions = Row(export, copy, clear);
        actions.FlowDirection = FlowDirection.RightToLeft;
        editor.Controls.Add(actions, 1, 0);
        editor.SetRowSpan(actions, 2);

        var textRegion = new CardPanel(Canvas)
        {
            Dock = DockStyle.Fill, Padding = new Padding(16, 15, 16, 14), Margin = new Padding(0, 5, 0, 1),
            BackColor = Surface
        };
        transcript.BackColor = Canvas;
        textRegion.Controls.Add(transcript);
        empty.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        empty.BackColor = Canvas;
        empty.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        empty.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        empty.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        empty.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        var emptyTitle = MakeLabel("Nenhum texto ainda", 15, Ink, bold: true);
        var emptyGuide = MakeLabel("Abra o TikTok e inicie a captura.\nVocê pode editar o texto assim que ele aparecer.", 10, Muted);
        emptyTitle.TextAlign = emptyGuide.TextAlign = ContentAlignment.MiddleCenter;
        empty.Controls.Add(emptyTitle, 0, 1);
        empty.Controls.Add(emptyGuide, 0, 2);
        textRegion.Controls.Add(empty);
        editor.Controls.Add(textRegion, 0, 2);
        editor.SetColumnSpan(textRegion, 2);
        layout.Controls.Add(MakeCard(editor, Padding.Empty, new Padding(18, 12, 18, 18)), 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 67));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        updateStatus.Text = $"Versão {AppUpdate.Current.ToString(3)} • Atualizações automáticas";
        feedback.TextAlign = ContentAlignment.MiddleRight;
        footer.Controls.Add(updateStatus, 0, 0);
        footer.Controls.Add(feedback, 1, 0);
        layout.Controls.Add(footer, 0, 3);

        setup.Visible = !engine.Ready(bridge.Mode);
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
        copy.Click += (_, _) => { if (CopyToClipboard(GetText())) feedback.Text = "Transcrição copiada."; };
        export.Click += (_, _) => ExportText();
        clear.Click += (_, _) => { ResetTranscript(); feedback.Text = ""; };
        transcript.TextChanged += (_, _) =>
        {
            UpdateEditorActions();
            feedback.Text = "";
        };
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
                SetStatus(ReadyMessage());
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
        var mode = bridge.Mode;
        installingEngine = true;
        PaintCaptureState();
        try
        {
            await engine.InstallAsync(mode, new Progress<string>(message => SetStatus(message)), lifetime.Token);
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
            if (!closing) PaintCaptureState();
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
        if (installingEngine || transcript.TextLength != 0 || !bridge.TryBeginUpdate())
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
        var modelReady = engine.Ready(bridge.Mode);
        var label = !ready ? "Iniciando" : finishing ? "Finalizando" : active ? "Capturando" : waiting ? "Buscando aba" :
            installingEngine ? "Baixando modelo" : !modelReady ? "Modelo pendente" :
            bridge.Browser.Connected ? "Extensão conectada" : "Aguardando extensão";
        state.ShowState(label, active && !finishing, bridge.Browser.Connected);
        stop.Visible = active;
        begin.Visible = !active && modelReady && !installingEngine;
        begin.Text = waiting ? "Buscando aba…" : "Iniciar captura";
        stop.Text = finishing ? "Finalizando…" : "Finalizar captura";
        stop.Enabled = active && !finishing;
        begin.Enabled = ready && modelReady && !active && !waiting && !installingEngine;
        language.Enabled = !active;
        quality.Enabled = !active && !waiting && !installingEngine;
        setup.Visible = !active && (!modelReady || installingEngine);
        setup.Enabled = ready && !active && !waiting && !installingEngine;
        setup.Text = installingEngine ? "Baixando…" :
            bridge.Mode == RecognitionMode.Precise ? "Baixar modelo · 574 MB" : "Baixar modelo de voz";
        meter.SetLevel(active && !finishing ? level : 0);
    }

    private string ReadyMessage() => engine.Ready(bridge.Mode)
        ? "Modelo pronto. Abra o TikTok para iniciar a captura."
        : bridge.Mode == RecognitionMode.Precise
            ? "Baixe o modelo de alta precisão. Pode levar mais tempo para transcrever."
            : "Baixe o modelo de voz para começar. Cerca de 190 MB.";

    private void Append(DateTime time, string text)
    {
        empty.Visible = false;
        transcript.Visible = true;
        var keepPosition = transcript.Focused || transcript.SelectionLength > 0;
        var start = transcript.SelectionStart;
        var length = transcript.SelectionLength;
        Point scroll = default;
        if (keepPosition) SendMessage(transcript.Handle, 0x04DD, IntPtr.Zero, ref scroll); // EM_GETSCROLLPOS

        transcript.SelectionStart = transcript.TextLength;
        transcript.SelectionLength = 0;
        if (transcript.TextLength > 0)
            transcript.AppendText(Environment.NewLine + Environment.NewLine);
        transcript.SelectionColor = Accent;
        transcript.SelectionFont = TimestampFont;
        transcript.AppendText($"[{time:HH:mm:ss}]" + Environment.NewLine);
        transcript.SelectionColor = Ink;
        transcript.SelectionFont = transcript.Font;
        transcript.AppendText(text);

        if (keepPosition)
        {
            transcript.Select(start, length);
            SendMessage(transcript.Handle, 0x04DE, IntPtr.Zero, ref scroll); // EM_SETSCROLLPOS
        }
        else
        {
            transcript.SelectionStart = transcript.TextLength;
            transcript.ScrollToCaret();
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, ref Point point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, in int value, uint length);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        const uint useImmersiveDarkMode = 20;
        int darkMode = 1;
        DwmSetWindowAttribute(Handle, useImmersiveDarkMode, in darkMode, sizeof(int));
    }

    private void ResetTranscript()
    {
        transcript.Clear();
        transcript.Visible = false;
        empty.Visible = true;
        empty.BringToFront();
        UpdateEditorActions();
    }

    private void UpdateEditorActions() => copy.Enabled = export.Enabled = clear.Enabled = transcript.TextLength > 0;

    private string GetText() => transcript.Text.ReplaceLineEndings(Environment.NewLine);

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
        try
        {
            File.WriteAllText(dialog.FileName, GetText(), Encoding.UTF8);
            feedback.Text = "Transcrição salva.";
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S) && export.Enabled)
        {
            ExportText();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
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

    private static ComboBox MakeSelect(string name, string[] items)
    {
        var select = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed,
            Dock = DockStyle.Top, AccessibleName = name, ItemHeight = 24, FlatStyle = FlatStyle.Flat,
            BackColor = Raised, ForeColor = Ink, Margin = Padding.Empty
        };
        select.Items.AddRange(items);
        select.SelectedIndex = 0;
        select.DrawItem += (_, args) =>
        {
            if (args.Index < 0) return;
            using var brush = new SolidBrush((args.State & DrawItemState.Selected) != 0 ? Border : Raised);
            args.Graphics.FillRectangle(brush, args.Bounds);
            var bounds = args.Bounds;
            bounds.Inflate(-4, 0);
            TextRenderer.DrawText(args.Graphics, select.GetItemText(select.Items[args.Index]), select.Font,
                bounds, select.Enabled ? Ink : Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            args.DrawFocusRectangle();
        };
        return select;
    }

    private static Panel MakeCard(Control content, Padding margin, Padding padding)
    {
        var card = new CardPanel(Surface) { Dock = DockStyle.Fill, Margin = margin, Padding = padding, BackColor = Canvas };
        card.Controls.Add(content);
        return card;
    }

    private static GraphicsPath Round(RectangleF rect, float radius)
    {
        var shape = new GraphicsPath();
        float size = radius * 2;
        shape.AddArc(rect.Left, rect.Top, size, size, 180, 90);
        shape.AddArc(rect.Right - size, rect.Top, size, size, 270, 90);
        shape.AddArc(rect.Right - size, rect.Bottom - size, size, size, 0, 90);
        shape.AddArc(rect.Left, rect.Bottom - size, size, size, 90, 90);
        shape.CloseFigure();
        return shape;
    }

    private sealed class CardPanel : Panel
    {
        private readonly Color background;

        public CardPanel(Color background)
        {
            this.background = background;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width < 24 || ClientSize.Height < 24) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 13);
            using var fill = new SolidBrush(background);
            using var pen = new Pen(Border);
            e.Graphics.FillPath(fill, shape);
            e.Graphics.DrawPath(pen, shape);
        }
    }

    private sealed class CaptureBadge : Control
    {
        private static readonly Color Active = Color.FromArgb(141, 229, 190);
        private static readonly Font BadgeFont = new("Segoe UI", 9, FontStyle.Bold);
        private string label = "Iniciando";
        private Color tone = Muted;
        public CaptureBadge()
        {
            DoubleBuffered = true;
            TabStop = false;
            AccessibleName = label;
        }

        public void ShowState(string value, bool live, bool connected)
        {
            var next = live ? Active : connected ? Accent : Muted;
            if (value == label && next == tone) return;
            label = value;
            tone = next;
            AccessibleName = value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var box = new Rectangle(0, Math.Max(0, (Height - 32) / 2), Width - 1, 32);
            if (box.Width < 24 || Height < 32) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Round(box, 10);
            using var fill = new SolidBrush(Raised);
            using var dot = new SolidBrush(tone);
            e.Graphics.FillPath(fill, shape);
            e.Graphics.FillEllipse(dot, box.X + 12, box.Y + 13, 6, 6);
            TextRenderer.DrawText(e.Graphics, label, BadgeFont,
                new Rectangle(box.X + 25, box.Y, box.Width - 28, box.Height), tone,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    private sealed class SignalMeter : Control
    {
        private int level;
        public SignalMeter()
        {
            DoubleBuffered = true;
            TabStop = false;
            AccessibleName = "Nível de áudio";
        }

        public void SetLevel(int value)
        {
            if (value == level) return;
            level = value;
            AccessibleDescription = $"{level} de 100";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            const int bars = 24;
            float width = (ClientSize.Width - 18) / (float)bars;
            if (width <= 3 || Height < 10) return;
            using var on = new SolidBrush(Color.FromArgb(141, 229, 190));
            using var off = new SolidBrush(Border);
            for (int i = 0; i < bars; i++)
                e.Graphics.FillRectangle(i * 100 < level * bars ? on : off,
                    i * width, (ClientSize.Height - 9) / 2f, width - 3, 9);
        }
    }

    private static Button MakeButton(string text, bool primary = false) => new ActionButton(primary)
    {
        Text = text, AutoSize = true, Padding = new Padding(10, 0, 10, 0),
        MinimumSize = new Size(0, 36), Margin = new Padding(6, 4, 0, 0),
        FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false
    };

    private sealed class ActionButton(bool primary) : Button
    {
        private bool hovering;

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hovering = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovering = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Canvas);
            if (Width < 18 || Height < 18) return;
            var background = !Enabled ? Surface : primary ? (hovering ? Color.FromArgb(220, 204, 255) : Accent) : hovering ? Border : Raised;
            var foreground = !Enabled ? Muted : primary ? Canvas : Ink;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 8);
            using var fill = new SolidBrush(background);
            using var pen = new Pen(Focused ? Accent : Enabled && primary ? background : Border);
            e.Graphics.FillPath(fill, shape);
            e.Graphics.DrawPath(pen, shape);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
