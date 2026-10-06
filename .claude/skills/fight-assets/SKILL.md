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

**fighter.json:** `{ "id", "displayName", "description", "spritePath": "sprite.webp", "scale": 1.0, "outfits": [...] }`.
`outfits` is optional: `sprite2.webp` … `sprite9.webp` beside `sprite.webp` are picked up as outfits 2–9 on their own.
The `displayName` is what the chat types to vote (`!p1 dj jenni`), so keep it short and typeable.
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
  An outfit is not a new fighter. Install it as the next free `sprite<N>.webp` (2–9) in the
  fighter's existing folder, and optionally name it in that fighter.json's `outfits` list:
  `"outfits": [{ "code": 2, "name": "Läderjacka" }]`. Codes above 9, or files with other names,
  need an `outfits` entry with `spritePath`. The chat picks it with the digit after the name
  (`!p1 my2`).
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

### Special attacks

A fighter's own super, described under `special` in fighter.json:
`{ "name": "Molotov", "style": "throw" | "swing" | "saw", "spritePath": "special.webp", "propPath": "prop.webp", "color": "#4dff6a" }`.
`throw` needs `prop.webp`, the thing thrown, and falls back to `swing` without it.

An outfit can have a special of its own, drawn in its clothes: put `special` inside its `outfits` entry,
`{ "code": 2, "name": "Rutig", "special": { "name": "Bitchslap", "style": "swing", "color": "#ff4fc3" } }`.
Its files default to `special<code>.webp` and `prop<code>.webp`. Cut that outfit's reference from its own
strip (`sprite<code>.webp`). An outfit without one uses the fighter's special (and its kick for the poses).

1. Cut a reference out of the fighter's own strip (its stance and kick side by side on grey) and attach that, so
   the poses match the game exactly.
2. Fill `prompts/special.txt`: `{SPECIAL}` is what the weapon is, `{WINDUP}` and `{STRIKE}` the two poses
   (the first cell is always the ordinary stance – it sets the scale). Use `prompts/prop.txt` for a thrown prop.
3. If the weapon or anything the fighter wears is green, generate on magenta: set `{BG_NAME}` to
   `chroma magenta` and `{BG_HEX}` to `#FF00FF`, and pass `--magenta` to the scripts below.
4. Process with room for the weapon:

```bash
node process-fighter.js <gen>/out/<id>_special.png <gen>/out/<id>/special.webp 3 --cell 1024x760 [--magenta]
node process-prop.js    <gen>/out/<id>_prop.png    <gen>/out/<id>/prop.webp [--magenta]
```

Check `special.preview.png`: stance, wind-up, strike, left to right, nothing clipped.

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
