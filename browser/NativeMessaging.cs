using System;
using System.IO;
using System.Text;

namespace Recognition.Browser
{
    // The browser native-messaging wire format (proposal 0012, Phase 4): each message is a 4-byte little-endian length followed by that
    // many bytes of UTF-8 JSON, over stdin/stdout. Pure and stream-based so it is tested with a MemoryStream. A length outside the
    // allowed range, or a truncated stream, returns null rather than throwing — the host loop ends cleanly on a bad or closed pipe.
    internal static class NativeMessaging
    {
        public const int MaxMessage = 8 * 1024 * 1024;   // the native-messaging host-bound limit

        public static void WriteFrame(Stream s, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var len = BitConverter.GetBytes((uint)body.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(len);
            s.Write(len, 0, 4);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        public static string? ReadFrame(Stream s)
        {
            var lenBuf = ReadExactly(s, 4);
            if (lenBuf == null) return null;
            if (!BitConverter.IsLittleEndian) Array.Reverse(lenBuf);
            uint len = BitConverter.ToUInt32(lenBuf, 0);
            if (len == 0 || len > MaxMessage) return null;
            var body = ReadExactly(s, (int)len);
            if (body == null) return null;
            return Encoding.UTF8.GetString(body);
        }

        private static byte[]? ReadExactly(Stream s, int n)
        {
            var b = new byte[n]; int off = 0;
            while (off < n)
            {
                int r = s.Read(b, off, n - off);
                if (r == 0) return null;   // end of stream
                off += r;
            }
            return b;
        }
    }
}
