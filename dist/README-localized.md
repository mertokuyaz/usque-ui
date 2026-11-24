# Usque Desktop — Çok Dilli Windows Kurulumu

Usque Desktop, Cloudflare WARP bağlantısını masaüstünden yönetmenizi sağlayan,
bağımsız ve açık kaynaklı bir Windows istemcisidir. Cloudflare'ın resmî uygulaması
değildir ve Cloudflare ile bağlantılı değildir.

## Paket

- **Kurulum dosyası:** `UsqueDesktop-Setup-1.3.2-localized.exe`
- **Sürüm:** 1.3.2 — güncellenmiş çevirilerle hazırlanmış ayrı paket
- **Platform:** Windows 10 (1809 ve üzeri) / Windows 11, x64
- **Gereksinim:** Yönetici yetkisi ve bağlantı kurmak için internet erişimi
- .NET çalışma zamanı pakete dahildir; ayrıca .NET yüklemeniz gerekmez.

Bu paket güncel masaüstü çevirilerini içerir. Önceden yayımlanmış
`UsqueDesktop-Setup-1.3.2.exe` ile aynı dosya değildir. Go motoru değiştirilmemiştir.

## Kurulum

1. Çalışan Usque uygulamasını **Usque'den Çık** seçeneğiyle kapatın.
2. `UsqueDesktop-Setup-1.3.2-localized.exe` dosyasını açın.
3. Windows yönetici izni isterse onaylayın.
4. Kurulum adımlarını tamamlayın; masaüstü kısayolu isteğe bağlıdır.
5. Usque'yi Başlat menüsünden veya kısayoldan açın.

**Not:** Kurulum dosyası dijital olarak imzalanmamıştır. Windows SmartScreen
uyarı verebilir. Dosyayı yalnızca güvendiğiniz kaynaktan edinin ve çalıştırmadan
önce aşağıdaki SHA-256 değeriyle doğrulayın.

## İlk bağlantı

1. İsterseniz **Ayarlar ve Profiller → Dil** bölümünden dili değiştirin.
2. Ana ekrandaki güç düğmesine basın.
3. Yeni profil oluşturulacaksa Cloudflare WARP hizmet şartlarını okuyup onaylayın.
4. Bağlantı ve doğrulama durumunu ana ekrandan takip edin.

İlk profil kaydı, ağ kesme koruması devreye girmeden önce normal internet
bağlantısını kullanır. Mevcut bir `usque config.json` dosyasını içe aktarmak da
mümkündür. Özel anahtarlarınızı ve profil dosyalarınızı paylaşmayın.

## Dil desteği

İngilizce, basitleştirilmiş Çince, Hintçe, İspanyolca, Fransızca, Arapça,
Bengalce, Rusça, Portekizce, Urduca ve Türkçe desteklenir.

Dil değişikliği uygulama içinde anında uygulanır. Arapça ve Urduca arayüzler
sağdan sola düzenlenir; teknik adresler, sayaçlar ve günlükler soldan sağa kalır.
Kurulum sihirbazının dil seçenekleri ise İngilizce ve Türkçedir.

## Bağlantı modları ve güvenlik

- **Cihaz genelinde tünel:** Cihazın IPv4 ve IPv6 trafiğini WARP üzerinden
  yönlendirir. Ağ kesme koruması (kill switch) varsayılan olarak açıktır.
- **SOCKS5 / HTTP proxy:** Yalnızca proxy'yi kullanacak şekilde ayarladığınız
  uygulamaları kapsar. Diğer uygulamaların trafiğini korumaz ve Windows sistem
  proxy ayarlarını değiştirmez. Bu modda cihaz genelinde kill switch kullanılamaz.
- **Otomatik aktarım:** Önce QUIC, başarısız denemeden sonra TCP / HTTP/2 kullanır.

Bildirim alanına gizleme varsayılan olarak kapalıdır. Küçültme veya pencereyi
kapatma, uygulamayı görev çubuğunda küçültür; bağlantıyı sonlandırmaz. İsterseniz
ayarlardan bildirim alanına gizlemeyi açabilirsiniz. Önceden kaydedilmiş tercihler
korunur. Tamamen kapatmak için **Usque'den Çık** seçeneğini kullanın.

### İnternet erişimi engellenirse

Kill switch, beklenmeyen kapanma veya bağlantı kaybından sonra etkin kalabilir.
Uygulamayı yeniden açıp bağlantıyı tekrar kurun ya da **İnternet Erişimini Geri
Yükle** seçeneğini kullanın. Geri yükleme, korumayı kaldırır ve normal,
korumasız internet bağlantısına döndürür.

**Uygulamayı kaldırmadan önce internet erişimini geri yükleyin.** Kaldırıcı,
profilleri ve etkin ağ korumasını otomatik olarak silmez. Koruma etkinken
uygulama verilerini elle silmeyin.

Ana pencere açılmıyorsa yönetici PowerShell penceresinde:

```powershell
& "$env:ProgramFiles\UsqueDesktop\Usque.Desktop.exe" --restore-network
```

Özel bir kurulum klasörü seçtiyseniz komuttaki yolu buna göre değiştirin.

## Veriler ve gizlilik

Profiller, ayarlar ve kurtarma bilgileri `%ProgramData%\UsqueDesktop` altında
tutulur. Erişim yönetici ve SYSTEM hesaplarıyla sınırlandırılır; profil dosyaları
diskte ayrıca şifrelenmez.

Tanılama raporlarında bilinen hassas bilgiler maskelenir. Yine de paylaşmadan
önce raporu mutlaka inceleyin. Bağlantı doğrulaması bir anonimlik garantisi veya
paket düzeyinde sızıntı testi değildir.

## Dosya doğrulama

Kurulum dosyası için SHA-256:

```text
fbb1cc43d38a682c8b178136e33681930ad93b997ce8ec9ef152e326cc6e5df9
```

Kurulum dosyasının bulunduğu klasörde PowerShell ile hesaplayabilirsiniz:

```powershell
Get-FileHash .\UsqueDesktop-Setup-1.3.2-localized.exe -Algorithm SHA256
```

Sonuç yukarıdaki değerle eşleşmelidir. Aynı değer
`UsqueDesktop-Setup-1.3.2-localized.exe.sha256` dosyasında da bulunur.

## Doğrulama ve lisanslar

Paketin kaynak kodu için **138/138 masaüstü testi** geçmiştir. Bu testler gerçek
ağ bağlantısını değiştirmez; her ağ ortamında sorunsuz çalışmayı garanti etmez.

Uygulama ve bileşenlerinin lisansları kurulum klasöründeki `USQUE-LICENSE.md`,
`WINTUN-LICENSE.txt` ve `Licenses` klasöründe bulunur.
