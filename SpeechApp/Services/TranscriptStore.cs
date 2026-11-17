using System.Globalization;
using SpeechApp.Models;

namespace SpeechApp.Services;

public sealed class TranscriptStore
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly List<RecognitionEntry> entries = new();

    public IReadOnlyList<RecognitionEntry> Entries => entries;

    public int Count => entries.Count;

    public event Action<RecognitionEntry>? EntryAdded;

    public event Action? Cleared;

    public RecognitionEntry Add(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Boş metin eklenemez.", nameof(text));
        }

        string trimmed = text.Trim();
        string sentence = char.ToUpper(trimmed[0], Turkish) + trimmed[1..];
        var entry = new RecognitionEntry(DateTime.Now, source, sentence);
        entries.Add(entry);
        EntryAdded?.Invoke(entry);
        return entry;
    }

    public void Clear()
    {
        entries.Clear();
        Cleared?.Invoke();
    }

    public string ToTimestampedText()
    {
        return string.Join(Environment.NewLine, entries.Select(e => e.ToString()));
    }
}

