namespace SpeechApp.Models;

/// <summary>
/// Sistemde yüklü bir konuşma sesi (ör. Microsoft Tolga, tr-TR).
/// </summary>
public sealed record VoiceOption(string Id, string DisplayName, string Language, string Gender)
{
    public bool IsTurkish => Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{DisplayName} ({Language}, {Gender})";
}
