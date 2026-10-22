using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Recognition.Browser
{
    // The header picture of a Firefox theme, drawn across the top of the window behind the toolbar and the tab strip (FirefoxThemes.cs
    // reads the theme, AnimatedImage.cs decodes animated PNG/GIF). The pictures are copied, content-addressed, to runtime\theme_header and
    // described by runtime\theme_header\header.v1.json, so the look survives a restart without the theme package.
    //
    // Animation: every frame is composed once, off the UI thread, then the layers swap frames on a timer. It stays still when Windows is set
    // to reduce animations, when you choose "Keep still", when the window is minimised, and when a picture is too large to keep every frame
    // (the first frame is shown instead). Nothing is fetched from the network.
    public partial class MainWindow
    {
        private ThemeHeader? _header;
        private bool _headerAnimate = true;
        private readonly List<DispatcherTimer> _headerTimers = new();
        private int _headerLoadGen;
        private string _headerNote = "";
        private const long MaxHeaderFileBytes = 16L * 1024 * 1024;

        private string HeaderDir => Path.Combine(_repoRoot, "runtime", "theme_header");
        private string HeaderStatePath => Path.Combine(HeaderDir, "header.v1.json");

        private void LoadThemeHeader()
        {
            try { _header = File.Exists(HeaderStatePath) ? FirefoxThemes.StateFromJson(File.ReadAllText(HeaderStatePath), out _headerAnimate) : null; }
            catch { _header = null; }
            if (_header != null && _header.Layers.Any(l => !File.Exists(Path.Combine(HeaderDir, l.Path)))) _header = null;   // a picture went missing: no header
            HookHeaderLayout();
        }

        private void SaveThemeHeaderState()
        {
            try
            {
                Directory.CreateDirectory(HeaderDir);
                if (_header == null) { if (File.Exists(HeaderStatePath)) File.Delete(HeaderStatePath); }
                else File.WriteAllText(HeaderStatePath, FirefoxThemes.StateToJson(_header, _headerAnimate), new UTF8Encoding(false));
                // pictures no layer uses any more are removed
                var used = new HashSet<string>((_header?.Layers ?? Array.Empty<HeaderLayer>()).Select(l => l.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(HeaderDir))
                {
                    var n = Path.GetFileName(f);
                    if (FirefoxThemes.IsStoredFileName(n) && !used.Contains(n)) { try { File.Delete(f); } catch { } }
                }
            }
            catch { }
        }

        // header == null removes the header. themeRoot: the unpacked theme the layer paths are relative to.
        private void SetThemeHeader(ThemeHeader? header, string? themeRoot)
        {
            if (header == null || themeRoot == null) { _header = null; _headerNote = ""; SaveThemeHeaderState(); ShowThemeHeader(); return; }
            var rootFull = Path.GetFullPath(themeRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var stored = new List<HeaderLayer>();
            Directory.CreateDirectory(HeaderDir);
            foreach (var l in header.Layers)
            {
                var safe = FirefoxThemes.SafeHeaderPath(l.Path); if (safe == null) continue;
                var full = Path.GetFullPath(Path.Combine(themeRoot, safe.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
                var fi = new FileInfo(full); if (fi.Length == 0 || fi.Length > MaxHeaderFileBytes) continue;
                var bytes = File.ReadAllBytes(full);
                var ext = Path.GetExtension(full).TrimStart('.').ToLowerInvariant();
                var name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + "." + ext;
                if (!FirefoxThemes.IsStoredFileName(name)) continue;
                var dst = Path.Combine(HeaderDir, name);
                if (!File.Exists(dst)) File.WriteAllBytes(dst, bytes);
                stored.Add(l with { Path = name });
            }
            _header = stored.Count == 0 ? null : header with { Layers = stored };
            _headerNote = stored.Count == header.Layers.Count ? "" : (header.Layers.Count - stored.Count) + " header picture(s) could not be used";
            SaveThemeHeaderState();
            _actions?.Append("theme.header", _header == null ? "none" : stored.Count + " layer(s)");
            ApplyChromeTheme();
            ShowThemeHeader();
        }

        private void SetHeaderAnimate(bool on)
        {
            _headerAnimate = on; SaveThemeHeaderState(); ShowThemeHeader();
        }

        private bool _headerLayoutHooked;
        private void HookHeaderLayout()
        {
            if (_headerLayoutHooked) return; _headerLayoutHooked = true;
            void Fit() { try { HeaderArt.Height = Math.Max(0, TopChrome.ActualHeight + Tabs.ActualHeight); } catch { } }
            TopChrome.SizeChanged += (_, __) => Fit();
            Tabs.SizeChanged += (_, __) => Fit();
            StateChanged += (_, __) => { foreach (var t in _headerTimers) { if (WindowState == WindowState.Minimized) t.Stop(); else t.Start(); } };
            Fit();
        }

        private void StopHeaderTimers() { foreach (var t in _headerTimers) t.Stop(); _headerTimers.Clear(); }

        private sealed record DecodedLayer(BitmapSource[] Frames, int[] Delays, int Loops, string? Note);

        // Builds the layers. Decoding runs off the UI thread; a newer call makes an older, still-running one drop its result.
        private async void ShowThemeHeader()
        {
            int gen = ++_headerLoadGen;
            StopHeaderTimers();
            HeaderArt.Children.Clear();
            var h = _header;
            if (h == null) { HeaderArt.Visibility = Visibility.Collapsed; ApplyChromeTheme(); return; }
            HeaderArt.Background = FrozenBrush(h.FrameColor);
            HeaderArt.Visibility = Visibility.Visible;
            bool animate = _headerAnimate && SystemParameters.ClientAreaAnimation;
            var notes = new List<string>();
            foreach (var layer in h.Layers)
            {
                var file = Path.Combine(HeaderDir, layer.Path);
                DecodedLayer? dl = null;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(file);
                    dl = layer.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? RenderSvgLayer(bytes) : await Task.Run(() => DecodeHeaderLayer(bytes, animate));
                }
                catch (Exception ex) { notes.Add(layer.Path.Substring(0, 8) + ": " + ex.Message); }
                if (gen != _headerLoadGen) return;
                if (dl == null || dl.Frames.Length == 0) continue;
                if (dl.Note != null) notes.Add(dl.Note);
                AddHeaderLayer(layer, dl);
            }
            if (notes.Count > 0) _headerNote = string.Join("; ", notes);
        }

        private void AddHeaderLayer(HeaderLayer layer, DecodedLayer dl)
        {
            var first = dl.Frames[0];
            double pw = first.PixelWidth, ph = first.PixelHeight;
            var brush = new ImageBrush(first)
            {
                Stretch = Stretch.None,
                AlignmentX = layer.AlignX switch { "left" => AlignmentX.Left, "center" => AlignmentX.Center, _ => AlignmentX.Right },
                AlignmentY = layer.AlignY switch { "bottom" => AlignmentY.Bottom, "center" => AlignmentY.Center, _ => AlignmentY.Top },
            };
            var rect = new System.Windows.Shapes.Rectangle { Fill = brush, IsHitTestVisible = false };
            if (layer.Tile != "no-repeat")
            {
                // WPF tiles from the brush origin, so a tiled layer starts at the left/top edge whatever its alignment says
                brush.TileMode = TileMode.Tile; brush.Viewport = new Rect(0, 0, pw, ph); brush.ViewportUnits = BrushMappingMode.Absolute;
                if (layer.Tile == "repeat-x") { rect.Height = ph; rect.VerticalAlignment = layer.AlignY == "bottom" ? VerticalAlignment.Bottom : layer.AlignY == "center" ? VerticalAlignment.Center : VerticalAlignment.Top; }
                else if (layer.Tile == "repeat-y") { rect.Width = pw; rect.HorizontalAlignment = layer.AlignX == "left" ? HorizontalAlignment.Left : layer.AlignX == "center" ? HorizontalAlignment.Center : HorizontalAlignment.Right; }
            }
            HeaderArt.Children.Add(rect);
            if (dl.Frames.Length < 2) return;

            int i = 0, plays = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(dl.Delays[0]) };
            timer.Tick += (_, __) =>
            {
                i++;
                if (i >= dl.Frames.Length)
                {
                    plays++;
                    if (dl.Loops > 0 && plays >= dl.Loops) { timer.Stop(); return; }   // finite loop count: stays on the last frame
                    i = 0;
                }
                brush.ImageSource = dl.Frames[i];
                timer.Interval = TimeSpan.FromMilliseconds(dl.Delays[i]);
            };
            _headerTimers.Add(timer);
            if (WindowState != WindowState.Minimized) timer.Start();
        }

        // Off the UI thread. Every BitmapSource returned is frozen, at 96 dpi (one picture pixel = one layout unit, like Firefox).
        private static DecodedLayer DecodeHeaderLayer(byte[] bytes, bool animate)
        {
            string? note = null;
            if (animate)
            {
                AnimInfo? info = null;
                try { info = AnimatedImage.Probe(bytes); } catch (AnimException ex) { note = "animation not used (" + ex.Message + ")"; }
                if (info != null)
                {
                    var (rows, ok) = AnimatedImage.Budget(info.Width, info.Height, info.Frames.Count);
                    if (!ok) note = "animation too large to keep every frame, shown still";
                    else
                    {
                        try
                        {
                            GifBitmapDecoder? gif = info.Kind == "gif" ? new GifBitmapDecoder(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad) : null;
                            Func<int, byte[]> px = idx =>
                            {
                                var f = info.Frames[idx];
                                BitmapSource src = gif != null ? gif.Frames[idx] : new PngBitmapDecoder(new MemoryStream(f.Png!), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                                if (src.PixelWidth != f.W || src.PixelHeight != f.H) throw new AnimException("frame " + idx + " decodes to a different size");
                                return Bgra(src);
                            };
                            var composed = AnimatedImage.Compose(info.Width, info.Height, info.Frames, px, rows);
                            var frames = composed.Select(b => Frozen(BitmapSource.Create(info.Width, rows, 96, 96, PixelFormats.Bgra32, null, b, info.Width * 4))).ToArray();
                            return new DecodedLayer(frames, info.Frames.Select(f => f.DelayMs).ToArray(), info.Loops, null);
                        }
                        catch (Exception ex) when (ex is AnimException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
                        { note = "animation not used (" + ex.Message + "), shown still"; }
                    }
                }
            }
            // a still picture, or the first frame of an animation
            var dec = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var first = dec.Frames[0];
            if (first.PixelWidth > AnimatedImage.MaxSide || first.PixelHeight > AnimatedImage.MaxSide) throw new AnimException("picture is larger than " + AnimatedImage.MaxSide + " pixels");
            var still = Frozen(BitmapSource.Create(first.PixelWidth, first.PixelHeight, 96, 96, PixelFormats.Bgra32, null, Bgra(first), first.PixelWidth * 4));
            return new DecodedLayer(new[] { still }, new[] { 0 }, 1, note);
        }

        // An SVG pattern tile (SvgLite: paths and fills only), drawn with WPF's own path geometry. UI thread: drawing visuals need it; tiles are small.
        private static DecodedLayer? RenderSvgLayer(byte[] bytes)
        {
            var svg = SvgLite.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            if (svg == null) throw new AnimException("pattern is not a simple SVG this browser reads");
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                foreach (var p in svg.Paths)
                {
                    var g = Geometry.Parse(p.Data).Clone();
                    if (g is PathGeometry pg) pg.FillRule = p.EvenOdd ? FillRule.EvenOdd : FillRule.Nonzero;
                    else { var pg2 = PathGeometry.CreateFromGeometry(g); pg2.FillRule = p.EvenOdd ? FillRule.EvenOdd : FillRule.Nonzero; g = pg2; }
                    var (r, gg, b) = Rgb.Parse(p.Fill);
                    var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(p.OpacityPct * 2.55), (byte)r, (byte)gg, (byte)b)); brush.Freeze();
                    dc.DrawGeometry(brush, null, g);
                }
            var rtb = new RenderTargetBitmap(svg.Width, svg.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv); rtb.Freeze();
            return new DecodedLayer(new BitmapSource[] { rtb }, new[] { 0 }, 1, null);
        }

        private static byte[] Bgra(BitmapSource src)
        {
            var conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var buf = new byte[conv.PixelWidth * conv.PixelHeight * 4];
            conv.CopyPixels(buf, conv.PixelWidth * 4, 0);
            return buf;
        }

        private static BitmapSource Frozen(BitmapSource b) { b.Freeze(); return b; }

        // the toolbar over the header picture, and the tab strip see-through so the picture shows behind the tabs
        private void ApplyHeaderBrushes(Dictionary<string, string> colors)
        {
            var res = Application.Current.Resources;
            if (_header == null)
            {
                res["ChromeTopBg"] = FrozenBrush(colors["Bar"]);
                res["ChromeTabStrip"] = FrozenBrush(colors["Side"]);
                return;
            }
            var (r, g, b) = Rgb.Parse(colors["Bar"]);
            var top = new SolidColorBrush(Color.FromArgb((byte)Math.Round(_header.ToolbarOpacity * 2.55), (byte)r, (byte)g, (byte)b)); top.Freeze();
            res["ChromeTopBg"] = top;
            res["ChromeTabStrip"] = Brushes.Transparent;
        }

        // the Customize page card
        private string ThemeHeaderSectionHtml()
        {
            if (_header == null) return "";
            string E(string s) => System.Net.WebUtility.HtmlEncode(s);
            var sb = new StringBuilder("<div class='sec'>Header picture</div><div class='row'><div><div class='t'>From the Firefox theme you applied</div><div class='u'>");
            sb.Append(_header.Layers.Count).Append(" picture(s) across the top of the window, behind the toolbar and tabs. ");
            sb.Append(_headerAnimate ? "Animated pictures play" : "Animated pictures are kept still");
            if (!SystemParameters.ClientAreaAnimation) sb.Append(" (Windows is set to reduce animations, so they stay still)");
            sb.Append(".");
            if (_headerNote.Length > 0) sb.Append(" Note: ").Append(E(_headerNote)).Append('.');
            sb.Append("</div></div><div class='ts'>");
            sb.Append(_headerAnimate ? "<a class='btn ghost' onclick=\"send('cz-header-anim:off')\">Keep still</a> " : "<a class='btn ghost' onclick=\"send('cz-header-anim:on')\">Animate</a> ");
            sb.Append("<a class='btn ghost' onclick=\"send('cz-header-remove')\">Remove the picture</a></div></div>");
            return sb.ToString();
        }
    }
}
