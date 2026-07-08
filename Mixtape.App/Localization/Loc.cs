namespace iPodCommander;

/// <summary>
/// Lightweight UI localization. The ENGLISH text is the key: a per-language table maps it to a
/// translation and falls back to the English key when one is missing — so an untranslated string still
/// reads fine instead of showing a blank or a raw token. <see cref="Lang"/> is set ONCE at startup from
/// <see cref="AppSettings.Language"/>; changing the language restarts the app, so it never changes mid-run.
/// Call sites wrap their literals: <c>Loc.T("All songs")</c>, or <c>Loc.T("Deleted {0} photo(s).", n)</c>.
/// </summary>
internal static class Loc
{
    /// <summary>Active language code: "en" (English — keys are used verbatim) or "hu" (Magyar).</summary>
    public static string Lang = "en";

    /// <summary>The languages the picker offers: code + the name shown in that language.</summary>
    public static readonly (string Code, string Native)[] Languages =
    {
        ("en", "English"),
        ("hu", "Magyar"),
    };

    /// <summary>Resolve a stored setting ("", "en", "hu") to an active code. "" / unknown = auto-detect from
    /// the OS UI language (Hungarian Windows → Hungarian), so a Hungarian user gets Hungarian on first run.</summary>
    public static string Resolve(string? stored)
    {
        if (stored == "hu" || stored == "en") return stored;
        try { return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "hu" ? "hu" : "en"; }
        catch { return "en"; }
    }

    /// <summary>Translate an English UI string for the active language (falls back to the English key).</summary>
    public static string T(string en) => Lang == "hu" && Hu.TryGetValue(en, out var v) ? v : en;

    /// <summary>Translate, then <see cref="string.Format(string, object?[])"/> — for templated strings such as
    /// <c>T("Deleted {0} photo(s).", count)</c>. The template's {0}/{1}… placeholders are kept in the translation.</summary>
    public static string T(string en, params object?[] args) => string.Format(T(en), args);

    // English-key → Hungarian. Curated in LocHu.cs.
    private static readonly Dictionary<string, string> Hu = LocHu.Map;
}
