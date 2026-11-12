namespace SpeechApp.Models;

/// <summary>
/// Seslendirme servisinin durumu.
/// </summary>
public enum SpeechPlaybackState
{
    /// <summary>Okuma yapılmıyor.</summary>
    Idle,
    /// <summary>Metin ses akışına dönüştürülüyor.</summary>
    Preparing,
    /// <summary>Ses çalınıyor.</summary>
    Speaking,
    /// <summary>Okuma duraklatıldı; kaldığı yerden devam edebilir.</summary>
    Paused
}
