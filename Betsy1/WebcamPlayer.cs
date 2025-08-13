// WebcamToCallbackPlayer.cs
// .NET Framework 4.8, x86 recommended
// Reference: DirectShowLib (same bitness)

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using DirectShowLib;

namespace VideoCallbackPlayer
{
    public sealed class WebcamToCallbackPlayer : IDisposable
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool TopDown { get; private set; }
        public Guid ConnectedSubtype { get; private set; }

        private IGraphBuilder _graph;
        private ICaptureGraphBuilder2 _cap;
        private IMediaControl _mc;
        private IBasicAudio _audio;

        private IBaseFilter _camera;
        private IBaseFilter _mic;
        private IBaseFilter _sgFilter;
        private ISampleGrabber _grab;
        private IBaseFilter _nullVid;
        private IBaseFilter _audioOut;

        private bool _running;

        public static WebcamToCallbackPlayer StartFirstCamera(ISampleGrabberCB callback, bool previewAudio, out int width, out int height)
        {
            if (callback == null) throw new ArgumentNullException("callback");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Call from STA thread");

            // Try OBS virtual cam, including VCamFilter
            DsDevice obs = FindObsVirtualCam();
            if (obs != null)
            {
                var pObs = new WebcamToCallbackPlayer();
                var proxy = new CbProxy(callback);
                try
                {
                    pObs.BuildGraphForDevice(obs, proxy, previewAudio);
                    int hr = pObs._mc.Run(); DsError.ThrowExceptionForHR(hr);
                    Debug.WriteLine("[Webcam] Running OBS VCAM graph, waiting for first frame...");
                    if (proxy.WaitForFirstFrame(1200))
                    {
                        pObs._running = true;
                        width = pObs.Width; height = pObs.Height;
                        return pObs;
                    }
                    Debug.WriteLine("[Webcam] OBS VCAM produced no frames, falling back.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Webcam] OBS VCAM failed: " + ex.Message);
                }
                finally { if (!pObs._running) pObs.Dispose(); }
            }

            // Try CVCam style
            DsDevice cv = FindCvCam();
            if (cv != null)
            {
                var pCv = new WebcamToCallbackPlayer();
                var proxy = new CbProxy(callback);
                try
                {
                    pCv.BuildGraphForDevice(cv, proxy, previewAudio);
                    int hr = pCv._mc.Run(); DsError.ThrowExceptionForHR(hr);
                    Debug.WriteLine("[Webcam] Running CVCam graph, waiting for first frame...");
                    if (proxy.WaitForFirstFrame(1200))
                    {
                        pCv._running = true;
                        width = pCv.Width; height = pCv.Height;
                        return pCv;
                    }
                    Debug.WriteLine("[Webcam] CVCam produced no frames, falling back.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Webcam] CVCam failed: " + ex.Message);
                }
                finally { if (!pCv._running) pCv.Dispose(); }
            }

            // Fallback to first hardware camera
            DsDevice hw = FindFirstHardwareCam();
            if (hw == null) throw new InvalidOperationException("No video capture device found");

            var p = new WebcamToCallbackPlayer();
            var proxy2 = new CbProxy(callback);
            p.BuildGraphForDevice(hw, proxy2, previewAudio);
            int hr2 = p._mc.Run(); DsError.ThrowExceptionForHR(hr2);
            p._running = true;

            width = p.Width; height = p.Height;
            return p;
        }

        public static WebcamToCallbackPlayer StartByName(string cameraFriendlyName, string micFriendlyName, ISampleGrabberCB callback, out int width, out int height)
        {
            if (string.IsNullOrWhiteSpace(cameraFriendlyName)) throw new ArgumentException("cameraFriendlyName");
            if (callback == null) throw new ArgumentNullException("callback");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Call from STA thread");

            var p = new WebcamToCallbackPlayer();
            var proxy = new CbProxy(callback);
            try
            {
                p.BuildGraphForNamedDevices(cameraFriendlyName, micFriendlyName, proxy);
                int hr = p._mc.Run(); DsError.ThrowExceptionForHR(hr);
                p._running = true;
                width = p.Width; height = p.Height;
                return p;
            }
            catch
            {
                p.Dispose();
                throw;
            }
        }

        private void BuildGraphForDevice(DsDevice camDev, ISampleGrabberCB cb, bool previewAudio)
        {
            _graph = (IGraphBuilder)new FilterGraph();
            _cap = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            DsError.ThrowExceptionForHR(_cap.SetFiltergraph(_graph));

            object camObj; Guid iid = typeof(IBaseFilter).GUID;
            camDev.Mon.BindToObject(null, null, ref iid, out camObj);
            _camera = (IBaseFilter)camObj;
            DsError.ThrowExceptionForHR(_graph.AddFilter(_camera, camDev.Name));
            Debug.WriteLine("[Webcam] Using camera: " + camDev.Name);

            if (previewAudio)
            {
                _mic = AddFirstDevice(FilterCategory.AudioInputDevice);
                if (_mic != null) { DsError.ThrowExceptionForHR(_graph.AddFilter(_mic, "Microphone")); }
            }

            // Critical: set a sane format and fps on the capture pin before connecting
            TryForceStreamFormat(_camera, preferYuy2First: true, targetW: 640, targetH: 480, fpsNum: 30, fpsDen: 1);

            BuildVideoPath(cb);
            BuildAudioPathIfPresent();
            ReadConnectedMediaType();

            // Ensure we have a clock; some virtual cams will not push without one
            try { ((IMediaFilter)_graph).SetSyncSource(null); } catch { }
        }

        private void BuildGraphForNamedDevices(string camName, string micName, ISampleGrabberCB cb)
        {
            _graph = (IGraphBuilder)new FilterGraph();
            _cap = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            DsError.ThrowExceptionForHR(_cap.SetFiltergraph(_graph));

            _camera = AddDeviceByName(FilterCategory.VideoInputDevice, camName);
            if (_camera == null) throw new InvalidOperationException("Camera not found: " + camName);
            DsError.ThrowExceptionForHR(_graph.AddFilter(_camera, "Camera"));
            Debug.WriteLine("[Webcam] Using camera by name: " + camName);

            if (!string.IsNullOrWhiteSpace(micName))
            {
                _mic = AddDeviceByName(FilterCategory.AudioInputDevice, micName);
                if (_mic != null) { DsError.ThrowExceptionForHR(_graph.AddFilter(_mic, "Microphone")); }
            }

            TryForceStreamFormat(_camera, preferYuy2First: true, targetW: 640, targetH: 480, fpsNum: 30, fpsDen: 1);

            BuildVideoPath(cb);
            BuildAudioPathIfPresent();
            ReadConnectedMediaType();

            try { ((IMediaFilter)_graph).SetSyncSource(null); } catch { }
        }

        private void BuildVideoPath(ISampleGrabberCB cb)
        {
            // SampleGrabber, accept any video subtype, we convert in your callback
            _sgFilter = (IBaseFilter)new SampleGrabber();
            _grab = (ISampleGrabber)_sgFilter;

            var req = new AMMediaType { majorType = MediaType.Video, subType = Guid.Empty, formatType = FormatType.VideoInfo };
            DsError.ThrowExceptionForHR(_grab.SetMediaType(req));
            DsUtils.FreeAMMediaType(req);

            DsError.ThrowExceptionForHR(_graph.AddFilter(_sgFilter, "SampleGrabber"));

            _nullVid = (IBaseFilter)new NullRenderer();
            DsError.ThrowExceptionForHR(_graph.AddFilter(_nullVid, "NullRenderer"));

            // Try CAPTURE, then PREVIEW, then manual
            int hr = _cap.RenderStream(PinCategory.Capture, MediaType.Video, _camera, _sgFilter, _nullVid);
            if (hr < 0)
            {
                Debug.WriteLine("[Webcam] CAPTURE path failed: 0x" + hr.ToString("X"));
                hr = _cap.RenderStream(PinCategory.Preview, MediaType.Video, _camera, _sgFilter, _nullVid);
                if (hr < 0) Debug.WriteLine("[Webcam] PREVIEW path failed: 0x" + hr.ToString("X"));
            }

            if (hr < 0)
            {
                Debug.WriteLine("[Webcam] Manual connect.");
                IPin camOut = FindPinOnFilterByMajorType(_camera, PinDirection.Output, MediaType.Video);
                if (camOut == null) throw new InvalidOperationException("Camera exposes no video pin");

                IPin sgIn = FindUnconnectedPin(_sgFilter, PinDirection.Input);

                hr = _graph.Connect(camOut, sgIn);
                if (hr < 0)
                {
                    Debug.WriteLine("[Webcam] Direct connect failed 0x" + hr.ToString("X") + ", trying a converter.");
                    IBaseFilter conv = AddFilterByName(_graph, "Color Space Converter");
                    if (conv == null) conv = AddFilterByName(_graph, "AVI Decompressor");

                    if (conv != null)
                    {
                        IPin ccIn = FindUnconnectedPin(conv, PinDirection.Input);
                        IPin ccOut = FindUnconnectedPin(conv, PinDirection.Output);
                        DsError.ThrowExceptionForHR(_graph.Connect(camOut, ccIn));
                        DsError.ThrowExceptionForHR(_graph.Connect(ccOut, sgIn));
                    }
                    else
                    {
                        DsError.ThrowExceptionForHR(_graph.Connect(camOut, sgIn));
                    }
                }

                IPin sgOut = FindUnconnectedPin(_sgFilter, PinDirection.Output);
                IPin nullIn = FindUnconnectedPin(_nullVid, PinDirection.Input);
                DsError.ThrowExceptionForHR(_graph.Connect(sgOut, nullIn));
            }

            _grab.SetOneShot(false);
            _grab.SetBufferSamples(true);
            _grab.SetCallback(cb, 1); // BufferCB
        }

        private void BuildAudioPathIfPresent()
        {
            if (_mic == null) return;

            _audioOut = (IBaseFilter)new DSoundRender();
            DsError.ThrowExceptionForHR(_graph.AddFilter(_audioOut, "DefaultAudio"));

            int hr = _cap.RenderStream(PinCategory.Capture, MediaType.Audio, _mic, null, _audioOut);
            if (hr < 0)
            {
                IPin micOut = FindPinOnFilterByMajorType(_mic, PinDirection.Output, MediaType.Audio);
                IPin audIn = FindUnconnectedPin(_audioOut, PinDirection.Input);
                if (micOut != null && audIn != null) DsError.ThrowExceptionForHR(_graph.Connect(micOut, audIn));
            }

            _audio = (IBasicAudio)_graph;
        }

        private void ReadConnectedMediaType()
        {
            AMMediaType connected = new AMMediaType();
            int hr = _grab.GetConnectedMediaType(connected); DsError.ThrowExceptionForHR(hr);
            try
            {
                ConnectedSubtype = connected.subType;
                Debug.WriteLine("[Webcam] Connected subtype: " + ConnectedSubtype);

                if (connected.formatType == FormatType.VideoInfo && connected.formatPtr != IntPtr.Zero)
                {
                    var vih = (VideoInfoHeader)Marshal.PtrToStructure(connected.formatPtr, typeof(VideoInfoHeader));
                    Width = vih.BmiHeader.Width;
                    Height = Math.Abs(vih.BmiHeader.Height);
                    TopDown = vih.BmiHeader.Height < 0;
                }
                else if (connected.formatType == FormatType.VideoInfo2 && connected.formatPtr != IntPtr.Zero)
                {
                    var vih2 = (VideoInfoHeader2)Marshal.PtrToStructure(connected.formatPtr, typeof(VideoInfoHeader2));
                    Width = vih2.BmiHeader.Width;
                    Height = Math.Abs(vih2.BmiHeader.Height);
                    TopDown = vih2.BmiHeader.Height < 0;
                }
                else
                {
                    Width = Math.Max(Width, 0); Height = Math.Max(Height, 0); TopDown = false;
                }
                Debug.WriteLine("[Webcam] Negotiated " + Width + "x" + Height + " TopDown=" + TopDown);
            }
            finally { DsUtils.FreeAMMediaType(connected); }

            _mc = (IMediaControl)_graph;
        }

        // Force a valid format and fps on the camera pin
        private void TryForceStreamFormat(IBaseFilter camera, bool preferYuy2First, int targetW, int targetH, int fpsNum, int fpsDen)
        {
            try
            {
                IPin pin = FindPinOnFilterByMajorType(camera, PinDirection.Output, MediaType.Video);
                if (pin == null) return;

                var cfg = pin as IAMStreamConfig;
                if (cfg == null) return;

                int count, size;
                int hr = cfg.GetNumberOfCapabilities(out count, out size); DsError.ThrowExceptionForHR(hr);
                if (count <= 0 || size <= 0) return;

                IntPtr caps = Marshal.AllocCoTaskMem(size);
                try
                {
                    AMMediaType bestYuy2 = null, bestRgb32 = null, any = null;
                    int bestScoreYuy2 = int.MaxValue, bestScoreRgb32 = int.MaxValue, bestScoreAny = int.MaxValue;

                    for (int i = 0; i < count; i++)
                    {
                        AMMediaType mt; hr = cfg.GetStreamCaps(i, out mt, caps);
                        if (hr < 0 || mt == null) continue;

                        try
                        {
                            if (mt.majorType != MediaType.Video) { DsUtils.FreeAMMediaType(mt); continue; }

                            int w = 0, h = 0;
                            long atpf = 0;
                            if (mt.formatType == FormatType.VideoInfo && mt.formatPtr != IntPtr.Zero)
                            {
                                var vih = (VideoInfoHeader)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader));
                                w = vih.BmiHeader.Width; h = Math.Abs(vih.BmiHeader.Height); atpf = vih.AvgTimePerFrame;
                            }
                            else if (mt.formatType == FormatType.VideoInfo2 && mt.formatPtr != IntPtr.Zero)
                            {
                                var vih2 = (VideoInfoHeader2)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader2));
                                w = vih2.BmiHeader.Width; h = Math.Abs(vih2.BmiHeader.Height); atpf = vih2.AvgTimePerFrame;
                            }

                            int score = Math.Abs(w - targetW) + Math.Abs(h - targetH);
                            if (mt.subType == MediaSubType.YUY2 && score < bestScoreYuy2) { bestScoreYuy2 = score; bestYuy2 = mt; mt = null; }
                            else if (mt.subType == MediaSubType.RGB32 && score < bestScoreRgb32) { bestScoreRgb32 = score; bestRgb32 = mt; mt = null; }
                            else if (score < bestScoreAny) { bestScoreAny = score; any = mt; mt = null; }
                        }
                        finally
                        {
                            if (mt != null) DsUtils.FreeAMMediaType(mt);
                        }
                    }

                    AMMediaType chosen = preferYuy2First ? (bestYuy2 ?? bestRgb32 ?? any) : (bestRgb32 ?? bestYuy2 ?? any);
                    if (chosen == null) return;

                    // Ensure a sane frame rate
                    if (chosen.formatType == FormatType.VideoInfo && chosen.formatPtr != IntPtr.Zero)
                    {
                        var vih = (VideoInfoHeader)Marshal.PtrToStructure(chosen.formatPtr, typeof(VideoInfoHeader));
                        if (vih.AvgTimePerFrame <= 0)
                            vih.AvgTimePerFrame = FpsToAvgTimePerFrame(fpsNum, fpsDen); // 30 fps default
                        // also enforce target size if driver allows it
                        if (targetW > 0 && targetH > 0) { vih.BmiHeader.Width = targetW; vih.BmiHeader.Height = targetH; }
                        Marshal.StructureToPtr(vih, chosen.formatPtr, true);
                    }
                    else if (chosen.formatType == FormatType.VideoInfo2 && chosen.formatPtr != IntPtr.Zero)
                    {
                        var vih2 = (VideoInfoHeader2)Marshal.PtrToStructure(chosen.formatPtr, typeof(VideoInfoHeader2));
                        if (vih2.AvgTimePerFrame <= 0)
                            vih2.AvgTimePerFrame = FpsToAvgTimePerFrame(fpsNum, fpsDen);
                        if (targetW > 0 && targetH > 0) { vih2.BmiHeader.Width = targetW; vih2.BmiHeader.Height = targetH; }
                        Marshal.StructureToPtr(vih2, chosen.formatPtr, true);
                    }

                    Debug.WriteLine("[Webcam] Setting camera subtype to " + chosen.subType);
                    DsError.ThrowExceptionForHR(cfg.SetFormat(chosen));
                    DsUtils.FreeAMMediaType(chosen);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(caps);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Webcam] TryForceStreamFormat: " + ex.Message);
            }
        }

        private static long FpsToAvgTimePerFrame(int num, int den)
        {
            if (num <= 0 || den <= 0) return 333333; // 30 fps
            double fps = (double)num / den;
            if (fps <= 0.1) return 333333;
            return (long)(10000000.0 / fps); // 100 ns units
        }

        public void SetVolumeDb(int decibels)
        {
            if (_audio == null) return;
            if (decibels > 0) decibels = 0;
            if (decibels < -100) decibels = -100;
            _audio.put_Volume(decibels * 100);
        }

        public void Stop()
        {
            if (_mc != null && _running) { _mc.Stop(); _running = false; }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }

            ReleaseCom(ref _audioOut);
            ReleaseCom(ref _nullVid);
            ReleaseCom(ref _grab);
            ReleaseCom(ref _sgFilter);
            ReleaseCom(ref _mic);
            ReleaseCom(ref _camera);

            ReleaseCom(ref _audio);
            ReleaseCom(ref _cap);
            ReleaseCom(ref _graph);
            GC.SuppressFinalize(this);
        }

        // ---------- Device selection helpers ----------

        private static DsDevice FindObsVirtualCam()
        {
            DsDevice[] devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            if (devs == null) return null;
            foreach (var d in devs)
            {
                string n = d.Name ?? ""; string p = d.DevicePath ?? "";
                if (n.Equals("VCamFilter", StringComparison.OrdinalIgnoreCase)) return d;
                if (n.IndexOf("OBS", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (n.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("VCam", StringComparison.OrdinalIgnoreCase) >= 0)) return d;
                string lp = p.ToLowerInvariant();
                if (lp.Contains("vcamfilter") || (lp.Contains("obs") && lp.Contains("virtual"))) return d;
            }
            return null;
        }

        private static DsDevice FindCvCam()
        {
            DsDevice[] devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            if (devs == null) return null;
            foreach (var d in devs)
            {
                string n = d.Name ?? "";
                if (n.IndexOf("CVCam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Virtual Cam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("VirtualCam", StringComparison.OrdinalIgnoreCase) >= 0) return d;
            }
            return null;
        }

        private static DsDevice FindFirstHardwareCam()
        {
            DsDevice[] devs = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            if (devs == null) return null;
            foreach (var d in devs)
            {
                string n = d.Name ?? "";
                if (n.Equals("VCamFilter", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.IndexOf("OBS", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (n.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("VCam", StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (n.IndexOf("CVCam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Virtual Cam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("VirtualCam", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return d;
            }
            return null;
        }

        private static IBaseFilter AddDeviceByName(Guid category, string friendlyName)
        {
            DsDevice[] devs = DsDevice.GetDevicesOfCat(category);
            if (devs == null) return null;
            for (int i = 0; i < devs.Length; i++)
            {
                if (string.Equals(devs[i].Name, friendlyName, StringComparison.OrdinalIgnoreCase))
                {
                    object obj; Guid iid = typeof(IBaseFilter).GUID;
                    devs[i].Mon.BindToObject(null, null, ref iid, out obj);
                    return (IBaseFilter)obj;
                }
            }
            return null;
        }

        private static IBaseFilter AddFirstDevice(Guid category)
        {
            DsDevice[] devs = DsDevice.GetDevicesOfCat(category);
            if (devs == null || devs.Length == 0) return null;

            object obj; Guid iid = typeof(IBaseFilter).GUID;
            devs[0].Mon.BindToObject(null, null, ref iid, out obj);
            return (IBaseFilter)obj;
        }

        private static DirectShowLib.IBaseFilter AddFilterByName(DirectShowLib.IGraphBuilder graph, string friendlyName)
        {
            DirectShowLib.DsDevice[] devs = DirectShowLib.DsDevice.GetDevicesOfCat(DirectShowLib.FilterCategory.LegacyAmFilterCategory);
            if (devs == null || devs.Length == 0) return null;

            for (int i = 0; i < devs.Length; i++)
            {
                if (!string.Equals(devs[i].Name, friendlyName, StringComparison.OrdinalIgnoreCase)) continue;

                object filterObj; Guid iid = typeof(DirectShowLib.IBaseFilter).GUID;
                devs[i].Mon.BindToObject(null, null, ref iid, out filterObj);
                DirectShowLib.IBaseFilter f = (DirectShowLib.IBaseFilter)filterObj;

                int hr = graph.AddFilter(f, friendlyName);
                DirectShowLib.DsError.ThrowExceptionForHR(hr);
                Debug.WriteLine("[Webcam] Added helper filter: " + friendlyName);
                return f;
            }
            return null;
        }

        // ---------- Graph helpers ----------

        private static IPin FindUnconnectedPin(IBaseFilter filter, PinDirection dir)
        {
            IEnumPins en; int hr = filter.EnumPins(out en); DsError.ThrowExceptionForHR(hr);
            try
            {
                IPin[] pins = new IPin[1];
                while (en.Next(1, pins, IntPtr.Zero) == 0)
                {
                    PinDirection pd; pins[0].QueryDirection(out pd);
                    if (pd == dir)
                    {
                        IPin other; if (pins[0].ConnectedTo(out other) != 0) return pins[0];
                        if (other != null) Marshal.ReleaseComObject(other);
                    }
                    Marshal.ReleaseComObject(pins[0]);
                }
            }
            finally { Marshal.ReleaseComObject(en); }
            return null;
        }

        private static IPin FindPinOnFilterByMajorType(IBaseFilter filter, PinDirection dir, Guid majorType)
        {
            IEnumPins en; int hr = filter.EnumPins(out en); DsError.ThrowExceptionForHR(hr);
            try
            {
                IPin[] pins = new IPin[1];
                while (en.Next(1, pins, IntPtr.Zero) == 0)
                {
                    PinDirection pd; pins[0].QueryDirection(out pd);
                    if (pd == dir)
                    {
                        IEnumMediaTypes emt; if (pins[0].EnumMediaTypes(out emt) == 0 && emt != null)
                        {
                            try
                            {
                                AMMediaType[] mts = new AMMediaType[1];
                                while (emt.Next(1, mts, IntPtr.Zero) == 0)
                                {
                                    bool match = mts[0].majorType == majorType;
                                    DsUtils.FreeAMMediaType(mts[0]);
                                    if (match) return pins[0];
                                }
                            }
                            finally { Marshal.ReleaseComObject(emt); }
                        }
                    }
                    Marshal.ReleaseComObject(pins[0]);
                }
            }
            finally { Marshal.ReleaseComObject(en); }
            return null;
        }

        private static void ReleaseCom<T>(ref T o) where T : class
        {
            if (o == null) return;
            try { while (Marshal.ReleaseComObject(o) > 0) { } } catch { }
            o = null;
        }

        // ---------- First-frame proxy ----------

        private sealed class CbProxy : ISampleGrabberCB
        {
            private readonly ISampleGrabberCB _inner;
            private readonly System.Threading.ManualResetEvent _gotFrame = new System.Threading.ManualResetEvent(false);

            public CbProxy(ISampleGrabberCB inner) { _inner = inner; }
            public bool WaitForFirstFrame(int timeoutMs) { return _gotFrame.WaitOne(timeoutMs); }

            public int SampleCB(double sampleTime, IMediaSample pSample)
            { try { return _inner != null ? _inner.SampleCB(sampleTime, pSample) : 0; } finally { _gotFrame.Set(); } }

            public int BufferCB(double sampleTime, IntPtr pBuffer, int bufferLen)
            { try { return _inner != null ? _inner.BufferCB(sampleTime, pBuffer, bufferLen) : 0; } finally { _gotFrame.Set(); } }
        }
    }
}
