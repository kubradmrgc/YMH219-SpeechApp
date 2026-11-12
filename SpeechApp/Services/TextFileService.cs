using System.Text;
using SpeechApp.Models;

namespace SpeechApp.Services;

/// <summary>
/// Seslendirilecek metni ve tanıma sonuçlarını .txt dosyası olarak okur/yazar.
/// </summary>
public static class TextFileService
{
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static TextFileService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Metin dosyasını okur. UTF-8 olarak çözülemeyen dosyalar Türkçe Windows kodlamasıyla (1254) okunur.
    /// </summary>
    public static string ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new SpeechAppException($"Dosya bulunamadı:\n{path}");
            }

            if (info.Length > MaxFileBytes)
            {
                throw new SpeechAppException($"Dosya çok büyük ({info.Length / 1024} KB). En fazla {MaxFileBytes / 1024} KB'lık metin dosyaları açılabilir.");
            }

            byte[] bytes = File.ReadAllBytes(path);
            ReadOnlySpan<byte> preamble = Encoding.UTF8.Preamble;
            if (bytes.AsSpan().StartsWith(preamble))
            {
                return Normalize(Encoding.UTF8.GetString(bytes, preamble.Length, bytes.Length - preamble.Length));
            }

            try
            {
                return Normalize(StrictUtf8.GetString(bytes));
            }
            catch (DecoderFallbackException)
            {
                return Normalize(Encoding.GetEncoding(1254).GetString(bytes));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechAppException($"Dosya okunamadı: {ex.Message}", ex);
        }
    }

    /// <summary>Metni UTF-8 (BOM'lu) olarak kaydeder; Türkçe karakterler Not Defteri'nde de doğru görünür.</summary>
    public static void WriteText(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text.ReplaceLineEndings(Environment.NewLine), Utf8WithBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechAppException(
                $"Dosya kaydedilemedi: {ex.Message}\nDosya başka bir programda açık olabilir veya klasöre yazma izniniz olmayabilir.", ex);
        }
    }

    /// <summary>Tanıma sonuçlarını başlık ve zaman damgalarıyla birlikte metne dönüştürür.</summary>
    public static string BuildTranscriptDocument(IReadOnlyList<RecognitionEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("YMH219 Konuşma Uygulaması - Tanıma Sonuçları");
        builder.AppendLine($"Kayıt tarihi: {DateTime.Now:dd.MM.yyyy HH:mm}");
        builder.AppendLine($"Cümle sayısı: {entries.Count}");
        builder.AppendLine(new string('-', 50));
        foreach (RecognitionEntry entry in entries)
        {
            builder.AppendLine(entry.ToString());
        }

        return builder.ToString();
    }

    private static string Normalize(string text) => text.ReplaceLineEndings("\n");
}
