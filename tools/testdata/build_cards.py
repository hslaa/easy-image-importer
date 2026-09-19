#!/usr/bin/env python3
"""Build fake trail-camera SD cards from real LILA BC camera-trap images.

Downloads a fixed, deterministic selection of images (cached under testdata/.cache/),
lays them out as camera-like cards under testdata/cards/<name>/DCIM/...,
rewrites EXIF DateTimeOriginal/CreateDate and file mtimes, and writes a
ground-truth expected.json next to each card's DCIM folder.

Python 3 stdlib only, plus `exiftool` on PATH (or /opt/homebrew/bin/exiftool).
Usage: python3 tools/testdata/build_cards.py
"""
import json
import math
import os
import shutil
import subprocess
import time
import urllib.parse
import urllib.request
import zipfile
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "testdata"
CACHE = OUT / ".cache"
CARDS = OUT / "cards"
EXIFTOOL = shutil.which("exiftool") or "/opt/homebrew/bin/exiftool"
GCP = "https://storage.googleapis.com/public-datasets-lila"
CDLA = "CDLA-Permissive-1.0 (Community Data License Agreement - Permissive, Version 1.0)"

LYNX = {
    "dataset": "WSU Lynx",
    "folder": "wsu-lynx",
    "meta": "https://lilawildlife.blob.core.windows.net/lila-wildlife/wsu-lynx/wsu-lynx.26.02.13.1705.zip",
    "url": "https://lila.science/datasets/wsu-lynx/",
    "license": CDLA,
    "citation": "Thornton D, Morris D, King T, Perera-Romero L, Anderson L, Garcia-Anleu R, Fitkin S, "
                "Vynne C. Identification of camera trap images by artificial intelligence and human "
                "experts produces similar multi-species occupancy models. Journal of Applied Ecology, 2026.",
}
DUCK = {
    "dataset": "Duck Pictures in Wetlands",
    "folder": "duck-pictures-in-wetlands",
    "meta": "https://lilawildlife.blob.core.windows.net/lila-wildlife/duck-pictures-in-wetlands/duck_pictures_in_wetlands.json.zip",
    "url": "https://lila.science/datasets/duck-pictures-in-wetlands/",
    "license": CDLA,
    "citation": "Duck Pictures in Wetlands (Finland), LILA BC. Contact: Basile Marteau, University of Helsinki.",
}
# WSU Lynx cameras are in north-central/north-eastern Washington; clocks are local summer time.
LYNX_LAT, LYNX_LON, LYNX_UTC_OFFSET = 48.6, -118.8, -7
DUCK_FOLDER = "Keskinen mustajarvi/Camera2/100KDSP1"  # one Burrel camera, files IMAG0001..IMAG8924
START = datetime(2026, 6, 1)  # re-timed cards start on this date (time of day is kept)
BORING = {"empty", "unknown", "domestic cattle", "domestic horse"}


# ---------------------------------------------------------------- download

def fetch(url, dest):
    if dest.exists():
        return dest
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_name(dest.name + ".part")
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=120) as r, open(tmp, "wb") as f:
                shutil.copyfileobj(r, f)
            tmp.replace(dest)
            return dest
        except Exception as e:
            if attempt == 2:
                raise RuntimeError(f"download failed: {url}: {e}") from e
            time.sleep(3)


def image_path(ds, file_name):
    return CACHE / "images" / ds["folder"] / file_name


def download_images(ds, file_names):
    def one(fn):
        return fetch(f"{GCP}/{ds['folder']}/{urllib.parse.quote(fn)}", image_path(ds, fn))
    with ThreadPoolExecutor(8) as pool:
        list(pool.map(one, file_names))


def load_meta(ds):
    """Return (images, labels-by-image-id) from the dataset's COCO Camera Traps json (read from the zip)."""
    z = fetch(ds["meta"], CACHE / "meta" / ds["meta"].rsplit("/", 1)[1])
    with zipfile.ZipFile(z) as zf:
        name = next(n for n in zf.namelist() if n.endswith(".json"))
        with zf.open(name) as f:
            d = json.load(f)
    cats = {c["id"]: c["name"] for c in d["categories"]}
    labels = defaultdict(set)
    for a in d["annotations"]:
        labels[a["image_id"]].add(cats[a["category_id"]])
    return d["images"], labels


# ---------------------------------------------------------------- day / night

def sun_elevation(local, lat, lon, utc_offset):
    """Approximate solar elevation in degrees (NOAA formulas)."""
    hours_utc = local.hour + local.minute / 60 + local.second / 3600 - utc_offset
    g = 2 * math.pi / 365 * (local.timetuple().tm_yday - 1 + (hours_utc - 12) / 24)
    eqt = 229.18 * (0.000075 + 0.001868 * math.cos(g) - 0.032077 * math.sin(g)
                    - 0.014615 * math.cos(2 * g) - 0.040849 * math.sin(2 * g))
    decl = (0.006918 - 0.399912 * math.cos(g) + 0.070257 * math.sin(g) - 0.006758 * math.cos(2 * g)
            + 0.000907 * math.sin(2 * g) - 0.002697 * math.cos(3 * g) + 0.00148 * math.sin(3 * g))
    hour_angle = math.radians((hours_utc * 60 + eqt + 4 * lon) / 4 - 180)
    la = math.radians(lat)
    cos_zenith = math.sin(la) * math.sin(decl) + math.cos(la) * math.cos(decl) * math.cos(hour_angle)
    return 90 - math.degrees(math.acos(max(-1.0, min(1.0, cos_zenith))))


def lynx_night(dt):
    """True below civil twilight, False when the sun is up, None in between (camera may be in either mode)."""
    e = sun_elevation(dt, LYNX_LAT, LYNX_LON, LYNX_UTC_OFFSET)
    return True if e < -6 else False if e > 0 else None


# ---------------------------------------------------------------- WSU Lynx selection

def lynx_sequences():
    """Location -> list of sequences (each a list of image records), in time order."""
    images, labels = load_meta(LYNX)
    seqs = defaultdict(list)
    for im in images:
        dt = datetime.fromisoformat(im["datetime"])
        if not 2016 <= dt.year <= 2017:
            continue  # skip cameras with obviously wrong clocks
        seqs[(im["location"], im["seq_id"])].append({
            "file_name": im["file_name"], "dt": dt, "seq": im["seq_id"], "frame": im["frame_num"],
            "location": im["location"], "species": sorted(labels[im["id"]] - {"empty"}),
        })
    by_loc = defaultdict(list)
    for (loc, _), frames in seqs.items():
        frames.sort(key=lambda r: (r["dt"], r["frame"]))
        by_loc[loc].append(frames)
    for loc in by_loc:
        by_loc[loc].sort(key=lambda s: (s[0]["dt"], s[0]["seq"]))
    return by_loc


def score(window):
    nights = [lynx_night(s[0]["dt"]) for s in window]
    species = {sp for s in window for r in s for sp in r["species"]}
    cattle = sum(1 for s in window if any("domestic cattle" in r["species"] for r in s))
    return (min(nights.count(True), 4) + min(nights.count(False), 4) + len(species - BORING)
            + 3 * ("aves" in species) - cattle)


def candidate_windows(seqs, target, need_burst=False, max_days=14):
    """All runs of whole consecutive sequences totalling target..1.25*target images, with a score."""
    out = []
    for i in range(len(seqs)):
        n = 0
        for j in range(i, len(seqs)):
            n += len(seqs[j])
            if n >= target:
                break
        if n < target or n > target * 1.25:
            continue
        window = seqs[i:j + 1]
        if window[-1][-1]["dt"] - window[0][0]["dt"] > timedelta(days=max_days):
            continue
        if need_burst and not any(30 <= len(s) <= 120 for s in window):
            continue
        out.append((score(window), i, j))
    return out


def best(cands_by_loc, exclude, by_loc):
    """Best (score, loc, i, j) over locations not excluded whose camera writes small (~0.7 MB,
    1920x1080) files, to keep the download modest. Ties are broken by name/position."""
    ranked = sorted((-sc, loc, i, j) for loc, cands in cands_by_loc.items() if loc not in exclude
                    for sc, i, j in cands)
    checked = {}
    for c in ranked:
        loc = c[1]
        if loc not in checked:
            fn = by_loc[loc][0][0]["file_name"]
            req = urllib.request.Request(f"{GCP}/{LYNX['folder']}/{urllib.parse.quote(fn)}", method="HEAD")
            with urllib.request.urlopen(req, timeout=60) as r:
                checked[loc] = int(r.headers["Content-Length"]) < 1_000_000
        if checked[loc]:
            return c
    raise RuntimeError("no suitable location")


def flatten(window):
    return [r for s in window for r in s]


def select_lynx():
    by_loc = lynx_sequences()
    used = set()

    single = {loc: candidate_windows(s, 200, need_burst=True) for loc, s in by_loc.items()}
    _, loc_s, i, j = best(single, used, by_loc)
    used.add(loc_s)
    single_site = flatten(by_loc[loc_s][i:j + 1])

    # three-sites: A needs two windows at least 2 days apart (a revisit), B any other location.
    seg = {loc: candidate_windows(s, 85) for loc, s in by_loc.items()}
    pairs = {}
    for loc, cands in seg.items():
        if loc in used or not cands:
            continue
        seqs = by_loc[loc]
        top = sorted(cands, key=lambda c: (-c[0], c[1]))[0]
        far = [c for c in cands
               if seqs[c[1]][0]["dt"] - seqs[top[2]][-1]["dt"] > timedelta(days=2)
               or seqs[top[1]][0]["dt"] - seqs[c[2]][-1]["dt"] > timedelta(days=2)]
        if far:
            second = sorted(far, key=lambda c: (-c[0], c[1]))[0]
            pairs[loc] = [(top[0] + second[0], *sorted([top[1:], second[1:]]))]
    _, loc_a, (a1i, a1j), (a2i, a2j) = best(pairs, used, by_loc)
    used.add(loc_a)
    _, loc_b, bi, bj = best(seg, used, by_loc)
    used.add(loc_b)
    segments = [("A", flatten(by_loc[loc_a][a1i:a1j + 1])), ("B", flatten(by_loc[loc_b][bi:bj + 1])),
                ("A", flatten(by_loc[loc_a][a2i:a2j + 1]))]

    reset = {loc: candidate_windows(s, 100) for loc, s in by_loc.items()}
    _, loc_r, i, j = best(reset, used, by_loc)
    reset_clock = flatten(by_loc[loc_r][i:j + 1])
    return single_site, segments, reset_clock


def shift_days(records, first_day):
    """Shift records by a whole number of days so the first one falls on first_day (keeps time of day)."""
    delta = timedelta(days=(first_day.date() - records[0]["dt"].date()).days)
    return [dict(r, taken_at=r["dt"] + delta) for r in records]


# ---------------------------------------------------------------- Duck selection

def select_duck():
    images, labels = load_meta(DUCK)
    files = sorted((im["file_name"] for im in images if im["file_name"].startswith(DUCK_FOLDER + "/IMAG")))
    animals = [1 if labels[f] - {"empty"} else 0 for f in files]
    half = len(files) // 2

    def densest(lo, hi, n=30):
        return max(range(lo, hi - n), key=lambda k: (sum(animals[k:k + n]), -k))

    windows = [files[k:k + 30] for k in (densest(0, half), densest(half, len(files)))]
    download_images(DUCK, [f for w in windows for f in w])
    out = []
    for w in windows:
        times = exif_times([image_path(DUCK, f) for f in w])
        out.append([{"file_name": f, "dt": t, "taken_at": t, "seq": None, "frame": None,
                     "location": DUCK_FOLDER.split("/")[0], "species": sorted(labels[f] - {"empty"})}
                    for f, t in zip(w, times)])
    out.sort(key=lambda w: w[0]["dt"])
    return out


def exif_times(paths):
    res = json.loads(subprocess.run([EXIFTOOL, "-j", "-DateTimeOriginal", *map(str, paths)],
                                    check=True, capture_output=True, text=True).stdout)
    by_path = {r["SourceFile"]: datetime.strptime(r["DateTimeOriginal"], "%Y:%m:%d %H:%M:%S") for r in res}
    return [by_path[str(p)] for p in paths]


# ---------------------------------------------------------------- card writing

def write_card(name, description, ds_list, folders):
    """folders: list of (DCIM subfolder, records); records get IMAG0001.. per folder in list order."""
    card = CARDS / name
    shutil.rmtree(card, ignore_errors=True)
    files, args = [], []
    for sub, records in folders:
        for n, r in enumerate(records, 1):
            ds = r["ds"]
            rel = f"DCIM/{sub}/IMAG{n:04d}.JPG"
            dest = card / rel
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(image_path(ds, r["file_name"]), dest)
            stamp = r["taken_at"].strftime("%Y:%m:%d %H:%M:%S")
            args += ["-q", "-m", "-overwrite_original", f"-DateTimeOriginal={stamp}", f"-CreateDate={stamp}",
                     str(dest), "-execute"]
            files.append({
                "path": rel, "taken_at": r["taken_at"].isoformat(), "sequence": r["seq"], "frame": r["frame"],
                "site": r["site"], "species": r["species"], "night": r["night"],
                "source": {"dataset": ds["dataset"], "file_name": r["file_name"],
                           "location": r["location"], "datetime": r["dt"].isoformat()},
            })
    argfile = CACHE / f"exiftool-{name}.args"
    argfile.write_text("\n".join(args) + "\n")
    subprocess.run([EXIFTOOL, "-@", str(argfile)], check=True)
    for f in files:
        t = time.mktime(datetime.fromisoformat(f["taken_at"]).timetuple())
        os.utime(card / f["path"], (t, t))
    expected = {"card": name, "description": description,
                "sources": [{"dataset": d["dataset"], "license": d["license"], "url": d["url"]} for d in ds_list],
                "files": files}
    (card / "expected.json").write_text(json.dumps(expected, indent=1, ensure_ascii=False) + "\n")
    print(f"{name}: {len(files)} images, {sum((card / f['path']).stat().st_size for f in files) / 1e6:.0f} MB")


def lynx_records(records, site, taken_at=None):
    out = []
    for r in records:
        out.append(dict(r, ds=LYNX, site=site, night=lynx_night(r["dt"]),
                        taken_at=taken_at(r) if taken_at else r["taken_at"]))
    return out


def main():
    if not Path(EXIFTOOL).exists():
        raise SystemExit("exiftool not found")
    CACHE.mkdir(parents=True, exist_ok=True)

    single, segments, reset = select_lynx()
    download_images(LYNX, [r["file_name"] for r in single + reset + [r for _, s in segments for r in s]])

    write_card("single-site",
               "One WSU Lynx camera location; whole sequences in a time window with at least one "
               "burst of 30+ frames, day and night images. Re-timed by whole days to start 2026-06-01.",
               [LYNX], [("100MEDIA", lynx_records(shift_days(single, START), "A"))])

    retimed, day = [], START
    for n, (site, recs) in enumerate(segments):
        recs = shift_days(recs, day)
        retimed += lynx_records(recs, site)
        day = recs[-1]["taken_at"] + timedelta(days=2 + n % 3)  # 1-3 empty days between segments
    write_card("three-sites",
               "Three WSU Lynx segments visited A -> B -> A (A is the same camera location revisited). "
               "Offsets inside each segment are original; segments are shifted by whole days so they "
               "follow each other with 1-3 empty days between them.",
               [LYNX], [("100MEDIA", retimed)])

    t0 = reset[0]["dt"]
    write_card("reset-clock",
               "One WSU Lynx location, timestamps rebased so the first image is 2000-01-01 00:00:00 "
               "(a camera whose clock was reset). Relative offsets are original; 'night' reflects "
               "the original capture time.",
               [LYNX], [("100MEDIA", lynx_records(reset, "A",
                                                  lambda r: datetime(2000, 1, 1) + (r["dt"] - t0)))])

    first, second = select_duck()
    write_card("counter-restart",
               "Two runs of 30 consecutive images from one Finnish wetland camera (Burrel, original "
               "EXIF times and Make/Model kept). The camera 'restarted its counter': 100MEDIA and "
               "101MEDIA both use IMAG0001..IMAG0030; 101MEDIA is later in time. The dataset has no "
               "sequence ids, so 'sequence'/'frame' are null; 'night' is unknown (null).",
               [DUCK], [(sub, [dict(r, ds=DUCK, site="A", night=None) for r in recs])
                        for sub, recs in (("100MEDIA", first), ("101MEDIA", second))])

    write_readme()


def write_readme():
    (OUT / "README.md").write_text(f"""# Generated test data (do not commit)

Built by `python3 tools/testdata/build_cards.py`. See `tools/testdata/README.md`.

Fake trail-camera SD cards under `cards/<name>/` (`DCIM/...` plus a ground-truth `expected.json`).
`.cache/` holds the downloaded metadata and original images so rebuilding does not re-download.

## Sources and attribution

Images come from LILA BC (https://lila.science). EXIF dates, file names and folders have been
changed; pixels are original. Burned-in info bars still show the original date/time.

- **{LYNX['dataset']}** ({LYNX['url']}), license {LYNX['license']}.
  Cite: {LYNX['citation']}
- **{DUCK['dataset']}** ({DUCK['url']}), license {DUCK['license']}.
  {DUCK['citation']}

License text: https://cdla.dev/permissive-1-0/
""")


if __name__ == "__main__":
    main()
