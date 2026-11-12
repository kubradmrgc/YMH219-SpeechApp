using SpeechApp.Models;
using SpeechApp.Services;

namespace SpeechApp;

/// <summary>
/// Metni sesli okuma (Text-to-Speech) ve konuşmayı yazıya çevirme (Speech-to-Text) penceresi.
/// </summary>
public partial class MainForm : Form
{
    private readonly TextToSpeechService tts = new();

    public MainForm()
    {
        BuildLayout();

        ttsTextBox.TextChanged += (_, _) => charCountLabel.Text = $"{ttsTextBox.TextLength} karakter";
        rateTrackBar.ValueChanged += (_, _) => UpdateRateLabel();
        UpdateRateLabel();

        speakButton.Click += async (_, _) => await SpeakAsync();
        stopSpeakButton.Click += (_, _) => tts.Stop();
        clearTextButton.Click += (_, _) =>
        {
            tts.Stop();
            ttsTextBox.Clear();
            ttsTextBox.Focus();
        };

        tts.StateChanged += state => RunOnUi(() => UpdateTtsState(state));
        tts.Completed += () => RunOnUi(() => statusLabel.Text = "Okuma tamamlandı.");
        tts.Failed += message => RunOnUi(() => ShowError(message));

        pauseButton.Enabled = false;
        cancelDownloadButton.Enabled = false;
        stopListenButton.Enabled = false;
        sendToTtsButton.Enabled = false;
        saveResultsButton.Enabled = false;

        LoadVoices();
        UpdateTtsState(SpeechPlaybackState.Idle);
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
            await tts.SpeakAsync(ttsTextBox.Text, voiceCombo.SelectedItem as VoiceOption);
        }
        catch (SpeechAppException ex)
        {
            ShowWarning(ex.Message);
        }
    }

    private void UpdateTtsState(SpeechPlaybackState state)
    {
        bool idle = state == SpeechPlaybackState.Idle;
        speakButton.Enabled = idle && voiceCombo.Items.Count > 0;
        stopSpeakButton.Enabled = !idle;
        voiceCombo.Enabled = idle;

        (ttsIndicator.Text, ttsIndicator.BackColor) = state switch
        {
            SpeechPlaybackState.Preparing => ("● HAZIRLANIYOR", Color.DarkOrange),
            SpeechPlaybackState.Speaking => ("● OKUNUYOR", Color.SeaGreen),
            _ => ("■ HAZIR", Color.DimGray)
        };

        activeOperationLabel.Text = idle ? "Aktif işlem: yok" : "Aktif işlem: Metin okunuyor";
        if (state == SpeechPlaybackState.Speaking)
        {
            statusLabel.Text = $"Okunuyor: {(voiceCombo.SelectedItem as VoiceOption)?.DisplayName}";
        }
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
        tts.Dispose();
        base.OnFormClosed(e);
    }
}
