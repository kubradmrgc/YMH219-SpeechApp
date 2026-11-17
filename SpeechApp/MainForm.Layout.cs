namespace SpeechApp;

public partial class MainForm
{
    private static readonly Font IndicatorFont = new("Segoe UI", 10f, FontStyle.Bold);

    // --- Metni Seslendir sekmesi ---
    private readonly ComboBox voiceCombo = new() { Name = "voiceCombo", DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly TrackBar rateTrackBar = new()
    {
        Name = "rateTrackBar",
        Minimum = 5,
        Maximum = 20,
        Value = 10,
        TickFrequency = 5,
        SmallChange = 1,
        LargeChange = 5,
        Width = 160,
        AutoSize = false,
        Height = 32
    };
    private readonly Label rateLabel = new() { Name = "rateLabel", Text = "1.0x", AutoSize = true, Margin = new Padding(0, 8, 12, 3) };
    private readonly Label ttsIndicator = CreateIndicator("ttsIndicator", "■ HAZIR");
    private readonly RichTextBox ttsTextBox = new()
    {
        Name = "ttsTextBox",
        Dock = DockStyle.Fill,
        BackColor = Color.White,
        Font = new Font("Segoe UI", 12f),
        HideSelection = false,
        DetectUrls = false
    };
    private readonly Button speakButton = CreateButton("speakButton", "▶ Oku");
    private readonly Button pauseButton = CreateButton("pauseButton", "❚❚ Duraklat");
    private readonly Button stopSpeakButton = CreateButton("stopSpeakButton", "■ Durdur");
    private readonly Button clearTextButton = CreateButton("clearTextButton", "Temizle");
    private readonly Button openTextButton = CreateButton("openTextButton", "Metin Aç...");
    private readonly Button saveTextButton = CreateButton("saveTextButton", "Metni Kaydet...");
    private readonly Label charCountLabel = new() { Name = "charCountLabel", Text = "0 karakter", AutoSize = true, Margin = new Padding(12, 9, 3, 3), ForeColor = Color.DimGray };

    // --- Sesi Yazıya Çevir sekmesi ---
    private readonly Label modelStatusLabel = new() { Name = "modelStatusLabel", Text = "Türkçe model: kontrol ediliyor...", AutoSize = true, Margin = new Padding(3, 9, 12, 3) };
    private readonly Button downloadModelButton = CreateButton("downloadModelButton", "Modeli İndir");
    private readonly Button cancelDownloadButton = CreateButton("cancelDownloadButton", "İptal");
    private readonly Button chooseModelButton = CreateButton("chooseModelButton", "Klasör Seç...");
    private readonly ProgressBar downloadProgress = new() { Name = "downloadProgress", Width = 180, Height = 22, Margin = new Padding(12, 7, 3, 3), Visible = false };

    private readonly ComboBox microphoneCombo = new() { Name = "microphoneCombo", DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, DropDownWidth = 420 };
    private readonly Button refreshMicrophonesButton = CreateButton("refreshMicrophonesButton", "Yenile");
    private readonly Button listenButton = CreateButton("listenButton", "● Dinlemeyi Başlat");
    private readonly Button stopListenButton = CreateButton("stopListenButton", "■ Durdur");
    private readonly ProgressBar levelBar = new() { Name = "levelBar", Width = 90, Height = 16, Margin = new Padding(12, 10, 3, 3), Maximum = 100 };
    private readonly Label sttIndicator = CreateIndicator("sttIndicator", "■ HAZIR");

    private readonly RichTextBox sttTextBox = new()
    {
        Name = "sttTextBox",
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 12f),
        HideSelection = false,
        DetectUrls = false
    };
    private readonly Label partialLabel = new()
    {
        Name = "partialLabel",
        Dock = DockStyle.Bottom,
        Height = 30,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", 10.5f, FontStyle.Italic),
        ForeColor = Color.DimGray,
        AutoEllipsis = true
    };
    private readonly ListView resultsList = new()
    {
        Name = "resultsList",
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        ShowItemToolTips = true
    };
    private readonly GroupBox resultsGroup = new() { Text = "Sonuçlar (0)", Dock = DockStyle.Fill, Padding = new Padding(8) };
    private readonly Button recognizeFileButton = CreateButton("recognizeFileButton", "WAV Dosyasından Tanı...");
    private readonly Button sendToTtsButton = CreateButton("sendToTtsButton", "Seslendir'e Aktar");
    private readonly Button saveResultsButton = CreateButton("saveResultsButton", "Sonuçları Kaydet...");
    private readonly Button clearResultsButton = CreateButton("clearResultsButton", "Temizle");

    // --- Ortak ---
    private readonly TabControl tabs = new() { Name = "tabs", Dock = DockStyle.Fill, Padding = new Point(14, 5) };
    private readonly TabPage ttsTab = new() { Text = "Metni Seslendir (Text-to-Speech)", Padding = new Padding(8) };
    private readonly TabPage sttTab = new() { Text = "Sesi Yazıya Çevir (Speech-to-Text)", Padding = new Padding(8) };
    private readonly StatusStrip statusStrip = new();
    private readonly ToolStripStatusLabel activeOperationLabel = new() { Name = "activeOperationLabel", Text = "Aktif işlem: yok", Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    private readonly ToolStripStatusLabel statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Hazır" };

    private void BuildLayout()
    {
        Text = "YMH219 - Konuşma Uygulaması (Speech-to-Text / Text-to-Speech)";
        ClientSize = new Size(1060, 640);
        MinimumSize = new Size(900, 520);
        StartPosition = FormStartPosition.CenterScreen;

        BuildTtsTab();
        BuildSttTab();

        tabs.TabPages.Add(ttsTab);
        tabs.TabPages.Add(sttTab);

        statusStrip.Items.Add(activeOperationLabel);
        statusStrip.Items.Add(new ToolStripSeparator());
        statusStrip.Items.Add(statusLabel);

        Controls.Add(tabs);
        Controls.Add(statusStrip);
    }

    private void BuildTtsTab()
    {
        var settingsBar = CreateBar(DockStyle.Top);
        settingsBar.Controls.AddRange(new Control[]
        {
            CreateLabel("Ses:"), voiceCombo, CreateLabel("Hız:"), rateTrackBar, rateLabel, ttsIndicator
        });

        var textGroup = new GroupBox { Text = "Okunacak Metin", Dock = DockStyle.Fill, Padding = new Padding(8) };
        textGroup.Controls.Add(ttsTextBox);

        var buttonBar = CreateBar(DockStyle.Bottom);
        buttonBar.Controls.AddRange(new Control[]
        {
            speakButton, pauseButton, stopSpeakButton, clearTextButton, openTextButton, saveTextButton, charCountLabel
        });

        ttsTab.Controls.Add(textGroup);
        ttsTab.Controls.Add(buttonBar);
        ttsTab.Controls.Add(settingsBar);
    }

    private void BuildSttTab()
    {
        var modelBar = CreateBar(DockStyle.Top);
        modelBar.Controls.AddRange(new Control[]
        {
            modelStatusLabel, downloadModelButton, cancelDownloadButton, chooseModelButton, downloadProgress
        });
        var modelGroup = new GroupBox { Text = "Konuşma Tanıma Modeli (Vosk)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        modelGroup.Controls.Add(modelBar);

        var microphoneBar = CreateBar(DockStyle.Top);
        microphoneBar.Controls.AddRange(new Control[]
        {
            CreateLabel("Mikrofon:"), microphoneCombo, refreshMicrophonesButton, listenButton, stopListenButton,
            CreateLabel("Seviye:"), levelBar, sttIndicator
        });

        var textGroup = new GroupBox { Text = "Tanınan Metin", Dock = DockStyle.Fill, Padding = new Padding(8) };
        textGroup.Controls.Add(sttTextBox);
        textGroup.Controls.Add(partialLabel);

        resultsList.Columns.Add("Saat", 70);
        resultsList.Columns.Add("Metin", 300);
        resultsList.Columns.Add("Kaynak", 100);
        resultsList.Resize += (_, _) =>
            resultsList.Columns[1].Width = Math.Max(100,
                resultsList.ClientSize.Width - resultsList.Columns[0].Width - resultsList.Columns[2].Width - 4);
        resultsGroup.Controls.Add(resultsList);

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(textGroup);
        split.Panel2.Controls.Add(resultsGroup);
        Load += (_, _) => split.SplitterDistance = split.Width / 2;

        var buttonBar = CreateBar(DockStyle.Bottom);
        buttonBar.Controls.AddRange(new Control[]
        {
            recognizeFileButton, sendToTtsButton, saveResultsButton, clearResultsButton
        });

        sttTab.Controls.Add(split);
        sttTab.Controls.Add(buttonBar);
        sttTab.Controls.Add(microphoneBar);
        sttTab.Controls.Add(modelGroup);
    }

    private static FlowLayoutPanel CreateBar(DockStyle dock)
    {
        return new FlowLayoutPanel { Dock = dock, AutoSize = true, WrapContents = false, Padding = new Padding(2, 4, 2, 4) };
    }

    private static Label CreateLabel(string text)
    {
        return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 9, 3, 3) };
    }

    private static Label CreateIndicator(string name, string text)
    {
        return new Label
        {
            Name = name,
            AutoSize = false,
            Size = new Size(170, 30),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = IndicatorFont,
            ForeColor = Color.White,
            BackColor = Color.DimGray,
            Text = text,
            Margin = new Padding(12, 3, 3, 3)
        };
    }

    private static Button CreateButton(string name, string text)
    {
        return new Button { Name = name, Text = text, AutoSize = true, MinimumSize = new Size(90, 32), Margin = new Padding(3, 3, 3, 3) };
    }
}

