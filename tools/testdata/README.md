# Test-data generator: fake trail-camera SD cards

`build_cards.py` downloads a fixed selection of real camera-trap images from
[LILA BC](https://lila.science) and lays them out as fake SD cards with a ground-truth file,
for testing sequence (hendelse) and site (sted) grouping. See `technical-plan.md` §4.2.

```
python3 tools/testdata/build_cards.py
```

Needs Python 3 (stdlib only) and `exiftool` (on PATH or `/opt/homebrew/bin/exiftool`).
First run downloads about 30 MB of metadata and about 600 MB of images (632 images) and takes a
few minutes. Later runs reuse `testdata/.cache/` and only rebuild the cards (about 1 minute,
mostly loading the WSU Lynx metadata). The selection is deterministic, and every run rebuilds
`testdata/cards/` from scratch.

**Output goes to `testdata/` in the repo root. Do not commit it.** Only this script is committed.

## Cards

Each card is `testdata/cards/<name>/DCIM/<folder>/IMAG0001.JPG…`, numbered in time order, with
`expected.json` next to `DCIM`.

| card | images | what it tests |
|---|---|---|
| `single-site` | 208 | one location, 20 sequences including a 32-frame burst, day and IR night |
| `three-sites` | 264 | locations A → B → A (a revisit), shifted by whole days so the segments follow each other with 1–3 empty days |
| `reset-clock` | 100 | one location with timestamps rebased to start at 2000-01-01 00:00:00 |
| `counter-restart` | 60 | `100MEDIA` and `101MEDIA` both contain `IMAG0001–0030` (the camera restarted its counter); 101MEDIA is later in time |

For every image, EXIF `DateTimeOriginal` and `CreateDate` and the file mtime are set to the card
timestamp (local time). Pixels are untouched: exiftool rewrites only the metadata, without
re-encoding.

`expected.json` has one entry per file with these fields:
- `taken_at`: the timestamp written to the file.
- `sequence` and `frame`: the dataset's `seq_id` and `frame_num`.
- `site`: `A`, `B` and so on.
- `species`: the dataset labels, with `empty` removed.
- `night`: see below.
- `source`: the dataset, original file name, location and original datetime.

## Datasets and licenses

Both datasets are released under the **Community Data License Agreement – Permissive 1.0**
(https://cdla.dev/permissive-1-0/), which allows use and redistribution with attribution. The
images are still not committed, to keep the repo small.

- **WSU Lynx** (https://lila.science/datasets/wsu-lynx/): conifer-forest roads and trails in
  north-eastern Washington, USA (2016–2017). Browning cameras, IR at night. Includes moose, lynx,
  snowshoe hare, marten and birds (`aves`). Metadata has per-image `datetime`, `location`,
  `seq_id` and `frame_num`. Cite: Thornton D, Morris D, King T, Perera-Romero L, Anderson L,
  Garcia-Anleu R, Fitkin S, Vynne C. *Identification of camera trap images by artificial
  intelligence and human experts produces similar multi-species occupancy models.* Journal of
  Applied Ecology, 2026. Used for `single-site`, `three-sites` and `reset-clock`.
- **Duck Pictures in Wetlands** (https://lila.science/datasets/duck-pictures-in-wetlands/):
  Finnish wetlands, raw card dumps from Burrel cameras with the original EXIF (Make/Model,
  DateTimeOriginal). Contact: Basile Marteau, University of Helsinki. The metadata has no
  datetime or sequence ids, so times come from the images' own EXIF and `sequence`/`frame` are
  `null`. Used for `counter-restart`.

Images are fetched over plain HTTPS from LILA's public GCP bucket:
`https://storage.googleapis.com/public-datasets-lila/<dataset-folder>/<file_name>`.

## Caveats

- **Burned-in info bars** (date/time/temperature strip) still show the *original* date. They
  will not match the rewritten EXIF on re-timed cards.
- **`night`** for WSU Lynx is computed from the sun's elevation at the original capture time
  (about 48.6° N, 118.8° W, UTC−7). It is `true` below −6°, `false` above 0°, and `null` in
  twilight, when the camera could be in either mode. It is not taken from the pixels. For the
  Duck card it is `null`.
- WSU Lynx `seq_id`s are LILA's own time-based grouping of the raw images, not a camera-reported
  burst id.
- `three-sites` segments are 1–2 weeks long and have multi-day quiet gaps *inside* a segment as
  well. Only a change of location is a site boundary.
- To keep the download small, the script only picks WSU Lynx cameras that write 1920×1080
  (~0.7 MB) files. It checks this with one HTTP HEAD request per candidate location. Duck images
  are ~3.8 MB each.
- The Duck camera's EXIF `Make` contains a literal tab (`BURREL BY SPROMISE<TAB>E`), and exiftool
  warns that its maker notes can't be parsed. Both come from the original files.
