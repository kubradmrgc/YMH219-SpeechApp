namespace SpeechApp.Models;

public readonly record struct DownloadProgress(long ReceivedBytes, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? ReceivedBytes * 100.0 / TotalBytes.Value : null;

    public override string ToString()
    {
        string received = $"{ReceivedBytes / 1024.0 / 1024.0:0.0} MB";
        return TotalBytes is > 0
            ? $"{received} / {TotalBytes.Value / 1024.0 / 1024.0:0.0} MB (%{Percent:0})"
            : received;
    }
}

