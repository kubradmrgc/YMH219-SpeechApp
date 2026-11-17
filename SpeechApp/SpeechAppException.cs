namespace SpeechApp;

public sealed class SpeechAppException : Exception
{
    public SpeechAppException(string message) : base(message)
    {
    }

    public SpeechAppException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

