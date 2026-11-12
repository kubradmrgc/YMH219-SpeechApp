namespace SpeechApp.Models;

/// <summary>
/// Konuşma tanıma servisinin anlık durumu.
/// </summary>
public enum RecognitionState
{
    Idle,
    LoadingModel,
    Listening
}
