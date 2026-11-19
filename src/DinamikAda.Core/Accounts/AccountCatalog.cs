namespace DinamikAda.Core.Accounts;

/// <summary>One value the user pastes to connect a service (a key, a username, a URL...).</summary>
/// <param name="Key">Storage key inside the account's value map.</param>
/// <param name="Secret">Secrets are DPAPI-protected at rest and shown in a password box.</param>
/// <param name="Optional">The account counts as connected without it.</param>
public sealed record AccountField(
    string Key,
    string LabelTr,
    string LabelEn,
    bool Secret = false,
    string Placeholder = "",
    bool Optional = false)
{
    public string Label(string lang) => lang == "en" ? LabelEn : LabelTr;
}

/// <summary>A service a dock widget reads with the user's own read-only credentials.</summary>
public sealed record AccountServiceDescriptor(
    string Id,
    string Name,
    string Glyph,
    string DashboardUrl,
    IReadOnlyList<AccountField> Fields,
    IReadOnlyList<string> StepsTr,
    IReadOnlyList<string> StepsEn,
    int RefreshMinutes = 15)
{
    public IReadOnlyList<string> Steps(string lang) => lang == "en" ? StepsEn : StepsTr;
}

/// <summary>Every service the account widgets can connect to.</summary>
public static class AccountCatalog
{
    private static AccountField Key(string tr = "API anahtarı", string en = "API key", string placeholder = "") =>
        new("key", tr, en, Secret: true, Placeholder: placeholder);

    public static IReadOnlyList<AccountServiceDescriptor> All { get; } =
    [
        // Finance
        new("stripe", "Stripe", "", "https://dashboard.stripe.com/apikeys",
            [Key("Kısıtlı anahtar", "Restricted key", "rk_live_…")],
            ["Stripe panosunda Geliştiriciler → API anahtarları'nı aç.", "\"Kısıtlı anahtar oluştur\"a bas, Charges ve Balance için yalnızca Okuma izni ver.", "Oluşan rk_live_… anahtarını buraya yapıştır."],
            ["In the Stripe dashboard open Developers → API keys.", "Create a restricted key with Read access to Charges and Balance only.", "Paste the rk_live_… key here."]),
        new("lemonsqueezy", "Lemon Squeezy", "", "https://app.lemonsqueezy.com/settings/api",
            [Key()],
            ["Lemon Squeezy'de Settings → API'ye git.", "Yeni bir API anahtarı oluştur.", "Anahtarı buraya yapıştır."],
            ["In Lemon Squeezy go to Settings → API.", "Create a new API key.", "Paste the key here."]),
        new("gumroad", "Gumroad", "", "https://app.gumroad.com/settings/advanced",
            [Key("Erişim jetonu", "Access token")],
            ["Gumroad'da Settings → Advanced → Applications bölümünde bir uygulama oluştur.", "\"Generate access token\"a bas.", "Jetonu buraya yapıştır."],
            ["In Gumroad open Settings → Advanced → Applications and create an application.", "Press \"Generate access token\".", "Paste the token here."]),
        new("polar", "Polar", "", "https://polar.sh/settings",
            [Key("Organizasyon erişim jetonu", "Organization access token", "polar_oat_…")],
            ["Polar'da organizasyonunun Settings → Developers bölümünü aç.", "orders:read izinli bir Organization Access Token oluştur.", "Jetonu buraya yapıştır."],
            ["In Polar open your organization's Settings → Developers.", "Create an Organization Access Token with orders:read.", "Paste the token here."]),
        new("paddle", "Paddle", "", "https://vendors.paddle.com/authentication-v2",
            [Key("API anahtarı", "API key", "pdl_live_apikey_…")],
            ["Paddle'da Developer tools → Authentication'a git.", "Transactions için yalnızca okuma izinli bir API anahtarı oluştur.", "Anahtarı buraya yapıştır."],
            ["In Paddle go to Developer tools → Authentication.", "Create an API key with read-only access to Transactions.", "Paste the key here."]),

        // Developer & analytics
        new("github", "GitHub", "", "https://github.com/settings/personal-access-tokens",
            [new("user", "Kullanıcı adı", "Username", Placeholder: "octocat"),
             new("key", "Erişim jetonu (bildirimler için)", "Access token (for notifications)", Secret: true, Placeholder: "github_pat_…", Optional: true)],
            ["Katkı tablosu için yalnızca GitHub kullanıcı adın yeter.", "Bildirimler için github.com/settings/personal-access-tokens adresinden \"Notifications\" okuma izinli bir jeton oluştur (klasik jetonda notifications kapsamı).", "Kullanıcı adını ve istersen jetonu buraya yapıştır."],
            ["Your GitHub username is enough for the contribution grid.", "For notifications create a token with read access to Notifications at github.com/settings/personal-access-tokens (the notifications scope on a classic token).", "Paste the username and, if you want, the token here."], 10),
        new("linear", "Linear", "", "https://linear.app/settings/account/security",
            [Key("Kişisel API anahtarı", "Personal API key", "lin_api_…")],
            ["Linear'da Settings → Security & access → Personal API keys'e git.", "Yalnızca okuma izinli yeni bir anahtar oluştur.", "Anahtarı buraya yapıştır."],
            ["In Linear go to Settings → Security & access → Personal API keys.", "Create a new read-only key.", "Paste the key here."], 10),
        new("resend", "Resend", "", "https://resend.com/api-keys",
            [Key("API anahtarı", "API key", "re_…")],
            ["Resend panosunda API Keys'e git.", "\"Full access\" izinli bir anahtar oluştur (gönderilen e-postaları listelemek için gerekli).", "Anahtarı buraya yapıştır."],
            ["In the Resend dashboard go to API Keys.", "Create a key with Full access (needed to list sent emails).", "Paste the key here."]),
        new("plausible", "Plausible", "", "https://plausible.io/settings/api-keys",
            [Key("Stats API anahtarı", "Stats API key"),
             new("site", "Site alan adı", "Site domain", Placeholder: "ornek.com"),
             new("host", "Sunucu (kendi kurulumun varsa)", "Host (if self-hosted)", Placeholder: "https://plausible.io", Optional: true)],
            ["Plausible'da hesap ayarlarından API Keys'e git.", "Stats API türünde yeni bir anahtar oluştur.", "Anahtarı ve sitenin alan adını buraya yaz."],
            ["In Plausible open account settings → API Keys.", "Create a new Stats API key.", "Paste the key and the site's domain here."]),

        // Social & learning
        new("youtube", "YouTube", "", "https://console.cloud.google.com/apis/credentials",
            [Key("YouTube Data API anahtarı", "YouTube Data API key", "AIza…"),
             new("channel", "Kanal kimliği veya @kullanıcı", "Channel ID or @handle", Placeholder: "@kanalin")],
            ["Google Cloud Console'da bir proje aç ve YouTube Data API v3'ü etkinleştir.", "Credentials → Create credentials → API key ile bir anahtar oluştur.", "Anahtarı ve kanalının @kullanıcı adını (ya da UC… kimliğini) buraya yaz."],
            ["In Google Cloud Console create a project and enable YouTube Data API v3.", "Create a key with Credentials → Create credentials → API key.", "Paste the key and your channel's @handle (or UC… ID) here."], 30),
        new("leetcode", "LeetCode", "", "https://leetcode.com/",
            [new("user", "Kullanıcı adı", "Username", Placeholder: "kullanici")],
            ["LeetCode profil sayfandaki kullanıcı adını yaz.", "Anahtar gerekmez; yalnızca herkese açık profil bilgileri okunur."],
            ["Type the username from your LeetCode profile page.", "No key needed; only public profile data is read."], 30),

        // Mail & calendar
        new("gmail", "Gmail", "", "https://myaccount.google.com/apppasswords",
            [new("user", "Gmail adresi", "Gmail address", Placeholder: "ad@gmail.com"),
             new("key", "Uygulama şifresi", "App password", Secret: true, Placeholder: "abcd efgh ijkl mnop")],
            ["Google hesabında 2 adımlı doğrulamayı aç.", "myaccount.google.com/apppasswords adresinde \"DinamikAda\" adlı bir uygulama şifresi oluştur.", "Gmail adresini ve 16 harfli uygulama şifresini buraya yaz. Gerçek şifreni yazma.", "Okunmamış sayısı IMAP ile, yalnızca okunarak alınır."],
            ["Turn on 2-Step Verification for your Google account.", "Create an app password named \"DinamikAda\" at myaccount.google.com/apppasswords.", "Type your Gmail address and the 16-character app password here. Never your real password.", "The unread count is read over IMAP, read-only."], 5),
        new("gcal", "Google Takvim", "", "https://calendar.google.com/calendar/r/settings",
            [new("url", "Gizli iCal adresi", "Secret iCal address", Secret: true, Placeholder: "https://calendar.google.com/calendar/ical/…/basic.ics")],
            ["Google Takvim'de Ayarlar'ı aç ve sol listeden takvimini seç.", "\"Takvimi entegre et\" bölümündeki \"iCal biçimindeki gizli adres\"i kopyala.", "Adresi buraya yapıştır. Bu adres takvimini okumaya yeter, kimseyle paylaşma."],
            ["In Google Calendar open Settings and pick your calendar on the left.", "Under \"Integrate calendar\" copy the \"Secret address in iCal format\".", "Paste it here. It is enough to read your calendar, so don't share it."], 10),

        // AI & apps
        new("deepseek", "DeepSeek", "", "https://platform.deepseek.com/api_keys",
            [Key("API anahtarı", "API key", "sk-…")],
            ["DeepSeek platformunda API keys sayfasını aç.", "Yeni bir anahtar oluştur.", "Anahtarı buraya yapıştır; yalnızca bakiye okunur."],
            ["Open API keys on the DeepSeek platform.", "Create a new key.", "Paste it here; only the balance is read."], 30),
        new("unsplash", "Unsplash", "", "https://unsplash.com/oauth/applications",
            [Key("Access Key", "Access Key")],
            ["unsplash.com/developers'ta geliştirici olarak kaydol ve yeni bir uygulama oluştur.", "Uygulama sayfasındaki \"Access Key\"i kopyala.", "Anahtarı buraya yapıştır."],
            ["Register as a developer at unsplash.com/developers and create a new application.", "Copy the application's \"Access Key\".", "Paste the key here."], 60)
    ];

    public static AccountServiceDescriptor? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when every required field has a value.</summary>
    public static bool IsComplete(AccountServiceDescriptor service, Func<string, string?> valueOf) =>
        service.Fields.All(f => f.Optional || !string.IsNullOrWhiteSpace(valueOf(f.Key)));
}
