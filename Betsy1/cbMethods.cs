using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DirectShowLib;

namespace Betsy1
{
    /// <summary>
    /// Ultra‑defensive ISampleGrabberCB: no GDI+, no Bitmaps, just raw math.
    /// Copies each RGB24 frame into a managed byte buffer then performs a
    /// manual nearest‑neighbour scale down to 162×108 directly into the
    /// LED‑panel layer.  All computation happens inside managed memory so
    /// AccessViolation cannot be triggered by GDI+ native code.
    /// </summary>
    public sealed class cbMethods : ISampleGrabberCB
    {
        public Form1 myOwner;
        public int vw;          // source frame width
        public int vh;          // source frame height
        public IMediaSeeking seeker;
        public long seekTime;
        public SignLayer pLayer;

        byte[] srcBuf;                    // RGB24 from DirectShow

        // Mapping tables to speed up scaling
        int[] mapX;                       // dest x -> src byte offset
        int[] mapY;                       // dest y -> src row offset

        public int BufferCB(double SampleTime, IntPtr pBuffer, int BufferLen) => 0; // unused

        public int SampleCB(double sampleTime, IMediaSample pSample)
        {
            try
            {
                if (vw <= 0 || vh <= 0 || pSample == null) return 0;

                int len = pSample.GetActualDataLength();
                if (len <= 0) return 0;

                if (FrameIsLate(pSample)) return 0;

                // Setup buffers on first call or resolution change
                if (srcBuf == null || srcBuf.Length < len)
                {
                    srcBuf = new byte[len];
                    BuildMappingTables();
                }

                // Copy unmanaged sample to managed buffer
                if (pSample.GetPointer(out var srcPtr) != 0) return 0;
                Marshal.Copy(srcPtr, srcBuf, 0, len);

                // Scale down to 162×108
                DownscaleToPanel();

                pLayer.lastPacket = DateTime.Now;
                myOwner.drawNext();
            }
            catch (AccessViolationException ave)
            {
                Debug.WriteLine($"SampleCB AV: {ave.Message}\n{ave.StackTrace}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SampleCB error: {ex}");
            }
            return 0;
        }

        bool FrameIsLate(IMediaSample s)
        {
            s.GetTime(out long start, out long _);
            start += seekTime;
            return seeker != null && seeker.GetCurrentPosition(out long now) == 0 && now > start;
        }

        void BuildMappingTables()
        {
            double sx = (double)vw / 162.0;
            double sy = (double)vh / 108.0;

            mapX = new int[162];
            mapY = new int[108];

            for (int x = 0; x < 162; x++)
            {
                int xs = (int)(x * sx);
                mapX[x] = xs * 3;                 // byte offset inside a row
            }
            for (int y = 0; y < 108; y++)
            {
                int ys = (int)(y * sy);
                mapY[y] = ys * vw * 3;            // byte offset of the row start
            }
        }

        void DownscaleToPanel()
        {
            for (int y = 0; y < 108; y++)
            {
                int rowOffset = mapY[y];
                for (int x = 0; x < 162; x++)
                {
                    int srcIndex = rowOffset + mapX[x];
                    // src is BGR24, order into RGB for LED panel
                    pLayer.pData[x, 107 - y, 0] = srcBuf[srcIndex + 2]; // R
                    pLayer.pData[x, 107 - y, 1] = srcBuf[srcIndex + 1]; // G
                    pLayer.pData[x, 107 - y, 2] = srcBuf[srcIndex + 0]; // B
                }
            }
        }
    }
}
