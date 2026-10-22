using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Recognition.Browser
{
    // Animated header pictures of Firefox themes: animated PNG (APNG) and GIF. Pure byte work, executed by the tests:
    //   * Apng.Parse  - reads the chunks (CRC checked), the animation control and every frame, and rebuilds each frame as a small standalone PNG
    //                   (its own IHDR, the palette and transparency chunks, its image data) that any PNG decoder can read;
    //   * Gif.Parse   - walks the GIF blocks for each frame's place, size, delay and disposal (the pixels come from the platform GIF decoder);
    //   * Compose     - draws the frames onto the canvas the way browsers do (dispose none / background / previous, blend source / over),
    //                   keeping only the top rows the window can show.
    // Limits keep a hostile file small: canvas sides, frame count and the memory the finished frames may take. Over the memory budget the
    // picture is shown still (first frame), never half-animated.
    internal sealed class AnimException : Exception { public AnimException(string m) : base(m) { } }

    internal enum AnimDispose { None = 0, Background = 1, Previous = 2 }

    internal sealed record AnimFrame(int X, int Y, int W, int H, int DelayMs, AnimDispose Dispose, bool BlendOver, byte[]? Png, int Index);

    internal sealed record AnimInfo(string Kind, int Width, int Height, int Loops, IReadOnlyList<AnimFrame> Frames);

    internal static class AnimatedImage
    {
        public const int MaxSide = 8192, MaxFrames = 300, MaxRows = 240;
        public const long MaxFramesBytes = 96L * 1024 * 1024;

        // What browsers do: a delay of 10 ms or less is shown as 100 ms; nothing waits longer than a minute.
        public static int NormalizeDelay(int ms) => ms <= 10 ? 100 : Math.Min(ms, 60_000);

        // How many rows of the canvas are kept, and whether every frame fits the memory budget.
        public static (int Rows, bool Animate) Budget(int width, int height, int frames)
        {
            int rows = Math.Min(height, MaxRows);
            long bytes = (long)width * rows * 4 * frames;
            return (rows, frames > 1 && bytes <= MaxFramesBytes);
        }

        // null for a still picture (or a format that is not APNG/GIF); AnimException for a damaged animation.
        public static AnimInfo? Probe(byte[] data)
        {
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return Apng.Parse(data);
            if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F') { var g = Gif.Parse(data); return g.Frames.Count > 1 ? g : null; }
            return null;
        }

        // Straight-alpha BGRA. framePixels(i) returns frame i's own pixels (W*H*4). Returns one buffer of width*rows*4 per frame.
        public static List<byte[]> Compose(int width, int height, IReadOnlyList<AnimFrame> frames, Func<int, byte[]> framePixels, int rows)
        {
            if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide) throw new AnimException("canvas size out of range");
            rows = Math.Clamp(rows, 1, height);
            var canvas = new byte[width * height * 4];
            var outList = new List<byte[]>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                if (f.W <= 0 || f.H <= 0 || f.X < 0 || f.Y < 0 || f.X + f.W > width || f.Y + f.H > height) throw new AnimException("frame " + i + " lies outside the canvas");
                var px = framePixels(i);
                if (px == null || px.Length != f.W * f.H * 4) throw new AnimException("frame " + i + " has the wrong pixel count");
                byte[]? saved = null;
                if (f.Dispose == AnimDispose.Previous) saved = CopyRegion(canvas, width, f);
                for (int y = 0; y < f.H; y++)
                {
                    int src = y * f.W * 4, dst = ((f.Y + y) * width + f.X) * 4;
                    if (!f.BlendOver) { Buffer.BlockCopy(px, src, canvas, dst, f.W * 4); continue; }
                    for (int x = 0; x < f.W; x++, src += 4, dst += 4)
                    {
                        int sa = px[src + 3];
                        if (sa == 0) continue;
                        if (sa == 255) { canvas[dst] = px[src]; canvas[dst + 1] = px[src + 1]; canvas[dst + 2] = px[src + 2]; canvas[dst + 3] = 255; continue; }
                        int da = canvas[dst + 3];
                        int oa = sa * 255 + da * (255 - sa);            // alpha * 255
                        if (oa == 0) { canvas[dst] = canvas[dst + 1] = canvas[dst + 2] = canvas[dst + 3] = 0; continue; }
                        for (int c = 0; c < 3; c++)
                            canvas[dst + c] = (byte)((px[src + c] * sa * 255 + canvas[dst + c] * da * (255 - sa) + oa / 2) / oa);
                        canvas[dst + 3] = (byte)((oa + 127) / 255);
                    }
                }
                var shot = new byte[width * rows * 4];
                Buffer.BlockCopy(canvas, 0, shot, 0, shot.Length);
                outList.Add(shot);
                if (f.Dispose == AnimDispose.Background) ClearRegion(canvas, width, f);
                else if (f.Dispose == AnimDispose.Previous && saved != null) PasteRegion(canvas, width, f, saved);
            }
            return outList;
        }

        private static byte[] CopyRegion(byte[] c, int width, AnimFrame f)
        {
            var r = new byte[f.W * f.H * 4];
            for (int y = 0; y < f.H; y++) Buffer.BlockCopy(c, ((f.Y + y) * width + f.X) * 4, r, y * f.W * 4, f.W * 4);
            return r;
        }
        private static void PasteRegion(byte[] c, int width, AnimFrame f, byte[] r)
        {
            for (int y = 0; y < f.H; y++) Buffer.BlockCopy(r, y * f.W * 4, c, ((f.Y + y) * width + f.X) * 4, f.W * 4);
        }
        private static void ClearRegion(byte[] c, int width, AnimFrame f)
        {
            for (int y = 0; y < f.H; y++) Array.Clear(c, ((f.Y + y) * width + f.X) * 4, f.W * 4);
        }
    }

    internal static class Apng
    {
        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n => { uint c = (uint)n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; return c; }).ToArray();
        public static uint Crc(byte[] b, int off, int len) { uint c = 0xFFFFFFFFu; for (int i = off; i < off + len; i++) c = CrcTable[(c ^ b[i]) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }
        private static uint BE32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        private static int BE16(byte[] b, int o) => b[o] << 8 | b[o + 1];
        private static void PutBE32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

        public static void WriteChunk(Stream s, string type, byte[] data, int off, int len)
        {
            PutBE32(s, (uint)len);
            var buf = new byte[4 + len];
            for (int i = 0; i < 4; i++) buf[i] = (byte)type[i];
            Buffer.BlockCopy(data, off, buf, 4, len);
            s.Write(buf, 0, buf.Length);
            PutBE32(s, Crc(buf, 0, buf.Length));
        }

        private sealed class Pending { public int X, Y, W, H, Delay; public AnimDispose Dispose; public bool Over; public List<(int Off, int Len)> Data = new(); }

        // null when the PNG is not animated (no acTL, or a single frame).
        public static AnimInfo? Parse(byte[] b)
        {
            var sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (b.Length < 8 || !b.Take(8).SequenceEqual(sig)) throw new AnimException("not a PNG");
            int pos = 8; byte[]? ihdr = null; uint numFrames = 0, plays = 0; bool sawActl = false, sawIdat = false;
            var keep = new List<(string Type, int Off, int Len)>();     // PLTE, tRNS and colour chunks every frame needs
            var frames = new List<Pending>(); Pending? cur = null; long lastSeq = -1; bool firstIsDefault = false;
            while (true)
            {
                if (pos + 12 > b.Length) throw new AnimException("truncated PNG");
                uint len = BE32(b, pos);
                if (len > int.MaxValue - 12 || pos + 12 + len > b.Length) throw new AnimException("chunk runs past the end of the file");
                var type = new string(new[] { (char)b[pos + 4], (char)b[pos + 5], (char)b[pos + 6], (char)b[pos + 7] });
                int d = pos + 8, n = (int)len;
                if (Crc(b, pos + 4, n + 4) != BE32(b, d + n)) throw new AnimException("bad CRC in " + type);
                switch (type)
                {
                    case "IHDR":
                        if (n != 13) throw new AnimException("bad IHDR");
                        ihdr = b.Skip(d).Take(13).ToArray(); break;
                    case "acTL":
                        if (n != 8 || sawIdat) throw new AnimException("bad acTL");
                        numFrames = BE32(b, d); plays = BE32(b, d + 4); sawActl = true; break;
                    case "fcTL":
                    {
                        if (n != 26) throw new AnimException("bad fcTL");
                        long seq = BE32(b, d); if (seq <= lastSeq) throw new AnimException("frames out of order"); lastSeq = seq;
                        int dn = BE16(b, d + 20), dd = BE16(b, d + 22); if (dd == 0) dd = 100;
                        byte dop = b[d + 24], bop = b[d + 25];
                        if (dop > 2 || bop > 1) throw new AnimException("bad dispose or blend value");
                        cur = new Pending { W = (int)BE32(b, d + 4), H = (int)BE32(b, d + 8), X = (int)BE32(b, d + 12), Y = (int)BE32(b, d + 16), Delay = AnimatedImage.NormalizeDelay((int)((long)dn * 1000 / dd)), Dispose = (AnimDispose)dop, Over = bop == 1 };
                        if (frames.Count >= AnimatedImage.MaxFrames) throw new AnimException("more than " + AnimatedImage.MaxFrames + " frames");
                        frames.Add(cur);
                        if (!sawIdat) firstIsDefault = true;
                        break;
                    }
                    case "IDAT":
                        sawIdat = true;
                        if (cur != null && frames.Count == 1 && firstIsDefault) cur.Data.Add((d, n));
                        break;
                    case "fdAT":
                    {
                        if (n < 4 || cur == null) throw new AnimException("bad fdAT");
                        long seq = BE32(b, d); if (seq <= lastSeq) throw new AnimException("frames out of order"); lastSeq = seq;
                        cur.Data.Add((d + 4, n - 4)); break;
                    }
                    case "PLTE": case "tRNS": case "gAMA": case "cHRM": case "sRGB": case "iCCP": case "sBIT":
                        if (!sawIdat) keep.Add((type, d, n)); break;
                }
                pos += 12 + n;
                if (type == "IEND") break;
            }
            if (ihdr == null) throw new AnimException("no IHDR");
            int width = (int)BE32(ihdr, 0), height = (int)BE32(ihdr, 4);
            if (width <= 0 || height <= 0 || width > AnimatedImage.MaxSide || height > AnimatedImage.MaxSide) throw new AnimException("picture size out of range");
            if (!sawActl || frames.Count < 2 || numFrames < 2) return null;
            if (frames.Any(f => f.Data.Count == 0)) throw new AnimException("a frame has no image data");
            if (frames[0].Dispose == AnimDispose.Previous) frames[0].Dispose = AnimDispose.Background;   // nothing before the first frame
            var list = new List<AnimFrame>();
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                if (f.W <= 0 || f.H <= 0 || f.X < 0 || f.Y < 0 || (long)f.X + f.W > width || (long)f.Y + f.H > height) throw new AnimException("frame " + i + " lies outside the picture");
                using var ms = new MemoryStream();
                ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
                var h = (byte[])ihdr.Clone();
                h[0] = (byte)(f.W >> 24); h[1] = (byte)(f.W >> 16); h[2] = (byte)(f.W >> 8); h[3] = (byte)f.W;
                h[4] = (byte)(f.H >> 24); h[5] = (byte)(f.H >> 16); h[6] = (byte)(f.H >> 8); h[7] = (byte)f.H;
                WriteChunk(ms, "IHDR", h, 0, 13);
                foreach (var k in keep) WriteChunk(ms, k.Type, b, k.Off, k.Len);
                foreach (var (off, len) in f.Data) WriteChunk(ms, "IDAT", b, off, len);
                WriteChunk(ms, "IEND", Array.Empty<byte>(), 0, 0);
                list.Add(new AnimFrame(f.X, f.Y, f.W, f.H, f.Delay, f.Dispose, f.Over, ms.ToArray(), i));
            }
            return new AnimInfo("apng", width, height, (int)Math.Min(plays, int.MaxValue), list);
        }
    }

    internal static class Gif
    {
        private static int LE16(byte[] b, int o) => b[o] | b[o + 1] << 8;

        public static AnimInfo Parse(byte[] b)
        {
            if (b.Length < 13 || b[0] != 'G' || b[1] != 'I' || b[2] != 'F') throw new AnimException("not a GIF");
            int width = LE16(b, 6), height = LE16(b, 8);
            if (width <= 0 || height <= 0 || width > AnimatedImage.MaxSide || height > AnimatedImage.MaxSide) throw new AnimException("picture size out of range");
            int pos = 13;
            if ((b[10] & 0x80) != 0) pos += 3 * (1 << ((b[10] & 7) + 1));
            int loops = 1; int delay = 0; AnimDispose disp = AnimDispose.None;
            var frames = new List<AnimFrame>();
            int SkipSubBlocks(int p)
            {
                while (true)
                {
                    if (p >= b.Length) throw new AnimException("truncated GIF");
                    int n = b[p++]; if (n == 0) return p;
                    p += n;
                }
            }
            while (true)
            {
                if (pos >= b.Length) break;   // a missing trailer is tolerated, as browsers do
                byte tag = b[pos++];
                if (tag == 0x3B) break;
                if (tag == 0x21)
                {
                    if (pos >= b.Length) throw new AnimException("truncated GIF");
                    byte label = b[pos++];
                    if (label == 0xF9 && pos + 5 < b.Length && b[pos] == 4)
                    {
                        int packed = b[pos + 1];
                        delay = LE16(b, pos + 2) * 10;
                        int dm = (packed >> 2) & 7;
                        disp = dm == 2 ? AnimDispose.Background : dm == 3 ? AnimDispose.Previous : AnimDispose.None;
                        pos = SkipSubBlocks(pos);
                    }
                    else if (label == 0xFF && pos + 15 < b.Length && b[pos] == 11 && System.Text.Encoding.ASCII.GetString(b, pos + 1, 11) is "NETSCAPE2.0" or "ANIMEXTS1.0")
                    {
                        int p = pos + 12;
                        if (p + 3 < b.Length && b[p] == 3 && b[p + 1] == 1) { int l = LE16(b, p + 2); loops = l == 0 ? 0 : l + 1; }
                        pos = SkipSubBlocks(pos + 12);
                    }
                    else pos = SkipSubBlocks(pos);
                    continue;
                }
                if (tag == 0x2C)
                {
                    if (pos + 9 > b.Length) throw new AnimException("truncated GIF");
                    int x = LE16(b, pos), y = LE16(b, pos + 2), w = LE16(b, pos + 4), h = LE16(b, pos + 6), packed = b[pos + 8];
                    pos += 9;
                    if ((packed & 0x80) != 0) pos += 3 * (1 << ((packed & 7) + 1));
                    pos += 1;   // LZW minimum code size
                    pos = SkipSubBlocks(pos);
                    if (frames.Count >= AnimatedImage.MaxFrames) throw new AnimException("more than " + AnimatedImage.MaxFrames + " frames");
                    // a frame reaching past the screen is clipped by browsers; here it must fit (the decoder hands back the frame's own size)
                    if (w <= 0 || h <= 0 || x + w > width || y + h > height) throw new AnimException("frame " + frames.Count + " lies outside the picture");
                    frames.Add(new AnimFrame(x, y, w, h, AnimatedImage.NormalizeDelay(delay), disp, true, null, frames.Count));
                    delay = 0; disp = AnimDispose.None;
                    continue;
                }
                throw new AnimException("unknown GIF block 0x" + tag.ToString("x2"));
            }
            if (frames.Count == 0) throw new AnimException("GIF has no frames");
            return new AnimInfo("gif", width, height, loops, frames);
        }
    }
}
