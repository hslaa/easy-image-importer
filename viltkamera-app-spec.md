# Viltkamera Import — Product Spec

A guided desktop app that takes a camera-trap SD card from "insert" to "safely archived, sorted, findable in Explorer, card wiped" in one linear flow.

**Status:** product definition. No technical decisions made yet.
**Primary user:** a bird-watching hobbyist, Windows, non-technical, not comfortable with file paths.
**Developer:** macOS. So the app must be cross-platform for development even though the only production target is Windows.
**UI language:** Norwegian (bokmål). Species names in Norwegian.

---

## 1. The problem

He places trail cameras in the wild, baits them with roadkill, leaves, and returns days later. The card comes back with hundreds or thousands of images. Today he struggles with:

- Getting images off the card at all
- Wading through long bursts of uninteresting animals (e.g. 200 frames of nøtteskrike) to find the two sequences that matter
- Keeping track of which images came from which camera placement
- Knowing where the files ended up afterwards — "so where did my files go now??"
- Knowing when it's safe to erase the card

The images are irreplaceable when the right bird is in frame.

## 2. The core promise

> The card is treated as read-only until every file has been copied and byte-verified.

Everything else in this spec is negotiable. This isn't. Two hard invariants:

1. **The erase action does not exist** until all files from this session are copied and hash-verified. Not greyed out — absent. It appears only when verification passes 100%.
2. **"Discard" never means delete.** Discarded images are moved to a `Sortert bort` subfolder inside the same import folder, where he can find and recover them.

## 3. User flow

### Step 1 — Insert the card

App lives in the system tray. On card insert, a window opens:

> **Fant 1 247 bilder fra kortet. 312 er nye.**

Already-imported files are recognised by content hash, so a partially-emptied card never causes duplicates or double-counting. Card can also be selected manually (folder picker) for people who use a card reader oddly or copy files by hand first.

### Step 2 — Copy first, decide later

Before showing him anything, the app copies every new file to a local staging area and verifies each one (hash of source vs. destination).

- Plain-language progress: "Kopierer bilde 214 av 312…"
- Resumable: pulling the card, sleeping the PC, or closing the app mid-copy must not corrupt state. On restart it picks up where it stopped.
- Any file that fails verification is retried, then flagged loudly and blocks the erase step.

This is the safety trick that makes everything downstream safe: **all reviewing happens on verified copies, the card is never written to.**

### Step 3 — Automatic grouping

Two levels, matching how the camera actually works.

**Sted (site)** — the camera stands still for a whole placement, so the background is nearly identical across all images from that placement. Cluster by background similarity + timestamp continuity. Sites are *remembered between seasons*: if a new batch matches a known site, pre-fill the name — "Dette ser ut som Høgfjellåsen."

**Hendelse (sequence)** — images within a short time window (seconds to minutes) of each other are one animal visit. A 200-image nøtteskrike burst collapses into **one card**: "Nøtteskrike · 203 bilder · 07:12–07:41", not 200 things to scroll.

He can always split, merge, rename and move groups by hand. Automatic grouping is a starting point, never a cage.

### Step 4 — Species highlighting

Each sequence gets a suggested label, shown with a confidence indication:

- `Kongeørn`, `Nøtteskrike`, `Rev`, `Ravn` …
- `Tomt bilde` (wind/vegetation false trigger)
- `Usikker`

Suggestions are **advisory, never destructive**. Nothing is ever hidden or discarded on the strength of a detection alone.

Review screen is filter chips with counts:

`Kongeørn (2)` `Nøtteskrike (14)` `Rev (3)` `Tom (890)` `Usikker (31)`

Empty-frame detection alone probably halves his review time.

### Step 5 — Select what to keep

- Default is **keep everything**. He actively discards; he never has to actively rescue.
- Per sequence: keep all / discard all / open and pick individual frames.
- **"Foreslå beste bilder"** — picks the sharpest, best-composed frames from a long burst, so keeping 5 of 203 is two clicks.
- Discarded ≠ deleted (see invariant 2).

### Step 6 — Name and tag

Per site:

- **Navn** — e.g. `Høgfjellåsen` (required; pre-filled if the site is recognised)
- **Beskrivelse** — free text
- **Tagger** — free text, autocompleting from previously used tags
- Species tags carry over automatically from step 4
- Date span is derived, shown, editable

### Step 7 — Import

Files land in a boring, predictable structure under his existing Pictures folder:

```
Bilder\Viltkamera\2026\2026-09-18 Høgfjellåsen\
    2026-09-18_Høgfjellåsen_0712_Nøtteskrike_001.jpg
    2026-09-18_Høgfjellåsen_0712_Nøtteskrike_002.jpg
    ...
    Sortert bort\
    OM DENNE MAPPEN.txt
```

- `OM DENNE MAPPEN.txt` is a human-readable summary: site name, description, dates, counts, species list. Readable with zero tooling, and survives the app being uninstalled.
- Tags, description and species are also written to EXIF/XMP so Windows Explorer's own search and Properties panel pick them up. **He keeps browsing images exactly the way he already does.**

### Step 8 — "Where did my files go?"

The finishing screen is deliberately minimal:

> **Ferdig. 47 bilder er lagret i:**
> `C:\Users\…\Bilder\Viltkamera\2026\2026-09-18 Høgfjellåsen`
>
> [ **Åpne mappen** ]

And permanently, inside the app: **Mine importer** — a list of every past import with name, date, image count and an "open folder" button. He never has to remember or type a path.

### Step 9 — Empty the card

Only now does the erase step appear:

> **312 bilder er kopiert og kontrollert.**
> Slett dem fra kortet?

- Shows the exact count being erased
- Requires a deliberate confirmation (not a single stray click)
- **Refuses outright** if even one file failed verification, with a clear explanation
- Afterwards: "Kortet er klart for neste tur."

## 4. Design principles

| Principle | What it means in practice |
|---|---|
| One thing at a time | One window, one linear path, one primary button per screen |
| No file trees | He never navigates a folder picker to do normal work |
| Never a dead end | Close mid-review, reopen, continue exactly where he left off |
| Undo everywhere | Including "angre hele importen" for 24 hours after import |
| Norwegian | UI and species names |
| Boring is good | Predictable folder names, plain text sidecar, standard EXIF |
| Speak in images, not files | "312 bilder", not "312 objects" or byte counts |

## 5. Explicit non-goals (v1)

- Not a photo editor
- Not a cloud service, no account, no sync
- Not a general photo library manager — it hands off to Explorer and gets out of the way
- No multi-user, no sharing features
- Not a tool for continuously-recording video cameras

## 6. Risks and edge cases to design for

- Card pulled mid-copy
- PC sleeps or loses power mid-copy
- Full disk on the destination drive
- Card full of images from *several* placements in one batch (the site clustering must handle this — it's the normal case, not an edge case)
- Camera moved slightly at the same site between visits — does background similarity still cluster it correctly?
- Camera with a wrong or reset clock (timestamps unreliable) — grouping must degrade gracefully
- Duplicate filenames across placements (cameras restart counters)
- Multiple camera models with different folder layouts and EXIF quirks
- Night / IR images — do the detectors work on them? They're a large share of the data
- Very long bursts (1000+ frames) — UI must not choke
- He closes the app halfway through review and comes back three days later

## 7. Open questions for the user

1. Should species detection auto-tag, or only suggest and let him confirm?
2. Should discarded images be kept forever, or cleaned up after N months with a warning?
3. How many camera models, and which ones?
4. Does he ever reposition a camera slightly within the same site?
5. Does he already have an existing folder structure of past imports we should respect or migrate?
6. Does he want video files handled too, or images only?
7. Which species actually matter to him? A short list of "always highlight these" is more useful than broad coverage.

## 8. Suggested build order

1. **Safe copy + verify + erase-gate.** The whole safety story, with a dumb flat import and no grouping. Already useful on day one.
2. **"Mine importer" list + open-folder.** Kills the "where did my files go" problem.
3. **Time-based sequence grouping.** Collapses the 200-frame bursts. Biggest usability win per unit effort.
4. **Naming, tagging, EXIF writing, folder structure.**
5. **Site clustering by background similarity**, incl. remembering sites across seasons.
6. **Empty-frame detection.** Probably the single highest-value detector.
7. **Species detection and filter chips.**
8. **"Best frames" suggestion.**

Ship 1–2 to him early and let real use shape the rest.

---

## Appendix — glossary

- **Sted** — a camera placement. One physical spot, camera stationary. E.g. *Høgfjellåsen*.
- **Hendelse** — one animal visit; a burst of images close in time at one site.
- **Sortert bort** — the folder holding images he chose not to keep. Recoverable, never auto-deleted.
- **Staging** — the app's internal verified copy of the card contents, before he decides what to keep.
