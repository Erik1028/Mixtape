# 🛠️ Troubleshooting

Fixes for the issues people hit most often. If none of these solve it, see **[Still stuck?](#still-stuck)** at the bottom.

---

### "Windows protected your PC" / SmartScreen blocks the app
Mixtape is community software and isn't code-signed, so Windows SmartScreen warns the first time.

**Fix:** On the blue dialog click **More info → Run anyway**. (You only have to do this once.) If your browser blocked the download instead, choose **Keep**.

---

### My iPod doesn't show up
- Make sure it's a **click-wheel iPod that mounts as a drive** — it should appear in File Explorer with its own drive letter. iPod Touch / iPhone don't mount as disks and aren't supported.
- Click **Refresh** in the bottom-left of the sidebar.
- If it still doesn't appear, click **Open folder** and point Mixtape at the iPod's **drive root** (the folder that contains an `iPod_Control` folder).
- Try a different **USB cable or port** — many sync issues are a charge-only cable.
- If the iPod is stuck syncing/charging, eject it safely and reconnect.

---

### "Add music" (or Add video / Add photos) is greyed out
This is almost always the iPod's **database signature**, not a bug. Apple signs the database on 2007-and-later iPods; writing the wrong signature would make the iPod show *0 songs*, so Mixtape only enables writing when it can produce a valid one.

**Fix — check why first:** click your iPod in the sidebar to open its **device page**. The **"Why read-only"** line tells you the exact reason:

- **hash58** (iPod Classic, nano 3G/4G) and it says the FireWire GUID wasn't found → click **"Read device ID"** on the device page. It's a safe, read-only query straight to the iPod (the same thing iTunes does) and enables writing once the ID is read and verified.
- **hash72** (nano 5G, Touch) → not supported; this iPod stays read-only by design. Browsing, playing and copying music *off* still work.
- **hashAB** (nano 6G/7G) → experimental and off by default; read-only for now.
- **No signature** (1G–5G, photo, mini, nano 1G/2G) → these are always writable; if one of these is read-only, it's usually a detection issue — use **Save report…** and open an issue.

| Signature | iPods | Writable? |
| --- | --- | --- |
| None | 1G–5G, photo, mini, nano 1G/2G | ✅ always |
| hash58 | Classic, nano 3G/4G | ✅ after **Read device ID** |
| hash72 / hashAB | nano 5G+, Touch | ❌ read-only (signature not reproducible yet) |

---

### My iPod shows "0 songs" or asks to Restore after a change
Mixtape backs up before every write and verifies afterward, so this should be rare — but if it happens:

**Fix:** restore the backup. Open the **device page → Restore…** (or **Settings → Safety → Restore…**). Mixtape keeps a rolling backup (`iTunesDB.bak`) and a one-time pristine copy (`iTunesDB.original`) in `iPod_Control\iTunes`, so you can roll back to before the last change — or to before Mixtape ever touched the iPod.

---

### Wrong iPod model, generation, or colour is shown
Mixtape reads the model number from the device; if it's missing, it infers the generation from the photo/artwork formats on the iPod.

**Fix:** use **Save report…** on the device page and open an issue with it — the model can be added to the table. (A wrong colour just affects the on-screen picture; it doesn't affect anything you copy.)

---

### Video won't copy / "ffmpeg not found"
Converting video (and some audio formats the iPod can't play) needs **FFmpeg**, which isn't bundled.

**Fix:** install [FFmpeg](https://ffmpeg.org/) and point Mixtape at it in **Settings → Video → Browse…** (select `ffmpeg.exe`). You can also pick the conversion quality there — *iPod-safe* plays everywhere; *High* is Classic / 5.5G only.

---

### Photos won't show or can't be added
Only **colour-screen** iPods support photos (photo, 5G video, Classic, nano 3G+).

**Fix:** make sure **Show Photos** is on in **Settings → Library**. If the iPod has never held photos, the Photos area is created on the first import.

---

### A change I made isn't showing
Click **Refresh** in the sidebar to re-read the iPod. Check the status line at the bottom of the window — it reports the result of the last action (and any warnings).

---

### The app takes a moment to start
A self-contained build unpacks on first launch, so the very first start can be a second or two slower than later ones. This is normal.

---

## Still stuck?
1. Open your iPod's **device page** and click **Save report…** — it writes a small diagnostic file (model, signature, why it's read-only, drive info).
2. Open an issue at **<https://github.com/fgs8z2n9qh-tech/Mixtape/issues>** and attach that report (it contains no personal data — no song titles or files, just device details).

Please **always keep your own backup** of the iPod's contents before writing, and test with one song first on a device you care about.
