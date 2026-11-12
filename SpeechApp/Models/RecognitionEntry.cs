namespace SpeechApp.Models;

/// <summary>
/// Tanınan tek bir cümle: ne zaman, hangi kaynaktan (mikrofon veya dosya) tanındığı ve metni.
/// </summary>
public sealed record RecognitionEntry(DateTime Time, string Source, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public override string ToString() => $"[{TimeText}] ({Source}) {Text}";
}
