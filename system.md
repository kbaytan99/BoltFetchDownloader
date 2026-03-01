# BoltFetch Downloader - Derinlemesine Sistem Analizi ve Kod Mimarisi

Bu doküman, sistemdeki her bir dosyanın kod bazlı, sınıf sınıf, metod seviyesinde incelemesi ve arka planda akan verinin tam teknik altyapısını açıklamaktadır.

---

## 🏗️ 1. Modeller (Data & Konfigürasyon)

### `Models/UserSettings.cs` & `SettingsService.cs`
Sistemin beyninin yapılandırıldığı yerdir.
- **`UserSettings` Sınıfı**: `DownloadPath` (Varsayılan: `UserProfile\Downloads\BoltFetch`), `SpeedLimitKB` (Varsayılan: 0, limitsiz), `MaxParallelDownloads` (Varsayılan: 3), `SegmentsPerFile` (Varsayılan: 4 parçaya bölme) özelliklerini tutar. Ek olarak uygulanan çoklu dil desteği için `Language` (Varsayılan: "en") özelliğini de artık kendi içinde barındırır.
- **`SettingsService` Sınıfı**: `Newtonsoft.Json` kütüphanesini kullanarak bu ayarları `AppDomain` ana dizinindeki `settings.json` dosyasına şifrelemeden, okunabilir bir şekilde (`Formatting.Indented`) okur (`Load`) ve üstüne yazar (`Save`). 

### `Locales/en.xaml` (Çoklu Dil Altyapısı)
Projede Hard-Coded (koda gömülü) yazıları kaldırmak için WPF'in Native (Yerel) `ResourceDictionary` mimarisi kurulmuştur.
- Tüm statik yazılar (Butonlar, Sütun isimleri, Uyarılar) bu XAML dosyasına Key-Value (Örn: `Loc_AppTitle`) olarak girilir.
- `App.xaml` başlarken bu dosyayı `MergedDictionaries` içine yükleyip tüm pencerelere dağıtır (`{DynamicResource}`). Gelecekte Türkçe gibi yeni bir dil ekleneceğinde sadece bu belgenin kopyalanıp çevrilmesi yeterlidir. Koda dokunulmaz.

### `Models/IDownloadProvider.cs`
Tüm servis sağlayıcıların (hiyerarşik olarak) izlemesi gereken arabirimdir (Interface). Sisteme yeni bir indirme sitesi ekleneceği zaman bu sözleşmeyi uygulamak zorundadır:
- `string Name { get; }` (Platform adı, örn: "GoFile")
- `bool CanHandle(string url)` (Verilen URL'nin bu servise ait olup olmadığını söyler)
- `Task<List<GoFileItem>> FetchFilesAsync(string url)` (URL'den dosyaları çekme algoritması)

### `Models/GoFileService.cs` (Akıllı Entegrasyon Sistemi)
Sistemin belkemiği ve dış ağla (GoFile.iovb) güvenli el sıkışan modüldür. "Smart Token" (Akıllı jeton) mekanizmasına sahiptir.
- **Akıllı Yeniden Deneme (Smart Retry & Token Recycling)**: Her indirme işleminde gereksiz yere yeni misafir oturumu (Guest Token) açıp banlanmamak için mevcut token'i hafızaya alır (`_guestToken`). Eğer Cloudflare korumasına takılırsa ya da API `401 Unauthorized`/`error-auth` hatası döndürürse sistemi çökertmez. Hatalı jetonu silip `Exponential Backoff` (artan aralıklarla) mantığı ile 2 defa baştan yaratıp sessizce kurtarır.
- **User-Agent & Header Koruması**: API'nin bot olarak algılamaması için otomatik olarak Chrome 121 Windows 10 taklidi (`Mozilla/5.0...`) yapar. Ayrıca `Origin` ve `Referer` başlıklarını ayarlayarak tarayıcıdan giriliyormuş hissi verir.
- **`CanHandle(string url)`**: Sadece içinde `"gofile.io/d/"` geçen URL'leri kabul eder.
- **`FetchFilesAsync` Algoritması**: Gelen URL'yi `@"^.*/d/([a-zA-Z0-9]+)"` şeklindeki Regex ile tarar ve klasör ID'sini (Token'ı) ayırır. Son olarak `GetFolderContents` aracılığıyla JSON formatında gerçek dosya direkt linklerini çeker ve `GoFileItem` objelerine dönüştürür.

---

## 🕵️ 2. Servisler (Arka Plan Dinleyicileri ve Yöneticiler)

### `Services/ClipboardMonitor.cs` (Pano Tarayıcısı)
Asla uyumayan bir sızıntı tespit ve panoya kopyalama aracıdır.
- `DispatcherTimer` kullanır ve her **1.5 saniyede bir** (Tick süresi) uyanır.
- `System.Windows.Clipboard.GetText()` ile metni alır (Erişim engeli/başka araç tarafından kilitlenme durumlarına karşı Try/Catch içindedir).
- Çekilen metni `@"https://gofile\.io/d/[a-zA-Z0-9]+"` Regex'i ile tarar. Metin bir önceki döngüde yakalanan metinle aynıysa (`_lastCapturedText == text`) tepki vermez/spam engeller.
- Yeni bir uygun link bulursa, `LinksDetected` event'ini tetikleyerek veriyi (array olarak) ana ekrana fırlatır.

### `Services/DownloadOrchestrator.cs` (Eşzamanlılık ve Sıra Yönetimi)
Kuyruğa alınmış veri patlamalarını önleyen akış denetleyicisidir (Traffic Controller).
- `ProcessQueueAsync` methodu bir `while (true)` döngüsüne girer ama sistemi kitlememek için uykuya yatar (`Task.Delay(1000)` her 1 saniyede).
- Verilen listedeki (DataGrid listesi) durumu `"Queued"` (sırada) olan tüm dosyaları filtreler.
- `_settings.MaxParallelDownloads - activeCount` matematiği ile o an boşta kaç indirme yuvası (slot) olduğunu hesaplar. Yer yoksa `continue` diyip bir saniye daha bekler.
- Eğer yer varsa (slotsAvailable > 0), kuyruktaki dosyanın durumunu `"Downloading..."` olarak günceller ve Task'i başlatması için ateşi **DownloadManager**'a verir (`StartItemDownload`).

### `Services/NotificationService.cs` (UI Uyarıları)
Diğer arka plan thread'lerinin ana ekran UI'sine kilitlenmeden, kendi bağımsız görsel pencerelerini açmaları için yazılmıştır.
- WPF dispatcher'ı (`Application.Current.Dispatcher.Invoke`) üzerinden, Thread çakışmalarını engelleyerek `NotificationWindow` class'ını somutlaştırır ve `Show()` diyerek sağ alttan kayan şık animasyonlu bildirim widget'larını renderlar.

---

## 🚀 3. Çekirdek Motor: `Models/DownloadManager.cs`

İnternetten bayt bayt dosya söküp alan, uygulamanın en karmaşık ama en sağlam (Robust) sistemidir.
- **Parçalara Bölme (`CheckRangeSupport` & `DownloadSegmentedAsync`)**:
  - Önce sunucuya sadece başlık (Headers) isteği gönderir ve `Accept-Ranges: bytes` dönüp dönmediğine bakar.
  - Eğer sunucu destekliyorsa, dosya boyutunu kullanıcının girdiği `SegmentsPerFile` ayarına böler. (Örn 100 MB / 4 = 25'er MB).
  - Her bir 25 MB'lık kısım için kendi Thread'ini ve kendi HttpClient Request'ini (Ağ İsteği) yaratır `RangeHeaderValue(from, to)`.
  - İndirilen verileri diske `dosya.txt.part1`, `dosya.txt.part2` gibi ayrı ayrı yazar.
- **Hız Kısıtlayıcı (`ThrottleInstant`)**: Kullanıcı 1 MB/s limiti koyduysa, `DownloadProgress.UpdateInstantSpeed` sayacı ile o anki indirme hızını ölçer. Sınırı aştığını farkederse, ağdan veri çekme döngüsü içine anlık `Task.Delay` koyarak (`Thread.Sleep` mantığıyla) yapay olarak trafiği boğar ve hızı limitin altına çeker.
- **Birleştirme (`MergeSegmentsAsync`)**: Tüm part'ların indirme yüzdesi 100'e ulaşınca bir `FileStream` açar, sırasıyla tüm `.partX` dosyalarını ana dosyanın içine akıtır. Akıtma bitince çöp `.part` dosyalarını yok eder.
- **Tek Parça İndirme (`DownloadSingleStreamAsync`)**: Eğer sunucu "Range" desteklemiyorsa sistemi bozmaz, C#'ın `CopyToAsync` metodunu modifiye ederek ('CopyWithReporting' döngüsü yaratarak), klasik bir şekilde baştan sona tek parça olarak çeker ve anlık yüzde hesaplamasını o şekilde yapar.
- **Akıllı Yeniden Deneme (Retry Mechanism)**: GoFile sunucuları aşırı yoğun istek (429 Too Many Requests) hatası fırlattığında sistemi iptal etmek yerine, `Polly` benzeri bir yaklaşımla, Thread'i 2 ile 5 saniye arası (`Task.Delay`) uyutup **maksimum 5 kereye kadar** indirmeyi sürdürme/yeniden deneme algoritması çalıştırır.

---

## 🖥 4. Kullanıcı Arayüzü: `MainWindow.xaml.cs` (Orkestrasyonun Vitrini)

* `MainWindow` oluşturulur oluşturulmaz `InitializeComponent()` ile kendini çizer ve bellek üzerinde `SettingsService` ile son ayarları çeker. 
* Kendi içinde tuttuğu bir `ObservableCollection<FileDisplayItem>` kümesi (Listesi) vardır. Ekranda kullanıcıya gösterilen liste tam olarak bu koleksiyonun yansımasıdır (DataBinding). Her "Queued" veya "%45" değişikliğinde, `INotifyPropertyChanged` sayesinde ekran anında güncellenir.
* **Başlık Çubuğu (`WindowChrome`)**: Klasik Windows penceresinden sıkılıp, özel minimize etme (`MinimizeBtn_Click`), kapatma (`CloseBtn_Click`) işlevli kendi çizilmiş butonları vardır. Kapatma düğmesine "Sistemi Tepsisine (System Tray) Küçült" fonksiyonu bağlanmıştır. Ayrıca program ilk açılışta `1200x850` çözünürlüğünde tam ekranın ortasında başlatılır (`CenterScreen`).
* **Sistem Tepsisi (Context Menu Geliştirmesi)**: Tepsiden Ayarlar ve Hakkında kısımlarına doğrudan ulaşılabilir.
* **Akıllı DataGrid (Satır Seçimi)**: `MainWindow`'daki tablodaki satırlar `Select All` ile seçildiğinde, WPF'in native görünmez yazı sorununu çözen özel `DataGridCell` Foreground (Beyaz Renk) Setter Trigger'ı kullanılmıştır.
* **Link Ekleme (`AddLinkWindow.xaml`)**: Temiz bir metin panosudur.
* **Hakkında Ekranı (`AboutWindow.xaml`)**: Projenin açık kaynak vitrinidir. Artık sadece GoFile değil "Modern Multi-Provider" mimarisine uygun yazılar içerir.
* **Ayarlar ve Servis Paneli (`SettingsWindow.xaml`)**: Klasik klasör ve hız ayarları dışında, artık sistemdeki modüllerin/eklentilerin sağlığının göründüğü "Active Providers (Aktif Sağlayıcılar)" izleme bloğunu da barındırmaktadır.

--- 

Özet olarak sistem; *Gözlemci (Clipboard)* -> *Parser (GoFile API)* -> *Hakem (Orchestrator)* -> *Motor (DownloadManager)* silsilesi ile, WPF'in modern animasyonları (NotificationWindow & Custom Chrome) ve Asenkron (`async/await`) Thread yapısı üstüne inşa edilmiş kusursuz bir makine çarkı gibi tıkır tıkır işlemektedir. Mevcut durumda da aktif olarak sadece GoFile mimarisine %100 uyumludur.

---

## 🏃 5. Nasıl Çalıştırılır?

Bu projeyi derleyip çalıştırmak oldukça basittir:

1. **Visual Studio'da Açma:**
   - Proje dizinindeki `BoltFetchDownloader.csproj` dosyasına çift tıklayarak projeyi Visual Studio (veya Rider) ile açın.
2. **Derleme (Build) ve Çalıştırma:**
   - Visual Studio üst menüsünden **"Start (Başlat)"** butonuna veya klavyeden **`F5`** tuşuna basın.
   - Proje derlenecek ve ana pencere otomatik olarak açılacaktır.
3. **Kullanım:**
   - Program açıkken veya sistem tepsisinde arka planda çalışırken, tarayıcınızdan bir GoFile linki kopyalayın (`Ctrl+C`).
   - Program bunu otomatik yakalayacak ve ana ekrandaki listeye ekleyecektir. "İndir" simgesine basarak veya ayarlardan limitsiz indirme kuyruğu oluşturarak işlemleri seyredebilirsiniz!
