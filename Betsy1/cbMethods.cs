// VideoSampleCallback.cs
// .NET Framework 4.8, x86
// Works with FileToCallbackPlayer that sets BufferCB mode

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DirectShowLib;

namespace Betsy1
{
    public sealed class cbMethods : ISampleGrabberCB
    {
        // Set these from your start function
        public Form1 myOwner;
        public int vw;
        public int vh;
        public bool TopDown;
        public Guid Subtype = MediaSubType.RGB32; // set to player.ConnectedSubtype
        public IMediaSeeking seeker;              // optional
        public long seekTime = 0;                 // optional
        public SignLayer pLayer;                  // destination LED layer [162 x 108 x 3]

        // Internal
        private const int DW = 162;
        private const int DH = 108;
        private byte[] _src;
        private int[] _mapX;
        private int[] _mapY;
        private int _stride;   // bytes per source scanline
        private int _bpp;      // bytes per pixel in source
        private int _frame;
        private int _graceMs = 1000;
        private int _t0;

        public void SetGracePeriod(int ms) { _graceMs = ms < 0 ? 0 : ms; }

        // We use BufferCB for reliability. SampleCB is ignored.
        public int SampleCB(double sampleTime, IMediaSample pSample) { return 0; }

        public int BufferCB(double sampleTime, IntPtr pBuffer, int bufferLen)
        {
            try
            {
                if (vw <= 0 || vh <= 0 || pLayer == null || pBuffer == IntPtr.Zero || bufferLen <= 0)
                    return 0;

                if (_t0 == 0) _t0 = Environment.TickCount;

                EnsureMaps(bufferLen);

                if (_src == null || _src.Length < bufferLen)
                    _src = new byte[bufferLen];

                Marshal.Copy(pBuffer, _src, 0, bufferLen);

                int score;
                if (Subtype == MediaSubType.RGB32)
                    score = ScaleFromRGB32();
                else if (Subtype == MediaSubType.YUY2)
                    score = ScaleFromYUY2();
                else if (Subtype == MediaSubType.RGB24)
                    score = ScaleFromRGB24();
                else
                    score = ScaleFromRGB32(); // try anyway

                if ((_frame++ & 31) == 0)
                    Debug.WriteLine("cb frame=" + _frame + " subtype=" + Subtype + " stride=" + _stride + " score=" + score);

                if (score > 0 || Environment.TickCount - _t0 < _graceMs)
                {
                    pLayer.lastPacket = DateTime.Now;
                    myOwner?.drawNext(); // marshals to UI in your Form1
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("BufferCB error: " + ex.Message);
            }
            return 0;
        }

        private void EnsureMaps(int bufferLen)
        {
            _bpp = BytesPerPixel(Subtype);

            // infer stride from buffer length, accept padding
            int minStride = vw * _bpp;
            _stride = minStride;
            if (vh > 0)
            {
                int guess = bufferLen / vh;
                if (guess >= minStride && guess <= minStride + 512) _stride = guess;
            }

            if (_mapX != null && _mapY != null && _mapX.Length == DW) return;

            _mapX = new int[DW];
            _mapY = new int[DH];

            double sx = (double)vw / DW;
            double sy = (double)vh / DH;

            // mapX stores byte offset within a row. For YUY2 we still map per pixel, conversion will align to pairs.
            for (int x = 0; x < DW; x++)
            {
                int xs = (int)(x * sx);
                if (xs >= vw) xs = vw - 1;
                _mapX[x] = xs * _bpp;
            }

            for (int y = 0; y < DH; y++)
            {
                int ys = (int)(y * sy);
                if (ys >= vh) ys = vh - 1;

                int srcRow = TopDown ? ys : (vh - 1 - ys);
                _mapY[y] = srcRow * _stride;
            }
        }

        private static int BytesPerPixel(Guid subtype)
        {
            if (subtype == MediaSubType.RGB32) return 4;
            if (subtype == MediaSubType.RGB24) return 3;
            if (subtype == MediaSubType.YUY2) return 2; // packed 4 bytes per 2 pixels
            return 4;
        }

        // returns a small non black score
        private int ScaleFromRGB32()
        {
            int score = 0;
            for (int y = 0; y < DH; y++)
            {
                int rowOffset = _mapY[y];
                int destY = DH - 1 - y;

                for (int x = 0; x < DW; x++)
                {
                    int si = rowOffset + _mapX[x];
                    byte b = _src[si + 0];
                    byte g = _src[si + 1];
                    byte r = _src[si + 2];

                    pLayer.pData[x, destY, 0] = r;
                    pLayer.pData[x, destY, 1] = g;
                    pLayer.pData[x, destY, 2] = b;

                    if (((x ^ y) & 7) == 0 && (r | g | b) != 0) score++;
                }
            }
            return score;
        }

        private int ScaleFromRGB24()
        {
            int score = 0;
            for (int y = 0; y < DH; y++)
            {
                int rowOffset = _mapY[y];
                int destY = DH - 1 - y;

                for (int x = 0; x < DW; x++)
                {
                    int si = rowOffset + _mapX[x];
                    byte b = _src[si + 0];
                    byte g = _src[si + 1];
                    byte r = _src[si + 2];

                    pLayer.pData[x, destY, 0] = r;
                    pLayer.pData[x, destY, 1] = g;
                    pLayer.pData[x, destY, 2] = b;

                    if (((x ^ y) & 7) == 0 && (r | g | b) != 0) score++;
                }
            }
            return score;
        }

        // YUY2 packed: Y0 U Y1 V per 2 pixels
        private int ScaleFromYUY2()
        {
            int score = 0;
            for (int y = 0; y < DH; y++)
            {
                int rowOffset = _mapY[y];
                int destY = DH - 1 - y;

                for (int x = 0; x < DW; x++)
                {
                    // Map to source pixel, then align to even pair for YUY2
                    int sxBytes = _mapX[x];               // bytes within row as if 2 bytes per pixel
                    int pairBytes = (sxBytes & ~3);       // each pair is 4 bytes
                    int si = rowOffset + pairBytes;

                    byte y0 = _src[si + 0];
                    byte u = _src[si + 1];
                    byte y1 = _src[si + 2];
                    byte v = _src[si + 3];

                    int isOdd = (sxBytes & 2);           // bit 1 indicates second pixel in the pair
                    int Y = (isOdd == 0) ? y0 : y1;

                    int r, g, b;
                    YuvToRgb601(Y, u, v, out r, out g, out b);

                    pLayer.pData[x, destY, 0] = (byte)r;
                    pLayer.pData[x, destY, 1] = (byte)g;
                    pLayer.pData[x, destY, 2] = (byte)b;

                    if (((x ^ y) & 7) == 0 && (r | g | b) != 0) score++;
                }
            }
            return score;
        }

        private static void YuvToRgb601(int Y, int U, int V, out int r, out int g, out int b)
        {
            int c = Y - 16;
            int d = U - 128;
            int e = V - 128;

            r = (298 * c + 409 * e + 128) >> 8;
            g = (298 * c - 100 * d - 208 * e + 128) >> 8;
            b = (298 * c + 516 * d + 128) >> 8;

            if (r < 0) r = 0; else if (r > 255) r = 255;
            if (g < 0) g = 0; else if (g > 255) g = 255;
            if (b < 0) b = 0; else if (b > 255) b = 255;
        }
    }
}
