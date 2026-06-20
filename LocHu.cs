namespace iPodCommander;

/// <summary>English-key → Hungarian (Magyar) UI translations. Keys must match the English literal passed to
/// <see cref="Loc.T(string)"/> EXACTLY (including punctuation, ellipsis "…", and {0} placeholders). Missing
/// keys fall back to English, so partial coverage degrades gracefully. Keep translations natural, not literal.</summary>
internal static partial class LocHu
{
    public static readonly Dictionary<string, string> Map;

    static LocHu()
    {
        // The hand-curated core wins; the workflow-generated bulk (LocHuGen.cs) fills the rest. A static ctor runs
        // AFTER every static field initializer (in BOTH partial files, whose cross-file order is otherwise
        // unspecified), so Curated + Generated are both populated here. TryAdd keeps the curated value on a clash
        // and silently ignores duplicate generated keys.
        foreach (var (en, hu) in Generated) Curated.TryAdd(en, hu);
        Map = Curated;
    }

    private static readonly Dictionary<string, string> Curated = new(StringComparer.Ordinal)
    {
        // ---- Sidebar sections & navigation ----
        ["DEVICE"] = "ESZKÖZ",
        ["LIBRARY"] = "KÖNYVTÁR",
        ["PLAYLISTS"] = "LEJÁTSZÁSI LISTÁK",
        ["ON THIS PC"] = "EZEN A GÉPEN",
        ["All songs"] = "Összes dal",
        ["Albums"] = "Albumok",
        ["Artists"] = "Előadók",
        ["Videos"] = "Videók",
        ["Photos"] = "Fényképek",
        ["Local Music"] = "Helyi zene",
        ["Refresh"] = "Frissítés",
        ["Open folder"] = "Megnyitás",

        // ---- Header (library views) ----
        ["Cover Flow"] = "Cover Flow",
        ["Add music"] = "Zene hozzáadása",
        ["Add videos"] = "Videók hozzáadása",
        ["Add photos"] = "Fényképek hozzáadása",
        ["Delete"] = "Törlés",
        ["Manage…"] = "Kezelés…",
        ["Search songs, artists, albums…"] = "Dalok, előadók, albumok keresése…",
        ["{0} songs"] = "{0} dal",
        ["{0} albums"] = "{0} album",
        ["{0} artists"] = "{0} előadó",
        ["{0} photos"] = "{0} fénykép",
        ["{0} videos"] = "{0} videó",

        // ---- Column headers ----
        ["SONG"] = "DAL",
        ["ARTIST"] = "ELŐADÓ",
        ["ALBUM"] = "ALBUM",
        ["RATING"] = "ÉRTÉKELÉS",
        ["PLAYS"] = "LEJÁTSZÁS",
        ["ADDED"] = "HOZZÁADVA",
        ["TIME"] = "IDŐ",

        // ---- Now-playing bar ----
        ["Nothing playing"] = "Nincs lejátszás",
        ["Pick a song to start"] = "Válassz egy dalt a kezdéshez",

        // ---- Settings: categories ----
        ["Settings"] = "Beállítások",
        ["Appearance"] = "Megjelenés",
        ["Library"] = "Könyvtár",
        ["Video"] = "Videó",
        ["Safety"] = "Biztonság",
        ["This iPod"] = "Ez az iPod",
        ["About"] = "Névjegy",

        // ---- Settings: Appearance ----
        ["Accent colour"] = "Kiemelőszín",
        ["Used for highlights, buttons and selection."] = "Kiemelésekhez, gombokhoz és kijelöléshez.",
        ["Background"] = "Háttér",
        ["The window's colour palette."] = "Az ablak színpalettája.",
        ["Row density"] = "Sorsűrűség",
        ["How tall the song rows are."] = "Milyen magasak a dalsorok.",
        ["Comfortable"] = "Kényelmes",
        ["Compact"] = "Tömör",
        ["Show artwork"] = "Borítók megjelenítése",
        ["Show album/photo covers in lists."] = "Album- és fényképborítók megjelenítése a listákban.",
        ["Language"] = "Nyelv",
        ["Choose the app's language. Mixtape restarts to apply."] = "Válaszd ki az alkalmazás nyelvét. A Mixtape újraindul a módosítás alkalmazásához.",

        // ---- Settings: Library ----
        ["Default sort"] = "Rendezés",
        ["Column a list is sorted by when it opens."] = "Az oszlop, amely szerint a lista megnyitáskor rendeződik.",
        ["Sort descending"] = "Csökkenő sorrend",
        ["Reverse the default sort order."] = "Az alapértelmezett rendezés megfordítása.",
        ["Show Videos"] = "Videók megjelenítése",
        ["List the Videos library (video-capable iPods)."] = "A Videók könyvtár megjelenítése (videóképes iPodokon).",
        ["Show Photos"] = "Fényképek megjelenítése",
        ["List the Photos library (colour-screen iPods)."] = "A Fényképek könyvtár megjelenítése (színes kijelzős iPodokon).",
        ["Artist column"] = "Előadó oszlop",
        ["Show the Artist column in the song list."] = "Az Előadó oszlop megjelenítése a dallistában.",
        ["Album column"] = "Album oszlop",
        ["Show the Album column in the song list."] = "Az Album oszlop megjelenítése a dallistában.",
        ["Star rating column"] = "Csillagos értékelés oszlop",
        ["Show your star ratings in the song list."] = "A csillagos értékelések megjelenítése a dallistában.",
        ["Play count column"] = "Lejátszásszám oszlop",
        ["Show how many times each song has been played."] = "Hányszor játszották le az egyes dalokat.",
        ["Date added column"] = "Hozzáadás dátuma oszlop",
        ["Show when each song was added to the iPod."] = "Mikor került az egyes dalok az iPodra.",
        ["Time column"] = "Idő oszlop",
        ["Show the Time column in the song list."] = "Az Idő oszlop megjelenítése a dallistában.",

        // ---- Settings: Video ----
        ["Quality"] = "Minőség",
        ["Always re-encode"] = "Mindig újrakódolás",
        ["Convert even files that already look compatible."] = "A már kompatibilisnek tűnő fájlok konvertálása is.",
        ["Browse…"] = "Tallózás…",

        // ---- Settings: Photos ----
        ["Store full-screen image"] = "Teljes képernyős kép tárolása",

        // ---- Settings: Safety ----
        ["Confirm before writing"] = "Megerősítés írás előtt",
        ["Show a reminder before the first change each session."] = "Emlékeztető megjelenítése az első módosítás előtt minden munkamenetben.",
        ["Database backup"] = "Adatbázis biztonsági mentése",
        ["Restore…"] = "Visszaállítás…",

        // ---- Settings: This iPod ----
        ["Model"] = "Modell",
        ["Generation"] = "Generáció",
        ["Capacity"] = "Kapacitás",
        ["Writable"] = "Írható",
        ["Plays video"] = "Videolejátszás",
        ["Shows photos"] = "Fényképmegjelenítés",
        ["Yes"] = "Igen",
        ["No"] = "Nem",
        ["No iPod"] = "Nincs iPod",
        ["Connect an iPod to see its details."] = "Csatlakoztass egy iPodot a részletek megtekintéséhez.",

        // ---- Common buttons / dialogs ----
        ["OK"] = "OK",
        ["Cancel"] = "Mégse",
        ["Close"] = "Bezárás",
        ["Yes, delete"] = "Igen, törlés",
        ["Restart now"] = "Újraindítás most",
        ["Later"] = "Később",
        ["Restart Mixtape?"] = "Újraindítod a Mixtape-et?",
        ["The language changes after a restart. Restart Mixtape now?"] = "A nyelv újraindítás után változik meg. Újraindítod most a Mixtape-et?",

        // ---- Sort values (display-translated; stored in English) ----
        ["Playlist"] = "Lejátszási lista",
        ["Song"] = "Dal",
        ["Artist"] = "Előadó",
        ["Added"] = "Hozzáadva",
        ["Time"] = "Idő",
        ["Album"] = "Album",

        // ---- Settings: Video (more) ----
        ["iPod-safe"] = "iPod-barát",
        ["High (Classic)"] = "Magas (Classic)",
        ["iPod-safe (320×240) plays on every model; High (640×480) is Classic/5.5G only."] = "Az iPod-barát (320×240) minden modellen lejátszható; a Magas (640×480) csak Classic/5.5G.",
        ["Locate ffmpeg.exe"] = "Az ffmpeg.exe megkeresése",
        ["Not found — install ffmpeg or browse to ffmpeg.exe to enable video conversion."] = "Nem található — telepítsd az ffmpeg-et, vagy keresd meg az ffmpeg.exe-t a videokonvertálás engedélyezéséhez.",
        ["Found: {0}"] = "Megtalálva: {0}",

        // ---- Settings: Photos (more) ----
        ["Also write the 320×240 image so photos look sharp on the iPod (uses more space)."] = "A 320×240-es kép is mentésre kerül, hogy a fényképek élesek legyenek az iPodon (több helyet foglal).",

        // ---- Settings: Safety (more) ----
        ["Auto device-ID recovery"] = "Automatikus eszközazonosító-helyreállítás",
        ["When a hash58 iPod with no stored ID is plugged in, offer to read its hardware ID automatically (a safe, read-only query) so music can be written — no hunting for the “Read device ID” button."] = "Amikor egy tárolt azonosító nélküli hash58-as iPodot csatlakoztatsz, a Mixtape felajánlja a hardveres azonosító automatikus beolvasását (biztonságos, csak olvasó művelet), hogy zenét lehessen rá írni — nem kell keresgélni az „Eszközazonosító beolvasása” gombot.",
        ["Mixtape backs up before every change and verifies the result. Restore rolls back to the previous state."] = "A Mixtape minden módosítás előtt biztonsági mentést készít, és ellenőrzi az eredményt. A visszaállítás az előző állapotot állítja helyre.",

        // ---- Settings: This iPod (more) ----
        ["Signature"] = "Aláírás",
        ["Serial"] = "Sorozatszám",
        ["FireWire GUID"] = "FireWire GUID",
        ["Why read-only"] = "Miért csak olvasható",

        // ---- Settings: About ----
        ["Version {0}"] = "{0} verzió",
        ["A friendly manager for classic iPods"] = "Barátságos kezelő klasszikus iPodokhoz",
        ["Copy music, videos and photos; make playlists and mixtapes; choose covers — all written natively, no iTunes."] = "Másolj zenét, videókat és fényképeket; készíts lejátszási listákat és mixeket; válassz borítókat — mindezt natívan, iTunes nélkül.",

        // ---- Restore dialog ----
        ["Restore"] = "Visszaállítás",
        ["No database backup was found on this iPod yet."] = "Még nincs adatbázis-mentés ezen az iPodon.",
        ["the state before the last change (iTunesDB.bak)"] = "az utolsó módosítás előtti állapotra (iTunesDB.bak)",
        ["the original database from before Mixtape first wrote to it"] = "az eredeti adatbázisra, mielőtt a Mixtape először írt rá",
        ["Restore {0}?\n\nThe current database will be replaced."] = "Visszaállítod {0}?\n\nA jelenlegi adatbázis felülíródik.",
        ["Restore database"] = "Adatbázis visszaállítása",
        ["Restore failed:\n\n{0}"] = "A visszaállítás nem sikerült:\n\n{0}",
        ["Database restored."] = "Az adatbázis visszaállt.",

        // ---- Main window: headers, buttons, empty states, status ----
        ["PLAYLIST"] = "LEJÁTSZÁSI LISTA",
        ["No iPod is connected."] = "Nincs csatlakoztatott iPod.",
        ["Right-click here to add one"] = "Kattints ide jobb gombbal egy létrehozásához",
        ["Right-click to add a playlist"] = "Jobbkattintás új lejátszási listához",
        ["Untitled"] = "Névtelen",
        ["Untitled playlist"] = "Névtelen lejátszási lista",
        ["Unknown Artist"] = "Ismeretlen előadó",
        ["Add video"] = "Videó hozzáadása",
        ["Add folder"] = "Mappa hozzáadása",
        ["Add a folder first."] = "Előbb adj hozzá egy mappát.",
        ["Music from folders on your PC"] = "Zene a géped mappáiból",
        ["No results for “{0}”"] = "Nincs találat erre: „{0}”",
        ["This playlist is empty."] = "Ez a lejátszási lista üres.",
        ["No videos on this iPod."] = "Nincsenek videók ezen az iPodon.",
        ["No songs on this iPod."] = "Nincsenek dalok ezen az iPodon.",
        ["No albums on this iPod."] = "Nincsenek albumok ezen az iPodon.",
        ["No artists on this iPod."] = "Nincsenek előadók ezen az iPodon.",
        ["This iPod can't display photos."] = "Ez az iPod nem tud fényképeket megjeleníteni.",
        ["No photos yet — click “Add photos”."] = "Még nincsenek fényképek — kattints a „Fényképek hozzáadása” gombra.",
        ["Photos are read-only."] = "A fényképek csak olvashatók.",
        ["Photos are read-only on this iPod."] = "A fényképek csak olvashatók ezen az iPodon.",
        ["⚠ {0} warning(s)"] = "⚠ {0} figyelmeztetés",
        ["Read-only — {0}"] = "Csak olvasható — {0}",

        // ---- Wallpaper pack ----
        ["Add wallpapers"] = "Háttérképek hozzáadása",
        ["Add wallpapers…"] = "Háttérképek hozzáadása…",
        ["Pick wallpapers to add to your iPod's Photos. View them full-screen or as a slideshow on the device."] = "Válassz háttérképeket az iPod Fényképeihez. Teljes képernyőn vagy diavetítésként nézheted meg az eszközön.",
        ["Add to iPod"] = "Hozzáadás az iPodhoz",
        ["Add {0} to iPod"] = "{0} hozzáadása az iPodhoz",
        ["Adding wallpapers to your iPod"] = "Háttérképek hozzáadása az iPodhoz",
        ["Added {0} wallpaper(s)."] = "{0} háttérkép hozzáadva.",
        ["Stopped — added {0} wallpaper(s)."] = "Leállítva — {0} háttérkép hozzáadva.",
        ["Brushed Silver"] = "Szálcsiszolt ezüst",
        ["Graphite"] = "Grafit",
        ["Vinyl"] = "Bakelit",
        ["Cassette"] = "Kazetta",
        ["Aurora"] = "Aurora",
        ["Sunset"] = "Naplemente",
        ["Click Wheel"] = "Vezérlőkerék",
        ["Carbon"] = "Karbon",

        // ---- Duration (abbreviations, matching the English hr/min style) ----
        ["{0} hr"] = "{0} ó",
        ["{0} hr {1} min"] = "{0} ó {1} p",
        ["{0} min"] = "{0} p",
    };
}
