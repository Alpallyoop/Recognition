# Recognition themes

`library.v1.json` is the theme library. It ships with Recognition (shown offline) and is also what Settings, Customize, Theme library, Browse the library downloads from
`https://raw.githubusercontent.com/Alpallyoop/Recognition/main/themes/library.v1.json` (only when you press the button).

A theme is only data: three colours, a start page background name and a top bar style name. Nothing else is read, so a theme can never carry script, CSS, pictures or addresses.

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

The library file is `{"schema": "recognition.theme.library.v1", "themes": [ ...theme objects without the schema line... ]}`, up to 60 themes and 400 KB.
To add a theme to the library, add an object to `themes` and push to `main`.
