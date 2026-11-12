# YMH219 - Konuşma Uygulaması (Speech-to-Text / Text-to-Speech)

Nesne Tabanlı Programlama (YMH 219) dersi için Windows Forms ile geliştirilen, ses ve metin arasında çift yönlü dönüşüm yapan uygulama. Yazılan metin sesli okunur; mikrofondan alınan konuşma metne dönüştürülür.

> Proje geliştirme aşamasındadır. Şu an iki sekmeli arayüz iskeleti hazırdır.

## Kullanılan Teknolojiler

- C# / .NET 9, Windows Forms
- Windows.Media.SpeechSynthesis (metni seslendirme)
- [Vosk](https://alphacephei.com/vosk/) (internetsiz konuşma tanıma) - NuGet: `Vosk`
- [NAudio](https://github.com/naudio/NAudio) (mikrofondan ses alma) - NuGet: `NAudio`

## Çalıştırma Adımları

1. [.NET 9 SDK](https://dotnet.microsoft.com/download) kurulu olmalıdır (Windows 10/11, 64 bit).
2. Depoyu klonlayıp çalıştırın:
   ```bash
   git clone https://github.com/kubradmrgc/YMH219-SpeechApp.git
   cd YMH219-SpeechApp
   dotnet run --project SpeechApp
   ```

## Ekran Görüntüleri

<!-- Ekran görüntüleri daha sonra docs/screenshots klasörüne eklenecek. -->

## Öğrenci

- Ad Soyad: Kübra Demirgüç
- Öğrenci No: 230541115
