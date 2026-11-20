# DinamikAda — Kullanım ve Mimari Kılavuzu

[English README](../README.md) · [İlk Açılış](#i%CC%87lk-a%C3%A7%C4%B1l%C4%B1%C5%9F) · [Özellik Turu](#%C3%B6zellik-turu) · [Explorer.exe Görev Çubuğu](#explorerexe-g%C3%B6rev-%C3%A7ubu%C4%9Fu-modu) · [AI Kota](#ai-kota--%C3%A7oklu-hesap-takibi)

<div align="center">
  <img src="./screenshots/dinamikada-overview.png" alt="DinamikAda genel masaüstü görünümü: Üst Ada + Explorer.exe App Dock + Katlanabilir Sistem Tepsisi" width="100%"/>
</div>

DinamikAda; Windows 11 üzerinde üst **Dinamik Ada (MacBook Notch)** deneyimini, gerçek **`explorer.exe` yüzer görev çubuğu** yamalarını ve çok hesaplı **AI Kota** takibini tek bir native çatı altında birleştirir.

---

## Özellik Turu

> *Not: İşletim sisteminizde veya GitHub ayarlarında "Azaltılmış Hareket" (Reduced Motion) açıksa GitHub GIF'leri ilk karede duraklatabilir — animasyonu tam boy izlemek için altındaki **GIF'i Tam Boyut Aç** bağlantısına tıklayabilirsiniz.*

### 1. Üst Dinamik Ada (7 Sekme + Komut Paleti)
Kompakt medya/AI kapsülünden pürüzsüz yay animasyonuyla açılan 7 sekmeli komuta merkezi (**Akış**, **Ses**, **Denetim**, **Raf / QR**, **Sistem**, **Araçlar**, **AI Kota**) ve **Ctrl+Shift+Space** kısayoluyla açılan klavye odaklı **Komut Paleti**.

<div align="center">
  <img src="./screenshots/upper-island-motion.gif" alt="Üst Dinamik Ada tüm sekmeler ve Komut Paleti turu" width="800"/>
  <br/>
  <sub><a href="./screenshots/upper-island-motion.gif">GIF'i Tam Boyut Aç</a></sub>
</div>

- **Akış:** Canlı Spotify/sistem medyası, `48 kHz · 24-bit` kaynak bilgisi, kaynak değiştirici (`< 1/2 >`), **Odak Zamanlayıcı** (`+5m` / `+25m`) ve etkileşimli Windows bildirim akışı.
- **Ses:** Uygulama bazlı ses mikseri ve tek tıkla ses çıkış aygıtı yönlendirmesi.
- **Denetim & Araçlar:** Hızlı sistem/parlaklık anahtarları, zamanlanmış kapatma/yeniden başlatma sayacı, USB kısayolu, OCR, pano kasası ve anlık ekran **Renk Seçici (Color Picker)** HUD bildirimleri.
- **Raf & QR:** Sürükle-bırak geçici dosya rafı ve LAN üzerinden tek tıkla QR kod ile paylaşım.
- **Sistem:** Canlı CPU, RAM, GPU (`VRAM`, sıcaklık, fan hızı), Ağ hızı, Disk I/O, `1.1.1.1` ping ölçümü, RAM/Disk temizleme ve yanıt vermeyen süreç yönetimi.

---

### 2. AI Kota & Çoklu Hesap Takibi
Tek bir sekmeden tüm yapay zeka aboneliklerinizi gerçek zamanlı izleyin:
- **ChatGPT / Codex:** Sağlayıcının döndürdüğü `5 saatlik` ve **Haftalık (`7g`)** kalan kota barları ve yenilenme zamanları.
- **Google Antigravity:** Tek tık **Google OAuth** girişi + **Gemini Models**, **Claude & GPT** ve **Gemini 4 Argon** havuzlarının `5s` + `7g` ayrı kırılımları.
- **Claude, Gemini CLI, Cursor, GitHub Copilot ve Özel API:** Birden fazla hesabı etiketleyin, kritik eşik uyarısı (%) belirleyin ve verileri PC yeniden başlatsanız bile lokalde kalıcı olarak koruyun. Oturum süresi dolarsa veya yanlış hesap eşleşirse kart üzerindeki **Yeniden bağla** düğmesini kullanabilirsiniz.

<div align="center">
  <img src="./screenshots/ai-quota-showcase.gif" alt="AI Kota sekmesi: ChatGPT 5 saatlik + haftalık barlar, Antigravity havuzları, Claude ve hesap düzenleyici" width="800"/>
  <br/>
  <sub><a href="./screenshots/ai-quota-showcase.gif">GIF'i Tam Boyut Aç</a></sub>
</div>

---

### 3. `explorer.exe` Görev Çubuğu Modu (Windhawk)

<div align="center">
  <img src="./screenshots/explorer-taskbar.png" alt="Gerçek explorer.exe üzerinde ortalanmış App Dock ve sağ Sistem Tepsisi" width="100%"/>
</div>

- DinamikAda sahte görev çubuğu kullanmak yerine **gerçek Windows 11 `explorer.exe`** görev çubuğunu iki yüzer adaya (ortada **App Dock**, sağda **Sistem Tepsisi**) böler; arada kalan alanlar `GetWindowRgn` ile alt pencerelere tıklamayı ve pencere önizlemelerini sorunsuz iletir.
- Alt kenardaki çalışma alanı kilidini kaldırır; pencereler ekranı tam boy kullanır, fare **ekranın tam alt ortasına** geldiğinde dock akıcı şekilde yükselir (`Akıllı Gizleme`) ve uzaklaştığınızda gizlenir.
- İkon üzerine gelindiğinde dalga efekti (`wave lift`) ve yerel Windows araç ipuçları çalışmaya devam eder.
- Sağ taraftaki Sistem Tepsisi okuna (`<` / `>`) tıklayarak gereksiz simgeleri gizleyip tepsiyi küçültebilir, tekrar tıklayarak açabilirsiniz. Hızlı ses/sistem ayarı için **Win+A** kısayolunu kullanabilirsiniz.

<table>
  <tr>
    <td width="64%" align="center">
      <img src="./screenshots/explorer-dock-full-cycle.gif" alt="Explorer App Dock açılış, dalgalı hover ve otomatik gizlenme döngüsü" width="100%"/>
      <br/>
      <sub>App Dock tam döngü (<a href="./screenshots/explorer-dock-full-cycle.gif">GIF'i Tam Boyut Aç</a>)</sub>
    </td>
    <td width="36%" align="center">
      <img src="./screenshots/explorer-tray.gif" alt="Explorer Sistem Tepsisi açma/daraltma" width="100%"/>
      <br/>
      <sub>Tepsi daralt/genişlet (<a href="./screenshots/explorer-tray.gif">GIF'i Tam Boyut Aç</a>)</sub>
    </td>
  </tr>
</table>

---

### 4. Kesintisiz Albüm Kapağı Rengi (Ada + Görev Çubuğu + Başlat Menüsü)
Çalan müziğin baskın kapak renkleri gerçek zamanlı örneklenir ve `%LOCALAPPDATA%\DinamikAda\ExplorerHost\animation-state.ini` üzerinden yerel Windows kabuğuna aktarılır:
- **Üst Ada**, **Explorer App Dock**, **Sistem Tepsisi**, **Gizli Simgeler**, **Bildirim/Takvim panelleri**, **Hızlı Ayarlar (`Win+A`)** ve **Windows 11 Başlat Menüsü (`StartMenuExperienceHost`)** şarkı değiştikçe aynı renk paletiyle yumuşak geçiş yapar.

<div align="center">
  <img src="./screenshots/music-colors.gif" alt="Üst Ada, Explorer Dock ve Sistem Tepsisinde eşzamanlı albüm kapağı renk geçişi" width="100%"/>
  <br/>
  <sub><a href="./screenshots/music-colors.gif">GIF'i Tam Boyut Aç</a></sub>
</div>

<br/>

<div align="center">
  <img src="./screenshots/music-start-menu.gif" alt="Windows 11 Başlat Menüsünde sürekli albüm kapağı rengi" width="680"/>
  <br/>
  <sub>Windows 11 Başlat Menüsü senkronizasyonu (<a href="./screenshots/music-start-menu.gif">GIF'i Tam Boyut Aç</a>)</sub>
</div>

---

## İlk Açılış ve Kurulum

1. Depoyu klonlayın (veya GitHub'da **Code → Download ZIP** ile indirip çıkarın). Windows 11 x64 üzerinde **.NET 8 SDK** kurulu olmalıdır.
2. İlk derleme ve başlatma için kök dizindeki [DinamikAda-Baslat.bat](../DinamikAda-Baslat.bat) dosyasını çalıştırın.
3. **Explorer Görev Çubuğu Kurulumu:**
   - [Windhawk](https://windhawk.net/) ve beraberindeki derleyiciyi kurun.
   - DinamikAda açıksa sistem tepsisi simgesinden **Çıkış** seçin ve [DinamikAda-Explorer-Kur.bat](../DinamikAda-Explorer-Kur.bat) dosyasını çalıştırın.
   - **Ayarlar → Alt Dock** ekranından **Akıllı Gizleme** ve simge büyütme ayarlarını yapıp **Kaydet**'e basın.
4. **Explorer Modunu Güncelleme / Geri Dönüş:**
   - Güncel kodu çektikten (`git pull`) sonra DinamikAda'dan çıkıp önce [DinamikAda-Windows-Barina-Don.bat](../DinamikAda-Windows-Barina-Don.bat), ardından [DinamikAda-Explorer-Kur.bat](../DinamikAda-Explorer-Kur.bat) çalıştırın.
   - Windhawk özel bir dizindeyse:
     ```powershell
     powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-explorer-taskbar.ps1 -WindhawkDirectory "D:\Apps\Windhawk"
     .\DinamikAda-Baslat.bat --explorer-taskbar
     ```
   - Doğrulama testleri için:
     ```powershell
     .\DinamikAda-Baslat.bat --verify
     powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-explorer-taskbar.ps1 -RevealCycle
     ```

---

## Dock Widget'ları

Alt dock'a, uygulamaların yanına canlı widget'lar ekleyebilirsin. 8 kategoride 69 widget var: AI, Üretkenlik, Sistem, Medya ve Ses, Uygulamalar ve Dosyalar, Hava Durumu, Finans ve Saatler.

1. Dock'a sağ tıkla ve **Widget'lar…** seçeneğini seç. Aynı pencere **Ayarlar → Alt Dock → Widget Seçiciyi Aç…** ile de açılır.
2. Kategoriye göre gez veya ara. Bir önizlemeye tıklayınca widget dock'a eklenir, tekrar tıklayınca kaldırılır.
3. Seçici açıkken dock'taki widget'ların köşesinde bir **−** rozeti çıkar. Bu rozetle de widget'ı kaldırabilirsin.
4. Widget'ları dock üzerinde sürükleyerek sırala. Bir widget'a tıklayınca ayrıntı kartı açılır. Örneğin hava durumu tahmini, ekran süresi dökümü, yapılacaklar listesi veya kronometre turları.
5. Dock'u boş bir yerinden tutup ekranın üst yarısına bırakırsan dock üst kenara yapışır ve paneller aşağı doğru açılır. Alta geri sürükleyerek eski yerine dönebilirsin. Sağ tık menüsünde **Dock'u üste/alta taşı** seçeneği de var.
6. **Dock stili** menüsünden görünümü seç: Gece Yarısı, Siyah, Beyaz, Sıvı Cam veya iOS Cam.

**Ayarlar → Alt Dock → Dock Widget'ları ve Görünüm** bölümünde şunları ayarlayabilirsin:

- **Görünüm:** boyut (S/M/L), 12/24 saat biçimi ve °F.
- **Hava durumu:** şehir.
- **Saatler:** dünya saatleri ve dünya zamanı şehri.
- **Finans:** döviz çifti, kripto sembolü, hisseler ve Finnhub anahtarı.
- **Pomodoro:** odak ve mola süreleri.
- **Ekran süresi:** kaydı açıp kapatma.

Veriler doğrudan bu bilgisayardan alınır:

- Hava durumu Open-Meteo'dan, döviz Frankfurter'den, kripto Binance'ten alınır. Bunlar için anahtar gerekmez.
- Hisseler için Finnhub'un ücretsiz anahtarı gerekir. Anahtar bu bilgisayarda şifreli saklanır.
- Ekran süresi yalnızca bu bilgisayarda tutulur ve son 8 günü kapsar.
- Bir widget'ı kaldırınca o widget'ın istekleri de durur.

## Hesap Widget'ları

Bazı widget'lar kendi hesaplarını okur: Stripe, Lemon Squeezy, Gumroad, Polar, Paddle, GitHub (katkı tablosu ve bildirimler), Linear, Resend, Plausible, YouTube, LeetCode, Gmail, Google Takvim, DeepSeek ve Unsplash.

- Bağlanana kadar widget soluk renkte örnek veri gösterir. Köşedeki küçük zincir rozeti, bağlantı gerektiğini belirtir.
- Widget'a tıklayınca kart, bağlanmak için gereken adımları tek tek anlatır. Anahtarı ya da kullanıcı adını karttaki kutuya yapıştırıp **Bağla**'ya bas. **Panoyu aç**, anahtarın oluşturulduğu sayfayı açar.
- Mümkün olan her yerde salt-okunur bir anahtar kullan. Gmail'de gerçek şifreni değil, Google'ın verdiği 16 harfli uygulama şifresini yaz. Google Takvim'de takvimin "iCal biçimindeki gizli adresi" yeterli.
- Anahtarlar yalnızca bu bilgisayarda, Windows'un şifrelemesiyle (DPAPI) saklanır. Her widget sadece kendi servisine bağlanır, aradaki hiçbir sunucuya veri gitmez.
- Bağlıyken kartın altında son güncelleme saati ya da hata mesajı görünür. **Yenile**, **Düzenle** ve **Bağlantıyı kaldır** düğmeleri de oradadır. Hata olursa widget birkaç dakika sonra kendisi yeniden dener.
- Bağlı hesapların listesi Ayarlar → Alt Dock → **Bağlı Hesaplar** bölümündedir. Bir hesabı oradan da kaldırabilirsin.
- Bir widget'ı dock'tan kaldırdığında onun istekleri de durur.

## Sesli Asistan

Bir kısayolu basılı tutup konuş, bırakınca asistan işini yapar. Ayarlar → **Sesli Asistan** sekmesinden açıp kapatabilir, kısayolları değiştirebilirsin.

| Kısayol (basılı tut) | Ne yapar |
|---|---|
| **Ctrl + Win** | **Dikte:** Konuştuğunu imlecin olduğu yere yazar. Duraklamaları ve "şey"leri temizler. "Dokuzda… yok, onda" gibi düzeltmelerde yalnızca son hâlini yazar. "Yeni satır" dersen alt satıra geçer. |
| **Ctrl + Shift** | **Daire çiz ve sor:** Tuşları tutarken fareyle ekranda bir şeyin etrafına daire çiz ve sorunu sesli sor. Cevap, işaretlediğin yerin yanında bir kartta çıkar. **Kopyala** ile alabilirsin, Esc kartı kapatır. |
| **Alt + Win** | **Komut:** "Spotify'ı aç", "YouTube'da lo-fi ara", "yapılacaklara süt almayı ekle", "25 dakikalık zamanlayıcı kur", "10 Kasım saat 11'de Amazon'u aç", "her sabah 9'da Unity ve Spotify'ı aç" gibi komutları anlar. |

- Tuşları kısa basıp bırakırsan ya da arada başka bir tuşa basarsan asistan devreye girmez, normal kısayollar eskisi gibi çalışır. Tuşları bıraktığında Başlat menüsü açılmaz.
- Yapay zekâ, kendi **ücretsiz Gemini API anahtarınla** çalışır. Anahtarı Google AI Studio'dan alıp Ayarlar → Sesli Asistan'a yapıştır ve **Test et**'e bas. Anahtar yalnızca bu bilgisayarda şifreli saklanır, istekler doğrudan Google'a gider.
- Anahtar yoksa dikte, Windows'un çevrimdışı konuşma tanımasıyla çalışır. Bunun için Windows Ayarları → Saat ve dil → Konuşma'dan dil paketinin kurulu olması gerekir. Daire çizip sorma ve komutlar anahtar ister.
- Zamanlanmış görevler ve rutinler aynı sekmede listelenir; oradan silebilir, rutinleri açıp kapatabilirsin. Bilgisayar uykudaysa, kurulan saatten sonraki 10 dakika içinde uyandığında rutin yine çalışır.
- **Ada** adlı asistan karakteri dinlerken, düşünürken ve cevap verirken canlanır. Adını, rengini, göz stilini, aksesuarını ve konuşma tarzını aynı sekmeden değiştirebilirsin.
- **Hız testi** düğmesi, yazma hızını konuşma hızınla karşılaştırır.

## Genel Kontroller

| İşlem | Nereden Ulaşılır |
| --- | --- |
| Medya oynatma, Odak Zamanlayıcı & Bildirimler | Üst ada → **Akış** |
| Uygulama bazlı ses ve çıkış aygıtı yönlendirme | Üst ada → **Ses** |
| Parlaklık, güç ve zamanlayıcı kontrolleri | Üst ada → **Denetim** |
| Dosya rafı & LAN QR paylaşım | Üst ada → **Raf** |
| CPU / RAM / GPU VRAM / Disk / Ping yönetimi | Üst ada → **Sistem** |
| Renk seçici, pano kasası, OCR, USB araçları | Üst ada → **Araçlar** |
| Çoklu ChatGPT / Antigravity / Claude kota takibi | Üst ada → **AI Kota** |
| Komut paleti / hızlı pencere ve uygulama arama | **Ctrl+Shift+Space** |
| Windows Hızlı Ayarlar (Ses / Ağ / Parlaklık) | **Win+A** |
| Standart Windows görev çubuğunu geri yükleme | DinamikAda'dan çık → **DinamikAda-Windows-Barina-Don.bat** |
