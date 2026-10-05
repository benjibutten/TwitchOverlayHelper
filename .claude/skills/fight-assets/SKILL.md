---
name: fight-assets
description: Generate new fighters, outfits and arenas for the wait-screen fight (fight.html) with Codex image generation, and turn them into the sprite.webp / arena.webp folders the app loads. Use when the user wants a new character, a new outfit for an existing character, or a new scene/arena/background for the fight – including arenas with transparent parts that show the stream through.
---

# Fight assets: fighters, outfits and arenas

The wait screen draws fighters and arenas from the user's fight folder:

```
%LOCALAPPDATA%\TwitchOverlayHelper\fight\
  fighters\<id>\fighter.json + sprite.webp
  arenas\<id>\arena.json   + arena.webp
```

The app picks new folders up when the user clicks **Ladda om** on the **Fajt** tab. Nothing has to be
rebuilt. Only add to `src/TwitchOverlayHelper/Fight/Defaults/` when the user wants the asset to ship
with the app. Defaults are written to the profile once, on first start.

Tools live in `tools/fight-assets/`. Run `npm install` there once (it needs `sharp`).

## Formats (fixed: the page depends on them)

**Fighter strip:** `sprite.webp` is 8 cells side by side, 640 × 560 px each (5120 × 560), transparent
background, feet on a baseline 10 px above the cell bottom, torso centred, every pose facing **right**.
The frame order is: idle, idle (breathing), walk, punch, kick, hit, knocked out (lying), victory.
`process-fighter.js` produces exactly this from a 4 × 2 sheet on green.

**fighter.json:** `{ "id", "displayName", "description", "spritePath": "sprite.webp", "scale": 1.0 }`.
`scale` (0.3–3) is for a figure that came out too big or too small next to the others.

**Arena:** any 16:9 picture, scaled to cover 1920 × 1080. **arena.json:**
`{ "id", "displayName", "description", "imagePath": "arena.webp", "floor", "left", "right" }`.
- `floor`: where the fighters' feet go, as a fraction of the picture height from the top.
- `left` / `right`: how far fighters may walk, as fractions of the picture width (left < 0.45, right > 0.55).
- Transparent pixels show the stream through. `none` is reserved for the built-in "no background" arena.

## Workflow

Work in the session scratchpad, not the repo. Make a `gen/` folder there.

### 1. Gather references

Ask the user for the character reference (a character sheet works best) unless one is already in
the conversation or the repo. Copy it into `gen/` as `ref_<id>.png`. For arenas, attach one or two
fighter references so the style matches. The existing refs are the Silver and Ink character sheets.
If they are not around, the strips themselves in `Fight/Defaults/fighters/*/sprite.webp` work as
style references too.

### 2. Write the prompt from a template

Fill in the placeholders in `tools/fight-assets/prompts/`:

| Template | Use for | Placeholders |
|---|---|---|
| `fighter.txt` | new character, or new outfit | `{REFERENCE_FILE}`, `{LOOK}` (hair, clothes, accessories in words), `{OUTFIT}`, `{ID}` |
| `arena-backdrop.txt` | full opaque scene | `{SCENE}`, `{ID}` |
| `arena-transparent.txt` | scene over the stream | `{STAGE}` (what they stand on), `{EXTRAS}` (numbered corner objects), `{ID}` |

- **New character:** set `{OUTFIT}` to `Same outfit as the reference.`
- **New outfit** for an existing character: describe the new clothes in `{OUTFIT}`, for example
  `Wearing a red leather jacket, ripped jeans and boots INSTEAD of the outfit in the reference - only the face, hair, body and tattoos come from the reference.`
  Give it its own id, like `silver-leather`. It is a separate fighter that can stand in either corner.
- **Transparent arena:** keep the opaque parts few and at the edges (a platform, corner props, a crowd
  peeking up from the bottom edge, something hanging from the top). Leave the middle empty. Never use
  light beams, haze or glows. They cannot be keyed out cleanly. Be playful with what the fighters
  stand on.

Write the filled prompt to `gen/p_<id>.txt`.

### 3. Generate with Codex

From `gen/` (PowerShell or bash), attaching every reference with `-i`:

```bash
codex exec -s workspace-write --skip-git-repo-check \
  -c 'sandbox_workspace_write.network_access=true' \
  -i ref_<id>.png - < p_<id>.txt > log_<id>.txt 2>&1
```

This takes a minute or two, so run it in the background. The image lands in `gen/out/`. Look at it
with Read before going on. Regenerate if:
- a pose is missing, two poses touch, or a pose crosses into another cell;
- any pose faces left;
- the background is not flat green, or there is green on the character.

### 4. Process

```bash
cd tools/fight-assets
node process-fighter.js <gen>/out/<id>_sheet.png <gen>/out/<id>/sprite.webp
node process-arena.js   <gen>/out/<id>.png       <gen>/out/<id>/arena.webp            # transparent arena
node process-arena.js   <gen>/out/<id>.png       <gen>/out/<id>/arena.webp --opaque   # full backdrop
```

- `process-fighter.js` fails with `expected 8 figures` when poses touch or are missing. Regenerate.
- It writes `sprite.preview.png`. Check that all 8 poses are there in order, with no green fringe
  and nothing clipped.
- `process-arena.js` writes `arena.preview.png` (on a checkerboard) and prints `floorGuess`. Use the
  guess as the starting value for `floor`. Then look at the preview and adjust so the feet land on
  the walking surface, not on its front rim. Set `left`/`right` so the fighters' centres stay
  over the surface. Fighters stand about 0.24 of the picture width apart at the start.

### 5. Install

Make the folder with its manifest and copy the webp in:

```
%LOCALAPPDATA%\TwitchOverlayHelper\fight\fighters\<id>\fighter.json + sprite.webp
%LOCALAPPDATA%\TwitchOverlayHelper\fight\arenas\<id>\arena.json + arena.webp
```

Ids: lowercase a–z, 0–9, `-`, `_`. Display names and descriptions are Swedish, like the rest of
the app. Then tell the user to click **Ladda om** on the **Fajt** tab and pick the new fighter or arena.
An open wait screen in OBS updates by itself.

To ship it with the app as well, put the same folder under
`src/TwitchOverlayHelper/Fight/Defaults/fighters/<id>/` or `.../arenas/<id>/`. The csproj embeds
everything there. Existing users get it at their next start, because seeding is per id.

### 6. Check it on the real page (optional, recommended for arenas)

With the app running, click **Förhandsgranska** on the Fajt tab. Without the app, take a headless
screenshot. Serve `src/TwitchOverlayHelper/Web/wwwroot` plus `/fight/fighter/<id>` and
`/fight/arena/<id>`, and answer the `/ws` socket with
`{"type":"hello","payload":{p1,p2,arena,commands,headline,subline}}` (see `DockFightSetup` in
`Web/DockContracts.cs`). Then look at the result with Read. Check that the feet sit on the floor,
that nobody walks off the edge, and that the HUD does not cover anything important.
