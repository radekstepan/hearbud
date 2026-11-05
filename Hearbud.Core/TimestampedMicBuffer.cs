// Phase 2 (v2 port, L2 "timestamps, not chunk counts"): timestamped mic buffer
// with a drift-compensating read rate (PLL). Replaces the hand-rolled ring +
// "clear on silence" guillotine.
//
// Model: the mic leg is written with arrival QPC stamps; the loopback leg reads
// exactly N frames per block from (loopNow - TargetLatency). Three mechanisms:
//   1. Horizon: on every read, mic data older than MaxLatency is dropped by
//      TIMESTAMP (no mode branches, no stale-mic blast when loopback resumes).
//   2. PLL: buffer depth is servoed toward TargetLatency by bending the read
//      rate in [0.999, 1.001]. Independent crystals drift ~±50 ppm; the servo
//      absorbs it instead of letting the mix creep apart.
//   3. Fractional cursor: the ratio is applied through linear interpolation
//      with sub-frame position carried across blocks (no per-block rounding
//      jitter). Swap the inner lerp for WdlResampler in Phase 4 (NAudio).
//
// Threading: Write runs on the mic callback thread, Read on the loopback
// thread; a single short lock guards both (same pattern as the old _micRingLock
// — held for memcpy-scale work only, never I/O).

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;

namespace Hearbud
{
    public sealed class TimestampedMicBuffer
    {
        /// <summary>Mic depth the PLL servos toward, in ms (blueprint: 120).</summary>
        public const double TargetLatencyMs = 120.0;
        /// <summary>Mic older than this (vs the loop block) is dropped, in ms.</summary>
        public const double MaxLatencyMs = 1000.0;
        public const double MinRatio = 0.999;
        public const double MaxRatio = 1.001;
        private const double FilterAlpha = 0.002;  // heavy low-pass: drift is slow
        private const double ServoGain = 0.00002;  // ratio nudge per ms of error
        private const double StartCapacitySec = 4.0;
        private const double MaxCapacitySec = 30.0;

        private sealed class Segment
        {
            public double StartQpc;
            public int Frames;
        }

        private readonly int _rate;
        private readonly int _ch;
        private readonly double _qpcFreq;
        private readonly int _maxFloats;
        private readonly object _lock = new();
        private float[] _buf;
        private int _r, _w, _count; // circular buffer, counts in FLOATS
        private readonly Queue<Segment> _segs = new();
        private double _filteredErrMs;
        private double _cursor; // sub-frame read position carried across reads

        public TimestampedMicBuffer(int sampleRate, int channels)
            : this(sampleRate, channels, Stopwatch.Frequency)
        {
        }

        /// <param name="qpcFrequency">
        /// Ticks per second of the QPC stamps passed to <see cref="Write"/>/
        /// <see cref="Read"/>. Stopwatch domain by default; pass 10_000_000 for
        /// NAudio's 100-ns capture stamps (Phase 4) with zero conversion.
        /// </param>
        public TimestampedMicBuffer(int sampleRate, int channels, double qpcFrequency)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            if (qpcFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(qpcFrequency));
            _rate = sampleRate;
            _ch = channels;
            _qpcFreq = qpcFrequency;
            _buf = new float[(int)(StartCapacitySec * sampleRate * channels)];
            _maxFloats = (int)(MaxCapacitySec * sampleRate * channels);
        }

        /// <summary>Current PLL read-rate multiplier (1.0 = locked).</summary>
        public double Ratio { get; private set; } = 1.0;

        /// <summary>Buffered mic depth in seconds (0 when empty).</summary>
        public double DepthSec
        {
            get { lock (_lock) { return (double)(_count / _ch) / _rate; } }
        }

        public int AvailableFrames
        {
            get { lock (_lock) { return _count / _ch; } }
        }

        /// <summary>Total frames dropped for exceeding max capacity (overflow, not horizon).</summary>
        public long OverflowFramesDropped { get; private set; }

        public void Clear()
        {
            lock (_lock)
            {
                _r = _w = _count = 0;
                _segs.Clear();
                _filteredErrMs = 0.0;
                _cursor = 0.0;
                Ratio = 1.0;
                OverflowFramesDropped = 0;
            }
        }

        /// <summary>Appends converted mic floats stamped with arrival QPC.</summary>
        public void Write(float[] src, int floatCount, long qpcTick)
        {
            int frames = floatCount / _ch;
            if (frames <= 0) return;
            lock (_lock)
            {
                int need = frames * _ch;
                while (_buf.Length - _count < need && _buf.Length < _maxFloats)
                    GrowLocked();
                if (_buf.Length - _count < need)
                    DropOldestFramesLocked((_count + need - _buf.Length + _ch - 1) / _ch, countOverflow: true);
                for (int i = 0; i < need; i++)
                {
                    _buf[_w] = src[i];
                    _w = (_w + 1) % _buf.Length;
                }
                _count += need;
                _segs.Enqueue(new Segment { StartQpc = qpcTick, Frames = frames });
            }
        }

        /// <summary>
        /// Reads exactly <paramref name="frames"/> frames aligned to
        /// (<paramref name="loopQpcNow"/> − <see cref="TargetLatencyMs"/>).
        /// Shortfalls are zero-filled and reported via <paramref name="zeroFilledFrames"/>.
        /// </summary>
        public void Read(float[] dst, int dstOffset, int frames, long loopQpcNow, out int zeroFilledFrames)
        {
            if (frames <= 0) throw new ArgumentOutOfRangeException(nameof(frames));
            double qpcPerFrame = _qpcFreq / _rate;
            lock (_lock)
            {
                // 1. Horizon: drop mic older than MaxLatency vs this loop block.
                double horizon = loopQpcNow - MaxLatencyMs / 1000.0 * _qpcFreq;
                while (_segs.Count > 0)
                {
                    var s = _segs.Peek();
                    if (s.StartQpc >= horizon) break;
                    double endQpc = s.StartQpc + s.Frames * qpcPerFrame;
                    if (endQpc <= horizon)
                    {
                        DropOldestFramesLocked(s.Frames, countOverflow: false);
                    }
                    else
                    {
                        int stale = (int)((horizon - s.StartQpc) / qpcPerFrame);
                        if (stale <= 0) break;
                        DropOldestFramesLocked(stale, countOverflow: false);
                        break;
                    }
                }

                // 2. PLL: bend the read rate to hold target depth.
                double depthSec = (double)(_count / _ch) / _rate;
                double errMs = depthSec * 1000.0 - TargetLatencyMs;
                _filteredErrMs += (errMs - _filteredErrMs) * FilterAlpha;
                Ratio = Math.Clamp(1.0 + _filteredErrMs * ServoGain, MinRatio, MaxRatio);

                // 3. Resampled pull with fractional cursor (linear interp inside;
                // Wdl replaces it in Phase 4 — the ratio servo stays as-is).
                int availFrames = _count / _ch;
                int need = (int)Math.Ceiling(_cursor + frames * Ratio) + 1;
                int take = Math.Min(need, availFrames);
                float[] scratch = ArrayPool<float>.Shared.Rent(Math.Max(take * _ch, 1));
                try
                {
                    for (int i = 0; i < take * _ch; i++)
                        scratch[i] = _buf[(_r + i) % _buf.Length];

                    zeroFilledFrames = 0;
                    double advance = 0.0; // source frames consumed by this read
                    for (int f = 0; f < frames; f++)
                    {
                        double pos = _cursor + advance;
                        int i0 = (int)pos;
                        double t = pos - i0;
                        if (i0 + 1 >= take)
                        {
                            for (int g = f; g < frames; g++)
                                for (int c = 0; c < _ch; c++)
                                    dst[dstOffset + g * _ch + c] = 0f;
                            zeroFilledFrames = frames - f;
                            break;
                        }
                        for (int c = 0; c < _ch; c++)
                        {
                            float s0 = scratch[i0 * _ch + c];
                            float s1 = scratch[(i0 + 1) * _ch + c];
                            dst[dstOffset + f * _ch + c] = s0 + (float)((s1 - s0) * t);
                        }
                        advance += Ratio;
                    }

                    _cursor += advance;
                    int consume = (int)_cursor;
                    _cursor -= consume;
                    DropOldestFramesLocked(consume, countOverflow: false);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(scratch);
                }
            }
        }

        private void GrowLocked()
        {
            int newLen = Math.Min(_maxFloats, Math.Max(_buf.Length * 2, _count + 1));
            if (newLen <= _buf.Length) return;
            var grown = new float[newLen];
            for (int i = 0; i < _count; i++)
                grown[i] = _buf[(_r + i) % _buf.Length];
            _buf = grown;
            _r = 0;
            _w = _count;
        }

        private void DropOldestFramesLocked(int frames, bool countOverflow)
        {
            if (frames <= 0) return;
            double qpcPerFrame = _qpcFreq / _rate;
            int floats = Math.Min(frames * _ch, _count);
            frames = floats / _ch;
            _r = (_r + floats) % _buf.Length;
            _count -= floats;
            int left = frames;
            while (left > 0 && _segs.Count > 0)
            {
                var s = _segs.Peek();
                if (s.Frames <= left)
                {
                    left -= s.Frames;
                    _segs.Dequeue();
                }
                else
                {
                    s.StartQpc += left * qpcPerFrame;
                    s.Frames -= left;
                    left = 0;
                }
            }
            if (countOverflow) OverflowFramesDropped += frames;
        }
    }
}
