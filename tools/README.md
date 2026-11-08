# GEE screenshot tools

Automated build + in-game screenshot run + post-processing, driven by the AutoShot harness (`src/AutoShot.cs`).

## Run

From Claude (one call):

    powershell -File tools/run-shots.ps1 pad andes

Options: `-NoBuild` (skip build/DLL copy), `-Baseline` (promote this run's shots to `tools/baseline/`),
`-Timeout 600` (seconds), `-Threshold 6` (mean-abs-diff flag level, 0-255). No recipe name = `smoke`.

What it does: builds `src/net/GEE.Net.csproj` and `src/GEE.csproj` (Release) into `tools/.staging/`
(the running game does not block this), kills StarMap.exe, copies the DLLs into the mod folder,
writes `autoshot.txt` (`#autoquit` + recipes + `quit`), empties `shots/`, starts `mods/StarMap/StarMap.exe`,
waits for `shots/DONE` (fallback: process exit, then timeout; then taskkill), runs `postprocess.py`.

## Recipes (`tools/recipes/*.txt`)

Plain AutoShot commands, `#` comments. Do not put `#autoquit`/`quit` in them (the runner adds those).

Use `clouds off` (AutoShot command, right after the first `waitidle 30`) to hide volumetric clouds and cloud shadows; `clouds on` restores them. It sets the live graphics settings (Clouds, CloudShadows=Off). The console `renderclouds <celestial>` is only a per-body toggle and unreliable.
`smoke` (load, one shot, no exceptions), `pad` (LC-39A, sun 30 deg, 5 orbit shots), `vab`, `andes`
(satellite height check ~600 km), `night`. Each loads the game itself; recipes combined in one run
load the save again, so group related shots in one recipe.

## Output (`shots/`)

- `NAME.png` full size, `small/NAME.jpg` 1280 px (view these with Read)
- `contact_sheet.jpg` labelled grid of the run
- `diff_NAME.jpg` (x4 amplified) + score per shot vs `tools/baseline/NAME.png`; `<-- CHANGED` above the threshold
- assertion summary from `results.json`, error-like lines from the newest game log

`postprocess.py` needs Pillow (`pip install pillow`); without it only an exact-match compare runs and it says so.
It can be run alone: `python tools/postprocess.py --shots shots --baseline tools/baseline`.

## Caveats

Recipes were written from notes, not run: `view`'s ALT unit (metres assumed), `sunel` with negative angles,
and the exact `results.json` schema (parser is tolerant) need a first real run to confirm.

## Video (frame sequences)

`record NAME N [SECONDS]` in a recipe takes N screenshots `NAME_001..NAME_N`, SECONDS apart (default 1; a screenshot itself
takes about 1 s). `postprocess.py` turns each sequence into:

- `shots/film_NAME.jpg`: 12 evenly sampled frames in a labelled grid (this is what to look at)
- `shots/NAME.gif`: animation, max 60 frames
- `shots/NAME.mp4`: only if `ffmpeg` is on PATH
- a motion score per step; a sequence where 80 % of the steps are near-identical is flagged `FROZEN?`

The full-size frame PNGs/JPGs are moved to `shots/frames/`. Try it with `run-shots.ps1 video`.
Put camera and `simspeed` commands before the `record` line to film a camera move, a time warp or a launch.
