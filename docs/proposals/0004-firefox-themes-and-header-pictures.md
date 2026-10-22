# Proposal 0004: Firefox themes and animated header pictures

Status: implemented in code (2026-10-22), not yet built or run on Windows (smoke test section Y).
Files: `browser/FirefoxThemes.cs`, `browser/AnimatedImage.cs` (pure, tested), `browser/MainWindow.ThemeHeader.cs` (window glue), small changes in `Customize.cs`, `ThemeLibrary.cs`, `MainWindow.Customize.cs`, `MainWindow.Extensions.cs`, `MainWindow.xaml`, `App.xaml`.

## Why

The owner asked that Recognition take the themes on addons.mozilla.org (Firefox "Themes": saturn plus, Retro neon, Laser Chamber, Praise the sun (animated), Purple starfield - Animated, Nyan Cat (Animated), orange and black, Terminal Deluxe, Neapolitan Ice Cream...). Before this change a Firefox theme was not recognised as a theme at all (its colours are CSS strings, and its picture is a header, not a start page background).

## What it does

- **Reading** (`FirefoxThemes.cs`): CSS colours (`#rgb[a]`, `#rrggbb[aa]`, `rgb()/rgba()` with comma or space syntax and percentages, `hsl()/hsla()`, common names, `transparent`) and `[r,g,b(,a)]` arrays; Firefox and legacy keys (`frame`/`accentcolor`, `tab_background_text`/`textcolor`, `toolbar`, `toolbar_text`/`bookmark_text`, `tab_line`, `ntp_background`...). Bar = toolbar laid over frame by its alpha; text and accent replaced when unreadable or invisible. Unknown colours are ignored, never guessed.
- **Header picture**: `theme_frame` (legacy `headerURL`) at right-top, `additional_backgrounds` in front of it in CSS order with their alignment and tiling; at most 6 layers. Drawn behind the toolbar and the tab strip; the frame colour under it; the toolbar drawn see-through over it at the theme's toolbar alpha (at most 75 %, 60 % when the theme gives no toolbar colour), because in Recognition the toolbar sits above the tabs.
- **Animation** (`AnimatedImage.cs`): animated PNG (chunks CRC-checked, frames rebuilt as standalone PNGs, dispose none/background/previous, blend source/over) and GIF (block walk for place, delay, disposal, loop count; pixels from the platform decoder). Frames are composed once, off the UI thread, keeping at most 240 rows, within a 96 MB budget (otherwise shown still), then swapped on a timer. Delays of 10 ms or less play at 100 ms, like browsers. Still when Windows reduces animations, when "Keep still" is chosen on Customize, and while minimised.
- **SVG patterns** (`SvgLite`): the theme maker's tiles only (`svg`, `path`, `rect`, attribute-free `g`; fill, opacity, fill-rule). No DTDs, scripts, links, styles, images or transforms; anything else drops the layer.
- **Getting themes**: Customize accepts a Firefox Add-ons theme link (looked up through Mozilla's API, the same governed downloader), an `.xpi` file or a folder; the Extensions page recognises a downloaded Firefox theme as a theme ("Use this theme"), never as an extension.
- **Kept state**: pictures copied content-addressed to `runtime\theme_header\<sha256>.<ext>`, described by `runtime\theme_header\header.v1.json` (schema `recognition.theme_header.v1`: `frame`, `toolbar_opacity` 0-100, `animate`, `layers[{file,x,y,tile}]`; integers only, fixed key order, LF; anything else is refused on load). Applying any other look removes the header; your own colours keep it.

## Evidence

- `browser.tests`: colour grammar, mapping, layer order, hostile paths, the saved state (including negatives), SVG subset (16 hostile documents refused), the APNG and GIF decoders and compositor against fixtures whose reference frames were made with Pillow 12.3 (GIF: Pillow's own rendering; APNG: Pillow `alpha_composite` under the APNG rules, see the fixture note). Exact match.
- The nine real themes above, downloaded from addons.mozilla.org on 2026-10-22, run through the real unpacker, reader and decoder in a cloud harness: all nine read; the five animated layers (281 frames, about 4.5 million pixels) compose identically to Pillow's rendering of the same files.
- `stress.tests`: random manifests, thousands of damaged APNG/GIF copies and random SVG fragments: no crash, no out-of-range result.

## Not done / limits

- The WPF rendering itself (layer placement, see-through toolbar, timer, SVG drawing with WPF geometry) is not exercised by automated tests: smoke test section Y.
- Tiled layers start at the left/top edge whatever their alignment (WPF tiles from the brush origin).
- Firefox colours Recognition has no place for (`toolbar_field`, `popup`, `sidebar`, `tab_selected`...) are not used; the address field and menus are derived from the bar colour as for every other look.
- Dynamic themes (extensions that change the theme from script) are extensions, not themes; they go through the Extensions page like any other extension.
- WebP, AVIF and full SVG are not read.
