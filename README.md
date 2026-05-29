# iNat Bestiary

Windows desktop app for cataloging wildlife photos. Point it at a folder of species subfolders and it maps each folder to an iNaturalist taxon, writes full taxonomy keyword hierarchies to JPEG XMP metadata, and pulls GPS coordinates from your iNaturalist observations.

---

## Features

- **Folder tree browser** — navigate your photo library organized by species; the folder panel shows photo counts and sync status at a glance
- **Auto-sync on open** — folder names are matched against iNaturalist taxa in the background every time you open a catalog, keeping the database up to date
- **Taxonomy keyword tagging** — writes the full iNaturalist ancestor chain (`Animalia → Aves → Alcedinidae → …`) to `dc:subject` flat keywords and `lr:hierarchicalSubject` in JPEG XMP, compatible with Lightroom and digiKam
- **GPS tagging from iNaturalist** — matches your photos to your iNaturalist observations by timestamp and writes GPS coordinates to XMP; existing GPS is never overwritten
- **Location name resolution** — reverse-geocodes GPS coordinates via Nominatim (OpenStreetMap) to country-level names, shown in the photo list; results are cached in the local database
- **Species search** — search by species name with iNaturalist autocomplete, or plain folder/filename search; the tree filters to matching folders and the photo panel shows matching files
- **Sort and filter** — sort by filename, date taken, or star rating; toggle JPEG-only view; switch between list and preview-card layouts
- **Remembers last catalog** — reopens the last used folder automatically on startup

---

## Folder structure

The app expects photos organized in species-named subfolders:

```
My Wildlife Photos/
├── Common Kingfisher/
│   ├── DSC_0001.jpg
│   └── DSC_0002.jpg
├── European Otter/
│   └── IMG_4412.jpg
└── Brown Hare/
    ├── 2024-03-11 01.jpg
    └── 2024-03-11 02.jpg
```

Subfolder names are matched against iNaturalist common names (exact match, then suffix match). Numbered duplicates like `Dunlin 2` are normalized to `Dunlin` before matching.

---

## Getting started

### Download

Grab the latest `InatBestiary.exe` from [Releases](../../releases) — it is self-contained, no .NET installation required.

### Build from source

Requires [Docker Desktop](https://www.docker.com/products/docker-desktop/) on Windows.

```powershell
.\build.ps1
# Output: .\publish\InatBestiary.exe
```

The build runs inside a Linux container and cross-compiles to a self-contained `win-x64` binary. No local .NET SDK needed.

---

## Workflow

### 1 — Open a folder

Click **Open Folder** and select the root of your photo library. The folder tree populates and background auto-sync begins immediately, showing progress in the status bar.

### 2 — Build the taxonomy database

If auto-sync misses folders (unusual names, new additions), open the **Database ▾** menu and choose **Build taxonomy database**. This re-scans every folder and calls the iNaturalist API to find matches, rate-limited to one request per 100 ms.

### 3 — Tag with taxonomy keywords

**Database ▾ → Tag with taxonomy keywords** walks every JPEG under mapped folders and writes XMP keywords. Already-tagged files are skipped. Right-click any folder in the tree to tag just that subtree.

### 4 — Tag with GPS from iNaturalist

**Database ▾ → Tag with GPS from iNaturalist…** prompts for your iNaturalist username, fetches your observations for all mapped taxa, and matches each untagged JPEG to the closest observation in time (within 14 hours). Matched coordinates are written to XMP. Photos that already have GPS are left untouched.

### 5 — Resolve location names

**Database ▾ → Refresh location names** reverse-geocodes GPS coordinates to country names via Nominatim, displayed in the photo list. Results are cached so each unique location is only looked up once.

---

## XMP fields written

| Field | Value |
|---|---|
| `dc:subject` | Flat keyword list: common name, scientific name, and all ancestor taxa |
| `lr:hierarchicalSubject` | Full path: `Animalia\|Chordata\|Aves\|…\|Alcedo atthis\|Common Kingfisher` |
| `photocatalog:TaxonId` | iNaturalist taxon ID (used to detect already-tagged files) |
| `photocatalog:ScientificName` | Scientific name of the matched taxon |
| `exif:GPSLatitude` / `exif:GPSLongitude` | GPS coordinates in XMP `DD,MM.mmmmmmH` format |

Existing keywords in `dc:subject` and `lr:hierarchicalSubject` are preserved and merged — the app never removes keywords it didn't write.

---

## Local database

The catalog database is stored as `.bestiary.db` in the root of your photo folder (hidden file, SQLite). It holds:

- **taxa** — iNaturalist taxon records for matched species
- **folder_taxa** — mapping of folder paths to taxon IDs
- **location_cache** — cached Nominatim results keyed by 0.1° grid cell (~11 km)

The database travels with your photos — move the folder and the mappings move with it.

---

## Tech stack

| | |
|---|---|
| UI | [Avalonia UI](https://avaloniaui.net/) 11, MVVM via CommunityToolkit.Mvvm |
| Runtime | .NET 10, C# 13 |
| Metadata | [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) for EXIF/XMP reading; custom JPEG XMP writer |
| Database | SQLite via Microsoft.Data.Sqlite |
| APIs | [iNaturalist API v1](https://api.inaturalist.org/v1/docs/) · [Nominatim](https://nominatim.openstreetmap.org/) (OpenStreetMap) |
| Build | Docker cross-compile (`mcr.microsoft.com/dotnet/sdk:10.0`) → self-contained `win-x64` |

---

## License

MIT — see [LICENSE](LICENSE).
