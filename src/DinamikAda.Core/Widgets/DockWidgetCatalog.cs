namespace DinamikAda.Core.Widgets;

public enum DockWidgetCategory
{
    Ai = 0,
    Productivity = 1,
    System = 2,
    Media = 3,
    Apps = 4,
    Weather = 5,
    Finance = 6,
    Clocks = 7
}

/// <summary>
/// Static description of a Dock widget: what the picker lists and how wide the tile is, in
/// multiples of the dock icon size.
/// </summary>
public sealed record DockWidgetDescriptor(
    string Id,
    DockWidgetCategory Category,
    string Glyph,
    string NameTr,
    string NameEn,
    string DescriptionTr,
    string DescriptionEn,
    double WidthUnits = 1.0,
    bool NeedsNetwork = false,
    bool NeedsApiKey = false)
{
    public string Name(string lang) => lang == "en" ? NameEn : NameTr;
    public string Description(string lang) => lang == "en" ? DescriptionEn : DescriptionTr;
}

public static class DockWidgetCatalog
{
    public const int MaxWidgetsOnDock = 24;

    public static readonly IReadOnlyList<string> DefaultWidgets = ["np-mini", "cpu", "ram", "day-progress"];

    public static IReadOnlyList<DockWidgetDescriptor> All { get; } =
    [
        // AI
        new("ai-limits", DockWidgetCategory.Ai, "", "AI Limitleri", "AI Limits",
            "İki AI planın tek kutuda; her limitin ne zaman yenileneceğiyle.",
            "Two of your AI plans on one tile, with when each limit resets.", 2.6),
        new("ai-ring", DockWidgetCategory.Ai, "", "AI Halkası", "AI Ring",
            "En çok dikkat isteyen AI hesabının kalan kotası halka olarak.",
            "The AI account that needs attention most, as a remaining-quota ring.", 1.0),

        // Productivity
        new("day-timeline", DockWidgetCategory.Productivity, "", "Gün Çizelgesi", "Day Timeline",
            "Bütün günün tek çizgide: uygulama türlerine göre saatler, odak turları, gün doğumu ve batımı.",
            "Your whole day on one line: hours by kind of app, focus rounds, sunrise and sunset.", 3.4),
        new("screen-time", DockWidgetCategory.Productivity, "", "Ekran Süresi", "Screen Time",
            "Bugün bilgisayar başında geçen süre saat saat; uygulama başına günlük ve haftalık.",
            "Today's time in front of the PC, hour by hour, and every app's by day and week.", 2.2),
        new("pomodoro", DockWidgetCategory.Productivity, "", "Pomodoro", "Pomodoro",
            "Odak turlarını ve molaları dock'tan başlat ve atla.",
            "Focus rounds and breaks you start and skip from the dock.", 2.2),
        new("timer", DockWidgetCategory.Productivity, "", "Zamanlayıcı", "Timer",
            "Tek tıkla başlayan geri sayım.", "A countdown that starts in one click.", 2.1),
        new("stopwatch", DockWidgetCategory.Productivity, "", "Kronometre", "Stopwatch",
            "Dock'tan başlat, duraklat ve tur al; kart her turu saklar.",
            "Start, pause and lap from the dock; the card keeps every lap.", 2.1),
        new("todos", DockWidgetCategory.Productivity, "", "Yapılacaklar", "Todos",
            "İlerleme halkasıyla sıradaki açık görevin; dock'tan işaretle.",
            "Your next open task with a progress ring; tick it off from the dock.", 2.4),
        new("notes", DockWidgetCategory.Productivity, "", "Notlar", "Notes",
            "En son notun, düzenlemeye bir tık uzakta.", "Your latest note, one click from editing.", 2.2),
        new("calendar", DockWidgetCategory.Productivity, "", "Takvim", "Calendar",
            "Sıradaki etkinliğin ve ne zaman başlayacağı.", "Your next event and how soon it starts.", 2.4),
        new("currency", DockWidgetCategory.Productivity, "", "Döviz", "Currency",
            "Güncel kurla para birimleri arasında çevir.", "Convert between currencies at current rates.", 1.9,
            NeedsNetwork: true),

        // System
        new("top-cpu", DockWidgetCategory.System, "", "En Çok CPU", "Top CPU",
            "Şu an en çok işlemci kullanan üç uygulama; tıklayınca Görev Yöneticisi açılır.",
            "The three apps using the most CPU right now; a click opens Task Manager.", 2.2),
        new("top-ram", DockWidgetCategory.System, "", "En Çok RAM", "Top RAM",
            "En çok bellek tutan üç uygulama, Görev Yöneticisi gibi toplanmış.",
            "The three apps holding the most memory, added up as Task Manager does.", 2.2),
        new("language", DockWidgetCategory.System, "", "Klavye Dili", "Language",
            "Geçerli klavye düzeni; tıklayınca değiştir.", "The current keyboard layout; click to switch.", 1.0),
        new("temperature", DockWidgetCategory.System, "", "Sıcaklık", "Temperature",
            "Sıcaklık göstergesi, yanında işlemci ve bellek yükü.",
            "Temperature as a gauge, with CPU and memory load beside it.", 2.0),
        new("cpu", DockWidgetCategory.System, "", "CPU", "CPU",
            "İşlemci yükü halka olarak.", "Processor load as a ring.", 1.0),
        new("ram", DockWidgetCategory.System, "", "RAM", "RAM",
            "Kullanılan bellek halka olarak.", "Memory in use as a ring.", 1.0),
        new("network", DockWidgetCategory.System, "", "Ağ", "Network",
            "Canlı grafikle indirme ve yükleme hızı.", "Download and upload speed with a live chart.", 2.2),
        new("battery", DockWidgetCategory.System, "", "Pil", "Battery",
            "Dolan bir gösterge olarak şarj seviyesi ve şarj durumu.",
            "Charge level as a filling gauge, with the charging state.", 1.5),
        new("battery-ring", DockWidgetCategory.System, "", "Pil Halkası", "Battery Ring",
            "Sayının çevresinde halka; prize takılıyken etrafında bir kıvılcım döner.",
            "The charge as a ring round the number; a spark runs round it while plugged in.", 1.0),
        new("battery-core", DockWidgetCategory.System, "", "Enerji Çekirdeği", "Battery Core",
            "Şarja göre yanan on iki hücreli bir enerji çekirdeği.",
            "An energy core with twelve cells lit to the charge.", 1.0),
        new("battery-dots", DockWidgetCategory.System, "", "Nokta Pil", "Battery Dots",
            "Nokta matris pil: sayı ve şarj noktalarla.", "A dot-matrix battery: the number and the charge in dots.", 1.6),
        new("battery-gauge", DockWidgetCategory.System, "", "Yakıt Göstergesi", "Battery Gauge",
            "Pilin için E'den F'ye bir yakıt göstergesi.", "A car's fuel gauge, E to F, for your battery.", 1.5),
        new("quick-toggles", DockWidgetCategory.System, "", "Hızlı Geçişler", "Quick Toggles",
            "Wi-Fi, Bluetooth, mobil erişim noktası ve uçak modu.",
            "Wi-Fi, Bluetooth, mobile hotspot and airplane mode.", 2.6),
        new("brightness", DockWidgetCategory.System, "", "Parlaklık", "Brightness",
            "Ekran parlaklığı için bir kaydırıcı.", "A slider for the display's brightness.", 2.4),
        new("theme-toggle", DockWidgetCategory.System, "", "Tema", "Theme Toggle",
            "Windows'u açık ve koyu mod arasında değiştir.", "Switch Windows between light and dark mode.", 1.0),
        new("dnd", DockWidgetCategory.System, "", "Rahatsız Etme", "Do Not Disturb",
            "Windows bildirimlerini tek tıkla sustur.", "Silence Windows notifications in one click.", 1.0),
        new("notifications", DockWidgetCategory.System, "", "Bildirimler", "Notifications",
            "Bildirim Merkezi'ni dock'tan aç.", "Open the Notification Center from the dock.", 1.0),
        new("show-desktop", DockWidgetCategory.System, "", "Masaüstünü Göster", "Show Desktop",
            "Bütün pencereleri gizle ve geri getir.", "Hide every window, and bring them back.", 1.0),
        new("keyboard", DockWidgetCategory.System, "", "Ekran Klavyesi", "Keyboard",
            "Ekran klavyesi bir tık uzakta.", "The on-screen keyboard, one click away.", 1.0),
        new("month-progress", DockWidgetCategory.System, "", "Ay İlerlemesi", "Month Progress",
            "Ayın ne kadarının geride kaldığı.", "How much of the month is behind you.", 1.6),
        new("day-progress", DockWidgetCategory.System, "", "Gün İlerlemesi", "Day Progress",
            "Bugünün ne kadarının geride kaldığı.", "How much of today is behind you.", 1.6),
        new("year-progress", DockWidgetCategory.System, "", "Yıl İlerlemesi", "Year Progress",
            "Dolan noktalardan bir ızgara olarak yıl.", "The year as a grid of dots, filling up.", 2.4),

        // Media & audio
        new("now-playing", DockWidgetCategory.Media, "", "Şimdi Çalıyor", "Now Playing",
            "Albüm kapağı, başlık ve geçerli parça için tam kontroller.",
            "Cover art, title and full controls for the current track.", 3.6),
        new("audio-output", DockWidgetCategory.Media, "", "Ses Çıkışı", "Audio Output",
            "Geçerli hoparlör veya kulaklık için ses düzeyi.", "Volume for your current speakers or headphones.", 2.4),
        new("microphone", DockWidgetCategory.Media, "", "Mikrofon", "Microphone",
            "Bütün uygulamalar için bir aramanın iki düğmesi: mikrofonu ve sesi kapat.",
            "A call's two buttons for every app at once: mute your microphone, and mute the sound.", 1.8),
        new("np-compact", DockWidgetCategory.Media, "", "Şimdi Çalıyor (kompakt)", "Now Playing (compact)",
            "Başlık, sanatçı ve oynat/duraklat daha küçük bir kartta.",
            "Title, artist and play/pause in a smaller card.", 2.6),
        new("np-mini", DockWidgetCategory.Media, "", "Şimdi Çalıyor Mini", "Now Playing Mini",
            "Parçanın ilerlemesiyle tek bir yuvarlak oynat düğmesi.",
            "One round play button with the track's progress.", 1.0),
        new("np-bar", DockWidgetCategory.Media, "", "Şimdi Çalıyor Çubuğu", "Now Playing Bar",
            "İlerleme çubuğu ve atlama ile parça.", "The track with a progress bar and skip.", 3.0),

        // Apps & files
        new("recent", DockWidgetCategory.Apps, "", "Son Açılanlar", "Recently Opened",
            "En son kullandığın uygulamalar ve dosyalar.", "The apps and files you used last.", 1.0),
        new("recycle-bin", DockWidgetCategory.Apps, "", "Geri Dönüşüm Kutusu", "Recycle Bin",
            "Kutuda ne olduğu ve boşaltmak için bir tık.", "What the bin holds, and a click to empty it.", 1.6),
        new("search", DockWidgetCategory.Apps, "", "Arama", "Search",
            "Web'de ara veya açmak için bir klasör yolu yaz.", "Search the web, or type a folder path to open it.", 2.6),
        new("commands", DockWidgetCategory.Apps, "", "Komutlar", "Commands",
            "Bir uygulamayı veya eylemi başlatmak için bir kelime yaz.", "Type a word to launch an app or an action.", 1.0),
        new("storage", DockWidgetCategory.Apps, "", "Depolama", "Storage",
            "Kullanım çubuğuyla sürücündeki boş alan.", "Free space on your drive, with a usage bar.", 1.9),
        new("screenshot", DockWidgetCategory.Apps, "", "Ekran Görüntüsü", "Screenshot Capture",
            "Ekranı, bir pencereyi veya bir bölgeyi yakala.", "Capture the screen, a window or a region.", 1.0),

        // Weather
        new("weather", DockWidgetCategory.Weather, "", "Hava Durumu", "Weather",
            "Şu anki sıcaklık, bugünün en yüksek ve en düşüğüyle.", "Temperature now, with today's high and low.", 1.8,
            NeedsNetwork: true),
        new("weather-details", DockWidgetCategory.Weather, "", "Hava Ayrıntıları", "Weather Details",
            "Rüzgâr, yağış ve günün aralığı.", "Wind, rain and the day's range.", 2.4, NeedsNetwork: true),
        new("forecast", DockWidgetCategory.Weather, "", "Bugün ve Tahmin", "Today & Forecast",
            "Bugün ve sonraki üç gün yan yana.", "Today next to the next three days.", 3.0, NeedsNetwork: true),
        new("weather-status", DockWidgetCategory.Weather, "", "Hava Özeti", "Weather Status",
            "Şehrinle birlikte durum kelimelerle.", "The conditions in words, with your city.", 2.2, NeedsNetwork: true),
        new("hourly", DockWidgetCategory.Weather, "", "Saatlik", "Hourly",
            "Sonraki altı saat.", "The next six hours.", 3.2, NeedsNetwork: true),
        new("sun", DockWidgetCategory.Weather, "", "Gün Doğumu ve Batımı", "Sunrise & Sunset",
            "Güneşin gün doğumu ile batımı arasında nerede olduğu.", "Where the sun is between sunrise and sunset.", 2.0,
            NeedsNetwork: true),

        // Finance
        new("crypto", DockWidgetCategory.Finance, "", "Kripto", "Crypto",
            "Bir coinin fiyatı, 24 saatlik değişimi ve günün çizgisi (Binance).",
            "A coin's price, its 24-hour change and the day's line, from Binance.", 2.2, NeedsNetwork: true),
        new("stock-chart", DockWidgetCategory.Finance, "", "Hisse Grafiği", "Stock Chart",
            "Gün içi çizgisiyle tek bir hisse.", "One ticker with its intraday line.", 2.2, NeedsNetwork: true,
            NeedsApiKey: true),
        new("stock-watchlist", DockWidgetCategory.Finance, "", "Hisse Listesi", "Stock Watchlist",
            "Bir bakışta üç hisse.", "Three tickers at a glance.", 2.8, NeedsNetwork: true, NeedsApiKey: true),

        // Clocks
        new("clock", DockWidgetCategory.Clocks, "", "Saat", "Clock",
            "Saniyesi ve tarihiyle büyük, okunaklı saat.", "Large, readable time with its seconds and the date.", 1.9),
        new("flip-clock", DockWidgetCategory.Clocks, "", "Paletli Saat", "Flip Clock",
            "Her dakika dönen paletli rakamlar.", "Split-flap digits that turn over every minute.", 2.2),
        new("world-clocks", DockWidgetCategory.Clocks, "", "Dünya Saatleri", "World Clocks",
            "Çalıştığın şehirler için üç küçük analog kadran.", "Three small analog faces for the cities you work with.", 2.8),
        new("seven-days", DockWidgetCategory.Clocks, "", "7 Gün", "7 Days",
            "Bu hafta bir gün şeridi olarak, bugünün sıcaklığıyla.",
            "This week as a strip of days, with today's temperature.", 3.2),
        new("world-time", DockWidgetCategory.Clocks, "", "Dünya Zamanı", "World Time",
            "Bir şehir daha, büyük rakamlarla ve saat farkıyla.", "One more city in big digits, with its offset.", 2.0),
        new("today", DockWidgetCategory.Clocks, "", "Bugün", "Today",
            "Tek kutuda gün, tarih, saat ve hava.", "Weekday, date, time and weather in a single tile.", 2.6),
        new("date", DockWidgetCategory.Clocks, "", "Tarih", "Date",
            "Büyük yazılmış tam tarih.", "The full date, set large.", 2.0),
        new("day-number", DockWidgetCategory.Clocks, "", "Takvim Yaprağı", "Day Number",
            "Bugünün tarihi bir takvim yaprağı olarak.", "Today's date as a calendar page.", 1.0),
        new("analog", DockWidgetCategory.Clocks, "", "Analog", "Analog Minimal",
            "Koyu kadranda akrep, yelkovan ve saat çizgileri; başka hiçbir şey.",
            "Hands and hour ticks on a dark face, nothing else.", 1.0),
        new("segment-clock", DockWidgetCategory.Clocks, "", "Segment Saat", "Segment Clock",
            "Gün ve tarihin altında yedi segmentli saat.", "The time as a seven-segment display, under the weekday and date.", 2.2),
        new("clock-weather", DockWidgetCategory.Clocks, "", "Saat ve Hava", "Clock & Weather",
            "Saat, tarih ve sıcaklık tek kare kutuda üç renk bloğu.",
            "Time, date and temperature as three colour blocks in one square tile.", 2.0),
    ];

    private static readonly Dictionary<string, DockWidgetDescriptor> ById =
        All.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

    public static DockWidgetDescriptor? Find(string? id) =>
        id is not null && ById.TryGetValue(id, out var d) ? d : null;

    public static IEnumerable<DockWidgetDescriptor> InCategory(DockWidgetCategory category) =>
        All.Where(d => d.Category == category);

    /// <summary>Known ids only, first occurrence wins, capped at <see cref="MaxWidgetsOnDock"/>.</summary>
    public static List<string> Sanitize(IEnumerable<string>? ids)
    {
        var result = new List<string>();
        if (ids is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ids)
        {
            var d = Find(raw?.Trim());
            if (d is null || !seen.Add(d.Id)) continue;
            result.Add(d.Id);
            if (result.Count >= MaxWidgetsOnDock) break;
        }
        return result;
    }

    public static IReadOnlyList<DockWidgetDescriptor> Search(string? query, string lang)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;
        string q = query.Trim();
        return All.Where(d =>
                d.Name(lang).Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                d.Description(lang).Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                CategoryName(d.Category, lang).Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                d.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static string CategoryName(DockWidgetCategory category, string lang) => category switch
    {
        DockWidgetCategory.Ai => "AI",
        DockWidgetCategory.Productivity => lang == "en" ? "Productivity" : "Üretkenlik",
        DockWidgetCategory.System => lang == "en" ? "System" : "Sistem",
        DockWidgetCategory.Media => lang == "en" ? "Media & Audio" : "Medya ve Ses",
        DockWidgetCategory.Apps => lang == "en" ? "Apps & Files" : "Uygulamalar ve Dosyalar",
        DockWidgetCategory.Weather => lang == "en" ? "Weather" : "Hava Durumu",
        DockWidgetCategory.Finance => lang == "en" ? "Finance" : "Finans",
        DockWidgetCategory.Clocks => lang == "en" ? "Clocks" : "Saatler",
        _ => category.ToString()
    };
}
