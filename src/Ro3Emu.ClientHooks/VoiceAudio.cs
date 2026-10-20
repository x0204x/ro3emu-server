using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Voice audio through the Windows waveIn / waveOut API (winmm, default devices): this build has Unity audio disabled
    /// (AudioManager m_DisableAudio) and UnityEngine.Microphone stripped. 16 kHz mono 16-bit, 20 ms frames (320 samples);
    /// the wave mapper converts from and to the device format. Capture and playback each run on their own thread, woken by
    /// the driver's buffer-done event; nothing here touches Unity or Lua apart from Debug.Log.
    /// </summary>
    public static class VoiceAudio
    {
        public const int SampleRate = 16000;
        public const int FrameSamples = 320;

        /// <summary>Gains from the client's volume sliders (GME scale: 100 = unchanged); set from the main thread.</summary>
        public static volatile float MicGain = 1f, SpeakerGain = 1f;

        /// <summary>The game's Master volume (0..1, 0 when muted or minimized with "Mute When Minimized"; VoiceSettings).</summary>
        public static volatile float OutputScale = 1f;

        /// <summary>Counters for the 10 s stats line (VoiceClient): a speaker's buffer ran dry while playing / overflowed.</summary>
        public static int Underruns, Overflows;

        /// <summary>The longest a capture or playback thread sleeps without a buffer-done signal (a lost signal costs at most this).</summary>
        private const int WakeMs = 10;

        private static Capture _capture;
        private static Playback _playback;

        /// <summary>Starts the microphone; <paramref name="onFrame"/> gets every 20 ms frame on the capture thread.</summary>
        public static void StartCapture(Action<short[]> onFrame)
        {
            if (_capture != null) return;
            _capture = Capture.Open(onFrame);
        }

        public static void StopCapture()
        {
            var capture = _capture;
            _capture = null;
            capture?.Close();
        }

        public static void StartPlayback()
        {
            if (_playback != null) return;
            _playback = Playback.Open();
        }

        public static void StopPlayback()
        {
            var playback = _playback;
            _playback = null;
            playback?.Close();
        }

        /// <summary>A received frame of one speaker (any thread); dropped while playback is off.</summary>
        public static void Push(long speaker, short[] pcm) => _playback?.Mixer.Push(speaker, pcm);

        /// <summary>Drops what is buffered (speaker muted).</summary>
        public static void Flush() => _playback?.Mixer.Clear();

        /// <summary>Drops one speaker's buffer (it left the room).</summary>
        public static void Forget(long speaker) => _playback?.Mixer.Remove(speaker);

        /// <summary>
        /// Automatic gain for the microphone (capture thread only): many PC mics deliver speech far below full scale
        /// (peaks of a few percent), which plays back too quiet to follow. The gain rises slowly towards a level where
        /// speech peaks reach <see cref="Target"/>, at most <see cref="MaxGain"/>, falls at once when a frame would clip,
        /// and does not rise on frames quieter than <see cref="Gate"/> (background noise), which are also turned down.
        /// </summary>
        internal static class Agc
        {
            private const float Target = 12000f;
            private const float MaxGain = 12f;     // about +22 dB
            private const int Gate = 80;           // raw peak below this counts as silence
            private const float GateLevel = 0.3f;  // silence frames are played at this share of the gain
            private const float Release = 0.97f;   // the peak envelope halves in about 0.5 s
            private const float Rise = 0.05f;      // per frame, towards the target gain

            private static float _gain = 1f, _envelope;

            public static float Gain => _gain;

            /// <summary>Scales <paramref name="pcm"/> in place; returns the frame's raw peak (before the gain).</summary>
            public static int Process(short[] pcm)
            {
                int peak = 0;
                foreach (short v in pcm) peak = Math.Max(peak, Math.Abs((int)v));
                _envelope = Math.Max(peak, _envelope * Release);
                if (_envelope >= Gate)
                {
                    float target = Math.Max(1f, Math.Min(MaxGain, Target / _envelope));
                    _gain = target < _gain ? target : _gain + (target - _gain) * Rise;
                }
                if (peak > 0 && peak * _gain > 30000f) _gain = 30000f / peak; // never clip
                float applied = peak < Gate ? _gain * GateLevel : _gain;
                if (Math.Abs(applied - 1f) < 0.01f) return peak;
                for (int i = 0; i < pcm.Length; i++) pcm[i] = Clamp((int)(pcm[i] * applied));
                return peak;
            }
        }

        internal static short Clamp(int sample) => (short)(sample > short.MaxValue ? short.MaxValue : sample < short.MinValue ? short.MinValue : sample);

        /// <summary>Per-speaker jitter buffers mixed into one output frame.</summary>
        internal sealed class Mixer
        {
            private const int StartFrames = 2;   // a speaker plays once this many frames wait (40 ms)
            private const int MaxFrames = 8;     // beyond this the oldest frame goes (latency cap 160 ms)
            private const int MaxSpeakers = 40;  // a full room; ids past this are ignored, whatever the relay sends

            private sealed class Speaker
            {
                public readonly Queue<short[]> Frames = new Queue<short[]>();
                public bool Playing;
            }

            private readonly Dictionary<long, Speaker> _speakers = new Dictionary<long, Speaker>();
            private readonly int[] _sum = new int[FrameSamples];

            public void Push(long id, short[] pcm)
            {
                if (pcm == null || pcm.Length != FrameSamples) return;
                lock (_speakers)
                {
                    if (!_speakers.TryGetValue(id, out var speaker))
                    {
                        if (_speakers.Count >= MaxSpeakers) return;
                        _speakers[id] = speaker = new Speaker();
                    }
                    speaker.Frames.Enqueue(pcm);
                    while (speaker.Frames.Count > MaxFrames) { speaker.Frames.Dequeue(); Interlocked.Increment(ref Overflows); }
                }
            }

            public void Clear()
            {
                lock (_speakers) _speakers.Clear();
            }

            public void Remove(long id)
            {
                lock (_speakers) _speakers.Remove(id);
            }

            /// <summary>The next 20 ms of output; silence when nobody is due.</summary>
            public void Mix(short[] output)
            {
                Array.Clear(_sum, 0, _sum.Length);
                float gain = SpeakerGain * OutputScale;
                lock (_speakers)
                {
                    foreach (var speaker in _speakers.Values)
                    {
                        if (!speaker.Playing && speaker.Frames.Count >= StartFrames) speaker.Playing = true;
                        if (!speaker.Playing) continue;
                        if (speaker.Frames.Count == 0) { speaker.Playing = false; Interlocked.Increment(ref Underruns); continue; } // ran dry: buffer again
                        var frame = speaker.Frames.Dequeue();
                        for (int i = 0; i < FrameSamples; i++) _sum[i] += frame[i];
                    }
                }
                for (int i = 0; i < FrameSamples; i++) output[i] = Clamp((int)(_sum[i] * gain));
            }
        }

        private sealed class Capture
        {
            private const int Buffers = 6;

            private readonly Action<short[]> _onFrame;
            private readonly IntPtr _done = Winmm.CreateEventW(IntPtr.Zero, false, false, null);
            private readonly WaveBuffers _buffers = new WaveBuffers(Buffers, FrameSamples * 2);
            private IntPtr _device;
            private volatile bool _running;
            private Thread _thread;

            private Capture(Action<short[]> onFrame) { _onFrame = onFrame; }

            public static Capture Open(Action<short[]> onFrame)
            {
                var capture = new Capture(onFrame);
                var format = WaveFormat.Pcm16Mono(SampleRate);
                int result = Winmm.waveInOpen(out capture._device, Winmm.WaveMapper, ref format, capture._done, IntPtr.Zero, Winmm.CallbackEvent);
                if (result != 0)
                {
                    Debug.LogWarning("[Ro3Emu.Voice] no microphone (waveInOpen " + result + ")");
                    capture._buffers.Free();
                    Winmm.CloseHandle(capture._done);
                    return null;
                }
                for (int i = 0; i < Buffers; i++)
                {
                    result = Winmm.waveInPrepareHeader(capture._device, capture._buffers.Header(i), WaveBuffers.HeaderSize);
                    if (result == 0) result = Winmm.waveInAddBuffer(capture._device, capture._buffers.Header(i), WaveBuffers.HeaderSize);
                    if (result == 0) continue;
                    Debug.LogWarning("[Ro3Emu.Voice] microphone buffers refused (" + result + ")");
                    capture.Release();
                    return null;
                }
                capture._running = true;
                capture._thread = new Thread(capture.Run) { IsBackground = true, Name = "Ro3Emu voice capture" };
                capture._thread.Start();
                Winmm.waveInStart(capture._device);
                Debug.Log("[Ro3Emu.Voice] microphone on");
                return capture;
            }

            private void Run()
            {
                var bytes = new byte[FrameSamples * 2];
                while (_running)
                {
                    Winmm.WaitForSingleObject(_done, WakeMs);
                    for (int i = 0; i < Buffers && _running; i++)
                    {
                        var header = _buffers.Header(i);
                        if (!WaveBuffers.IsDone(header)) continue;
                        int recorded = WaveBuffers.BytesRecorded(header);
                        if (recorded == bytes.Length)
                        {
                            Marshal.Copy(_buffers.Data(i), bytes, 0, recorded);
                            var pcm = new short[FrameSamples];
                            float gain = MicGain;
                            for (int s = 0; s < FrameSamples; s++) pcm[s] = Clamp((int)((short)(bytes[2 * s] | (bytes[2 * s + 1] << 8)) * gain));
                            if (_running)
                                try { _onFrame(pcm); }
                                catch (Exception e) { Debug.LogWarning("[Ro3Emu.Voice] capture: " + e.Message); }
                        }
                        WaveBuffers.ClearDone(header);
                        if (_running) Winmm.waveInAddBuffer(_device, header, WaveBuffers.HeaderSize);
                    }
                }
                Release(); // on this thread: the main thread never waits for the device
            }

            /// <summary>Stops capturing at once (no frame is delivered after this); the capture thread then frees the
            /// device. Never blocks the caller.</summary>
            public void Close()
            {
                _running = false;
                Winmm.SetEvent(_done);
            }

            private void Release()
            {
                Winmm.waveInStop(_device);
                Winmm.waveInReset(_device); // returns every queued buffer
                for (int i = 0; i < Buffers; i++) Winmm.waveInUnprepareHeader(_device, _buffers.Header(i), WaveBuffers.HeaderSize);
                Winmm.waveInClose(_device);
                _buffers.Free();
                Winmm.CloseHandle(_done);
                Debug.Log("[Ro3Emu.Voice] microphone off");
            }
        }

        private sealed class Playback
        {
            private const int Buffers = 4; // 80 ms queued at the device

            public readonly Mixer Mixer = new Mixer();
            private readonly IntPtr _done = Winmm.CreateEventW(IntPtr.Zero, false, false, null);
            private readonly WaveBuffers _buffers = new WaveBuffers(Buffers, FrameSamples * 2);
            private IntPtr _device;
            private volatile bool _running;
            private Thread _thread;

            public static Playback Open()
            {
                var playback = new Playback();
                var format = WaveFormat.Pcm16Mono(SampleRate);
                int result = Winmm.waveOutOpen(out playback._device, Winmm.WaveMapper, ref format, playback._done, IntPtr.Zero, Winmm.CallbackEvent);
                if (result != 0)
                {
                    Debug.LogWarning("[Ro3Emu.Voice] no audio output (waveOutOpen " + result + ")");
                    playback._buffers.Free();
                    Winmm.CloseHandle(playback._done);
                    return null;
                }
                for (int i = 0; i < Buffers; i++)
                {
                    result = Winmm.waveOutPrepareHeader(playback._device, playback._buffers.Header(i), WaveBuffers.HeaderSize);
                    if (result == 0) continue;
                    Debug.LogWarning("[Ro3Emu.Voice] audio output buffers refused (" + result + ")");
                    playback.Release();
                    return null;
                }
                playback._running = true;
                playback._thread = new Thread(playback.Run) { IsBackground = true, Name = "Ro3Emu voice playback" };
                playback._thread.Start();
                Debug.Log("[Ro3Emu.Voice] playback on");
                return playback;
            }

            private void Run()
            {
                var pcm = new short[FrameSamples];
                var bytes = new byte[FrameSamples * 2];
                for (int i = 0; i < Buffers; i++) Write(i, pcm, bytes); // silence to start the queue
                while (_running)
                {
                    Winmm.WaitForSingleObject(_done, WakeMs);
                    for (int i = 0; i < Buffers && _running; i++)
                    {
                        if (!WaveBuffers.IsDone(_buffers.Header(i))) continue;
                        Mixer.Mix(pcm);
                        Write(i, pcm, bytes);
                    }
                }
                Release(); // on this thread: the main thread never waits for the device
            }

            private void Write(int index, short[] pcm, byte[] bytes)
            {
                for (int s = 0; s < FrameSamples; s++)
                {
                    bytes[2 * s] = (byte)pcm[s];
                    bytes[2 * s + 1] = (byte)(pcm[s] >> 8);
                }
                Marshal.Copy(bytes, 0, _buffers.Data(index), bytes.Length);
                WaveBuffers.ClearDone(_buffers.Header(index));
                Winmm.waveOutWrite(_device, _buffers.Header(index), WaveBuffers.HeaderSize);
            }

            /// <summary>Stops playback; the playback thread then frees the device. Never blocks the caller.</summary>
            public void Close()
            {
                _running = false;
                Winmm.SetEvent(_done);
            }

            private void Release()
            {
                Winmm.waveOutReset(_device);
                for (int i = 0; i < Buffers; i++) Winmm.waveOutUnprepareHeader(_device, _buffers.Header(i), WaveBuffers.HeaderSize);
                Winmm.waveOutClose(_device);
                _buffers.Free();
                Winmm.CloseHandle(_done);
                Debug.Log("[Ro3Emu.Voice] playback off");
            }
        }

        /// <summary>WAVEHDRs and their data in unmanaged memory (the driver keeps the pointers while a buffer is queued).</summary>
        private sealed class WaveBuffers
        {
            public static readonly int HeaderSize = Marshal.SizeOf(typeof(WaveHeader));
            private static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(WaveHeader), "Flags");
            private static readonly int RecordedOffset = (int)Marshal.OffsetOf(typeof(WaveHeader), "BytesRecorded");
            private const int Done = 0x1; // WHDR_DONE

            private readonly IntPtr[] _headers;
            private readonly IntPtr[] _data;

            public WaveBuffers(int count, int bytes)
            {
                _headers = new IntPtr[count];
                _data = new IntPtr[count];
                for (int i = 0; i < count; i++)
                {
                    _data[i] = Marshal.AllocHGlobal(bytes);
                    _headers[i] = Marshal.AllocHGlobal(HeaderSize);
                    Marshal.StructureToPtr(new WaveHeader { Data = _data[i], BufferLength = (uint)bytes }, _headers[i], false);
                }
            }

            public IntPtr Header(int index) => _headers[index];
            public IntPtr Data(int index) => _data[index];

            public static bool IsDone(IntPtr header) => (Marshal.ReadInt32(header, FlagsOffset) & Done) != 0;
            public static void ClearDone(IntPtr header) => Marshal.WriteInt32(header, FlagsOffset, Marshal.ReadInt32(header, FlagsOffset) & ~Done);
            public static int BytesRecorded(IntPtr header) => Marshal.ReadInt32(header, RecordedOffset);

            public void Free()
            {
                for (int i = 0; i < _headers.Length; i++)
                {
                    if (_headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_headers[i]);
                    if (_data[i] != IntPtr.Zero) Marshal.FreeHGlobal(_data[i]);
                    _headers[i] = _data[i] = IntPtr.Zero;
                }
            }
        }

        // C# structs are sequential by default (this client's mscorlib has no StructLayoutAttribute constructor).
        // The driver writes the fields this code never assigns.
#pragma warning disable 0649
        private struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength;
            public uint BytesRecorded;
            public IntPtr User;
            public uint Flags;
            public uint Loops;
            public IntPtr Next;
            public IntPtr Reserved;
        }
#pragma warning restore 0649

        /// <summary>WAVEFORMATEX (18 bytes; the default layout only pads the end, which PCM ignores).</summary>
        private struct WaveFormat
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSec;
            public uint AvgBytesPerSec;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort Size;

            public static WaveFormat Pcm16Mono(int rate) => new WaveFormat
            {
                FormatTag = 1, // WAVE_FORMAT_PCM
                Channels = 1,
                SamplesPerSec = (uint)rate,
                AvgBytesPerSec = (uint)rate * 2,
                BlockAlign = 2,
                BitsPerSample = 16,
                Size = 0,
            };
        }

        private static class Winmm
        {
            public const int WaveMapper = -1;
            public const int CallbackEvent = 0x00050000;

            [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr device, int deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
            [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr device, IntPtr header, int size);
            [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr device, IntPtr header, int size);
            [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr device, IntPtr header, int size);
            [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr device);
            [DllImport("winmm.dll")] public static extern int waveInStop(IntPtr device);
            [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr device);
            [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr device);
            [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr device, int deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
            [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);
            [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);
            [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr device, IntPtr header, int size);
            [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr device);
            [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr device);

            // A plain Win32 event for CALLBACK_EVENT (the driver signals it per finished buffer).
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string name);
            [DllImport("kernel32.dll")] public static extern int WaitForSingleObject(IntPtr handle, int milliseconds);
            [DllImport("kernel32.dll")] public static extern bool SetEvent(IntPtr handle);
            [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        }
    }
}
