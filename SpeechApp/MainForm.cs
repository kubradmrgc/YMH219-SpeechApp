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
    private CancellationTokenSource? downloadCts;
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

        cancelDownloadButton.Enabled = false;
        stopListenButton.Enabled = false;
        sendToTtsButton.Enabled = false;
        saveResultsButton.Enabled = false;

        LoadVoices();
        UpdateTtsState(SpeechPlaybackState.Idle);
        UpdateModelStatus();
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
        downloadModelButton.Enabled = !IsDownloading;
        chooseModelButton.Enabled = !IsDownloading;
        cancelDownloadButton.Enabled = IsDownloading;
        downloadProgress.Visible = IsDownloading;

        listenButton.Enabled = installed && !IsDownloading;
        recognizeFileButton.Enabled = installed && !IsDownloading;
        UpdateActiveOperation();
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
        tts.Dispose();
        base.OnFormClosed(e);
    }
}
