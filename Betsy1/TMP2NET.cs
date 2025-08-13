// TMP2NET_Alpha_Flex.cs
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Betsy1
{
    // TPM2.NET receiver that fills a SignLayer, optionally as an alpha overlay.
    // Tolerant of header variants and payload sizes; will resample or crop into the destination.
    sealed class TMP2NET : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly int _w, _h;
        private readonly SignLayer _dest;
        private bool _disposed;

        // --- NEW: per-scanline circular shift (pixels). Positive shifts right, negative left. ---
        // Your report: "each line is offset 32 pixels to the left" => set +32 to rotate right and correct it.
        public int HorizontalShiftPixels { get; set; } = 34;

        // Behavior
        public bool AlphaOverlay { get; set; } = true;      // zero means transparent in your mixer
        public byte LumaThreshold { get; set; } = 0;        // 0..255, treat near-black as transparent when >0
        public byte KeyR { get; set; } = 0;                 // optional color key instead of luma
        public byte KeyG { get; set; } = 0;
        public byte KeyB { get; set; } = 0;
        public byte KeyTolerance { get; set; } = 6;         // +/- per channel
        public string ChannelOrder { get; set; } = "RGB";   // "RGB", "BGR", "GRB" etc

        // Resizing mode when payload size does not match sign dimensions
        public enum SizeMode { Strict, CropTopLeft, ScaleNearest }
        public SizeMode PayloadSizeMode { get; set; } = SizeMode.ScaleNearest;

        public event EventHandler FrameReceived;

        private int _pktCount;
        private const int _logWarmup = 5;

        public TMP2NET(SignLayer destinationLayer, int port = 65506)
        {
            if (destinationLayer == null) throw new ArgumentNullException("destinationLayer");
            _dest = destinationLayer;
            _w = _dest.pData.GetLength(0);
            _h = _dest.pData.GetLength(1);

            // Dual-stack UDP, receives IPv4 and IPv6, with retry on bind errors
            try
            {
                Socket s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                s.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                _udp = new UdpClient { Client = s };
                Debug.WriteLine("[TPM] Listening dual-stack on UDP " + port + " for " + _w + "x" + _h);
            }
            catch (SocketException sex)
            {
                Debug.WriteLine("[TPM] Bind failed, falling back to IPv4: " + sex.Message);
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                Debug.WriteLine("[TPM] Listening IPv4 on UDP " + port + " for " + _w + "x" + _h);
            }

            BeginReceive();
        }

        public void Dispose()
        {
            _disposed = true;
            try { _udp.Close(); } catch { }
        }

        private void BeginReceive()
        {
            if (_disposed) return;
            try { _udp.BeginReceive(ReceiveCallback, null); }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("[TPM] BeginReceive error, retrying: " + ex.Message);
                System.Threading.ThreadPool.QueueUserWorkItem(_ => { System.Threading.Thread.Sleep(50); BeginReceive(); });
            }
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            IPEndPoint ep = null;
            byte[] data = null;

            try { data = _udp.EndReceive(ar, ref ep); }
            catch { BeginReceive(); return; }

            try
            {
                if (data == null || data.Length < 4) { TracePacket("short packet " + data?.Length); return; }

                if (data[0] != 0x9C || data[1] != 0xDA)
                {
                    TracePacket("bad magic " + ToHex2(data[0]) + " " + ToHex2(data[1]));
                    return;
                }

                int lenBE = ((data[2] & 0xFF) << 8) | (data[3] & 0xFF);
                int headerGuess = Math.Max(4, data.Length - lenBE); // typical 4 or 6
                if (headerGuess != 4 && headerGuess != 6)
                {
                    headerGuess = data.Length >= 6 ? 6 : 4;
                }
                int start = headerGuess;
                int avail = Math.Max(0, data.Length - start);

                int expected = _w * _h * 3;
                if (_pktCount < _logWarmup)
                {
                    Debug.WriteLine("[TPM] src=" + (ep != null ? ep.ToString() : "unknown")
                        + " bytes=" + data.Length + " hdr=" + headerGuess + " lenBE=" + lenBE
                        + " avail=" + avail + " expected=" + expected);
                }

                if (avail <= 0)
                {
                    TracePacket("no payload");
                    return;
                }

                // Decide how to map the payload into our 2D panel
                if (avail == expected)
                {
                    // Fast path 1: exact match
                    if (AlphaOverlay)
                        CopyWithAlpha(data, start);
                    else
                        CopyOpaque(data, start);
                }
                else
                {
                    // Payload size differs
                    if (PayloadSizeMode == SizeMode.Strict)
                    {
                        TracePacket("size mismatch, strict mode, dropping");
                        return;
                    }

                    int srcPixels = avail / 3;
                    if (srcPixels <= 0)
                    {
                        TracePacket("not enough bytes for even 1 RGB pixel");
                        return;
                    }

                    // Heuristics to guess source width, height
                    int sw = _w, sh = srcPixels / Math.Max(1, sw);
                    if (sw * sh != srcPixels)
                    {
                        sh = _h; sw = srcPixels / Math.Max(1, sh);
                        if (sw * sh != srcPixels)
                        {
                            sw = (int)Math.Round(Math.Sqrt(srcPixels));
                            if (sw <= 0) sw = _w;
                            sh = Math.Max(1, srcPixels / sw);
                        }
                    }

                    if (PayloadSizeMode == SizeMode.CropTopLeft)
                    {
                        int maxPix = Math.Min(srcPixels, _w * _h);
                        if (AlphaOverlay)
                            CopyCropWithAlpha(data, start, maxPix);
                        else
                            CopyCropOpaque(data, start, maxPix);
                    }
                    else // ScaleNearest
                    {
                        if (AlphaOverlay)
                            ScaleNearestWithAlpha(data, start, sw, sh);
                        else
                            ScaleNearestOpaque(data, start, sw, sh);
                    }
                }

                _dest.lastPacket = DateTime.Now;
                var h = FrameReceived; if (h != null) h(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[TPM] Parse error: " + ex.Message);
            }
            finally
            {
                _pktCount++;
                BeginReceive();
            }
        }

        // -------- Copy paths --------

        private void CopyOpaque(byte[] data, int start)
        {
            // Linear copy into panel, row-major
            int idx = start;
            for (int y = 0; y < _h; y++)
            {
                for (int x = 0; x < _w; x++)
                {
                    WriteRGB(x, y, data[idx + 0], data[idx + 1], data[idx + 2]);
                    idx += 3;
                }
            }
        }

        private void CopyWithAlpha(byte[] data, int start)
        {
            int idx = start;
            if (LumaThreshold > 0)
            {
                byte thr = LumaThreshold;
                for (int y = 0; y < _h; y++)
                {
                    for (int x = 0; x < _w; x++)
                    {
                        byte r = data[idx + 0], g = data[idx + 1], b = data[idx + 2];
                        idx += 3;
                        byte l = (byte)((r * 54 + g * 183 + b * 19) >> 8);
                        if (l <= thr) { Zero(x, y); } else { WriteRGB(x, y, r, g, b); }
                    }
                }
            }
            else
            {
                int tr = KeyR, tg = KeyG, tb = KeyB, tol = KeyTolerance;
                for (int y = 0; y < _h; y++)
                {
                    for (int x = 0; x < _w; x++)
                    {
                        byte r = data[idx + 0], g = data[idx + 1], b = data[idx + 2];
                        idx += 3;
                        bool key = Math.Abs(r - tr) <= tol && Math.Abs(g - tg) <= tol && Math.Abs(b - tb) <= tol;
                        if (key) { Zero(x, y); } else { WriteRGB(x, y, r, g, b); }
                    }
                }
            }
        }

        private void CopyCropOpaque(byte[] data, int start, int maxPixels)
        {
            int idx = start;
            int copied = 0;
            for (int y = 0; y < _h && copied < maxPixels; y++)
                for (int x = 0; x < _w && copied < maxPixels; x++, copied++)
                {
                    WriteRGB(x, y, data[idx + 0], data[idx + 1], data[idx + 2]);
                    idx += 3;
                }
        }

        private void CopyCropWithAlpha(byte[] data, int start, int maxPixels)
        {
            int idx = start;
            int copied = 0;
            if (LumaThreshold > 0)
            {
                byte thr = LumaThreshold;
                for (int y = 0; y < _h && copied < maxPixels; y++)
                    for (int x = 0; x < _w && copied < maxPixels; x++, copied++)
                    {
                        byte r = data[idx + 0], g = data[idx + 1], b = data[idx + 2];
                        idx += 3;
                        byte l = (byte)((r * 54 + g * 183 + b * 19) >> 8);
                        if (l <= thr) { Zero(x, y); } else { WriteRGB(x, y, r, g, b); }
                    }
            }
            else
            {
                int tr = KeyR, tg = KeyG, tb = KeyB, tol = KeyTolerance;
                for (int y = 0; y < _h && copied < maxPixels; y++)
                    for (int x = 0; x < _w && copied < maxPixels; x++, copied++)
                    {
                        byte r = data[idx + 0], g = data[idx + 1], b = data[idx + 2];
                        idx += 3;
                        bool key = Math.Abs(r - tr) <= tol && Math.Abs(g - tg) <= tol && Math.Abs(b - tb) <= tol;
                        if (key) { Zero(x, y); } else { WriteRGB(x, y, r, g, b); }
                    }
            }
        }

        private void ScaleNearestOpaque(byte[] data, int start, int srcW, int srcH)
        {
            int srcPix = srcW * srcH;
            if (srcPix <= 0) return;
            for (int y = 0; y < _h; y++)
            {
                int sy = y * srcH / _h;
                for (int x = 0; x < _w; x++)
                {
                    int sx = x * srcW / _w;
                    int sIdx = start + ((sy * srcW + sx) * 3);
                    WriteRGB(x, y, Safe(data, sIdx + 0), Safe(data, sIdx + 1), Safe(data, sIdx + 2));
                }
            }
        }

        private void ScaleNearestWithAlpha(byte[] data, int start, int srcW, int srcH)
        {
            if (LumaThreshold > 0)
            {
                byte thr = LumaThreshold;
                for (int y = 0; y < _h; y++)
                {
                    int sy = y * srcH / _h;
                    for (int x = 0; x < _w; x++)
                    {
                        int sx = x * srcW / _w;
                        int sIdx = start + ((sy * srcW + sx) * 3);
                        byte r = Safe(data, sIdx + 0), g = Safe(data, sIdx + 1), b = Safe(data, sIdx + 2);
                        byte l = (byte)((r * 54 + g * 183 + b * 19) >> 8);
                        if (l <= thr) { Zero(x, y); } else { WriteRGB(x, y, r, g, b); }
                    }
                }
            }
            else
            {
                int tr = KeyR, tg = KeyG, tb = KeyB, tol = KeyTolerance;
                for (int y = 0; y < _h; y++)
                {
                    int sy = y * srcH / _h;
                    for (int x = 0; x < _w; x++)
                    {
                        int sx = x * srcW / _w;
                        int sIdx = start + ((sy * srcW + sx) * 3);
                        byte r = Safe(data, sIdx + 0), g = Safe(data, sIdx + 1), b = Safe(data, sIdx + 2);
                        bool key = Math.Abs(r - tr) <= tol && Math.Abs(g - tg) <= tol && Math.Abs(b - tb) <= tol;
                        if (key) { Zero(x, y); } else { WriteRGB(x, y, r, g, b); }
                    }
                }
            }
        }

        // -------- Utilities --------

        private int ShiftX(int x)
        {
            int s = HorizontalShiftPixels;
            if (s == 0) return x;
            // proper modulo for negatives too
            int dx = x + s;
            if (dx >= _w) dx %= _w;
            else if (dx < 0) dx = (_w + (dx % _w)) % _w;
            return dx;
        }

        private void WriteRGB(int x, int y, byte rIn, byte gIn, byte bIn)
        {
            byte r = rIn, g = gIn, b = bIn;

            // Reorder if needed
            switch (ChannelOrder)
            {
                case "BGR": { r = bIn; g = gIn; b = rIn; break; }
                case "GRB": { r = gIn; g = rIn; b = bIn; break; }
                case "RGB": default: break;
            }

            int dx = ShiftX(x);
            _dest.pData[dx, y, 0] = r;
            _dest.pData[dx, y, 1] = g;
            _dest.pData[dx, y, 2] = b;
        }

        private static byte Safe(byte[] a, int i) { return (i >= 0 && i < a.Length) ? a[i] : (byte)0; }

        private void Zero(int x, int y)
        {
            int dx = ShiftX(x);
            _dest.pData[dx, y, 0] = 0;
            _dest.pData[dx, y, 1] = 0;
            _dest.pData[dx, y, 2] = 0;
        }

        private void TracePacket(string reason)
        {
            if (_pktCount < _logWarmup)
                Debug.WriteLine("[TPM] drop: " + reason);
        }

        private static string ToHex2(byte b) { return "0x" + b.ToString("X2"); }
    }
}
