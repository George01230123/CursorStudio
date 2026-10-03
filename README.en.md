# Cursor Studio

[中文说明](README.md) · **English**

Turn your own images into Windows mouse cursors and apply them system-wide in one click.

**[Download the latest release](https://github.com/George01230123/CursorStudio/releases/latest)** — a single-file
exe, ~47 MB, no installer and no .NET runtime required; copy it anywhere and run it.

Or build it yourself (binaries stay out of version control, as usual):

```bash
dotnet publish -c Release -o dist
```

The output is `dist\CursorStudio.exe` — rename it to whatever you like.

Double-click to run. It writes to `HKCU\Control Panel\Cursors`, so **no administrator rights are required**.

---

## How to use

1. Pick a cursor slot on the left (e.g. "Normal Select")
2. Click **Import image…**, or just press **Ctrl+V** to paste one
3. Click on the large preview to place the **hotspot** — the exact pixel where clicks register
4. Click **Apply to system**

Want to change everything at once? Import one image → click **Apply to all cursors** → **Apply to system**.
(Hotspots are recomputed per slot type: the tip for arrows, dead centre for crosses and resize arrows.)

If the cursor looks too big or too small, drag **Relative size**. Not sure? Click **Match system arrow** —
it measures how much of its canvas the arrow you're currently using actually occupies and matches that.

Changed your mind?
- **Restore backup** — goes back to the state before your first "Apply to system" (a backup is taken automatically the first time)
- **Restore Windows default** — straight back to the stock Aero cursors

---

## Design decisions

### Why replace the system cursors instead of drawing an overlay

An overlay (transparent topmost window + mouse hook) can show any size and add effects, but it is a hack:
the cursor disappears over UAC prompts, the login screen and some full-screen games, it always lags slightly
behind the real pointer, and if the tool crashes the cursor can vanish entirely. Replacing the system cursors
is the supported approach: system-wide, zero latency, reversible at any time.

The trade-off is that **Windows only renders cursors at 32×32 (48/64 on high-DPI)** — detail in large images
gets squeezed out. A cursor is fundamentally an icon: use images that are already simple (icons, pixel art,
geometric shapes, cartoon sprites), not a full-body illustration.

Each `.cur` bundles **four sizes: 32 / 48 / 64 / 96**, so Windows picks whichever fits your pointer size and
nothing looks blurry on a high-DPI screen. (The size ladder follows win2xcur's `align.py`, which uses the
standard set 32/48/64/96/128/256; beyond 96 a single size costs tens to hundreds of KB for very little gain.)

### The default is 70% of the canvas — and that number was measured, not guessed

The first version stretched the imported image **across the whole 32×32 canvas**. In use, that makes it
noticeably bigger and heavier than the system pointer in the same position — because the stock cursors don't
fill their canvas either.

The app contains a `.cur` reader, so it measured the actual ink box of the stock cursors:

| System cursor | Canvas | Ink box | % of canvas |
|---|---|---|---|
| **aero_arrow (default arrow)** | 32×32 | 12×19 | **59%** |
| aero_links (hand) | 32×32 | 18×24 | 75% |
| aero_move | 32×32 | 23×23 | 72% |
| aero_helpsel | 32×32 | 21×23 | 72% |
| aero_ns (vertical resize) | 32×32 | 9×23 | 72% |
| aero_unavail | 32×32 | 16×16 | 50% |
| **average** | | | **66%** |

So the default is **70%** (70% of 32 ≈ 22px), which puts imported images in the same visual weight class as
the stock cursors. The **Match system arrow** button simply runs that measurement for real and fills the
result into **Relative size**.

When the size or relative scale changes, a manually placed hotspot is carried over **by its relative position
inside the artwork**, not by simple multiplication — the image is centred, so the offset moves as it scales,
and multiplying drifts further off with every change.

### Drop shadow (off by default)

Adds a soft shadow behind the artwork so it stays readable on light backgrounds — standard practice in
finished cursor packs. The parameters come from Bibata: offset right 9.375%, down 3.125%, blur radius 3.125%,
25% black — all as percentages of the canvas, so 32px and 64px look consistent. GDI+ has no Gaussian blur,
so this approximates one by running a box blur three times.

### Background removal is automatic

On import it looks at the four corners: if all four are opaque and the same colour, it decides this is
"artwork on a flat background", switches keying on and sets the key colour to that colour. If any corner is
transparent or doesn't match, it leaves the image alone.

Keying does more than turn the background transparent. Antialiased edge pixels are a mix of
"foreground × coverage + background × (1−coverage)", so only changing alpha leaves them tinted with the
background colour — a pale halo that is very obvious once scaled down to 32px. So the background colour is
also subtracted out of those pixels' colours. The tolerance slider controls how aggressively it keys.

### Hotspots must be adjustable by hand

Automatic detection works well for arrows and hands, but for many images (a round avatar, a star) only you
know where the click should land. A misplaced hotspot feels like "the mouse doesn't click accurately", which
is far worse than an ugly cursor. So a single click on the large preview moves it, and dragging is real-time
because the render parameters haven't changed and the cache absorbs it.

---

## Animated cursors (.ani)

"Busy" and "Working in Background" are natively animated by Windows, so for those slots the app generates
`.ani` rather than `.cur`.

Import a GIF (or select several PNGs at once) and it becomes an animation automatically. The settings area
gains a row:

```
Animation   [50] ms / frame    [First frame only]    12 frames · ~20 fps
```

- The frame delay is adjustable; **the frame cap is 60**. Beyond that frames are sampled evenly rather than
  truncated, so the motion still reads correctly
- The large preview animates on its own. The "try it" area is handed a real `.ani`, so **Windows plays it
  itself** — what you see there is exactly what you get after applying
- **First frame only** drops back to a static cursor

A few implementation decisions:

- **Every frame shares one hotspot.** Windows reads the hotspot per frame, so computing it separately for
  each frame makes the pointer **jitter** on screen — far worse than an ugly cursor. The automatic hotspot is
  therefore computed from the **union** of all frames (per-pixel maximum alpha), and a hand-placed hotspot is
  applied to every frame unchanged
- **Each frame still bundles all four sizes (32/48/64/96)**, the same structure Microsoft's own
  `aero_busy.ani` uses (18 frames × 64/48/32). Animated cursors stay sharp on high-DPI screens
- Windows only animates **"Busy" and "Working in Background"**; other slots show the first frame even when
  handed an `.ani`. That is Windows' behaviour, not a limitation of this tool — the UI marks those slots with
  "not animated by system"
- The frame delay is stored in the `.ani` in units of **1/60 second** (Microsoft's own file uses
  `iDispRate=3`, i.e. 50 ms), so not every millisecond value is exactly representable; it rounds to the
  nearest

---

## Importing cursor packs made by others

Cursor theme packs downloaded from GitHub (Bibata, apple_cursor, BlueArchive…) can be pulled straight in:
click **Import theme pack…** at the bottom and pick the folder — no need to assign each slot by hand.

**Two ways to work out which file goes where, in priority order:**

1. **Read the pack's `install.inf`** — theme packs almost always ship one, and it already states which file
   belongs to which cursor slot. That is the author's own intent, far more reliable than guessing names.
2. **Guess from the filename** — the only option for packs without an INF. High-star packs are remarkably
   consistent (`normal` / `link` / `busy` / `resizeNS` / `dgn1` …), so the hit rate is decent.

**Files that can't be identified are reported, never force-fitted into a slot** — putting a cursor in the
wrong place is more annoying than admitting it wasn't recognised. (Windows 11 packs also carry `Pan`,
`Zoom-in` and `Zoom-out`, which aren't among this tool's 17 slots; they're listed as unrecognised.)

**Imported cursors are used exactly as-is**: no resizing, no hotspot recalculation, no re-encoding — not a
single pixel changes. Someone else's finished design shouldn't be reshaped by our scaling rules, and the
self-test asserts **the output is byte-identical to the file in the pack**. The trade-off is that the
rendering settings are greyed out for those slots (changing them would do nothing).

---

## Sharing with others

**Export scheme pack** produces a zip:

```
cursors/*.cur, *.ani   ← finished cursors, usable without this tool
images/*.png           ← source images (capped at 512px, all frames for animations) for further editing
install.inf            ← right-click "Install" to add it as a Windows cursor scheme
uninstall.bat          ← removes it from the scheme list again
scheme.json            ← this tool's scheme data
说明.txt               ← instructions (Chinese)
```

**Two ways to use it:**

1. **Right-click `install.inf` → Install** (this prompts for UAC, like every cursor theme pack). It then
   appears in the scheme dropdown of Control Panel → Mouse → Pointers. The recipient does not need this tool
   at all. The scripts copy files into `%windir%\Cursors\<scheme>\` and write the active cursor values to
   `HKCU\Control Panel\Cursors`, so the cursors are applied immediately as well as listed.
2. **Import scheme pack…** at the bottom of this tool, then **Apply to system**. This route needs **no
   administrator rights**.

The `install.inf` follows the same structure as the highest-star cursor packs
([Bibata_Cursor](https://github.com/ful1e5/Bibata_Cursor) 4.1k★, [apple_cursor](https://github.com/ful1e5/apple_cursor) 2.0k★).
Note that the scheme list is a **comma-separated, positionally significant** list of 17 entries (matching the
order shown in Mouse Properties); slots you didn't configure are left empty. Getting that order wrong is the
classic way these packs break — every cursor ends up in the wrong slot.

---

## Where files live

| Location | Contents |
|---|---|
| `%LOCALAPPDATA%\CursorStudio\cursors\*.cur`, `*.ani` | Generated cursor files. **The registry points here — don't move or delete them** |
| `%LOCALAPPDATA%\CursorStudio\images\` | Copies of imported images |
| `%LOCALAPPDATA%\CursorStudio\schemes\*.json` | Saved schemes |
| `%LOCALAPPDATA%\CursorStudio\backup-before-first-apply.json` | Backup of the system cursors before the first apply |
| `%LOCALAPPDATA%\CursorStudio\state.json` | State restored when you close the window |

Cursor filenames carry a content hash: change the content and the path changes. This is necessary —
Windows' `LoadCursorFromFile` caches by path, so a same-named file with new content is never re-read.

Imported images are **copied** into `images\`, so deleting or moving the original afterwards doesn't break
an already-configured scheme.

---

## Supported formats

PNG / JPG / BMP / GIF / TIFF / ICO.

- **GIFs become animated cursors** (see above), not just a first frame
- You can also select **several images at once**; they become the frames of one animation, in selection order
- **WebP and AVIF are not supported** — GDI+ ships no decoder for them (nothing to do with this tool; the OS
  simply doesn't have one). Convert to PNG first

---

## Building from source

```bash
dotnet build -c Release                    # build
dotnet publish -c Release -o dist          # single-file exe
```

### Self-test

The app ships a self-test: **183 checks** standalone, **197** with `--touch-system`. It covers image
processing (scaling / keying / de-haloing / shadow / hotspot mapping), the **full pipeline from UI settings to
`.cur`/`.ani`**, the `.cur` and `.ani` binary formats (write + read back + a real `LoadImage` by Windows), GIF
multi-frame import, **importing third-party theme packs**, scheme pack round-trip and `install.inf`, real
measurement of the stock cursors, UI
layout, and registry read/write/restore.

```bash
dist\CursorStudio.exe --selftest                    # logic and UI only, touches nothing
dist\CursorStudio.exe --selftest --touch-system     # also exercises the registry (restores afterwards)
```

It prints the results, writes `selftest-report.txt` in the current directory, and leaves a set of layout
screenshots at various window sizes in `selftest-shots\`.

The self-test runs entirely inside a temporary directory and never touches your real
`%LOCALAPPDATA%\CursorStudio`. `--touch-system` briefly changes the system cursors (tens of milliseconds) and
restores them in a `finally` block whether it succeeds or fails.

### Code layout

```
Core/
  Slots.cs            the 17 cursor slots (registry value names must match Windows exactly)
  CurFile.cs          .cur reader/writer; the format gotchas are in the class comment
  AniFile.cs          .ani reader/writer; each frame is a complete .cur inside
  Renderer.cs         scaling, keying, de-haloing, automatic hotspots, multi-frame union
                      (`Compose` is the single "render settings → pixels" implementation)
  RenderCache.cs      render cache + image loading (including multi-frame GIF)
  CursorRegistry.cs   registry read/write, snapshot, restore
  Store.cs            paths, import, .cur/.ani generation, schemes, pack + install.inf
  Win32.cs            P/Invoke
UI/
  MainForm.cs         main window
  HotSpotCanvas.cs    magnified preview + hotspot editor
  NameDialog.cs       scheme name prompt
SelfTest.cs           self-test
```

The `.cur` format's biggest trap (documented in `CurFile.cs`): it differs from `.ico` by one byte
(`idType` is 2, not 1), and the 4 bytes that hold "colour planes / bit depth" in `ICONDIRENTRY` are replaced
by the **hotspot coordinates** in cursor files.

A less obvious one is the **AND mask**. For 32-bit cursors modern Windows only reads the alpha channel and
the AND mask could be anything; but some older programs (old Win32 tools, certain remote desktop and terminal
clients) ignore alpha and read only the mask. Filling it with zeros makes them draw the entire square,
including the parts that should be transparent. So the mask is generated bit by bit from `alpha > 127`, and
the self-test asserts that the mask and the alpha channel agree pixel for pixel.

---

## Open-source projects referenced

Read while building this, mainly to confirm format details and a few trade-offs:

- **[quantum5/win2xcur](https://github.com/quantum5/win2xcur)** — Windows ⇄ Linux cursor conversion.
  Its `align.py` provides the standard size ladder `32/48/64/96/128/256`, and `scale.py` confirms hotspots
  scale with the image. This project's size ladder follows it.
- **[ful1e5/Bibata_Cursor](https://github.com/ful1e5/Bibata_Cursor)** (`anicursorgen.py`) — Bibata's generator.
  Two things were taken directly from it: **generating the AND mask bit by bit from alpha**, and the **shadow
  parameters** (right 9.375% / down 3.125% / blur 3.125% / 25% black, all as percentages of the canvas).
  Its `BITMAPINFOHEADER` writing matches this implementation field for field, cross-validating the format.
- **[ful1e5/clickgen](https://github.com/ful1e5/clickgen)** — a Python cursor build toolbox; confirms that
  "hotspots must be configured separately and generic image converters can't do it" is worth making visual.
  Its generated `install.inf` is the reference this project's pack layout follows.
- **[makipom/BlueArchive-Cursors](https://github.com/makipom/BlueArchive-Cursors)** — a real-world pack used
  to check what a complete cursor scheme covers and how `install.inf` is written in practice.
- **[Hello-lingu/win2xcur](https://github.com/Hello-lingu/win2xcur)** — theme-level conversion / install /
  uninstall management; the shape of the scheme pack (zip + instructions + ready-to-install output) follows
  its approach.

Not one line of the above was copied into this project (they're different languages; this is C#). They were
read for approach, and the two items explicitly noted above reuse their parameter values.

---

## Known limits

- A single `.cur` holds at most 255 frames; this tool makes static cursors (`.cur`) and animated ones
  (`.ani`, up to 60 frames). Windows only plays animation in **"Busy" and "Working in Background"**; other
  slots ignore it
- Windows constrains cursor images to roughly 32–64 pixels; larger sizes are simply not displayed
- After applying, a few already-running programs may keep the old cursor until restarted; the login screen
  and newly started programs always show the new one
