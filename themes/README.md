# Recognition themes

`library.v1.json` is the theme library. It ships with Recognition (shown offline) and is also what Settings, Customize, Theme library, Browse the library downloads from
`https://raw.githubusercontent.com/Alpallyoop/Recognition/main/themes/library.v1.json` (only when you press the button).

A theme is only data: three colours, a start page background name, a top bar style name and, optionally, an animation recipe (four checked values). Nothing else is read, so a theme can never carry script, CSS, pictures or addresses.

## One theme file (`.rtheme` or `.json`, up to 20 KB)

```json
{
  "schema": "recognition.theme.v1",
  "id": "my-theme",
  "name": "My theme",
  "bar": "#1e1e2e",
  "text": "#cdd6f4",
  "accent": "#cba6f7",
  "start_bg": "nebula",
  "top": "violet"
}
```

- `id`: 1 to 31 characters, lower-case letters, digits and `-`; it cannot be the id of a built-in look.
- `bar`, `text`, `accent`: `#rrggbb`. The text must be readable on the bar or the theme is refused.
- `start_bg`: one of `default aurora dusk forest slate light ocean sunset nebula midnight graphite rose lava horizon stars dots stripes blueprint mint sand ice paper`, or a `#rrggbb` colour.
- `top` (optional): `ocean violet emerald teal rose crimson amber mono frosted deep flat`, or empty to match the theme.

## Theme with an animated start page (`recognition.theme.v2`)

Exactly v1 plus an optional `motion` object. Recognition writes v2 only when a theme has an animation; otherwise it writes v1 as before.

```json
{
  "schema": "recognition.theme.v2",
  "id": "my-theme",
  "name": "My theme",
  "bar": "#1e1e2e",
  "text": "#cdd6f4",
  "accent": "#cba6f7",
  "start_bg": "nebula",
  "top": "violet",
  "motion": { "effect": "aurora", "duration_ms": 24000, "intensity_pct": 50, "seed": 1 }
}
```

- `effect` (required): `none kenburns pan drift breathe parallax particles aurora rain snow shimmer fireflies`. `kenburns`, `pan`, `drift`, `breathe` and `parallax` move the start page picture; the others draw over any background.
- `duration_ms`: 4000 to 120000 (one cycle), default 24000. `intensity_pct`: 0 to 100, default 50. `seed`: 0 to 2147483647, default 1.
- Whole numbers only (no `1.5`, `3e4` or `"24000"`), and no other keys. A bad `motion` makes the whole theme refused, like bad colours.
- `"effect": "none"` is the same as no `motion`.
- In a v1 file a `motion` key is ignored.

The animation is drawn by ThemeForge's motion runtime, which ships inside Recognition (no download); see `docs/proposals/0002-start-page-motion-and-theme-v2.md`.

## Chrome theme packages with a Recognition theme inside

A Chrome theme (`.crx`, `.zip` or folder) may also hold `recognition.theme.json` at its root: a v1 or v2 theme as above, up to 20 KB. Its colours, names and animation are used; the Chrome manifest still supplies the start page picture (`theme_ntp_background`). If that file is there but not valid, the theme is not applied. ThemeForge's `<id>.recognition-theme.zip` is built this way.

## The library file

The library file is `{"schema": "recognition.theme.library.v1", "themes": [ ...theme objects without the schema line... ]}`, up to 60 themes and 400 KB. An entry may carry the optional `motion` object (same rules); an entry with a bad one is left out. The schema id stays `recognition.theme.library.v1`.
To add a theme to the library, add an object to `themes` and push to `main`.
