namespace SpeechApp.Models;

/// <summary>
/// Kayıt yapılabilecek bir ses giriş aygıtı (WinMM aygıt numarası ve adı).
/// </summary>
public sealed record MicrophoneInfo(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}
