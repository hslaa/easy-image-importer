# EasyImageImporter — Technical Plan

Companion to [viltkamera-app-spec.md](viltkamera-app-spec.md). The spec says *what*; this says *how*.

**Status:** stack decided, nothing built yet.

## 0. Decisions

| Area | Choice | Why |
|---|---|---|
| Runtime | **.NET 10 (LTS)**, C# (currently built on .NET 9 until the .NET 10 SDK is installed; one line in `Directory.Build.props`) | One language end to end. Runs natively on the dev Mac (Apple Silicon); `dotnet publish -r win-x64` builds the Windows app straight from macOS. |
| UI | **Avalonia 12** + CommunityToolkit.Mvvm, Fluent theme | Same Skia renderer on Mac and Windows, so what you see in dev is what the user sees. Built-in `TrayIcon`. |
| State | **SQLite** (Microsoft.Data.Sqlite, WAL mode) | A transactional journal is what makes copy/review resumable. One file in `%LOCALAPPDATA%\EasyImageImporterData`. |
| Hashing | **SHA-256** (`System.Security.Cryptography`, hardware-accelerated) | Boring and standard. SD-card read speed is the bottleneck, not the hash. |
| Image decode / thumbs | **SkiaSharp** (already an Avalonia dependency), with the EXIF embedded thumbnail first | Scaled JPEG decode (1/8) is fast on old CPUs. |
| Metadata read | **MetadataExtractor** (pure .NET) | Fast `DateTimeOriginal`, make/model and serial at scan time. |
| Metadata write | **ExifTool**, bundled, driven in `-stay_open` batch mode | The only tool that reliably writes *both* XMP `dc:*` and Windows `XP*` tags with æøå intact. |
| Site grouping | **Classical image comparison** on small background images (SkiaSharp + plain C#) | Needs no ML model, so nothing heavy to ship or run on an older PC. |
| ML (later) | **ONNX Runtime** (CPU; DirectML on Windows when available) | Deferred. Only added for empty-frame/species detection, or if classical site matching turns out too weak on real cards. |
| Installer + updates | **Velopack** → GitHub Releases | Setup.exe, silent delta auto-update, shortcuts, no admin rights needed. |
| Signing | **Azure Trusted Signing** in CI | Avoids the SmartScreen "unknown publisher" wall, which would stop the user cold. About $10/month. |
| Logging | Serilog → rolling files | "Lag feilrapport" zips logs to the desktop so he can email them. |
| Tests | xUnit + fault-injection file-system layer; Avalonia.Headless for a few UI flows | The safety core must be tested under failure, not just the happy path. |

## 1. Solution layout

```
EasyImageImporter.sln
src/
  EasyImageImporter.Core/          Domain + pipeline. No UI, no platform code. Most tests target this.
    Cards/                  Card detection abstraction, card identity
    Import/                 Scan, copy+verify, session state machine, finalize, undo
    Grouping/               Sequence (hendelse) + site (sted) grouping, background plates, image similarity
    Metadata/               EXIF read, ExifTool writer, OM DENNE MAPPEN.txt
    Storage/                SQLite schema, migrations, repositories
    Naming/                 Folder/file naming, sanitising, collision handling
  EasyImageImporter.Ml/            (later) ONNX Runtime wrappers: detector, classifier
  EasyImageImporter.Platform/      IPlatform: Pictures folder, open-in-Explorer/Finder, autostart, single instance
    Windows/  MacOS/
  EasyImageImporter.App/           Avalonia app: tray, windows, views, view-models, nb-NO resources
tests/
  EasyImageImporter.Core.Tests/
  EasyImageImporter.Core.Tests/Grouping/  Golden tests on real card dumps: expected sequences + site boundaries
  fixtures/cards/           Synthetic cards (public camera-trap data), home-made cards, later the user's real ones
tools/
  exiftool/                 Pinned ExifTool builds for win-x64 and macOS
  models/                   ONNX models + license notes
.github/workflows/          ci.yml (mac + windows), release.yml (windows, sign, vpk)
```

Rule: `Core` never references Avalonia or anything platform-specific. The UI is a thin renderer of state that lives in the database.

## 2. The safety core (M1)

This is the part that must not be wrong. Everything else can be iterated.

### 2.1 Session state machine

Every card insert creates (or resumes) an **import session**, persisted in SQLite. The UI shows whatever state the session is in, so "close the app and come back three days later" works for free.

```
Detected → Scanning → Copying ⇄ PausedCardMissing → Copied
        → Reviewing → Naming → Finalizing → Imported → CardErased
                                              ↘ UndoneToReview (≤ 24 h)
```

State transitions happen in DB transactions. Long operations (copy, finalize, erase) are idempotent per file, so a crash anywhere means "run the same step again".

### 2.2 Card detection and identity

- **Detection:** poll `DriveInfo.GetDrives()` every ~2 s for a ready, non-system volume containing `DCIM/`. Polling is dull, works the same on Windows and in `/Volumes` on macOS, and avoids fragile WMI/DiskArbitration event code. A folder picker ("Velg mappe manuelt") covers odd card readers.
- **Identity:** volume serial (Windows) / volume UUID (macOS) + label, plus camera make/model/serial from EXIF. Used to resume a paused session when the same card comes back, and later as a strong hint for site matching.

### 2.3 Scan ("Fant 1 247 bilder.")

The normal cycle is **view and organise → import → empty the card**, so a card normally holds only new images. Leftovers from earlier imports are the exception (erase was skipped or refused). The scan is kept simple for that reason:

1. Enumerate all files under the card and count them (not just JPEGs; videos and other media are copied too so that erase is safe, even if v1 does not review them). This takes only a file listing, so it's instant.
2. There is no separate dedupe pass. Every file is hashed during copy anyway (§2.4). If a file's hash is already in `known_files`, it is marked `duplicate`: its staging copy is dropped, it is left out of review, and it is still included in the erase set.
3. Only when duplicates were found, the copy screen says so afterwards: "215 av bildene var importert fra før og hoppes over."

### 2.4 Copy + verify

Per file, for each `pending` row:

1. Read the source once, streaming, **hashing as we read**, writing to `staging/<session>/<id>.tmp` opened with `FileOptions.WriteThrough`.
2. `Flush(flushToDisk: true)`, close.
3. Re-open the staging file and hash it. On Windows, read unbuffered (`FILE_FLAG_NO_BUFFERING`, aligned buffers) so we verify the disk and not the OS page cache.
4. Hashes equal → rename `.tmp` → final name, mark `verified` in the same transaction. Not equal → retry up to 3 times, then mark `failed`. A failed file is shown loudly and blocks erase.

Failure handling:

| Event | Behaviour |
|---|---|
| Card pulled | `IOException` → session `PausedCardMissing`, "Sett inn kortet igjen for å fortsette". Resumes when card identity matches. |
| Sleep / power loss / app killed | On start: delete leftover `.tmp` files, reset `copying` → `pending`, continue. |
| Disk full | Pre-flight check before copying: bytes needed + 10 % margin, against both staging and the final volume. If still hit mid-copy: pause with a plain message, never a half-written "verified" file. |
| Duplicate names across placements | Staging uses DB ids, not camera filenames, so collisions are impossible until naming (§5). |

**Staging location:** `%LOCALAPPDATA%\EasyImageImporterData\staging`. If it is on the same volume as the destination, finalize is a rename (instant, no extra space). If not, finalize does copy → verify → delete-from-staging with the same routine.

### 2.5 "Discard never means delete"

There is no code path that calls `File.Delete` on a user image except (a) removing a verified staging duplicate after it has been verified at its final location, and (b) the gated card erase. Both go through one small, heavily tested `SafeDelete` service that requires the hash of a verified copy elsewhere. Discarded images are *moved* to `Sortert bort\`.

### 2.6 Erase gate

- The erase screen is not in the navigation graph until the session is `Imported` **and** every card file in the session is `verified` or `duplicate` (hash matches an already-archived file).
- Erase set = exactly those files. Shown as a count: "312 bilder er kopiert og kontrollert."
- Confirmation: a separate dialog with a clearly labelled "Ja, slett 312 bilder fra kortet" button, not default-focused, so Enter or a double click can't trigger it.
- Before deleting each file: **re-hash the card file** and require a match with a verified copy in the DB. This also catches the rare flaky card read that produced a consistent but wrong copy.
- Delete files one by one; leave folders and camera system files alone (no formatting, as some cameras are fussy about their own directory layout).
- Any failure → stop, explain, erase nothing further.

### 2.7 Undo ("angre hele importen", 24 h)

Finalize writes a **move manifest** (from → to for every file) to the DB. Undo replays it in reverse: files go back to staging, the session returns to `Reviewing`, and folders/sidecar created by the import are removed if empty. Undo never deletes an image. It stays available after the card is erased, because the archive then holds the only copy and undo just moves it back.

## 3. Data model (SQLite)

```
cards(id, volume_id, label, camera_make, camera_model, camera_serial, first_seen)
sessions(id, card_id, source_root, state, created_at, updated_at, import_id NULL)
session_files(id, session_id, rel_path, size, mtime, sha256,
              status[pending|copying|verified|duplicate|failed], attempts, staging_name,
              taken_at, taken_at_source[exif|mtime|order], media_type)
known_files(sha256 PK, size, import_id, final_path)
sequences(id, session_id, site_group_id, start, end, label, label_confidence, user_label)
sequence_frames(sequence_id, file_id, keep BOOL default 1, best_score)
site_groups(id, session_id, site_id NULL, name, description)
sequence_plates(sequence_id, kind[day|night], plate BLOB)   -- background plate, see §4.2
sites(id, name, created_at)                           -- remembered across seasons
site_prototypes(site_id, kind[day|night], plate BLOB, updated_at)
tags(id, text UNIQUE)                                  -- autocomplete
site_group_tags(site_group_id, tag_id)
imports(id, session_id, folder_path, created_at, image_count, undone_at NULL)
import_moves(import_id, file_id, from_path, to_path)
-- later: detections(file_id, model_version, class, confidence, bbox), cached by content hash
```

Everything the UI shows is recoverable from this DB plus the files. Schema migrations are versioned from day one.

## 4. Grouping

### 4.1 Hendelse (sequence), M3

- Sort by `taken_at` within a camera. Start a new sequence when the gap exceeds a threshold (start at **3 min**, tune on real cards).
- **Bad clocks:** detect obviously reset clocks (year < 2015, time going backwards against file-number order, clusters at `2000-01-01`). Fall back to camera file-number order, with gaps inferred from the file mtime, and show the date span as "ukjent" so he can fix it.
- A sequence shows as one card: representative thumbnail, count, time span, and an optional species label he can type (autocompletes from earlier labels). In v1 that manual label is what feeds species tags and the species part of file names; without a label that part is left out. Split and merge are cheap DB operations.

### 4.2 Sted (site): time + frame, M4

One card usually comes from **one camera**. The user moves that camera between placements without emptying the card. So within a card, sites are **contiguous stretches of time**, and the task is to find the points where the camera was moved. That is much easier and more robust than free-form clustering.

**Background plate per sequence.** For each sequence:
1. Take up to ~15 frames spread across it, decoded small (~160×120 greyscale, scaled JPEG decode, cheap).
2. Crop away the camera's info bar (the date/temperature strip at the top or bottom; its position is detected once per camera model from its constant layout).
3. Take the **per-pixel median** across the frames. A moving animal is at a different place in each frame, so the median removes it and leaves the empty scene: the background plate. For single-frame or very short sequences, use the frame itself and trust it less.
4. Classify as **day** or **night/IR** (IR frames have near-zero colour saturation). Plates are only ever compared day-to-day or night-to-night.

**Comparing plates.** Compare structure, not brightness, so it survives changing light, weather and seasons: normalised cross-correlation of **gradient-magnitude images** (tree trunks, horizon, stones), after a small ±5 % shift search so a nudged camera still matches. Result: a similarity score of 0–1.

**Finding the moves.** Walk the sequences in time order and put a boundary where:
- the plate similarity to the previous same-kind (day/night) sequence drops below a threshold, **and/or**
- there is a long gap (e.g. > 12 h with no triggers, typical of the camera being off or being moved), and/or
- EXIF camera serial/model changes (a second camera's images on the same card).

A time gap alone never creates a boundary (a quiet night at the same spot is normal). A time gap makes a lower similarity count as a boundary. Short sequences right at a boundary are often the user setting up the camera; they join the site that comes *after*.

**Revisiting a site** (A → B → A on one card): after segmentation, segments whose plates match are offered as one site: "Dette ser ut som samme sted som 12.–15. september". The user confirms; the app never merges silently.

**Remembered sites (across seasons):** each confirmed site stores a day and a night prototype plate. A new segment is compared with all known prototypes; a good match pre-fills the name: "Dette ser ut som Høgfjellåsen". Prototypes are updated on each confirmed import, which tracks slow changes (vegetation, a camera moved slightly).

**Fallbacks:** if the clock is unreliable (§4.1), order comes from file numbering and the method still works, because it uses time *order* more than absolute time. If the similarity is ambiguous, the app suggests a boundary rather than hiding one; splitting and merging sites is a single click either way.

**Tuning without the user's cards (for now):**
- **Synthetic cards from public camera-trap data.** The LILA BC datasets (lila.science) use the COCO Camera Traps format, which records a *location* and a *datetime* for every image. That is exactly the ground truth needed: build fake "cards" by concatenating 2–4 locations in time order, including an A → B → A revisit, and the correct site boundaries are known. Prefer datasets with forest/vegetation scenes and IR night images, since those are closest to his.
- **Stress variants** generated from the same data: a nudged camera (small crop/shift), a reset clock (timestamps set to 2000-01-01), a single-frame visit, a long quiet gap at the same site.
- **A few home-made cards.** Henrik puts any camera (even a phone on a tripod, in burst mode) at 2–3 spots in the garden, day and dusk. That tests the whole path from card to archive, including real EXIF and folder layouts.
- **Conservative defaults.** Until tuned on real data, lean towards suggesting a boundary too many rather than too few. Merging two sites is one click and obvious; noticing that two sites were wrongly lumped together is not.
- **Real data arrives by itself.** M1–M3 ship before site grouping, so by the time M4 is tuned, his own imports exist in his archive and can be copied on the next visit. Each time he splits or merges a site, the app also records the correction (sequence ids and timestamps, no images) in the DB. "Lag feilrapport" includes it, so the thresholds can be checked against what he actually did.

All of these live in `tests/fixtures/cards` (or outside the repo, if too large) with the expected boundaries written down, and double as regression tests.

**If this turns out too weak** on his real images (heavy foliage movement, snow changing the scene), the upgrade path is to swap the plate comparison for a small general-purpose image embedding model via ONNX Runtime (MobileNet/DINOv2-small class, ~10–150 ms per plate on CPU, and only one per sequence). The segmentation logic above stays the same; only the similarity function changes.

## 5. Naming, metadata, output (M5)

- **Destination root:** resolve the real Pictures folder via `SHGetKnownFolderPath(FOLDERID_Pictures)` (Explorer *displays* it as "Bilder"). Then `Viltkamera\<yyyy>\<yyyy-MM-dd> <Sted>\`.
- **File names:** `2026-09-18_Høgfjellåsen_0712_Nøtteskrike_001.jpg`. Normalise to **Unicode NFC** (macOS dev machines produce NFD, and æøå would round-trip wrong), strip Windows-reserved characters and trailing dots/spaces, keep total path well under 260 characters, resolve collisions with a counter.
- **Metadata** (via ExifTool, written to the final archive copy only):
  - `XMP-dc:Subject` + `XPKeywords` ← tags + species (Explorer "Koder/Tags")
  - `XMP-dc:Description` + `XPComment` ← description
  - `XMP-dc:Title` + `XPTitle` ← site name
  - Pixels are never re-encoded. The file's hash changes after this, so `known_files` stores the *original* card hash as the dedupe key.
- **`OM DENNE MAPPEN.txt`:** UTF-8 with BOM (so old Notepad shows æøå correctly), CRLF line endings. Site, description, date span, counts kept/discarded, species list, import date.
- **Mine importer:** reads `imports`. Each row has an "Åpne mappen" button (`explorer.exe "<path>"`). If the folder has been moved or deleted, show it as missing instead of crashing.

## 6. Detection (later, after v1)

Deferred: v1 ships without any ML model. Grouping by time + frame (§4) carries the review. When this is picked up, the user's older PC means it is **CPU-first, background, sampled, cached, and optional**.

- **Empty frames:** MegaDetector (animal / person / vehicle / empty). Use a *compact* MDv6 variant at 640 px rather than MDv5a at 1280 px, which is too slow on an old CPU. Check the variant's license (some MDv6 weights are AGPL via Ultralytics; there are MIT/Apache-licensed variants).
- **Scheduling:** runs after copy on a low-priority background thread. First pass samples each sequence (first, middle, last, plus a few more) so filter chips show up within a minute or two. A second pass fills in the rest while he reviews. Results are cached per content hash, so reopening costs nothing.
- **Species:** run a classifier on MegaDetector crops only. Candidates: **SpeciesNet** (Apache-2.0, broad coverage including birds; convert to ONNX), or **BioCLIP** zero-shot against the user's own short species list (open question 7). Decide by measuring on *his* images: by then, the labels he has given sequences by hand are a labelled dataset for free. Labels are mapped to Norwegian names through a small table (Artsdatabanken names). An allowlist of Norwegian species cuts false positives.
- **Best frames:** no new model. Score = sharpness (variance of Laplacian; on the detection crop once detection exists, otherwise on the region that differs most from the background plate) × subject size/centering, then pick the top N spread out in time so the picks aren't five nearly identical frames.
- **Always advisory:** detection results only set labels and chip counts. `keep` defaults to true and is only changed by his clicks.
- **Night/IR:** MegaDetector is trained on lots of IR camera-trap images and handles it well. Species classifiers are weaker on IR, so show "Usikker" generously rather than confidently wrong labels.
- **Models:** bundled in the installer. Velopack delta updates mean they aren't re-downloaded on every app update.

## 7. UI

- One window, one primary button per screen, large type (base ≥ 16 px), high contrast. Tray icon with "Åpne", "Mine importer", "Avslutt".
- Screens: *Kort funnet* → *Kopierer* → *Gjennomgang* (sites → sequence cards → frame picker) → *Navn og tagger* → *Lagrer* → *Ferdig* → *Tøm kortet*, plus *Mine importer*.
- Performance: the review level is sequences (dozens), not frames. The frame picker uses a virtualised grid with thumbnails from an on-disk cache, so a 1 000+ frame burst stays smooth.
- Language: all strings in `.resx` (nb-NO), numbers formatted with `nb-NO` culture ("1 247").
- Start with Windows: Velopack creates a Startup shortcut; single instance via named mutex. A second launch brings the existing window forward.

## 8. Build, CI, delivery

- **Dev loop (Mac):** Rider (best Avalonia previewer) or VS Code + C# Dev Kit. `dotnet run` on macOS for everything except Windows-specific platform code.
- **Windows testing:** a Windows 11 VM (Parallels on Apple Silicon runs x64 apps under emulation, with USB passthrough for real card readers), plus the `windows-latest` CI runner. Final checks on the user's actual machine class.
- **CI (`ci.yml`):** build + test on `macos-latest` and `windows-latest` on every push.
- **Release (`release.yml`, on tag):** `windows-latest` → `dotnet publish -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true` (ReadyToRun for faster startup on old hardware) → `vpk pack` with Azure Trusted Signing → `vpk upload github`.
- **Update feed:** the app checks on start and installs quietly on next launch. It never updates in the middle of a session. If the source repo is private, publish releases to a separate **public release-only repo** rather than embedding a GitHub token in the app.

## 9. Milestones

Mapped to the spec's build order. M1 includes the release pipeline, so every later milestone reaches the user the same day.

| # | Milestone | Done when |
|---|---|---|
| M1 | Safe copy + verify + erase gate, flat import to `Viltkamera\<yyyy>\<yyyy-MM-dd> Import\`; tray, card detection, signed installer, auto-update | Pull-the-card and kill-the-process tests pass; the user has installed it |
| M2 | Mine importer + Åpne mappen, Ferdig screen, undo | He can find any past import without a path |
| M3 | Time-based sequences, review grid, keep/discard, Sortert bort | A 200-frame burst is one card |
| M4 | Site grouping by time + frame (background plates, change points), manual split/merge, remembered sites | Synthetic LILA cards and home-made cards split correctly; checked again on his real imports when available |
| M5 | Naming and tags per site, tag autocomplete, ExifTool metadata, sidecar txt, final folder structure | Tags visible in Explorer's Properties panel and searchable |

**Later (not in v1):** empty-frame detection → species labels + filter chips → "Foreslå beste bilder". Order to be decided from how he actually uses v1.

Site grouping moved ahead of naming because naming and tagging happen per site, so the naming screen is built once, around sites.

## 10. Testing the safety core

- `IFileSystem` seam in Core so tests can inject: IOException after N bytes (card pulled), corrupted reads, disk full, rename failures.
- Crash tests: a child process runs the copier and is killed at random points. On restart, the invariants must hold (no `verified` row without a byte-identical file, no deleted source without a verified copy).
- Property test: across any sequence of copy/finalize/undo/erase with injected faults, the number of distinct image hashes that exist *somewhere* (card ∪ staging ∪ archive) never decreases.
- Golden tests for naming (æøå, NFC/NFD, reserved chars, collisions) and for the ExifTool output, read back through MetadataExtractor.

## 11. Open technical questions

1. **OneDrive:** Windows 11 often redirects Pictures into OneDrive. If the user's is redirected, thousands of trail-cam images would fill the free 5 GB and start the nagging. Do we detect this and store to a local `Viltkamera` folder instead, or accept it?
2. **Folder date:** should `2026-09-18 Høgfjellåsen` use the first image date, the last image date, or the day he emptied the card?
3. **Videos:** copied and archived either way (so erase is safe). Do they show up in review? (spec open question 6)
4. **Staging retention:** after finalize, staging is empty. Should we keep a hidden second copy for N days as extra insurance, disk permitting?
5. **Remote support:** is "Lag feilrapport → zip on the desktop" enough, or do you want opt-in log upload?
