namespace SpeechApp.Models;

public sealed record VoiceOption(string Id, string DisplayName, string Language, string Gender)
{
    public bool IsTurkish => Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{DisplayName} ({Language}, {Gender})";
}

