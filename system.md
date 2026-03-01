# BoltFetch Downloader - Derinlemesine Sistem Analizi ve Kod Mimarisi

Bu doküman, sistemdeki her bir dosyanın kod bazlı, sınıf sınıf, metod seviyesinde incelemesi ve arka planda akan verinin tam teknik altyapısını açıklamaktadır.

---

## 🏗️ 1. Modeller (Data & Konfigürasyon)

### `Models/UserSettings.cs` & `SettingsService.cs`
Sistemin beyninin yapılandırıldığı yerdir.
- **`UserSettings` Sınıfı**: `DownloadPath` (Varsayılan: `UserProfile\Downloads\BoltFetch`), `SpeedLimitKB` (Varsayılan: 0, limitsiz), `MaxParallelDownloads` (Varsayılan: 3) ve `SegmentsPerFile` (Varsayılan: 4 parçaya bölme) özelliklerini tutar.
- **`SettingsService` Sınıfı**: `Newtonsoft.Json` kütüphanesini kullanarak bu ayarları `AppDomain` ana dizinindeki `settings.json` dosyasına şifrelemeden, okunabilir bir şekilde (`Formatting.Indented`) okur (`Load`) ve üstüne yazar (`Save`). 

### `Models/IDownloadProvider.cs`
Tüm servis sağlayıcıların (hiyerarşik olarak) izlemesi gereken arabirimdir (Interface). Sisteme yeni bir indirme sitesi ekleneceği zaman bu sözleşmeyi uygulamak zorundadır:
- `string Name { get; }` (Platform adı, örn: "GoFile")
- `bool CanHandle(string url)` (Verilen URL'nin bu servise ait olup olmadığını söyler)
- `Task<List<GoFileItem>> FetchFilesAsync(string url)` (URL'den dosyaları çekme algoritması)

### `Models/GoFileService.cs` (Mevcut Tek Entegrasyon)
Sistemin belkemiği ve dış ağla (GoFile.io) güvenli el sıkışan modüldür. 
- **User-Agent & Header Koruması**: API'nin bot olarak algılamaması için otomatik olarak Chrome 121 Windows 10 taklidi (`Mozilla/5.0...`) yapar. Ayrıca `Origin` ve `Referer` başlıklarını ayarlayarak tarayıcıdan giriliyormuş hissi verir.
- **`CanHandle(string url)`**: Sadece içinde `"gofile.io/d/"` geçen URL'leri kabul eder.
- **`FetchFilesAsync` Algoritması**: Gelen URL'yi `@"^.*/d/([a-zA-Z0-9]+)"` şeklindeki Regex ile tarar ve klasör ID'sini (Token'ı) ayırır.
- **`FetchWebsiteToken` & `FetchGuestToken` (Atlatma Mekanizmaları)**:
  1. Önce `config.js` dosyasından Regex ile güncel Website doğrulama kodunu çeker (Regex patlarsa Hard-code "4fd6sg89d7s6" yedeği vardır).
  2. Sonra `https://api.gofile.io/accounts` API'sine boş istek atıp sıfır bir "Guest (Misafir) Token" yaratır ve bu hesabı doğrular (Aksi takdirde 401 hatası alır).
- Son olarak `GetFolderContents` aracılığıyla API'den (Sayfa boyutu 1000 limitli) JSON formatında gerçek dosya direkt linklerini (`link`), `md5` şifrelerini ve dosya büyüklüğünü (`size`) çeker ve bunları `GoFileItem` objelerine dönüştürür.

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

---

## 🖥 4. Kullanıcı Arayüzü: `MainWindow.xaml.cs` (Orkestrasyonun Vitrini)

* `MainWindow` oluşturulur oluşturulmaz `InitializeComponent()` ile kendini çizer ve bellek üzerinde `SettingsService` ile son ayarları çeker. 
* Kendi içinde tuttuğu bir `ObservableCollection<FileDisplayItem>` kümesi (Listesi) vardır. Ekranda kullanıcıya gösterilen liste tam olarak bu koleksiyonun yansımasıdır (DataBinding). Her "Queued" veya "%45" değişikliğinde, `INotifyPropertyChanged` sayesinde ekran anında güncellenir.
* **Başlık Çubuğu (`WindowChrome`)**: Klasik Windows penceresinden sıkılıp, özel minimize etme (`MinimizeBtn_Click`), kapatma (`CloseBtn_Click`) işlevli kendi çizilmiş butonları vardır. Kapatma düğmesine "Sistemi Tepsisine (System Tray) Küçült" fonksiyonu bağlanmıştır (`SetupTrayIcon` aracılığı ile `System.Windows.Forms.NotifyIcon`).
* Sağ üstte bir grafik motoru barındırır (`DrawSpeedGraph`). `DownloadManager`'in kendisine pasladığı `DownloadProgress` verilerini alır ve hız eğrilerini, boyutları (`FormatSizeGB` & `FormatSpeed` string dönüşümleriyle) modern ve anlaşılır bir tasarıma uydurarak ekrana boyar.

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
