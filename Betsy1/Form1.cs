using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Timers;
using System.Net.NetworkInformation;
using DirectShowLib;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Diagnostics;

namespace Betsy1
{
    delegate void DrawToSign();

    public partial class Form1 : Form
    {
        delegate void SetPicCallback(Bitmap b);
        delegate void SetDebugLabel(string t);
        IMediaSeeking seeker;

        Byte[,,] pixLast;

        // Objects to handle sign comms
        public HacklabBetsy Betsy;
        TMP2NET TPM;
        BMIX bMix;

        SignLayer[] pLayers; // The layers of my sign.

        Bitmap HLLogo;
        Graphics HLTime;
        Font HLFont;
        FontFamily HLFamily;
        Brush HLBrush;
        Brush HLClearBrush;
        StringFormat HLFormat;

        // --- NEW: per-layer double buffers + locks (replace old LayerPaint usage) ---
        Bitmap[] _layerFront;
        Bitmap[] _layerBack;
        object[] _layerSwapLock;

        String ipV6BindAddress = "fe80::1246:d064:bb87:b853%40";

        struct pixFrame // One frame of image data.
        {
            public Byte[,,] pixData;
        }

        pixFrame[] pixBuffer; // The entire buffer
        int bufPos = -1; // The last filled position in the buffer.
        int bufSize;
        bool SplitScanMode = true;

        Byte bSet = 0;

        long tStart = (DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond);

        System.Timers.Timer aTimer;

        cbMethods sampBuf;
        int SplitLinesCount = 1;

        PictureBox[] pBoxes;
        Thread RenderThread;

        public Form1()
        {
            InitializeComponent();
        }

        private void StartDisplay()
        {
            Betsy = new HacklabBetsy(ipV6BindAddress);
            Betsy.resetSign();

            while (!Betsy.SignOnline)
            {
                Console.WriteLine("Waiting for Betsy reset");
                Thread.Sleep(500);
            }

            Console.WriteLine("Betsy Online!");
        }

        private void InitVideoMixer()
        {
            // Define the layers.
            // 0 - Local plasma effect with Hacklab Logo
            // 1 - Local video or camera
            // 2 - TPM2.NET data
            // 3-6 - BMIX layers

            pLayers = new SignLayer[7];
            pBoxes = new PictureBox[pLayers.Length];

            // NEW: allocate per-layer buffers/locks
            _layerFront = new Bitmap[pLayers.Length];
            _layerBack = new Bitmap[pLayers.Length];
            _layerSwapLock = new object[pLayers.Length];

            for (int i = 0; i < pLayers.Length; i++)
            {
                pLayers[i] = new SignLayer(Betsy.LWIDTH, Betsy.LHEIGHT);

                pBoxes[i] = new PictureBox();
                pBoxes[i].Name = "pBoxLayer" + i;
                pBoxes[i].Location = new Point(250 + i * 180, 100);
                pBoxes[i].Size = new Size(Betsy.LWIDTH, Betsy.LHEIGHT);
                pBoxes[i].Visible = true;
                pBoxes[i].BackColor = Color.Black;
                pBoxes[i].SizeMode = PictureBoxSizeMode.Zoom;
                this.Controls.Add(pBoxes[i]);

                _layerSwapLock[i] = new object();
                // Pre-allocate double buffers for each layer
                _layerFront[i] = new Bitmap(Betsy.LWIDTH, Betsy.LHEIGHT, PixelFormat.Format24bppRgb);
                _layerBack[i] = new Bitmap(Betsy.LWIDTH, Betsy.LHEIGHT, PixelFormat.Format24bppRgb);
            }

            bMix = new BMIX(Betsy.LWIDTH, Betsy.LHEIGHT);
            bMix.FrameReceived += BMix_FrameReceived;

            // BMIX mapping (unchanged)
            bMix.addLayer(pLayers[3], 2329);            // Background
            pLayers[3].alpha = true;
            bMix.addLayer(pLayers[4], 2324);            // Foreground
            bMix.addLayer(pLayers[5], 2330);
            pLayers[5].alpha = true;                    // Chroma key plane
            bMix.addLayer(pLayers[6], 2331);            // Top
            pLayers[6].alpha = true;

            // TPM overlays on top layer
            TPM = new TMP2NET(pLayers[6]);
            TPM.AlphaOverlay = true;
            TPM.LumaThreshold = 4;
            TPM.FrameReceived += BMix_FrameReceived;

            aTimer = new System.Timers.Timer();
            aTimer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
            aTimer.Interval = 1;

            pixLast = new Byte[Betsy.LWIDTH, Betsy.LHEIGHT, 3];

            // Split scan buffer
            bufSize = 108;
            pixBuffer = new pixFrame[bufSize];
            for (int i = 0; i < bufSize; i++)
            {
                pixBuffer[i].pixData = new Byte[Betsy.LWIDTH, Betsy.LHEIGHT, 3];
            }

            // Default visuals
            HLLogo = new Bitmap(Directory.GetCurrentDirectory() + @"\HLLogo.png");
            HLTime = Graphics.FromImage(HLLogo);
            HLTime.CompositingQuality = CompositingQuality.HighQuality;
            HLTime.InterpolationMode = InterpolationMode.Bicubic;
            HLTime.PixelOffsetMode = PixelOffsetMode.HighQuality;
            HLTime.SmoothingMode = SmoothingMode.HighQuality;
            HLTime.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            HLFamily = new FontFamily("Tahoma");
            HLFont = new Font(HLFamily, 12, FontStyle.Bold);
            HLBrush = new SolidBrush(Color.White);
            HLClearBrush = new SolidBrush(Color.FromArgb(0, 0, 0, 0));
        }

        private void StartVideoMixer()
        {
            aTimer.Start();
        }

        private void RenderRun()
        {
            while (true)
            {
                mixLayers();
                Thread.Sleep(10);
            }
        }

        private void BMix_FrameReceived(object sender, EventArgs e)
        {
            mixLayers();
        }

        private void TPM_FrameReceived(object sender, EventArgs e)
        {
            pLayers[2].lastPacket = DateTime.Now;
            mixLayers();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            //MessageBox.Show("Starting");
        }

        private void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            aTimer.Enabled = false;

            bool hasData = false;

            // If no packets have been received from layers 1 through 3, draw the Hacklab layer.
            for (int i = 1; i < pLayers.Length; i++)
            {
                if (DateTime.Now.Subtract(pLayers[i].lastPacket).TotalSeconds <= 5) hasData = true;
            }

            if (!hasData)
            {
                // No layer data has been received from anyone else.  So generate Layer 0.
                plasmaEffect();
                mixLayers();
            }
            pLayers[0].Hidden = hasData; // If any of the other layers contain data, hide the plasma layer.

            aTimer.Enabled = true;
        }

        void SetLabel(string t)
        {
            if (label4.InvokeRequired)
            {
                try
                {
                    SetDebugLabel d = new SetDebugLabel(SetLabel);
                    this.Invoke(d, new object[] { t });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.ToString());
                }
            }
            else
            {
                label4.Text = t;
            }
        }

        void mixLayers()
        {
            Array.Clear(Betsy.pixData, 0, Betsy.LWIDTH * Betsy.LHEIGHT * 3);

            // Mix the layers together into the output array.
            for (int i = 0; i < pLayers.Length; i++)
            {
                bool isAlpha = pLayers[i].alpha;

                // Only render this layer if packets from it have been received recently.
                if (DateTime.Now.Subtract(pLayers[i].lastPacket).TotalSeconds > 5) continue;
                if (pLayers[i].Hidden) continue;

                if (isAlpha)
                {
                    for (int y = 0; y < Betsy.LHEIGHT; y++)
                    {
                        for (int x = 0; x < Betsy.LWIDTH; x++)
                        {
                            if (pLayers[i].pData[x, y, 0] > 0 || pLayers[i].pData[x, y, 1] > 0 || pLayers[i].pData[x, y, 2] > 0)
                            {
                                Betsy.pixData[x, y, 0] = pLayers[i].pData[x, y, 0];
                                Betsy.pixData[x, y, 1] = pLayers[i].pData[x, y, 1];
                                Betsy.pixData[x, y, 2] = pLayers[i].pData[x, y, 2];
                            }
                        }
                    }
                }
                else
                {
                    // Fast copy
                    Buffer.BlockCopy(pLayers[i].pData, 0, Betsy.pixData, 0, Betsy.LWIDTH * Betsy.LHEIGHT * 3);
                }

                // Show the individual layer in its PictureBox
                showLayerOnPBox(i);
            }

            draw();
        }

        // NEW: safe, double-buffered, per-layer painting
        public void showLayerOnPBox(int layerID)
        {
            // Defensive: skip if buffers not ready
            if (_layerBack == null || _layerFront == null) return;
            if (_layerBack.Length <= layerID || _layerFront.Length <= layerID) return;

            var back = _layerBack[layerID];
            var front = _layerFront[layerID];
            if (back == null || front == null) return;

            lock (_layerSwapLock[layerID])
            {
                BitmapData bData = null;
                try
                {
                    bData = back.LockBits(new Rectangle(0, 0, Betsy.LWIDTH, Betsy.LHEIGHT),
                                          ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

                    int stride = bData.Stride;
                    int width = Betsy.LWIDTH;
                    int height = Betsy.LHEIGHT;

                    int size = stride * height;
                    byte[] data = new byte[size];

                    // Fill 'data' from pLayers[layerID].pData
                    int i;
                    for (int y = 0; y < height; ++y)
                    {
                        int rowBase = y * stride;
                        for (int x = 0; x < width; ++x)
                        {
                            i = rowBase + (x * 3);
                            // pData is R,G,B in [,,0..2]; bitmap wants B,G,R
                            data[i + 2] = pLayers[layerID].pData[x, y, 0]; // R
                            data[i + 1] = pLayers[layerID].pData[x, y, 1]; // G
                            data[i + 0] = pLayers[layerID].pData[x, y, 2]; // B
                        }
                    }

                    Marshal.Copy(data, 0, bData.Scan0, size);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("showLayerOnPBox lock/copy failed: " + ex.Message);
                }
                finally
                {
                    if (bData != null) back.UnlockBits(bData);
                }

                // Swap back->front
                var tmp = _layerFront[layerID];
                _layerFront[layerID] = _layerBack[layerID];
                _layerBack[layerID] = tmp;

                // UI must assign on UI thread
                if (pBoxes[layerID].InvokeRequired)
                {
                    pBoxes[layerID].BeginInvoke(new Action(() =>
                    {
                        pBoxes[layerID].Image = _layerFront[layerID];
                        pBoxes[layerID].Invalidate();
                    }));
                }
                else
                {
                    pBoxes[layerID].Image = _layerFront[layerID];
                    pBoxes[layerID].Invalidate();
                }
            }
        }

        public void draw()
        {
            if (chkSplitScan.Checked)
            {
                // Copy into the buffer.
                bufPos++;
                if (bufPos >= bufSize) bufPos = 0;
                Buffer.BlockCopy(Betsy.pixData, 0, pixBuffer[bufPos].pixData, 0, Betsy.LWIDTH * Betsy.LHEIGHT * 3);

                // How much has the frame changed since last time?
                if (chkIntelli.Checked)
                {
                    int thresh = 100;
                    int chCount = 0;

                    for (int y = 0; y < Betsy.LHEIGHT; y++)
                    {
                        for (int x = 0; x < Betsy.LWIDTH; x++)
                        {
                            if (Math.Abs(
                                   (pixLast[x, y, 0] + pixLast[x, y, 1] + pixLast[x, y, 2]) -
                                   (Betsy.pixData[x, y, 0] + Betsy.pixData[x, y, 1] + Betsy.pixData[x, y, 2])) > thresh)
                            {
                                chCount++;
                            }
                        }
                    }

                    if (chCount > 3000)
                    {
                        for (int i = 0; i < bufSize; i++)
                        {
                            Buffer.BlockCopy(Betsy.pixData, 0, pixBuffer[i].pixData, 0, Betsy.LWIDTH * Betsy.LHEIGHT * 3);
                        }
                    }

                    Buffer.BlockCopy(Betsy.pixData, 0, pixLast, 0, Betsy.LWIDTH * Betsy.LHEIGHT * 3);
                }

                SplitScanPaint();
            }

            SetLabel("Draw " + DateTime.Now.ToString());
            Betsy.DrawAll();
        }

        private void SplitScanPaint()
        {
            // Paint into pixData from the Split Scan buffer.
            for (var y = 0; y < Betsy.LHEIGHT; y++)
            {
                var copyFrom = bufPos - (y / SplitLinesCount);
                if (copyFrom < 0) copyFrom += bufSize;

                for (var x = 0; x < Betsy.LWIDTH; x++)
                {
                    Betsy.pixData[x, y, 0] = pixBuffer[copyFrom].pixData[x, y, 0];
                    Betsy.pixData[x, y, 1] = pixBuffer[copyFrom].pixData[x, y, 1];
                    Betsy.pixData[x, y, 2] = pixBuffer[copyFrom].pixData[x, y, 2];
                }
            }
        }

        void plasmaEffect()
        {
            int w = Betsy.LWIDTH;
            int h = Betsy.LHEIGHT;

            double yv;
            double xv;
            long end_ts = 90;

            double o1, o2, o3;
            double timer = ((double)((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - tStart)) / 1000;

            o1 = (double)(4 - (end_ts - timer)) / 4;
            o2 = (double)(end_ts - timer) / 4;
            o3 = (double)timer;

            // Buffer we use while painting.
            Byte[,,] pInt = new Byte[Betsy.LWIDTH, Betsy.LHEIGHT, 3];

            for (int y = 0; y < h; y++)
            {
                yv = (double)y / (double)h - 0.5;
                for (int x = 0; x < w; x++)
                {
                    xv = (float)x / (float)w - 0.5;
                    pInt[x, y, 0] = (Byte)(calc_v(xv, yv, o1 / 3) * 0.5);
                    pInt[x, y, 1] = (Byte)(calc_v(xv, yv, o2 / 3) * 0.5);
                    pInt[x, y, 2] = (Byte)(calc_v(xv, yv, o3 / 3) * 0.5);
                }
            }

            // Time stamp in logo
            HLTime.CompositingMode = CompositingMode.SourceCopy;
            HLTime.FillRectangle(HLClearBrush, 80, 0, 90, 20);
            HLTime.CompositingMode = CompositingMode.SourceOver;

            using (Pen tPen = new Pen(Color.Black, 1.51f))
            using (GraphicsPath tPath = new GraphicsPath())
            {
                tPath.AddString(DateTime.Now.ToString("HH:mm:ss"), HLFamily, (int)FontStyle.Bold, 16, new Point(80, 0), StringFormat.GenericDefault);
                HLTime.FillPath(HLBrush, tPath);
                HLTime.DrawPath(tPen, tPath);
            }

            // Blend HLLogo over plasma into pInt
            BitmapData bData = null;
            try
            {
                bData = HLLogo.LockBits(new Rectangle(0, 0, 162, 108), ImageLockMode.ReadOnly, HLLogo.PixelFormat);

                int size = bData.Stride * bData.Height;
                byte[] data = new byte[size];

                Marshal.Copy(bData.Scan0, data, 0, size);

                int i;
                for (int y = 0; y < bData.Height; ++y)
                {
                    for (int x = 0; x < bData.Width; ++x)
                    {
                        i = (y * bData.Stride) + (x * 4);

                        float alphaScale = (float)data[i + 3] / 255;

                        if (data[i + 3] > 0 && !(x >= 80 && y <= 20))
                        {
                            yv = (double)y / (double)h - 0.5;
                            xv = (float)x / (float)w - 0.5;
                            byte bb = (byte)(calc_v(xv, yv, o3 / 4) * 0.7);
                            data[i] = (byte)Math.Max(data[i] - bb, 0);
                            data[i + 1] = (byte)Math.Max(data[i + 1] - bb, 0);
                            data[i + 2] = (byte)Math.Max(data[i + 2] - bb, 0);
                        }

                        pInt[x, y, 0] = (byte)(((float)pInt[x, y, 0] * (1 - alphaScale)) + ((float)data[i + 2] * alphaScale));
                        pInt[x, y, 1] = (byte)(((float)pInt[x, y, 1] * (1 - alphaScale)) + ((float)data[i + 1] * alphaScale));
                        pInt[x, y, 2] = (byte)(((float)pInt[x, y, 2] * (1 - alphaScale)) + ((float)data[i] * alphaScale));
                    }
                }
            }
            finally
            {
                if (bData != null) HLLogo.UnlockBits(bData);
            }

            pLayers[0].lastPacket = DateTime.Now; // Indicate that data is available.
            Buffer.BlockCopy(pInt, 0, pLayers[0].pData, 0, Betsy.LWIDTH * Betsy.LHEIGHT * 3);
        }

        // For plasma effect
        int calc_v(double xv, double yv, double offset)
        {
            double o_div_3 = offset / 3;
            double cy = yv + 0.5 * Math.Cos(o_div_3);
            double xv_sin_half = xv * Math.Sin(offset / 2);
            double yv_cos_od3 = yv * Math.Cos(o_div_3);
            double cx = xv + 0.5 * Math.Sin(offset / 5);
            double v2 = Math.Sin(10 * (xv_sin_half + yv_cos_od3) + offset);
            double magic = Hypot(cx, cy) * 10;
            double v1 = Math.Sin(xv * 10 + offset);
            double v3 = Math.Sin(magic + offset);
            double v = (v1 + v2 + v3) * Math.PI / 2;
            return (int)(127.5 * Math.Sin(v) + 127.5);
        }
        double Hypot(double x, double y) { return Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2)); }

        private void button3_Click(object sender, EventArgs e)
        {
            aTimer.Enabled = true;
        }

        private void button2_Click(object sender, EventArgs e)
        {
            Betsy.resetSign();
        }

        private void trkIntensity_Scroll(object sender, EventArgs e)
        {
            Betsy.gainScale = (double)trkIntensity.Value / 100;
        }

        private void button4_Click(object sender, EventArgs e)
        {
            StartFilePlaybackToLayer1();
        }

        private VideoCallbackPlayer.FileToCallbackPlayer _player;
        private Betsy1.cbMethods _cb;
        private void StartFilePlaybackToLayer1()
        {
            if (pLayers == null || pLayers.Length == 0) { InitVideoMixer(); StartVideoMixer(); }

            OpenFileDialog ofd = new OpenFileDialog();
            ofd.InitialDirectory = "C:\\";
            ofd.Filter = "Media|*.mkv;*.mp4;*.avi;*.mov;*.wmv;*.mpg;*.mpeg|All files|*.*";
            if (ofd.ShowDialog(this) != DialogResult.OK) { ofd.Dispose(); return; }
            string path = ofd.FileName;
            ofd.Dispose();

            if (_player != null) { _player.Dispose(); _player = null; }
            _cb = null;

            _cb = new cbMethods();
            _cb.myOwner = this;
            _cb.pLayer = pLayers[1];

            int w, h;
            _player = VideoCallbackPlayer.FileToCallbackPlayer.Play(path, _cb, out w, out h);

            // wire negotiated details
            _cb.vw = _player.Width;
            _cb.vh = _player.Height;
            _cb.TopDown = !_player.TopDown;
            _cb.seeker = _player.Seeker;
            _cb.Subtype = _player.ConnectedSubtype;

            seeker = _player.Seeker;
            sampBuf = _cb;

            _player.SetVolumeDb(0);
        }

        public IBaseFilter FindCaptureDevice()
        {
            Console.WriteLine("Start the Sub FindCaptureDevice");
            int hr = 0;
            IEnumMoniker classEnum = null;
            IMoniker[] moniker = new IMoniker[1];
            object source = null;
            ICreateDevEnum devEnum = (ICreateDevEnum)new CreateDevEnum();
            hr = devEnum.CreateClassEnumerator(FilterCategory.VideoInputDevice, out classEnum, 0);
            Console.WriteLine("Create an enumerator for the video capture devices : " + DsError.GetErrorText(hr));
            DsError.ThrowExceptionForHR(hr);
            if (classEnum == null)
            {
                throw new ApplicationException("No video capture device was detected.\r\n\r\nThis sample requires a video capture device, such as a USB WebCam,\r\nto be installed and working properly.  The sample will now close.");
            }
            if (classEnum.Next(moniker.Length, moniker, IntPtr.Zero) == 0)
            {
                Guid iid = typeof(IBaseFilter).GUID;
                moniker[0].BindToObject(null, null, iid, out source);
            }
            else
            {
                throw new ApplicationException("Unable to access video capture device!");
            }

            return (IBaseFilter)source;
        }

        public void drawNext()
        {
            DrawToSign d = new DrawToSign(mixLayers);
            this.Invoke(d);
        }

        private void videoTrack_MouseUp(object sender, MouseEventArgs e)
        {
            if (seeker != null)
            {
                long pDur;
                DsLong pPos;
                seeker.GetDuration(out pDur);
                pPos = (long)((double)pDur * ((double)videoTrack.Value / 10000));
                seeker.SetPositions(pPos, AMSeekingSeekingFlags.SeekToKeyFrame | AMSeekingSeekingFlags.AbsolutePositioning, 0, AMSeekingSeekingFlags.NoPositioning);
                sampBuf.seekTime = pPos;
            }
        }

        private void trackLines_Scroll(object sender, EventArgs e)
        {
            SplitLinesCount = trackLines.Value;
        }

        // Form1 fields (add if you don't have them yet)
        private VideoCallbackPlayer.WebcamToCallbackPlayer _webcam;
        private Betsy1.cbMethods _camCb;

        private void StartWebcamToLayer1()
        {
            StartWebcamToLayer1(false); // no audio loopback by default
        }

        private void StartWebcamToLayer1(bool previewAudio)
        {
            if (pLayers == null || pLayers.Length == 0)
            {
                InitVideoMixer();
                StartVideoMixer();
            }

            if (_webcam != null) { _webcam.Dispose(); _webcam = null; }
            _camCb = null;

            _camCb = new Betsy1.cbMethods();
            _camCb.myOwner = this;
            _camCb.pLayer = pLayers[1];
            _camCb.SetGracePeriod(500);

            int w, h;
            _webcam = VideoCallbackPlayer.WebcamToCallbackPlayer.StartFirstCamera(_camCb, previewAudio, out w, out h);

            _camCb.vw = _webcam.Width;
            _camCb.vh = _webcam.Height;
            _camCb.TopDown = _webcam.TopDown;
            _camCb.Subtype = _webcam.ConnectedSubtype;

            seeker = null;
            sampBuf = _camCb;

            if (previewAudio) _webcam.SetVolumeDb(0);
        }

        private void button5_Click(object sender, EventArgs e)
        {
            StartWebcamToLayer1(previewAudio: false);
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            try { aTimer?.Stop(); aTimer?.Dispose(); } catch { }

            // Dispose double buffers
            if (_layerFront != null)
            {
                for (int i = 0; i < _layerFront.Length; i++)
                {
                    try { _layerFront[i]?.Dispose(); } catch { }
                    try { _layerBack[i]?.Dispose(); } catch { }
                    _layerFront[i] = null;
                    _layerBack[i] = null;
                }
            }

            // Optional: dispose HL resources
            try { HLTime?.Dispose(); } catch { }
            try { HLLogo?.Dispose(); } catch { }
            try { HLFont?.Dispose(); } catch { }
            try { HLBrush?.Dispose(); } catch { }
            try { HLClearBrush?.Dispose(); } catch { }
        }

        private void button1_Click_1(object sender, EventArgs e)
        {
            MessageBox.Show("Sign is online: " + Betsy.SignOnline);
        }

        private void button3_Click_1(object sender, EventArgs e)
        {
            Betsy.setGain(100);
        }

        private void btnStartDisplay_Click(object sender, EventArgs e)
        {
            StartDisplay();
        }

        private void btnStartVideoMixer_Click(object sender, EventArgs e)
        {
            InitVideoMixer();
            StartVideoMixer();
        }
    }
}
