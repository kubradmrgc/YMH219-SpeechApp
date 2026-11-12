namespace SpeechApp;

/// <summary>
/// Metni sesli okuma (Text-to-Speech) ve konuşmayı yazıya çevirme (Speech-to-Text) penceresi.
/// </summary>
public partial class MainForm : Form
{
    public MainForm()
    {
        BuildLayout();

        ttsTextBox.TextChanged += (_, _) => charCountLabel.Text = $"{ttsTextBox.TextLength} karakter";
        rateTrackBar.ValueChanged += (_, _) => UpdateRateLabel();
        UpdateRateLabel();

        pauseButton.Enabled = false;
        stopSpeakButton.Enabled = false;
        cancelDownloadButton.Enabled = false;
        stopListenButton.Enabled = false;
        sendToTtsButton.Enabled = false;
        saveResultsButton.Enabled = false;
    }

    private void UpdateRateLabel()
    {
        rateLabel.Text = $"{rateTrackBar.Value / 10.0:0.0}x";
    }
}
