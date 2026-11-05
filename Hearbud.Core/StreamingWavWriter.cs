// Phase 3/4 (v2 port, L3 "crash-safe by construction"): an owned WAV writer.
// It re-patches the header every 2 s over a WriteThrough stream, so the file
// on disk is a valid, playable WAV at ANY instant (a kill loses ≤2 s of tail,
// never the take). Formats: 16-bit integer PCM (tag 1) or 32-bit IEEE float
// (tag 3); container RIFF (default) or RF64 (Phase 4: 64-bit ds64 sizes, no
// 4 GB wall — the engine records RF64 everywhere).
// Threading: single writer thread only (the engine's mixer). Not thread-safe
// by design — no locks on the sample path.

using System;
using System.IO;
using System.Text;

namespace Hearbud
{
    public sealed class StreamingWavWriter : IDisposable
    {
        /// <summary>Header re-patch cadence in ms (crash loses at most this much tail).</summary>
        public const int PatchIntervalMs = 2000;
        private const int RiffHeaderSize = 44;
        private const int Rf64HeaderSize = 80;

        private readonly FileStream _fs;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly bool _rf64;
        private long _dataBytes;
        private long _lastPatchMs;
        private bool _disposed;

        /// <summary>Total PCM payload bytes written so far.</summary>
        public long DataBytes => _dataBytes;

        public StreamingWavWriter(string tmpPath, int sampleRate, int channels, int bitsPerSample)
            : this(tmpPath, sampleRate, channels, bitsPerSample, rf64: false)
        {
        }

        /// <param name="bitsPerSample">16 = integer PCM (tag 1); 32 = IEEE float (tag 3).</param>
        /// <param name="rf64">RF64 container with 64-bit ds64 sizes (no 4 GB wall).</param>
        public StreamingWavWriter(string tmpPath, int sampleRate, int channels, int bitsPerSample, bool rf64)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            if (bitsPerSample != 16 && bitsPerSample != 32)
                throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Only 16-bit PCM / 32-bit float.");
            _sampleRate = sampleRate;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _rf64 = rf64;
            _fs = new FileStream(tmpPath, FileMode.Create, FileAccess.ReadWrite,
                FileShare.Read, bufferSize: 1 << 16, FileOptions.WriteThrough);
            WriteHeader();
            _lastPatchMs = Environment.TickCount64;
        }

        /// <summary>Appends PCM little-endian bytes; re-patches the header every 2 s.</summary>
        public void Write(byte[] data, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StreamingWavWriter));
            _fs.Write(data, offset, count);
            _dataBytes += count;

            long now = Environment.TickCount64;
            if (now - _lastPatchMs >= PatchIntervalMs)
            {
                PatchHeader();
                _lastPatchMs = now;
            }
        }

        /// <summary>Rewrites the header in place from the current byte count + flushes.</summary>
        public void PatchHeader()
        {
            if (_disposed) return;
            long pos = _fs.Position;
            try
            {
                _fs.Seek(0, SeekOrigin.Begin);
                WriteHeader();
                _fs.Flush();
            }
            finally
            {
                _fs.Seek(pos, SeekOrigin.Begin);
            }
        }

        /// <summary>Final header patch + flush. Dispose() does the same; call either.</summary>
        public void FinalizeFile()
        {
            PatchHeader();
        }

        private void WriteHeader()
        {
            int blockAlign = _channels * _bitsPerSample / 8;
            int byteRate = _sampleRate * blockAlign;
            short formatTag = _bitsPerSample == 32 ? (short)3 : (short)1; // 3 = IEEE float
            using var w = new BinaryWriter(_fs, Encoding.ASCII, leaveOpen: true);
            if (_rf64)
            {
                // RF64: 80-byte header; real sizes live in ds64 (re-patched below).
                w.Write(Encoding.ASCII.GetBytes("RF64"));
                w.Write(uint.MaxValue);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("ds64"));
                w.Write(28); // ds64 chunk size
                w.Write(Rf64HeaderSize - 8 + _dataBytes); // riffSize64
                w.Write(_dataBytes);                      // dataSize64
                w.Write(_dataBytes / blockAlign);         // sampleCount64
                w.Write(0);                               // tableLength (no extra table)
            }
            else
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(ClampRiffSize(RiffHeaderSize - 8 + _dataBytes));
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
            }
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16); // fmt chunk size
            w.Write(formatTag);
            w.Write((short)_channels);
            w.Write(_sampleRate);
            w.Write(byteRate);
            w.Write((short)blockAlign);
            w.Write((short)_bitsPerSample);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(_rf64 ? uint.MaxValue : ClampRiffSize(_dataBytes));
        }

        // RIFF sizes are uint32: past ~4 GB they saturate (truncated but playable
        // prefix). The engine records RF64, so this only guards the RIFF fallback.
        // Tested via reflection (writing 4 GB is silly).
        private static uint ClampRiffSize(long bytes) =>
            bytes < 0 ? 0u : bytes > uint.MaxValue ? uint.MaxValue : (uint)bytes;

        public void Dispose()
        {
            if (_disposed) return;
            try { PatchHeader(); } catch { /* best effort on a dying process */ }
            _disposed = true;
            try { _fs.Dispose(); } catch { }
        }
    }
}
