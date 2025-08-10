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
        // Negotiated info, same as FileToCallbackPlayer
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool TopDown { get; private set; }
        public Guid ConnectedSubtype { get; private set; }

        // Graph
        private IGraphBuilder _graph;
        private ICaptureGraphBuilder2 _cap;
        private IMediaControl _mc;
        private IBasicAudio _audio;

        // Filters
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

            WebcamToCallbackPlayer p = new WebcamToCallbackPlayer();
            try
            {
                p.BuildGraphForFirstDevices(callback, previewAudio);
                int hr = p._mc.Run();
                DsError.ThrowExceptionForHR(hr);
                p._running = true;
                width = p.Width;
                height = p.Height;
                return p;
            }
            catch
            {
                p.Dispose();
                throw;
            }
        }

        public static WebcamToCallbackPlayer StartByName(string cameraFriendlyName, string micFriendlyName, ISampleGrabberCB callback, out int width, out int height)
        {
            if (string.IsNullOrWhiteSpace(cameraFriendlyName)) throw new ArgumentException("cameraFriendlyName");
            if (callback == null) throw new ArgumentNullException("callback");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Call from STA thread");

            WebcamToCallbackPlayer p = new WebcamToCallbackPlayer();
            try
            {
                p.BuildGraphForNamedDevices(cameraFriendlyName, micFriendlyName, callback);
                int hr = p._mc.Run();
                DsError.ThrowExceptionForHR(hr);
                p._running = true;
                width = p.Width;
                height = p.Height;
                return p;
            }
            catch
            {
                p.Dispose();
                throw;
            }
        }

        private void BuildGraphForFirstDevices(ISampleGrabberCB cb, bool previewAudio)
        {
            _graph = (IGraphBuilder)new FilterGraph();
            _cap = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            DsError.ThrowExceptionForHR(_cap.SetFiltergraph(_graph));

            _camera = AddFirstDevice(FilterCategory.VideoInputDevice);
            if (_camera == null) throw new InvalidOperationException("No video capture device found");
            DsError.ThrowExceptionForHR(_graph.AddFilter(_camera, "Camera"));

            if (previewAudio)
            {
                _mic = AddFirstDevice(FilterCategory.AudioInputDevice);
                if (_mic != null)
                    DsError.ThrowExceptionForHR(_graph.AddFilter(_mic, "Microphone"));
            }

            BuildVideoPath(cb);
            BuildAudioPathIfPresent();
            ReadConnectedMediaType();
        }

        private void BuildGraphForNamedDevices(string camName, string micName, ISampleGrabberCB cb)
        {
            _graph = (IGraphBuilder)new FilterGraph();
            _cap = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            DsError.ThrowExceptionForHR(_cap.SetFiltergraph(_graph));

            _camera = AddDeviceByName(FilterCategory.VideoInputDevice, camName);
            if (_camera == null) throw new InvalidOperationException("Camera not found: " + camName);
            DsError.ThrowExceptionForHR(_graph.AddFilter(_camera, "Camera"));

            if (!string.IsNullOrWhiteSpace(micName))
            {
                _mic = AddDeviceByName(FilterCategory.AudioInputDevice, micName);
                if (_mic != null)
                    DsError.ThrowExceptionForHR(_graph.AddFilter(_mic, "Microphone"));
            }

            BuildVideoPath(cb);
            BuildAudioPathIfPresent();
            ReadConnectedMediaType();
        }

        private void BuildVideoPath(ISampleGrabberCB cb)
        {
            // SampleGrabber in RGB32, then NullRenderer
            _sgFilter = (IBaseFilter)new SampleGrabber();
            _grab = (ISampleGrabber)_sgFilter;

            AMMediaType mt = new AMMediaType();
            mt.majorType = MediaType.Video;
            mt.subType = MediaSubType.RGB32; // request RGB32, converter will be inserted if needed
            mt.formatType = FormatType.VideoInfo;
            DsError.ThrowExceptionForHR(_grab.SetMediaType(mt));
            DsUtils.FreeAMMediaType(mt);

            DsError.ThrowExceptionForHR(_graph.AddFilter(_sgFilter, "SampleGrabber"));

            _nullVid = (IBaseFilter)new NullRenderer();
            DsError.ThrowExceptionForHR(_graph.AddFilter(_nullVid, "NullRenderer"));

            // Try the canonical preview path first
            int hr = _cap.RenderStream(PinCategory.Preview, MediaType.Video, _camera, _sgFilter, _nullVid);

            if (hr < 0)
            {
                // Fallback, connect manually: camera video out -> [Color Space Converter] -> SG -> Null
                DsError.ThrowExceptionForHR(0); // reset last error reporting
                IBaseFilter colorConv = AddFilterByName(_graph, "Color Space Converter");
                if (colorConv != null) DsError.ThrowExceptionForHR(_graph.AddFilter(colorConv, "Color Space Converter"));

                IPin camOut = FindPinOnFilterByMajorType(_camera, PinDirection.Output, MediaType.Video);
                if (camOut == null) throw new InvalidOperationException("Camera exposes no video pin");

                IPin sgIn = FindUnconnectedPin(_sgFilter, PinDirection.Input);
                if (colorConv != null)
                {
                    IPin ccIn = FindUnconnectedPin(colorConv, PinDirection.Input);
                    DsError.ThrowExceptionForHR(_graph.Connect(camOut, ccIn));
                    IPin ccOut = FindUnconnectedPin(colorConv, PinDirection.Output);
                    DsError.ThrowExceptionForHR(_graph.Connect(ccOut, sgIn));
                }
                else
                {
                    DsError.ThrowExceptionForHR(_graph.Connect(camOut, sgIn));
                }

                IPin sgOut = FindUnconnectedPin(_sgFilter, PinDirection.Output);
                IPin nullIn = FindUnconnectedPin(_nullVid, PinDirection.Input);
                DsError.ThrowExceptionForHR(_graph.Connect(sgOut, nullIn));
            }

            // BufferCB mode for reliability
            _grab.SetOneShot(false);
            _grab.SetBufferSamples(true);
            _grab.SetCallback(cb, 1); // BufferCB
        }

        private void BuildAudioPathIfPresent()
        {
            if (_mic == null) return;

            _audioOut = (IBaseFilter)new DSoundRender(); // default output device
            DsError.ThrowExceptionForHR(_graph.AddFilter(_audioOut, "DefaultAudio"));

            // most microphones use PinCategory.Capture for audio
            int hr = _cap.RenderStream(PinCategory.Capture, MediaType.Audio, _mic, null, _audioOut);
            if (hr < 0)
            {
                // fallback, attempt generic connect
                IPin micOut = FindPinOnFilterByMajorType(_mic, PinDirection.Output, MediaType.Audio);
                IPin audIn = FindUnconnectedPin(_audioOut, PinDirection.Input);
                if (micOut != null && audIn != null)
                    DsError.ThrowExceptionForHR(_graph.Connect(micOut, audIn));
            }

            _audio = (IBasicAudio)_graph;
        }

        private void ReadConnectedMediaType()
        {
            AMMediaType connected = new AMMediaType();
            int hr = _grab.GetConnectedMediaType(connected);
            DsError.ThrowExceptionForHR(hr);
            try
            {
                ConnectedSubtype = connected.subType;

                if (connected.formatType == FormatType.VideoInfo && connected.formatPtr != IntPtr.Zero)
                {
                    VideoInfoHeader vih = (VideoInfoHeader)Marshal.PtrToStructure(connected.formatPtr, typeof(VideoInfoHeader));
                    Width = vih.BmiHeader.Width;
                    Height = Math.Abs(vih.BmiHeader.Height);
                    TopDown = vih.BmiHeader.Height < 0;
                }
                else if (connected.formatType == FormatType.VideoInfo2 && connected.formatPtr != IntPtr.Zero)
                {
                    VideoInfoHeader2 vih2 = (VideoInfoHeader2)Marshal.PtrToStructure(connected.formatPtr, typeof(VideoInfoHeader2));
                    Width = vih2.BmiHeader.Width;
                    Height = Math.Abs(vih2.BmiHeader.Height);
                    TopDown = vih2.BmiHeader.Height < 0;
                }
                else
                {
                    Width = Math.Max(Width, 0);
                    Height = Math.Max(Height, 0);
                    TopDown = false;
                }
            }
            finally
            {
                DsUtils.FreeAMMediaType(connected);
            }

            _mc = (IMediaControl)_graph;
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
            if (_mc != null && _running)
            {
                _mc.Stop();
                _running = false;
            }
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

        // ---------- Helpers ----------

        private static DirectShowLib.IBaseFilter AddFilterByName(DirectShowLib.IGraphBuilder graph, string friendlyName)
        {
            DirectShowLib.DsDevice[] devs = DirectShowLib.DsDevice.GetDevicesOfCat(DirectShowLib.FilterCategory.LegacyAmFilterCategory);
            if (devs == null || devs.Length == 0) return null;

            for (int i = 0; i < devs.Length; i++)
            {
                if (!string.Equals(devs[i].Name, friendlyName, StringComparison.OrdinalIgnoreCase)) continue;

                object filterObj;
                Guid iid = typeof(DirectShowLib.IBaseFilter).GUID;
                devs[i].Mon.BindToObject(null, null, ref iid, out filterObj);
                DirectShowLib.IBaseFilter f = (DirectShowLib.IBaseFilter)filterObj;

                int hr = graph.AddFilter(f, friendlyName);
                DirectShowLib.DsError.ThrowExceptionForHR(hr);
                return f;
            }
            return null;
        }

        private static IBaseFilter AddFirstDevice(Guid category)
        {
            DsDevice[] devs = DsDevice.GetDevicesOfCat(category);
            if (devs == null || devs.Length == 0) return null;

            object obj;
            Guid iid = typeof(IBaseFilter).GUID;
            devs[0].Mon.BindToObject(null, null, ref iid, out obj);
            return (IBaseFilter)obj;
        }

        private IBaseFilter AddDeviceByName(Guid category, string friendlyName)
        {
            DsDevice[] devs = DsDevice.GetDevicesOfCat(category);
            if (devs == null) return null;
            for (int i = 0; i < devs.Length; i++)
            {
                if (string.Equals(devs[i].Name, friendlyName, StringComparison.OrdinalIgnoreCase))
                {
                    object obj;
                    Guid iid = typeof(IBaseFilter).GUID;
                    devs[i].Mon.BindToObject(null, null, ref iid, out obj);
                    return (IBaseFilter)obj;
                }
            }
            return null;
        }

        private static IPin FindUnconnectedPin(IBaseFilter filter, PinDirection dir)
        {
            IEnumPins en;
            int hr = filter.EnumPins(out en);
            DsError.ThrowExceptionForHR(hr);
            try
            {
                IPin[] pins = new IPin[1];
                while (en.Next(1, pins, IntPtr.Zero) == 0)
                {
                    PinDirection pd;
                    pins[0].QueryDirection(out pd);
                    if (pd == dir)
                    {
                        IPin other;
                        if (pins[0].ConnectedTo(out other) != 0) return pins[0];
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
            IEnumPins en;
            int hr = filter.EnumPins(out en);
            DsError.ThrowExceptionForHR(hr);
            try
            {
                IPin[] pins = new IPin[1];
                while (en.Next(1, pins, IntPtr.Zero) == 0)
                {
                    PinDirection pd;
                    pins[0].QueryDirection(out pd);
                    if (pd == dir)
                    {
                        IEnumMediaTypes emt;
                        if (pins[0].EnumMediaTypes(out emt) == 0 && emt != null)
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
    }
}
