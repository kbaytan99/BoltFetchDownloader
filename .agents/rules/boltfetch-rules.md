# BoltFetch Downloader - Yapay Zeka Ajanı Kuralları

Bu dosya KDownloader (BoltFetch) projesinde çalışan Yapay Zeka (AI) ajanının **KESİN VE DEĞİŞTİRİLEMEZ** kurallarını içerir. Bu kurallar her türlü istekten önceliklidir.

## 1. İletişim, Dokümantasyon ve Altyapı
- Yapay zeka kullanıcıyla **her zaman Türkçe konuşacak** ve tüm dokümanları/açıklamaları **Türkçe** yazacaktır.
- Projede yazılan veya düzenlenen kod içerikleri (değişken isimleri, metotlar, mantıksal terimler, yorum satırları vb.) aksi belirtilmedikçe evrensel kod standartlarını korumak amacıyla **İngilizce** yazılacaktır.
- **Kritik Altyapı Kuralı:** Proje baştan sona **.NET 9** (net9.0-windows) üzerinde çalışmaktadır. Yapay zeka yazdığı her kodda, önerdiği her NuGet paketinde ve oluşturduğu her mimaride *kesinlikle* .NET 9'un en güncel özelliklerini (örn: yeni C# dil özellikleri, performans iyileştirmeleri) kullanmak ve bu sürüme sadık kalmak ZORUNDADIR. Eski .NET Framework veya .NET Core kod yapıları kullanılmayacaktır.

## 2. Vibe Coding (Akışta Kodlama) Mimarisi ve İzolasyon Zorunluluğu
- **Kapsam Kaymasını Önleme:** Yeni bir özellik veya sistem geliştirilirken (Vibe coding esnasında), kodları doğrudan ana karkas dosyalara (`MainWindow.xaml.cs`, `DownloadManager.cs` gibi) **GÖMMEK KESİNLİKLE YASAKTIR.**
- **İzolasyon Kuralı:** Aklına gelen her yeni sistem veya mekanizma, `Services/` veya `Models/` klasörü altına tamamen bağımsız ve izole yepyeni bir `.cs` dosyası (Class) olarak inşa edilmelidir.
- **Kablo Yaklaşımı:** Bu yeni bağımsız modüller, ana projelere sadece bir "Interface" (Arayüz Bağlantısı, örn: `IDownloadProvider`) veya bir "Event" (Tetikleyici) mekanizması ile dışarıdan bağlanmalıdır. Böylece kod çökerse veya vazgeçilirse sadece bağlantı kopartılarak projenin stabil hali korunmalıdır. Mümkün olduğunda Feature Flag (Deneysel Ayar bloğu) kullanılmalıdır.

## 3. Otomatik Jira (Atlassian) Entegrasyonu
- **Yeni Sistem Eklenmesi:** Yapay zeka projede yepyeni bir `.cs` dosyası veya sistemi oluşturduğunda, bunu **otomatik olarak Atlassian MCP aracı üzerinden** `kbaytan99` hesabındaki `SCRUM` (BoltFetch Downloader) panosuna yepyeni bir **Feature** (Özellik) görevi olarak eklemek *ZORUNDADIR.*
- **Mevcut Sistemin Güncellenmesi:** Eğer yapay zeka var olan bir kod sistemini/dosyasını büyük ölçüde değiştiriyor, refactor ediyor veya yeniliyorsa, Jira'daki o komponente ait Feature biletini bulup **proaktif olarak güncellemek** (durumu değiştirmek, yorum atmak veya etiketlemek) *ZORUNDADIR.*
- **Kullanıcıyı Beklememe:** Bu Jira senkronizasyon işlemleri kullanıcının hatırlatmasına gerek kalmadan, yapay zekanın kendi inisiyatifinde otomatik olarak yapılmalıdır.

## 4. Git Yönetimi ve Otomatik Push (Versiyon Kontrol)
- **Her Değişiklikten Sonra Kayıt (Commit & Push):** Yapay zeka projede işe yarar bir özelliği kodladığında, hata düzelttiğinde veya yeni bir sistem eklediğinde, kodun stabil olduğuna emin olduktan HEMEN SONRA kullanıcıdan talimat beklemeden projede `git add .`, `git commit` ve `git push` komutlarını çalıştırmak zorundadır.
- **Detaylı Açıklama (Commit Message):** Atılan commit mesajları asla "Updated code" gibi baştansavma olamaz. Yapay zeka, commit mesajında (veya terminal üzerinden `-m` parametrelerinde) **tam olarak nelerin değiştiğini, neden değiştiğini ve hangi Jira bileti (Örn: SCRUM-24) için yapıldığını** detaylı bir rapor halinde Türkçe olarak belgelendirecektir.
