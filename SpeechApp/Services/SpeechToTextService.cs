using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpeechApp.Models;
using Vosk;

namespace SpeechApp.Services;

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

    public event Action<string>? PartialResult;

    public event Action<string>? FinalResult;

    public event Action<float>? LevelChanged;

    public event Action<string>? Failed;

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

    public async Task<int> RecognizeFileAsync(string modelPath, string wavPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (State != RecognitionState.Idle)
        {
            throw new SpeechAppException("Dosyadan tanıma için önce mikrofon dinlemesini durdurun.");
        }

        if (!File.Exists(wavPath))
        {
            throw new SpeechAppException($"Ses dosyası bulunamadı:\n{wavPath}");
        }

        await LoadModelAsync(modelPath);
        SetState(RecognitionState.RecognizingFile);
        try
        {
            return await Task.Run(() => RecognizeFile(wavPath, progress, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or EndOfStreamException)
        {
            throw new SpeechAppException(
                $"Ses dosyası okunamadı: {Path.GetFileName(wavPath)}\n" +
                "Yalnızca sıkıştırılmamış (PCM veya IEEE float) WAV dosyaları desteklenir.\n" +
                $"Ayrıntı: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SpeechAppException($"Ses dosyası açılamadı: {ex.Message}", ex);
        }
        finally
        {
            SetState(RecognitionState.Idle);
        }
    }

    private int RecognizeFile(string wavPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(wavPath);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2)
        {
            samples = new StereoToMonoSampleProvider(samples);
        }
        else if (samples.WaveFormat.Channels != 1)
        {
            throw new SpeechAppException("Yalnızca mono veya stereo WAV dosyaları desteklenir.");
        }

        if (samples.WaveFormat.SampleRate != SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, SampleRate);
        }

        var pcm = new SampleToWaveProvider16(samples);
        using var fileRecognizer = new VoskRecognizer(model, SampleRate);
        byte[] buffer = new byte[SampleRate];
        int sentences = 0;
        int read;

        void Emit(string json)
        {
            string text = ReadText(json, "text");
            if (text.Length > 0)
            {
                sentences++;
                FinalResult?.Invoke(text);
            }
        }

        while ((read = pcm.Read(buffer.AsSpan())) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fileRecognizer.AcceptWaveform(buffer, read))
            {
                Emit(fileRecognizer.Result());
            }

            if (reader.Length > 0)
            {
                progress?.Report(Math.Min(100.0, reader.Position * 100.0 / reader.Length));
            }
        }

        Emit(fileRecognizer.FinalResult());
        return sentences;
    }

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

