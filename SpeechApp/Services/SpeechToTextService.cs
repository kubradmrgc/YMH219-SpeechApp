using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SpeechApp.Models;
using Vosk;

namespace SpeechApp.Services;

/// <summary>
/// Mikrofondan alınan sesi Vosk ile çevrimdışı olarak yazıya çevirir.
/// Ses NAudio ile 16 kHz, 16 bit, mono olarak kaydedilir ve parça parça tanıyıcıya verilir.
/// </summary>
public sealed class SpeechToTextService : IDisposable
{
    public const int SampleRate = 16000;
    private static readonly TimeSpan SilenceCheckDelay = TimeSpan.FromSeconds(4);

    private readonly object sync = new();
    private Model? model;
    private string? loadedModelPath;
    private VoskRecognizer? recognizer;
    private WaveIn? waveIn;
    private System.Threading.Timer? silenceTimer;
    private bool listening;
    private bool audioReceived;
    private string lastPartial = string.Empty;

    static SpeechToTextService()
    {
        Vosk.Vosk.SetLogLevel(-1);
    }

    public RecognitionState State { get; private set; } = RecognitionState.Idle;

    public event Action<RecognitionState>? StateChanged;

    /// <summary>Konuşma sürerken değişen ara metin.</summary>
    public event Action<string>? PartialResult;

    /// <summary>Bir cümle (sessizlikle biten bölüm) kesinleştiğinde gelen metin.</summary>
    public event Action<string>? FinalResult;

    /// <summary>0-1 arası anlık ses seviyesi.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Dinlemeyi durduran hatalar (ör. mikrofon çıkarıldı).</summary>
    public event Action<string>? Failed;

    /// <summary>Dinleme sürerken kullanıcıya gösterilecek uyarılar (ör. mikrofondan ses gelmiyor).</summary>
    public event Action<string>? Warning;

    public static IReadOnlyList<MicrophoneInfo> GetMicrophones()
    {
        IReadOnlyList<string> fullNames = GetCaptureEndpointNames();
        var microphones = new List<MicrophoneInfo>();
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            string shortName = WaveIn.GetCapabilities(i).ProductName;
            string name = fullNames.FirstOrDefault(n => n.StartsWith(shortName, StringComparison.OrdinalIgnoreCase)) ?? shortName;
            microphones.Add(new MicrophoneInfo(i, name));
        }

        return microphones;
    }

    /// <summary>
    /// WinMM aygıt adlarını 31 karakterde keser; tam adlar Windows ses uç noktalarından alınır.
    /// </summary>
    private static IReadOnlyList<string> GetCaptureEndpointNames()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(device =>
                {
                    using (device)
                    {
                        return device.FriendlyName;
                    }
                })
                .ToList();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Vosk modelini belleğe yükler. Model büyük olduğundan arka planda yüklenir ve önbellekte tutulur.
    /// </summary>
    public async Task LoadModelAsync(string modelPath)
    {
        if (model is not null && loadedModelPath == modelPath)
        {
            return;
        }

        if (!VoskModelManager.IsValidModelFolder(modelPath))
        {
            throw new SpeechAppException("Konuşma tanıma modeli bulunamadı. Lütfen önce Türkçe modeli indirin veya model klasörünü seçin.");
        }

        RecognitionState previous = State;
        SetState(RecognitionState.LoadingModel);
        try
        {
            Model loaded = await Task.Run(() => new Model(modelPath));
            if (!IsLoaded(loaded))
            {
                loaded.Dispose();
                throw new SpeechAppException($"Konuşma tanıma modeli yüklenemedi; model dosyaları bozuk olabilir.\nKlasör: {modelPath}\nModeli yeniden indirmeyi deneyin.");
            }

            model?.Dispose();
            model = loaded;
            loadedModelPath = modelPath;
        }
        finally
        {
            SetState(previous);
        }
    }

    public async Task StartAsync(string modelPath, MicrophoneInfo? microphone)
    {
        if (State != RecognitionState.Idle)
        {
            return;
        }

        if (WaveIn.DeviceCount == 0)
        {
            throw new SpeechAppException(
                "Bilgisayarda mikrofon bulunamadı.\nBir mikrofon takıp 'Yenile' düğmesine basın.");
        }

        if (microphone is null || microphone.DeviceNumber >= WaveIn.DeviceCount)
        {
            throw new SpeechAppException("Lütfen listeden bir mikrofon seçin. Liste güncel değilse 'Yenile' düğmesine basın.");
        }

        await LoadModelAsync(modelPath);

        var input = new WaveIn
        {
            DeviceNumber = microphone.DeviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 100
        };
        input.DataAvailable += OnDataAvailable;
        input.RecordingStopped += OnRecordingStopped;

        lock (sync)
        {
            recognizer = new VoskRecognizer(model, SampleRate);
            lastPartial = string.Empty;
            audioReceived = false;
            listening = true;
        }

        try
        {
            input.StartRecording();
        }
        catch (Exception ex)
        {
            input.DataAvailable -= OnDataAvailable;
            input.RecordingStopped -= OnRecordingStopped;
            input.Dispose();
            ReleaseRecognizer();
            throw new SpeechAppException(
                $"Mikrofon açılamadı ({microphone.Name}).\n" +
                "Mikrofon başka bir uygulama tarafından kullanılıyor ya da bağlantısı kesilmiş olabilir.\n" +
                $"Ayrıntı: {ex.Message}", ex);
        }

        waveIn = input;
        silenceTimer = new System.Threading.Timer(_ => CheckSilence(), null, SilenceCheckDelay, Timeout.InfiniteTimeSpan);
        SetState(RecognitionState.Listening);
    }

    /// <summary>Dinlemeyi durdurur; son söylenen bölüm de kesin sonuç olarak bildirilir.</summary>
    public void Stop()
    {
        if (State != RecognitionState.Listening)
        {
            return;
        }

        StopInput();
        string final = ReleaseRecognizer();
        if (final.Length > 0)
        {
            FinalResult?.Invoke(final);
        }

        LevelChanged?.Invoke(0);
        SetState(RecognitionState.Idle);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        float level = PeakLevel(e.Buffer, e.BytesRecorded);
        string? final = null;
        string? partial = null;

        lock (sync)
        {
            if (!listening || recognizer is null)
            {
                return;
            }

            if (level > 0)
            {
                audioReceived = true;
            }

            if (recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
            {
                final = ReadText(recognizer.Result(), "text");
                lastPartial = string.Empty;
            }
            else
            {
                string current = ReadText(recognizer.PartialResult(), "partial");
                if (current != lastPartial)
                {
                    lastPartial = current;
                    partial = current;
                }
            }
        }

        LevelChanged?.Invoke(level);
        if (final is not null)
        {
            PartialResult?.Invoke(string.Empty);
            if (final.Length > 0)
            {
                FinalResult?.Invoke(final);
            }
        }
        else if (partial is not null)
        {
            PartialResult?.Invoke(partial);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || State != RecognitionState.Listening)
        {
            return;
        }

        StopInput();
        ReleaseRecognizer();
        LevelChanged?.Invoke(0);
        SetState(RecognitionState.Idle);
        Failed?.Invoke($"Mikrofon kaydı beklenmedik şekilde durdu; mikrofon çıkarılmış olabilir.\nAyrıntı: {e.Exception.Message}");
    }

    private void CheckSilence()
    {
        bool silent;
        lock (sync)
        {
            silent = listening && !audioReceived;
        }

        if (silent)
        {
            Warning?.Invoke(
                "Mikrofondan hiç ses gelmiyor.\n" +
                "Windows mikrofon erişimini engelliyor olabilir: Ayarlar > Gizlilik ve güvenlik > Mikrofon bölümünden " +
                "'Masaüstü uygulamalarının mikrofonunuza erişmesine izin ver' seçeneğini açın.\n" +
                "Mikrofonun sessize alınmadığını ve doğru aygıtın seçildiğini de kontrol edin.");
        }
    }

    private void StopInput()
    {
        lock (sync)
        {
            listening = false;
        }

        silenceTimer?.Dispose();
        silenceTimer = null;

        if (waveIn is not null)
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.StopRecording();
            waveIn.Dispose();
            waveIn = null;
        }
    }

    private string ReleaseRecognizer()
    {
        lock (sync)
        {
            listening = false;
            if (recognizer is null)
            {
                return string.Empty;
            }

            string final = ReadText(recognizer.FinalResult(), "text");
            recognizer.Dispose();
            recognizer = null;
            return final;
        }
    }

    private void SetState(RecognitionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    private static float PeakLevel(byte[] buffer, int count)
    {
        int peak = 0;
        for (int i = 0; i + 1 < count; i += 2)
        {
            int sample = Math.Abs((int)BitConverter.ToInt16(buffer, i));
            if (sample > peak)
            {
                peak = sample;
            }
        }

        return Math.Min(1f, peak / 32768f);
    }

    private static string ReadText(string json, string property)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out JsonElement value)
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// Vosk, model dosyaları okunamazsa istisna atmak yerine boş tanıtıcı döndürür; bunu yakalamak gerekir.
    /// </summary>
    private static bool IsLoaded(Model loaded)
    {
        var field = typeof(Model).GetField("handle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(loaded) is not System.Runtime.InteropServices.HandleRef handle || handle.Handle != IntPtr.Zero;
    }

    public void Dispose()
    {
        if (State == RecognitionState.Listening)
        {
            StopInput();
            ReleaseRecognizer();
        }

        model?.Dispose();
        model = null;
    }
}
