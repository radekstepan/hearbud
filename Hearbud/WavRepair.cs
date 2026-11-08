using System;
using System.Collections.Generic;
using System.IO;

namespace Hearbud
{
    /// <summary>
    /// GAP-09 #8: startup scan for unfinalized WAVs. A crash/abort between
    /// WaveWriter creation and Dispose leaves placeholder RIFF/data sizes
    /// (the engine only rewrites them on Dispose — see FinalizeWavFiles).
    /// Repair rewrites both size fields in place from the actual file length.
    /// All IO is best-effort. No new packages.
    /// </summary>
    public static class WavRepair
    {
        /// <summary>True when the file parses as a RIFF WAV with a non-empty, in-bounds data chunk.</summary>
        public static bool IsValidWav(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 44) return false;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return IsValidWavStream(fs);
            }
            catch { return false; }
        }

        /// <summary>Lists *.wav files in <paramref name="dir"/> (top level) that fail <see cref="IsValidWav"/>.</summary>
        public static List<string> ScanDirectory(string? dir)
        {
            var suspects = new List<string>();
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return suspects;
                foreach (var f in Directory.EnumerateFiles(dir, "*.wav", SearchOption.TopDirectoryOnly))
                {
                    try { if (!IsValidWav(f)) suspects.Add(f); }
                    catch { suspects.Add(f); }
                }
            }
            catch { /* best-effort */ }
            return suspects;
        }

        /// <summary>
        /// Fixes RIFF chunk size (fileLen-8) and data chunk size from the actual
        /// file length. Returns false when the file has no RIFF/WAVE markers,
        /// no data chunk, or is otherwise unrepairable (caller may offer delete).
        /// </summary>
        public static bool TryRepair(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                long len = fs.Length;
                if (len <= 44 || len - 8 > int.MaxValue) return false;
                if (!HasMarkers(fs)) return false;
                long? dataSizePos = FindDataSizePos(fs, len);
                if (dataSizePos == null) return false;
                WriteInt32LE(fs, 4, (int)(len - 8));
                WriteInt32LE(fs, dataSizePos.Value, (int)(len - (dataSizePos.Value + 4)));
                fs.Flush();
                fs.Seek(0, SeekOrigin.Begin);
                return IsValidWavStream(fs);
            }
            catch { return false; }
        }

        private static bool IsValidWavStream(FileStream fs)
        {
            long len = fs.Length;
            if (len <= 44) return false;
            if (!HasMarkers(fs)) return false;
            long? dataSizePos = FindDataSizePos(fs, len);
            if (dataSizePos == null) return false;
            fs.Seek(dataSizePos.Value, SeekOrigin.Begin);
            long dataSize = ReadUInt32LE(fs);
            long dataStart = dataSizePos.Value + 4;
            if (dataSize == 0 || dataStart + dataSize > len) return false;
            fs.Seek(4, SeekOrigin.Begin);
            long riffSize = ReadUInt32LE(fs);
            return riffSize >= 36 && riffSize <= len;
        }

        private static bool HasMarkers(FileStream fs)
        {
            fs.Seek(0, SeekOrigin.Begin);
            Span<byte> head = stackalloc byte[12];
            if (fs.Read(head) < 12) return false;
            return head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E';
        }

        /// <returns>File offset of the data chunk's size field, or null when absent/malformed.</returns>
        private static long? FindDataSizePos(FileStream fs, long len)
        {
            long pos = 12;
            Span<byte> chunk = stackalloc byte[8];
            for (int i = 0; i < 10000 && pos + 8 <= len; i++)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                if (fs.Read(chunk) < 8) return null;
                long size = (uint)(chunk[4] | (chunk[5] << 8) | (chunk[6] << 16) | (chunk[7] << 24));
                if (chunk[0] == 'd' && chunk[1] == 'a' && chunk[2] == 't' && chunk[3] == 'a')
                    return pos + 4;
                if (size < 0 || pos + 8 + size > len) return null;
                pos += 8 + size + (size & 1); // chunks are word-aligned
            }
            return null;
        }

        private static long ReadUInt32LE(FileStream fs)
        {
            Span<byte> b = stackalloc byte[4];
            if (fs.Read(b) < 4) return -1;
            return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        private static void WriteInt32LE(FileStream fs, long pos, int value)
        {
            fs.Seek(pos, SeekOrigin.Begin);
            Span<byte> b = stackalloc byte[4];
            b[0] = (byte)value; b[1] = (byte)(value >> 8);
            b[2] = (byte)(value >> 16); b[3] = (byte)(value >> 24);
            fs.Write(b);
        }
    }
}
