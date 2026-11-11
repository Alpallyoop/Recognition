using System;
using System.Security.Cryptography;
using System.Text;

namespace Recognition.Browser
{
    // The one bit of the WebSocket protocol worth hand-writing and testing: the opening-handshake accept key (RFC 6455 §4.2.2). The
    // frame-level protocol itself is left to the BCL's System.Net.WebSockets.WebSocket (CdpServer hands it the raw stream after the
    // upgrade), so there is no hand-rolled masking/fragmentation to get wrong.
    internal static class WsHandshake
    {
        private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        // Sec-WebSocket-Accept = base64(SHA1(Sec-WebSocket-Key + magic GUID)).
        public static string AcceptKey(string clientKey)
            => Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes((clientKey ?? "").Trim() + Guid)));
    }
}
