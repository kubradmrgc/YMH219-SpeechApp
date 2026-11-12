using System.IO.Compression;
using SpeechApp.Models;

namespace SpeechApp.Services;

/// <summary>
/// Vosk konuşma tanıma modelinin bulunması, indirilmesi ve doğrulanmasından sorumludur.
/// Modeller %LOCALAPPDATA%\YMH219-SpeechApp\models altında tutulur.
/// </summary>
public sealed class VoskModelManager
{
    public const string DefaultModelName = "vosk-model-small-tr-0.3";
    public static readonly Uri DefaultModelUri = new("https://alphacephei.com/vosk/models/vosk-model-small-tr-0.3.zip");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly string appDataRoot;
    private readonly string customPathFile;

    public VoskModelManager(string? appDataRoot = null)
    {
        this.appDataRoot = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YMH219-SpeechApp");
        ModelsRoot = Path.Combine(this.appDataRoot, "models");
        customPathFile = Path.Combine(this.appDataRoot, "model-path.txt");
        Locate();
    }

    public string ModelsRoot { get; }

    public string DefaultModelPath => Path.Combine(ModelsRoot, DefaultModelName);

    /// <summary>Kullanılacak modelin klasörü; model yoksa null.</summary>
    public string? ModelPath { get; private set; }

    public bool IsInstalled => ModelPath is not null;

    /// <summary>Önce kullanıcının seçtiği klasöre, sonra varsayılan indirme klasörüne bakar.</summary>
    public bool Locate()
    {
        ModelPath = null;

        try
        {
            if (File.Exists(customPathFile))
            {
                string custom = File.ReadAllText(customPathFile).Trim();
                if (IsValidModelFolder(custom))
                {
                    ModelPath = custom;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ayar dosyası okunamazsa varsayılan klasörle devam edilir.
        }

        if (IsValidModelFolder(DefaultModelPath))
        {
            ModelPath = DefaultModelPath;
        }

        return IsInstalled;
    }

    /// <summary>
    /// Vosk modelinde akustik model (final.mdl) ve öznitelik ayarı (mfcc.conf) bulunur.
    /// Yeni modeller bunları am/ ve conf/ altında, eski modeller (ör. small-tr-0.3) kök klasörde tutar.
    /// </summary>
    public static bool IsValidModelFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        bool hasAcousticModel = File.Exists(Path.Combine(path, "am", "final.mdl"))
            || File.Exists(Path.Combine(path, "final.mdl"));
        bool hasFeatureConfig = File.Exists(Path.Combine(path, "conf", "mfcc.conf"))
            || File.Exists(Path.Combine(path, "mfcc.conf"));
        return hasAcousticModel && hasFeatureConfig;
    }

    /// <summary>
    /// Kullanıcının elle seçtiği model klasörünü doğrular ve kalıcı olarak kaydeder.
    /// Zip'ten çıkan üst klasör seçildiyse içindeki tek model klasörü kabul edilir.
    /// </summary>
    public string UseFolder(string path)
    {
        string? modelFolder = FindModelFolder(path);
        if (modelFolder is null)
        {
            throw new SpeechAppException(
                "Seçilen klasör geçerli bir Vosk modeli değil.\n" +
                "Klasörün içinde 'final.mdl' ve 'mfcc.conf' dosyaları (veya 'am' ve 'conf' klasörleri) bulunmalıdır.\n" +
                $"Seçilen klasör: {path}");
        }

        try
        {
            Directory.CreateDirectory(appDataRoot);
            File.WriteAllText(customPathFile, modelFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechAppException($"Model klasörü ayarı kaydedilemedi: {ex.Message}", ex);
        }

        ModelPath = modelFolder;
        return modelFolder;
    }

    /// <summary>Varsayılan Türkçe modeli indirir ve kurar.</summary>
    public async Task DownloadAsync(IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        await DownloadAsync(DefaultModelUri, progress, cancellationToken);
    }

    public async Task DownloadAsync(Uri source, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        string zipPath = Path.Combine(ModelsRoot, DefaultModelName + ".zip.part");

        try
        {
            Directory.CreateDirectory(ModelsRoot);

            using (HttpResponseMessage response = await Http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new SpeechAppException(
                        $"Model indirilemedi. Sunucu yanıtı: {(int)response.StatusCode} {response.ReasonPhrase}\nAdres: {source}");
                }

                long? total = response.Content.Headers.ContentLength;
                await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using FileStream output = File.Create(zipPath);

                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new DownloadProgress(received, total));
                }

                if (total is not null && received != total)
                {
                    throw new SpeechAppException("Model indirmesi yarıda kesildi. Lütfen tekrar deneyin.");
                }
            }

            await InstallFromZipAsync(zipPath, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or HttpIOException)
        {
            throw new SpeechAppException(
                "Model indirilemedi. İnternet bağlantınızı kontrol edin.\n" +
                $"Ayrıntı: {ex.Message}\n\n" +
                $"Modeli tarayıcıdan da indirebilirsiniz: {source}\nArdından zip'i açıp 'Klasör Seç...' ile gösterin.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechAppException("Model indirmesi zaman aşımına uğradı. İnternet bağlantınızı kontrol edin.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechAppException($"Model dosyası diske yazılamadı: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteFile(zipPath);
        }
    }

    /// <summary>
    /// İndirilmiş (veya elle verilmiş) model zip dosyasını açar ve varsayılan model klasörüne taşır.
    /// </summary>
    public async Task InstallFromZipAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        string extractDir = Path.Combine(ModelsRoot, "_extract_" + Guid.NewGuid().ToString("N"));

        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            string? modelFolder = FindModelFolder(extractDir) ?? throw new SpeechAppException(
                "İndirilen dosyada geçerli bir Vosk modeli bulunamadı ('final.mdl' veya 'mfcc.conf' eksik).");

            if (Directory.Exists(DefaultModelPath))
            {
                Directory.Delete(DefaultModelPath, recursive: true);
            }

            Directory.Move(modelFolder, DefaultModelPath);
        }
        catch (InvalidDataException ex)
        {
            throw new SpeechAppException("İndirilen model dosyası bozuk (geçerli bir zip değil). Lütfen tekrar indirin.", ex);
        }
        finally
        {
            TryDeleteDirectory(extractDir);
        }

        ModelPath = DefaultModelPath;
        TryDeleteFile(customPathFile);
    }

    private static string? FindModelFolder(string path)
    {
        if (IsValidModelFolder(path))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        return Directory.GetDirectories(path).FirstOrDefault(IsValidModelFolder);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Geçici dosya silinemezse bir sonraki indirmede üzerine yazılır.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Geçici klasör silinemezse sonraki çalıştırmada sorun çıkarmaz.
        }
    }
}
