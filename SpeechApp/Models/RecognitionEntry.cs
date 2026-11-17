namespace SpeechApp.Models;

public sealed record RecognitionEntry(DateTime Time, string Source, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public override string ToString() => $"[{TimeText}] ({Source}) {Text}";
}

