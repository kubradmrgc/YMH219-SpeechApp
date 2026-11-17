namespace SpeechApp.Models;

public sealed record MicrophoneInfo(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}

