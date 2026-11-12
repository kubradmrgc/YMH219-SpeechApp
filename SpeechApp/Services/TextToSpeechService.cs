using SpeechApp.Models;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace SpeechApp.Services;

/// <summary>
/// Windows konuşma sentezleyicisi ile metni ses akışına çevirir ve MediaPlayer ile çalar.
/// Olaylar arka plan iş parçacıklarından tetiklenebilir.
/// </summary>
public sealed class TextToSpeechService : IDisposable
{
    public const int MaxTextLength = 20_000;
    public const double MinRate = 0.5;
    public const double MaxRate = 2.0;

    private readonly SpeechSynthesizer synthesizer = new();
    private readonly MediaPlayer player = new() { AutoPlay = false };
    private SpeechSynthesisStream? currentStream;
    private int requestId;

    public TextToSpeechService()
    {
        synthesizer.Options.IncludeWordBoundaryMetadata = true;
        player.MediaEnded += (_, _) => OnPlaybackEnded();
        player.MediaFailed += (_, args) =>
        {
            SetState(SpeechPlaybackState.Idle);
            Failed?.Invoke($"Ses çalınamadı: {args.ErrorMessage}");
        };
    }

    public SpeechPlaybackState State { get; private set; } = SpeechPlaybackState.Idle;

    /// <summary>Çalma ses düzeyi (0 - 1).</summary>
    public double Volume
    {
        get => player.Volume;
        set => player.Volume = Math.Clamp(value, 0, 1);
    }

    public event Action<SpeechPlaybackState>? StateChanged;

    /// <summary>Okuma kendiliğinden (sona gelerek) bittiğinde tetiklenir.</summary>
    public event Action? Completed;

    /// <summary>Çalma sırasında oluşan hata; parametre kullanıcıya gösterilecek mesajdır.</summary>
    public event Action<string>? Failed;

    /// <summary>Bir kelime okunmaya başlandığında tetiklenir. Parametreler: metindeki başlangıç ve uzunluk.</summary>
    public event Action<int, int>? WordReached;

    public IReadOnlyList<VoiceOption> GetVoices()
    {
        return SpeechSynthesizer.AllVoices
            .Select(v => new VoiceOption(v.Id, v.DisplayName, v.Language, v.Gender == VoiceGender.Female ? "Kadın" : "Erkek"))
            .OrderByDescending(v => v.IsTurkish)
            .ThenBy(v => v.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// Metni seçilen ses ve hızla okumaya başlar. Geçersiz durumda <see cref="SpeechAppException"/> fırlatır.
    /// </summary>
    public async Task SpeakAsync(string text, VoiceOption? voice, double rate = 1.0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SpeechAppException("Okunacak metin boş. Lütfen önce bir metin yazın.");
        }

        if (text.Length > MaxTextLength)
        {
            throw new SpeechAppException($"Metin çok uzun. En fazla {MaxTextLength:N0} karakter okunabilir.");
        }

        Stop();
        int id = ++requestId;
        SetState(SpeechPlaybackState.Preparing);

        SpeechSynthesisStream stream;
        try
        {
            synthesizer.Voice = FindVoice(voice);
            synthesizer.Options.SpeakingRate = Math.Clamp(rate, MinRate, MaxRate);
            stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        }
        catch (SpeechAppException)
        {
            SetState(SpeechPlaybackState.Idle);
            throw;
        }
        catch (Exception ex)
        {
            SetState(SpeechPlaybackState.Idle);
            throw new SpeechAppException($"Metin sese dönüştürülemedi. Windows konuşma servisine erişilemiyor olabilir.\n\nAyrıntı: {ex.Message}", ex);
        }

        if (id != requestId)
        {
            // Hazırlık sürerken Durdur'a basıldı veya yeni bir okuma başlatıldı.
            stream.Dispose();
            return;
        }

        currentStream = stream;
        var item = new MediaPlaybackItem(MediaSource.CreateFromStream(stream, stream.ContentType));
        TrackWordBoundaries(item, id);
        player.Source = item;
        player.Play();
        SetState(SpeechPlaybackState.Speaking);
    }

    public void Pause()
    {
        if (State != SpeechPlaybackState.Speaking)
        {
            return;
        }

        player.Pause();
        SetState(SpeechPlaybackState.Paused);
    }

    public void Resume()
    {
        if (State != SpeechPlaybackState.Paused)
        {
            return;
        }

        player.Play();
        SetState(SpeechPlaybackState.Speaking);
    }

    public void Stop()
    {
        requestId++;
        player.Pause();
        player.Source = null;
        currentStream?.Dispose();
        currentStream = null;
        SetState(SpeechPlaybackState.Idle);
    }

    private void TrackWordBoundaries(MediaPlaybackItem item, int id)
    {
        void Register(uint index)
        {
            TimedMetadataTrack track = item.TimedMetadataTracks[(int)index];
            if (track.Id != "SpeechWord")
            {
                return;
            }

            item.TimedMetadataTracks.SetPresentationMode(index, TimedMetadataTrackPresentationMode.ApplicationPresented);
            track.CueEntered += (_, args) =>
            {
                if (id == requestId && args.Cue is SpeechCue { StartPositionInInput: int start, EndPositionInInput: int end })
                {
                    WordReached?.Invoke(start, end - start + 1);
                }
            };
        }

        for (uint i = 0; i < item.TimedMetadataTracks.Count; i++)
        {
            Register(i);
        }

        item.TimedMetadataTracksChanged += (_, args) =>
        {
            if (args.CollectionChange == Windows.Foundation.Collections.CollectionChange.ItemInserted)
            {
                Register(args.Index);
            }
        };
    }

    private static VoiceInformation FindVoice(VoiceOption? voice)
    {
        var voices = SpeechSynthesizer.AllVoices;
        if (voices.Count == 0)
        {
            throw new SpeechAppException("Bilgisayarda yüklü konuşma sesi bulunamadı. Windows Ayarlar > Zaman ve dil > Konuşma bölümünden bir ses ekleyin.");
        }

        return voices.FirstOrDefault(v => v.Id == voice?.Id) ?? SpeechSynthesizer.DefaultVoice ?? voices[0];
    }

    private void OnPlaybackEnded()
    {
        if (State == SpeechPlaybackState.Idle)
        {
            return;
        }

        SetState(SpeechPlaybackState.Idle);
        Completed?.Invoke();
    }

    private void SetState(SpeechPlaybackState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        Stop();
        player.Dispose();
        synthesizer.Dispose();
    }
}
