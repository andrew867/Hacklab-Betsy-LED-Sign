// FileToCallbackPlayer.cs
// .NET Framework 4.8, x86 recommended
// Reference: DirectShowLib (same bitness)

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using DirectShowLib;

namespace VideoCallbackPlayer
{
    public sealed class FileToCallbackPlayer : IDisposable
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool TopDown { get; private set; }
        public Guid ConnectedSubtype { get; private set; }
        public IMediaSeeking Seeker { get { return _seek; } }

        private IGraphBuilder _graph;
        private IMediaControl _mc;
        private IMediaSeeking _seek;
        private IBasicAudio _audio;

        private IBaseFilter _sgFilter;
        private ISampleGrabber _grab;
        private IBaseFilter _nullVid;
        private IBaseFilter _audioOut;

        private bool _running;

        public static FileToCallbackPlayer Play(string path, ISampleGrabberCB callback, out int width, out int height)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path");
            if (callback == null) throw new ArgumentNullException("callback");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Call from STA thread");

            var p = new FileToCallbackPlayer();
            try
            {
                p.BuildGraph(path, callback);
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

        private void BuildGraph(string path, ISampleGrabberCB cb)
        {
            _graph = (IGraphBuilder)new FilterGraph();

            // Prefer explicit LAV path, else fallback
            if (!TryBuildLavGraph(path, cb))
                BuildByReplacingRenderer(path, cb);

            _mc = (IMediaControl)_graph;
            _seek = (IMediaSeeking)_graph;
            _audio = (IBasicAudio)_graph;

            // Read negotiated media type from the grabber
            var connected = new AMMediaType();
            int hr = _grab.GetConnectedMediaType(connected);
            DsError.ThrowExceptionForHR(hr);
            try
            {
                ConnectedSubtype = connected.subType;

                if (connected.formatPtr != IntPtr.Zero)
                {
                    if (connected.formatType == FormatType.VideoInfo)
                    {
                        var vih = (VideoInfoHeader)Marshal.PtrToStructure(connected.formatPtr, typeof(VideoInfoHeader));
                        Width = vih.BmiHeader.Width;
                        Height = Math.Abs(vih.BmiHeader.Height);
                        TopDown = vih.BmiHeader.Height < 0;
                    }
                    else if (connected.formatType == FormatType.VideoInfo2)
                    {
                        var vih2 = (VideoInfoHeader2)Marshal.PtrToStructure(connected.formatPtr, typeof(VideoInfoHeader2));
                        Width = vih2.BmiHeader.Width;
                        Height = Math.Abs(vih2.BmiHeader.Height);
                        TopDown = vih2.BmiHeader.Height < 0;
                    }
                }
            }
            finally
            {
                DsUtils.FreeAMMediaType(connected);
            }
        }

        private bool TryBuildLavGraph(string path, ISampleGrabberCB cb)
        {
            IBaseFilter lavSource = AddFilterByName(_graph, "LAV Splitter Source");
            IBaseFilter lavVideo = AddFilterByName(_graph, "LAV Video Decoder");
            IBaseFilter lavAudio = AddFilterByName(_graph, "LAV Audio Decoder");

            if (lavSource == null || lavVideo == null)
            {
                ReleaseCom(ref lavSource);
                ReleaseCom(ref lavVideo);
                ReleaseCom(ref lavAudio);
                return false;
            }

            int hr;
            var loader = (IFileSourceFilter)lavSource;
            hr = loader.Load(path, null);
            DsError.ThrowExceptionForHR(hr);

            _sgFilter = (IBaseFilter)new SampleGrabber();
            _grab = (ISampleGrabber)_sgFilter;

            var mt = new AMMediaType();
            mt.majorType = MediaType.Video;
            mt.subType = MediaSubType.RGB32;       // request RGB32
            mt.formatType = FormatType.VideoInfo;
            hr = _grab.SetMediaType(mt);
            DsError.ThrowExceptionForHR(hr);
            DsUtils.FreeAMMediaType(mt);

            hr = _graph.AddFilter(_sgFilter, "SampleGrabber");
            DsError.ThrowExceptionForHR(hr);

            IBaseFilter colorConv = AddFilterByName(_graph, "Color Space Converter");

            _nullVid = (IBaseFilter)new NullRenderer();
            hr = _graph.AddFilter(_nullVid, "NullRenderer");
            DsError.ThrowExceptionForHR(hr);

            _audioOut = (IBaseFilter)new DSoundRender();
            hr = _graph.AddFilter(_audioOut, "DefaultAudio");
            DsError.ThrowExceptionForHR(hr);

            // Video chain
            IPin lavVidOut = FindPinOnFilterByMajorType(lavSource, PinDirection.Output, MediaType.Video);
            if (lavVidOut == null) throw new InvalidOperationException("No video from splitter");

            IPin lavVidIn = FindUnconnectedPin(lavVideo, PinDirection.Input);
            hr = _graph.Connect(lavVidOut, lavVidIn);
            DsError.ThrowExceptionForHR(hr);

            IPin vidOut = FindUnconnectedPin(lavVideo, PinDirection.Output);
            if (colorConv != null)
            {
                IPin ccIn = FindUnconnectedPin(colorConv, PinDirection.Input);
                if (ccIn != null)
                {
                    hr = _graph.Connect(vidOut, ccIn);
                    DsError.ThrowExceptionForHR(hr);
                    vidOut = FindUnconnectedPin(colorConv, PinDirection.Output);
                }
            }

            IPin sgIn = FindUnconnectedPin(_sgFilter, PinDirection.Input);
            hr = _graph.Connect(vidOut, sgIn);
            DsError.ThrowExceptionForHR(hr);

            IPin sgOut = FindUnconnectedPin(_sgFilter, PinDirection.Output);
            IPin nullIn = FindUnconnectedPin(_nullVid, PinDirection.Input);
            hr = _graph.Connect(sgOut, nullIn);
            DsError.ThrowExceptionForHR(hr);

            // Audio chain
            IPin lavAudOut = FindPinOnFilterByMajorType(lavSource, PinDirection.Output, MediaType.Audio);
            if (lavAudOut != null)
            {
                if (lavAudio != null)
                {
                    IPin lavAudIn = FindUnconnectedPin(lavAudio, PinDirection.Input);
                    hr = _graph.Connect(lavAudOut, lavAudIn);
                    DsError.ThrowExceptionForHR(hr);

                    IPin lavAudDecOut = FindUnconnectedPin(lavAudio, PinDirection.Output);
                    IPin audIn = FindUnconnectedPin(_audioOut, PinDirection.Input);
                    hr = _graph.Connect(lavAudDecOut, audIn);
                    DsError.ThrowExceptionForHR(hr);
                }
                else
                {
                    IPin audIn = FindUnconnectedPin(_audioOut, PinDirection.Input);
                    hr = _graph.Connect(lavAudOut, audIn);
                    DsError.ThrowExceptionForHR(hr);
                }
            }

            // BufferCB mode
            _grab.SetOneShot(false);
            _grab.SetBufferSamples(true);
            _grab.SetCallback(cb, 1);   // 1 = BufferCB

            return true;
        }

        private void BuildByReplacingRenderer(string path, ISampleGrabberCB cb)
        {
            int hr = _graph.RenderFile(path, null);
            DsError.ThrowExceptionForHR(hr);

            IBaseFilter videoRenderer = FindVideoRenderer(_graph);
            IPin upstream = null, rendIn = null;

            if (videoRenderer != null)
            {
                rendIn = FindUnconnectedOrFirstPin(videoRenderer, PinDirection.Input);
                if (rendIn == null) throw new InvalidOperationException("Renderer has no input");
                upstream = GetConnectedTo(rendIn);
                if (upstream == null) throw new InvalidOperationException("Renderer input not connected");

                hr = _graph.Disconnect(rendIn);
                DsError.ThrowExceptionForHR(hr);
                hr = _graph.Disconnect(upstream);
                DsError.ThrowExceptionForHR(hr);
                RemoveFilter(_graph, videoRenderer);
            }
            else
            {
                upstream = FindFirstVideoOutputPin(_graph);
                if (upstream == null) throw new InvalidOperationException("No video stream in graph");
            }

            _sgFilter = (IBaseFilter)new SampleGrabber();
            _grab = (ISampleGrabber)_sgFilter;

            var mt = new AMMediaType();
            mt.majorType = MediaType.Video;
            mt.subType = MediaSubType.RGB32;
            mt.formatType = FormatType.VideoInfo;
            hr = _grab.SetMediaType(mt);
            DsError.ThrowExceptionForHR(hr);
            DsUtils.FreeAMMediaType(mt);

            hr = _graph.AddFilter(_sgFilter, "SampleGrabber");
            DsError.ThrowExceptionForHR(hr);

            _nullVid = (IBaseFilter)new NullRenderer();
            hr = _graph.AddFilter(_nullVid, "NullRenderer");
            DsError.ThrowExceptionForHR(hr);

            IPin sgIn = FindUnconnectedPin(_sgFilter, PinDirection.Input);
            IPin sgOut = FindUnconnectedPin(_sgFilter, PinDirection.Output);
            IPin nullIn = FindUnconnectedPin(_nullVid, PinDirection.Input);

            hr = _graph.Connect(upstream, sgIn);
            DsError.ThrowExceptionForHR(hr);
            hr = _graph.Connect(sgOut, nullIn);
            DsError.ThrowExceptionForHR(hr);

            _grab.SetOneShot(false);
            _grab.SetBufferSamples(true);
            _grab.SetCallback(cb, 1);   // BufferCB
        }

        public void SetVolumeDb(int decibels)
        {
            if (_audio == null) return;
            if (decibels > 0) decibels = 0;
            if (decibels < -100) decibels = -100;
            _audio.put_Volume(decibels * 100);
        }

        public void Pause()
        {
            if (_mc != null && _running) DsError.ThrowExceptionForHR(_mc.Pause());
        }

        public void Stop()
        {
            if (_mc != null && _running)
            {
                _mc.Stop();
                _running = false;
            }
        }

        public void SeekToStart()
        {
            if (_seek == null) return;
            long zero = 0;
            DsError.ThrowExceptionForHR(_seek.SetPositions(
                DsLong.FromInt64(zero), AMSeekingSeekingFlags.AbsolutePositioning,
                DsLong.FromInt64(zero), AMSeekingSeekingFlags.NoPositioning));
        }

        public void Dispose()
        {
            try { Stop(); } catch { }

            ReleaseCom(ref _audioOut);
            ReleaseCom(ref _nullVid);
            ReleaseCom(ref _grab);
            ReleaseCom(ref _sgFilter);

            ReleaseCom(ref _mc);
            ReleaseCom(ref _seek);
            ReleaseCom(ref _audio);
            ReleaseCom(ref _graph);
            GC.SuppressFinalize(this);
        }

        // --------- Helpers ----------
        private static void ReleaseCom<T>(ref T o) where T : class
        {
            if (o == null) return;
            try { while (Marshal.ReleaseComObject(o) > 0) { } } catch { }
            o = null;
        }

        private static IBaseFilter AddFilterByName(IGraphBuilder graph, string friendlyName)
        {
            DsDevice[] devs = DsDevice.GetDevicesOfCat(FilterCategory.LegacyAmFilterCategory);
            if (devs == null) return null;
            for (int i = 0; i < devs.Length; i++)
            {
                if (!string.Equals(devs[i].Name, friendlyName, StringComparison.OrdinalIgnoreCase)) continue;
                object filterObj;
                Guid iid = typeof(IBaseFilter).GUID;
                devs[i].Mon.BindToObject(null, null, ref iid, out filterObj);
                var f = (IBaseFilter)filterObj;
                int hr = graph.AddFilter(f, friendlyName);
                DsError.ThrowExceptionForHR(hr);
                return f;
            }
            return null;
        }

        private static IBaseFilter FindVideoRenderer(IGraphBuilder graph)
        {
            IEnumFilters ef;
            int hr = graph.EnumFilters(out ef);
            DsError.ThrowExceptionForHR(hr);
            try
            {
                IBaseFilter[] f = new IBaseFilter[1];
                while (ef.Next(1, f, IntPtr.Zero) == 0)
                {
                    if (IsVideoRenderer(f[0])) return f[0];
                    Marshal.ReleaseComObject(f[0]);
                }
            }
            finally
            {
                if (ef != null) Marshal.ReleaseComObject(ef);
            }
            return null;
        }

        private static bool IsVideoRenderer(IBaseFilter filter)
        {
            bool hasVideoInput = false, hasOutput = false;
            IEnumPins en;
            if (filter.EnumPins(out en) != 0 || en == null) return false;
            try
            {
                IPin[] pins = new IPin[1];
                while (en.Next(1, pins, IntPtr.Zero) == 0)
                {
                    PinDirection dir;
                    pins[0].QueryDirection(out dir);
                    if (dir == PinDirection.Output) hasOutput = true;
                    if (dir == PinDirection.Input)
                    {
                        IEnumMediaTypes emt;
                        if (pins[0].EnumMediaTypes(out emt) == 0 && emt != null)
                        {
                            try
                            {
                                AMMediaType[] mts = new AMMediaType[1];
                                while (emt.Next(1, mts, IntPtr.Zero) == 0)
                                {
                                    if (mts[0].majorType == MediaType.Video) hasVideoInput = true;
                                    DsUtils.FreeAMMediaType(mts[0]);
                                }
                            }
                            finally { Marshal.ReleaseComObject(emt); }
                        }
                    }
                    Marshal.ReleaseComObject(pins[0]);
                }
            }
            finally { Marshal.ReleaseComObject(en); }
            return hasVideoInput && !hasOutput;
        }

        private static IPin FindUnconnectedOrFirstPin(IBaseFilter filter, PinDirection dir)
        {
            IEnumPins en;
            int hr = filter.EnumPins(out en);
            DsError.ThrowExceptionForHR(hr);
            try
            {
                IPin first = null;
                IPin[] pins = new IPin[1];
                while (en.Next(1, pins, IntPtr.Zero) == 0)
                {
                    PinDirection pd;
                    pins[0].QueryDirection(out pd);
                    if (pd == dir)
                    {
                        if (first == null) first = pins[0];
                        IPin other;
                        if (pins[0].ConnectedTo(out other) != 0) return pins[0];
                        if (other != null) Marshal.ReleaseComObject(other);
                    }
                    Marshal.ReleaseComObject(pins[0]);
                }
                return first;
            }
            finally { Marshal.ReleaseComObject(en); }
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

        private static IPin FindFirstVideoOutputPin(IGraphBuilder graph)
        {
            IEnumFilters ef;
            int hr = graph.EnumFilters(out ef);
            DsError.ThrowExceptionForHR(hr);
            try
            {
                IBaseFilter[] f = new IBaseFilter[1];
                while (ef.Next(1, f, IntPtr.Zero) == 0)
                {
                    IPin p = FindPinOnFilterByMajorType(f[0], PinDirection.Output, MediaType.Video);
                    if (p != null) return p;
                    Marshal.ReleaseComObject(f[0]);
                }
            }
            finally { if (ef != null) Marshal.ReleaseComObject(ef); }
            return null;
        }

        private static IPin GetConnectedTo(IPin pin)
        {
            IPin other;
            if (pin.ConnectedTo(out other) == 0) return other;
            return null;
        }

        private static void RemoveFilter(IGraphBuilder graph, IBaseFilter filter)
        {
            int hr = graph.RemoveFilter(filter);
            DsError.ThrowExceptionForHR(hr);
            Marshal.ReleaseComObject(filter);
        }
    }
}
