using SpeechApp.Models;
using SpeechApp.Services;

namespace SpeechApp;

/// <summary>
/// Metni sesli okuma (Text-to-Speech) ve konuşmayı yazıya çevirme (Speech-to-Text) penceresi.
/// </summary>
public partial class MainForm : Form
{
    private readonly TextToSpeechService tts = new();
    private readonly VoskModelManager modelManager = new();
    private readonly SpeechToTextService stt = new();
    private readonly TranscriptStore transcript = new();
    private CancellationTokenSource? downloadCts;
    private CancellationTokenSource? fileCts;
    private string recognitionSource = MicrophoneSource;
    private const string MicrophoneSource = "Mikrofon";
    private int highlightStart = -1;
    private int highlightLength;

    public MainForm()
    {
        BuildLayout();

        ttsTextBox.TextChanged += (_, _) => charCountLabel.Text = $"{ttsTextBox.TextLength} karakter";
        rateTrackBar.ValueChanged += (_, _) => UpdateRateLabel();
        UpdateRateLabel();

        speakButton.Click += async (_, _) => await SpeakAsync();
        stopSpeakButton.Click += (_, _) =>
        {
            tts.Stop();
            statusLabel.Text = "Okuma durduruldu.";
        };
        pauseButton.Click += (_, _) =>
        {
            if (tts.State == SpeechPlaybackState.Paused)
            {
                tts.Resume();
            }
            else
            {
                tts.Pause();
            }
        };
        clearTextButton.Click += (_, _) =>
        {
            tts.Stop();
            ttsTextBox.Clear();
            ttsTextBox.Focus();
        };

        tts.StateChanged += state => RunOnUi(() => UpdateTtsState(state));
        tts.Completed += () => RunOnUi(() => statusLabel.Text = "Okuma tamamlandı.");
        tts.Failed += message => RunOnUi(() => ShowError(message));
        tts.WordReached += (start, length) => RunOnUi(() => HighlightWord(start, length));

        downloadModelButton.Click += async (_, _) => await DownloadModelAsync();
        cancelDownloadButton.Click += (_, _) => downloadCts?.Cancel();
        chooseModelButton.Click += (_, _) => ChooseModelFolder();

        refreshMicrophonesButton.Click += (_, _) => LoadMicrophones();
        listenButton.Click += async (_, _) => await StartListeningAsync();
        stopListenButton.Click += (_, _) =>
        {
            if (stt.State == RecognitionState.RecognizingFile)
            {
                fileCts?.Cancel();
                return;
            }

            stt.Stop();
            statusLabel.Text = "Dinleme durduruldu.";
        };
        recognizeFileButton.Click += async (_, _) => await RecognizeFileAsync();
        sendToTtsButton.Click += (_, _) => SendToTts();
        clearResultsButton.Click += (_, _) =>
        {
            sttTextBox.Clear();
            partialLabel.Text = string.Empty;
            transcript.Clear();
        };
        sttTextBox.TextChanged += (_, _) => sendToTtsButton.Enabled = sttTextBox.TextLength > 0;

        openTextButton.Click += (_, _) => OpenTextFile();
        saveTextButton.Click += (_, _) => SaveTextFile();
        saveResultsButton.Click += (_, _) => SaveResultsFile();

        transcript.EntryAdded += AddResultRow;
        transcript.Cleared += () =>
        {
            resultsList.Items.Clear();
            UpdateResultsTitle();
        };

        stt.StateChanged += state => RunOnUi(() => UpdateSttState(state));
        stt.PartialResult += text => RunOnUi(() => partialLabel.Text = text.Length == 0 ? string.Empty : $"… {text}");
        stt.FinalResult += text => RunOnUi(() => AppendRecognizedText(text));
        stt.LevelChanged += level => RunOnUi(() => levelBar.Value = Math.Clamp((int)(Math.Sqrt(level) * 100), 0, 100));
        stt.Failed += message => RunOnUi(() => ShowError(message));
        stt.Warning += message => RunOnUi(() => ShowWarning(message));

        cancelDownloadButton.Enabled = false;
        sendToTtsButton.Enabled = false;
        saveResultsButton.Enabled = false;

        LoadVoices();
        UpdateTtsState(SpeechPlaybackState.Idle);
        LoadMicrophones();
        UpdateModelStatus();
    }

    private void LoadMicrophones()
    {
        string? previous = (microphoneCombo.SelectedItem as MicrophoneInfo)?.Name;
        IReadOnlyList<MicrophoneInfo> microphones;
        try
        {
            microphones = SpeechToTextService.GetMicrophones();
        }
        catch (Exception ex)
        {
            microphones = Array.Empty<MicrophoneInfo>();
            statusLabel.Text = $"Mikrofon listesi alınamadı: {ex.Message}";
        }

        microphoneCombo.Items.Clear();
        foreach (MicrophoneInfo microphone in microphones)
        {
            microphoneCombo.Items.Add(microphone);
        }

        if (microphones.Count == 0)
        {
            statusLabel.Text = "Mikrofon bulunamadı. Bir mikrofon takıp 'Yenile' düğmesine basın.";
        }
        else
        {
            MicrophoneInfo selected = microphones.FirstOrDefault(m => m.Name == previous) ?? microphones[0];
            microphoneCombo.SelectedItem = selected;
        }

        UpdateSttState(stt.State);
    }

    private async Task StartListeningAsync()
    {
        if (!modelManager.Locate())
        {
            UpdateModelStatus();
            ShowWarning("Konuşma tanıma modeli yüklü değil.\n'Modeli İndir' düğmesiyle Türkçe modeli indirin veya 'Klasör Seç...' ile model klasörünü gösterin.");
            return;
        }

        if (tts.State != SpeechPlaybackState.Idle)
        {
            DialogResult answer = MessageBox.Show(this,
                "Şu anda metin sesli okunuyor. Hoparlörden gelen ses de mikrofona girip yazıya çevrilebilir.\n\n" +
                "Evet: okumayı durdur ve dinlemeye başla\nHayır: okuma sürerken dinle\nİptal: dinlemeye başlama",
                "Okuma sürüyor", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
            {
                return;
            }

            if (answer == DialogResult.Yes)
            {
                tts.Stop();
            }
        }

        try
        {
            statusLabel.Text = "Konuşma tanıma modeli yükleniyor...";
            recognitionSource = MicrophoneSource;
            await stt.StartAsync(modelManager.ModelPath!, microphoneCombo.SelectedItem as MicrophoneInfo);
            statusLabel.Text = $"Dinleniyor ({microphoneCombo.SelectedItem}). Konuşmaya başlayabilirsiniz.";
        }
        catch (SpeechAppException ex)
        {
            ShowWarning(ex.Message);
        }
    }

    private async Task RecognizeFileAsync()
    {
        if (!modelManager.Locate())
        {
            UpdateModelStatus();
            ShowWarning("Konuşma tanıma modeli yüklü değil. Önce Türkçe modeli indirin.");
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Yazıya çevrilecek ses dosyasını seçin",
            Filter = "WAV ses dosyası (*.wav)|*.wav"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string fileName = Path.GetFileName(dialog.FileName);
        recognitionSource = fileName;
        fileCts = new CancellationTokenSource();
        var progress = new Progress<double>(percent =>
        {
            if (stt.State == RecognitionState.RecognizingFile)
            {
                sttIndicator.Text = $"● TANINIYOR %{percent:0}";
                statusLabel.Text = $"{fileName} yazıya çevriliyor... %{percent:0}";
            }
        });

        try
        {
            statusLabel.Text = $"{fileName} yazıya çevriliyor...";
            int count = await stt.RecognizeFileAsync(modelManager.ModelPath!, dialog.FileName, progress, fileCts.Token);
            statusLabel.Text = count == 0
                ? $"{fileName} dosyasında konuşma bulunamadı."
                : $"{fileName}: {count} cümle tanındı.";
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "Dosyadan tanıma iptal edildi.";
        }
        catch (SpeechAppException ex)
        {
            ShowWarning(ex.Message);
        }
        finally
        {
            fileCts.Dispose();
            fileCts = null;
            recognitionSource = MicrophoneSource;
        }
    }

    private void AppendRecognizedText(string text)
    {
        RecognitionEntry entry = transcript.Add(text, recognitionSource);
        if (sttTextBox.TextLength > 0 && !sttTextBox.Text.EndsWith('\n'))
        {
            sttTextBox.AppendText(Environment.NewLine);
        }

        sttTextBox.AppendText(entry.Text);
        sttTextBox.ScrollToCaret();
        statusLabel.Text = $"Tanındı: {entry.Text}";
    }

    private void AddResultRow(RecognitionEntry entry)
    {
        var item = new ListViewItem(entry.TimeText) { Tag = entry, ToolTipText = entry.Text };
        item.SubItems.Add(entry.Text);
        item.SubItems.Add(entry.Source);
        resultsList.Items.Add(item);
        item.EnsureVisible();
        UpdateResultsTitle();
    }

    private void UpdateResultsTitle()
    {
        resultsGroup.Text = $"Sonuçlar ({transcript.Count})";
        saveResultsButton.Enabled = transcript.Count > 0;
    }

    private const string TextFileFilter = "Metin dosyası (*.txt)|*.txt|Tüm dosyalar (*.*)|*.*";

    private void OpenTextFile()
    {
        using var dialog = new OpenFileDialog { Title = "Seslendirilecek metin dosyasını açın", Filter = TextFileFilter };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            string text = TextFileService.ReadText(dialog.FileName);
            if (text.Length > TextToSpeechService.MaxTextLength)
            {
                ShowWarning($"Dosyadaki metin çok uzun ({text.Length} karakter). En fazla {TextToSpeechService.MaxTextLength} karakter seslendirilebilir.");
                return;
            }

            ttsTextBox.Text = text;
            statusLabel.Text = $"{Path.GetFileName(dialog.FileName)} açıldı ({text.Length} karakter).";
        }
        catch (SpeechAppException ex)
        {
            ShowWarning(ex.Message);
        }
    }

    private void SaveTextFile()
    {
        if (string.IsNullOrWhiteSpace(ttsTextBox.Text))
        {
            ShowWarning("Kaydedilecek metin yok. Önce bir metin yazın.");
            return;
        }

        SaveWithDialog("Metni kaydet", "metin.txt", ttsTextBox.Text);
    }

    private void SaveResultsFile()
    {
        if (transcript.Count == 0)
        {
            ShowWarning("Kaydedilecek tanıma sonucu yok.");
            return;
        }

        SaveWithDialog("Tanıma sonuçlarını kaydet", $"tanima-sonuclari-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            TextFileService.BuildTranscriptDocument(transcript.Entries));
    }

    private void SaveWithDialog(string title, string defaultName, string content)
    {
        using var dialog = new SaveFileDialog { Title = title, Filter = TextFileFilter, FileName = defaultName, DefaultExt = "txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            TextFileService.WriteText(dialog.FileName, content);
            statusLabel.Text = $"Kaydedildi: {dialog.FileName}";
        }
        catch (SpeechAppException ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// Listede seçili sonuçlar varsa onları, yoksa tanınan metnin tamamını seslendirme sekmesine aktarır.
    /// </summary>
    private void SendToTts()
    {
        string text = resultsList.SelectedItems.Count > 0
            ? string.Join(Environment.NewLine, resultsList.SelectedItems.Cast<ListViewItem>().Select(i => ((RecognitionEntry)i.Tag!).Text))
            : sttTextBox.Text.Trim();

        if (text.Length == 0)
        {
            ShowWarning("Aktarılacak tanınmış metin yok.");
            return;
        }

        tts.Stop();
        ttsTextBox.Text = text;
        tabs.SelectedTab = ttsTab;
        ttsTextBox.Focus();
        statusLabel.Text = resultsList.SelectedItems.Count > 0
            ? $"{resultsList.SelectedItems.Count} seçili sonuç seslendirme sekmesine aktarıldı."
            : "Tanınan metin seslendirme sekmesine aktarıldı. '▶ Oku' ile dinleyebilirsiniz.";
    }

    private void UpdateSttState(RecognitionState state)
    {
        bool idle = state == RecognitionState.Idle;
        bool listening = state == RecognitionState.Listening;
        bool modelReady = modelManager.IsInstalled && !IsDownloading;

        listenButton.Enabled = idle && modelReady && microphoneCombo.Items.Count > 0;
        stopListenButton.Enabled = listening || state == RecognitionState.RecognizingFile;
        microphoneCombo.Enabled = idle;
        refreshMicrophonesButton.Enabled = idle;
        recognizeFileButton.Enabled = idle && modelReady;
        downloadModelButton.Enabled = idle && !IsDownloading;
        chooseModelButton.Enabled = idle && !IsDownloading;

        (sttIndicator.Text, sttIndicator.BackColor) = state switch
        {
            RecognitionState.LoadingModel => ("● YÜKLENİYOR", Color.DarkOrange),
            RecognitionState.Listening => ("● DİNLENİYOR", Color.Firebrick),
            RecognitionState.RecognizingFile => ("● TANINIYOR", Color.SteelBlue),
            _ => ("■ HAZIR", Color.DimGray)
        };

        if (idle)
        {
            levelBar.Value = 0;
            partialLabel.Text = string.Empty;
        }

        UpdateActiveOperation();
    }

    private bool IsDownloading => downloadCts is not null;

    private void UpdateModelStatus()
    {
        bool installed = modelManager.IsInstalled;
        if (installed)
        {
            modelStatusLabel.Text = $"Türkçe model hazır ✓  ({Path.GetFileName(modelManager.ModelPath)})";
            modelStatusLabel.ForeColor = Color.SeaGreen;
        }
        else
        {
            modelStatusLabel.Text = "Türkçe model yüklü değil. Konuşma tanıma için modeli indirin (~35 MB).";
            modelStatusLabel.ForeColor = Color.Firebrick;
        }

        downloadModelButton.Text = installed ? "Yeniden İndir" : "Modeli İndir";
        cancelDownloadButton.Enabled = IsDownloading;
        downloadProgress.Visible = IsDownloading;
        UpdateSttState(stt.State);
    }

    private async Task DownloadModelAsync()
    {
        if (IsDownloading)
        {
            return;
        }

        downloadCts = new CancellationTokenSource();
        downloadProgress.Value = 0;
        downloadProgress.Style = ProgressBarStyle.Continuous;
        UpdateModelStatus();
        modelStatusLabel.Text = "Model indiriliyor...";
        modelStatusLabel.ForeColor = Color.DarkOrange;
        statusLabel.Text = $"İndiriliyor: {VoskModelManager.DefaultModelUri}";

        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Percent is double percent)
            {
                downloadProgress.Value = Math.Clamp((int)percent, 0, 100);
                if (percent >= 100)
                {
                    downloadProgress.Style = ProgressBarStyle.Marquee;
                    modelStatusLabel.Text = "Model açılıyor...";
                    return;
                }
            }
            else
            {
                downloadProgress.Style = ProgressBarStyle.Marquee;
            }
            modelStatusLabel.Text = $"Model indiriliyor: {p}";
        });

        try
        {
            await modelManager.DownloadAsync(progress, downloadCts.Token);
            statusLabel.Text = $"Türkçe model indirildi: {modelManager.ModelPath}";
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "Model indirme iptal edildi.";
        }
        catch (SpeechAppException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            downloadCts.Dispose();
            downloadCts = null;
            if (!IsDisposed)
            {
                modelManager.Locate();
                UpdateModelStatus();
            }
        }
    }

    private void ChooseModelFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Vosk model klasörünü seçin (içinde final.mdl bulunan klasör)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            string folder = modelManager.UseFolder(dialog.SelectedPath);
            statusLabel.Text = $"Model klasörü seçildi: {folder}";
        }
        catch (SpeechAppException ex)
        {
            ShowWarning(ex.Message);
        }

        UpdateModelStatus();
    }

    private void UpdateActiveOperation()
    {
        var operations = new List<string>();
        switch (tts.State)
        {
            case SpeechPlaybackState.Preparing:
            case SpeechPlaybackState.Speaking:
                operations.Add("Metin okunuyor");
                break;
            case SpeechPlaybackState.Paused:
                operations.Add("Okuma duraklatıldı");
                break;
        }

        if (IsDownloading)
        {
            operations.Add("Model indiriliyor");
        }

        switch (stt.State)
        {
            case RecognitionState.LoadingModel:
                operations.Add("Tanıma modeli yükleniyor");
                break;
            case RecognitionState.Listening:
                operations.Add("Mikrofon dinleniyor");
                break;
            case RecognitionState.RecognizingFile:
                operations.Add("WAV dosyası yazıya çevriliyor");
                break;
        }

        activeOperationLabel.Text = operations.Count == 0
            ? "Aktif işlem: yok"
            : "Aktif işlem: " + string.Join(" + ", operations);
    }

    private void LoadVoices()
    {
        IReadOnlyList<VoiceOption> voices;
        try
        {
            voices = tts.GetVoices();
        }
        catch (Exception ex)
        {
            voices = Array.Empty<VoiceOption>();
            statusLabel.Text = $"Konuşma sesleri okunamadı: {ex.Message}";
        }

        voiceCombo.Items.Clear();
        foreach (VoiceOption voice in voices)
        {
            voiceCombo.Items.Add(voice);
        }

        if (voices.Count == 0)
        {
            speakButton.Enabled = false;
            statusLabel.Text = "Bilgisayarda yüklü konuşma sesi bulunamadı; seslendirme kullanılamıyor.";
            return;
        }

        voiceCombo.SelectedIndex = 0;
        statusLabel.Text = voices[0].IsTurkish
            ? $"{voices.Count} ses bulundu. Türkçe ses seçildi: {voices[0].DisplayName}"
            : $"Türkçe ses bulunamadı; {voices[0].DisplayName} kullanılacak.";
    }

    private async Task SpeakAsync()
    {
        try
        {
            await tts.SpeakAsync(ttsTextBox.Text, voiceCombo.SelectedItem as VoiceOption, rateTrackBar.Value / 10.0);
        }
        catch (SpeechAppException ex)
        {
            ShowWarning(ex.Message);
        }
    }

    private void UpdateTtsState(SpeechPlaybackState state)
    {
        bool idle = state == SpeechPlaybackState.Idle;
        bool paused = state == SpeechPlaybackState.Paused;
        speakButton.Enabled = idle && voiceCombo.Items.Count > 0;
        stopSpeakButton.Enabled = !idle;
        pauseButton.Enabled = state is SpeechPlaybackState.Speaking or SpeechPlaybackState.Paused;
        pauseButton.Text = paused ? "▶ Devam Et" : "❚❚ Duraklat";
        voiceCombo.Enabled = idle;
        rateTrackBar.Enabled = idle;
        clearTextButton.Enabled = idle;
        openTextButton.Enabled = idle;
        ttsTextBox.ReadOnly = !idle;

        (ttsIndicator.Text, ttsIndicator.BackColor) = state switch
        {
            SpeechPlaybackState.Preparing => ("● HAZIRLANIYOR", Color.DarkOrange),
            SpeechPlaybackState.Speaking => ("● OKUNUYOR", Color.SeaGreen),
            SpeechPlaybackState.Paused => ("❚❚ DURAKLATILDI", Color.Goldenrod),
            _ => ("■ HAZIR", Color.DimGray)
        };

        UpdateActiveOperation();

        if (state == SpeechPlaybackState.Speaking)
        {
            statusLabel.Text = $"Okunuyor: {(voiceCombo.SelectedItem as VoiceOption)?.DisplayName}, hız {rateLabel.Text}";
        }
        else if (idle)
        {
            ClearHighlight();
        }
    }

    private void HighlightWord(int start, int length)
    {
        if (tts.State == SpeechPlaybackState.Idle || start < 0 || start + length > ttsTextBox.TextLength)
        {
            return;
        }

        ClearHighlight();
        ttsTextBox.Select(start, length);
        ttsTextBox.SelectionBackColor = Color.Gold;
        ttsTextBox.Select(start, 0);
        ttsTextBox.ScrollToCaret();
        highlightStart = start;
        highlightLength = length;
    }

    private void ClearHighlight()
    {
        if (highlightStart < 0)
        {
            return;
        }

        if (highlightStart + highlightLength <= ttsTextBox.TextLength)
        {
            ttsTextBox.Select(highlightStart, highlightLength);
            ttsTextBox.SelectionBackColor = ttsTextBox.BackColor;
            ttsTextBox.Select(highlightStart, 0);
        }
        highlightStart = -1;
    }

    private void UpdateRateLabel()
    {
        rateLabel.Text = $"{rateTrackBar.Value / 10.0:0.0}x";
    }

    private void ShowWarning(string message)
    {
        statusLabel.Text = message.Split('\n')[0];
        MessageBox.Show(this, message, "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ShowError(string message)
    {
        statusLabel.Text = message.Split('\n')[0];
        MessageBox.Show(this, message, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Pencere kapanırken gelen olaylar yok sayılır.
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        downloadCts?.Cancel();
        fileCts?.Cancel();
        stt.Dispose();
        tts.Dispose();
        base.OnFormClosed(e);
    }
}
