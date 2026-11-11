using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Hearbud
{
    /// <summary>
    /// Offline take scanner: detects block-splice damage in a recorded mix file.
    /// Pure file/struct IO — never touches audio devices. Shared by the
    /// `verify-take` CLI verb and the pair-by-count regression tests.
    ///
    /// Splice rule (from rec-20261111_003933 forensics):
    /// - exact-zero runs of [32, 48000) floats with audio on both sides are
    ///   zero-fill pads (>= 48000 is pause/silence, not a pad);
    /// - jump runs (>0.05 inter-sample step) starting on a 480-frame boundary
    ///   are frame-edge splices. Chance alignment is ~0.1%, so >= 5 boundary
    ///   runs at >= 1% of >= 50 total runs fails the take.
    /// </summary>
    public static class TakeVerifier
    {
        public const double JumpThreshold = 0.05;
        public const int FramesPerQuantum = 480;
        public const int MinPadZeros = 32;
        public const int MaxPadZeros = 48000;
        public const int SideWindowFloats = 4800;
        public const double SideAudioFloor = 1e-4;
        public const int MinRunsForBoundaryRule = 50;
        public const int MinBoundaryRunsFail = 5;
        public const double BoundaryFractionFail = 0.01;

        public sealed class PadInfo
        {
            public long OffsetFloats { get; set; }
            public int LengthFloats { get; set; }
            public double PrePeak { get; set; }
            public double PostPeak { get; set; }
            public double TimeSec(int channels, int rate) => OffsetFloats / ((double)channels * rate);
        }

        public sealed class ScanResult
        {
            public string File { get; set; } = "";
            public int Channels { get; set; }
            public int SampleRate { get; set; }
            public long Frames { get; set; }
            public double Peak { get; set; }
            public double Rms { get; set; }
            public int JumpRuns { get; set; }
            public int BoundaryRuns { get; set; }
            public List<PadInfo> PadRuns { get; } = new();
            public List<long> SilenceRuns { get; } = new();
            public bool HasSplice { get; set; }
            public string Report { get; set; } = "";
        }

        public static bool IsSplice(int jumpRuns, int boundaryRuns, int padCount)
        {
            if (padCount > 0) return true;
            if (jumpRuns < MinRunsForBoundaryRule) return false;
            if (boundaryRuns < MinBoundaryRunsFail) return false;
            return boundaryRuns / (double)jumpRuns >= BoundaryFractionFail;
        }

        public static ScanResult Scan(float[] samples, int channels, int sampleRate, string file = "")
        {
            var r = new ScanResult { File = file, Channels = channels, SampleRate = sampleRate };
            if (channels <= 0 || sampleRate <= 0 || samples.Length == 0) { r.Report = "empty input"; return r; }
            long frameFloats = (long)FramesPerQuantum * channels;
            double peak = 0, sumSq = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double a = Math.Abs(samples[i]);
                if (a > peak) peak = a;
                sumSq += (double)samples[i] * samples[i];
            }
            r.Peak = peak;
            r.Rms = Math.Sqrt(sumSq / samples.Length);
            r.Frames = samples.Length / channels;

            // Jump runs: consecutive inter-sample steps over threshold.
            int jumpRuns = 0, boundaryRuns = 0;
            bool inRun = false;
            for (int i = 1; i < samples.Length; i++)
            {
                bool jump = Math.Abs(samples[i] - samples[i - 1]) > JumpThreshold;
                if (jump && !inRun)
                {
                    jumpRuns++;
                    if (i % frameFloats == 0) boundaryRuns++;
                    inRun = true;
                }
                else if (!jump) inRun = false;
            }
            r.JumpRuns = jumpRuns;
            r.BoundaryRuns = boundaryRuns;

            // Exact-zero runs: pads (short, embedded in audio) vs silence.
            int z = 0;
            for (int i = 0; i <= samples.Length; i++)
            {
                bool isZero = i < samples.Length && samples[i] == 0f;
                if (isZero) { z++; continue; }
                if (z > 0)
                {
                    long start = i - z;
                    if (z >= MaxPadZeros) r.SilenceRuns.Add(start);
                    else if (z >= MinPadZeros)
                    {
                        double pre = SidePeak(samples, start - SideWindowFloats, SideWindowFloats);
                        double post = SidePeak(samples, start + z, SideWindowFloats);
                        if (pre > SideAudioFloor && post > SideAudioFloor)
                            r.PadRuns.Add(new PadInfo { OffsetFloats = start, LengthFloats = z, PrePeak = pre, PostPeak = post });
                    }
                    z = 0;
                }
            }

            r.HasSplice = IsSplice(jumpRuns, boundaryRuns, r.PadRuns.Count);
            var sb = new StringBuilder();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            sb.AppendLine(ci, $"file: {(file.Length > 0 ? file : "(buffer)")}");
            sb.AppendLine(ci, $"rate={sampleRate} ch={channels} frames={r.Frames} dur={r.Frames / (double)sampleRate:F2}s");
            sb.AppendLine(ci, $"peak={r.Peak:F4} ({Dbfs.ToDbfs(r.Peak):F1} dBFS) rms={r.Rms:F5}");
            sb.AppendLine(ci, $"jumpRuns={jumpRuns} boundaryRuns={boundaryRuns} ({(jumpRuns > 0 ? 100.0 * boundaryRuns / jumpRuns : 0):F1}% on 480-frame edge)");
            sb.AppendLine(ci, $"padRuns(32..48000 zeros, audio both sides)={r.PadRuns.Count}");
            foreach (var p in r.PadRuns)
                sb.AppendLine(ci, $"  pad @ {p.TimeSec(channels, sampleRate):F3}s len={p.LengthFloats / channels} frames pre={Dbfs.ToDbfs(p.PrePeak):F1} dBFS post={Dbfs.ToDbfs(p.PostPeak):F1} dBFS");
            sb.AppendLine(ci, $"silenceRuns(>=48000 zeros)={r.SilenceRuns.Count}");
            sb.AppendLine(r.HasSplice ? "VERDICT: SPLICE (non-zero exit)" : "VERDICT: CLEAN");
            r.Report = sb.ToString();
            return r;
        }

        private static double SidePeak(float[] s, long from, int count)
        {
            double m = 0;
            long end = Math.Min(s.Length, from + count);
            for (long i = Math.Max(0, from); i < end; i++)
            {
                double a = Math.Abs(s[i]);
                if (a > m) m = a;
            }
            return m;
        }

        // ---- WAV IO (PCM16 / PCM32-int / IEEE-float32, any channel count) ----

        public static float[] ReadWavSamples(string path, out int channels, out int sampleRate)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            string ChunkId(BinaryReader r) => new string(r.ReadChars(4));
            if (ChunkId(br) != "RIFF") throw new InvalidDataException("not a RIFF/WAV file");
            br.ReadInt32(); // file size
            if (ChunkId(br) != "WAVE") throw new InvalidDataException("not a WAVE file");
            int fmtTag = -1, ch = 0, rate = 0, bits = 0;
            long dataPos = -1;
            int dataLen = 0;
            while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
            {
                string id = ChunkId(br);
                int len = br.ReadInt32();
                long next = br.BaseStream.Position + len;
                if (id == "fmt ")
                {
                    fmtTag = br.ReadUInt16();
                    ch = br.ReadUInt16();
                    rate = br.ReadInt32();
                    br.ReadInt32(); // byte rate
                    br.ReadUInt16(); // block align
                    bits = br.ReadUInt16();
                }
                else if (id == "data")
                {
                    dataPos = br.BaseStream.Position;
                    dataLen = len;
                    break;
                }
                br.BaseStream.Position = next;
            }
            if (dataPos < 0) throw new InvalidDataException("WAV has no data chunk");
            if (ch <= 0 || rate <= 0) throw new InvalidDataException("WAV has no fmt chunk");
            br.BaseStream.Position = dataPos;
            byte[] raw = br.ReadBytes(dataLen);
            float[] out_;
            if (fmtTag == 1 && bits == 16)
            {
                int n = raw.Length / 2;
                out_ = new float[n];
                for (int i = 0; i < n; i++)
                    out_[i] = (short)(raw[2 * i] | (raw[2 * i + 1] << 8)) / 32768f;
            }
            else if (fmtTag == 1 && bits == 32)
            {
                int n = raw.Length / 4;
                out_ = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int v = raw[4 * i] | (raw[4 * i + 1] << 8) | (raw[4 * i + 2] << 16) | (raw[4 * i + 3] << 24);
                    out_[i] = (float)(v / 2147483648.0);
                }
            }
            else if (fmtTag == 3 && bits == 32)
            {
                int n = raw.Length / 4;
                out_ = new float[n];
                Buffer.BlockCopy(raw, 0, out_, 0, n * 4);
            }
            else throw new InvalidDataException($"unsupported WAV format tag={fmtTag} bits={bits}");
            channels = ch;
            sampleRate = rate;
            return out_;
        }

        /// <summary>
        /// `verify-take &lt;dir-or-wav&gt;`: scan the take's mix file, print the
        /// report, return the process exit code (0 clean, 1 splice, 2 usage/IO).
        /// Offline: pure file IO, never touches audio devices.
        /// </summary>
        public static int VerifyTake(string[] args, TextWriter output)
        {
            string? target = args.Length > 0 ? args[0] : null;
            if (string.IsNullOrWhiteSpace(target) || target == "-h" || target == "--help")
            {
                output.WriteLine("usage: hearbud verify-take <recordings-dir | mix.wav>");
                output.WriteLine("Scans the take mix file for block splices (pads, frame-edge jumps).");
                output.WriteLine("Exit codes: 0 clean, 1 splice, 2 usage or file error. Offline only.");
                return target == null ? 2 : 0;
            }
            string mixPath;
            if (Directory.Exists(target))
            {
                var mixes = Directory.GetFiles(target, "*-mix.wav");
                if (mixes.Length == 0) { output.WriteLine($"verify-take: no *-mix.wav in {target}"); return 2; }
                if (mixes.Length > 1)
                {
                    output.WriteLine($"verify-take: {mixes.Length} mix files in {target}, pass one file explicitly:");
                    foreach (var m in mixes) output.WriteLine($"  {m}");
                    return 2;
                }
                mixPath = mixes[0];
            }
            else if (File.Exists(target)) mixPath = target;
            else { output.WriteLine($"verify-take: not found: {target}"); return 2; }
            float[] samples;
            int ch, rate;
            try { samples = ReadWavSamples(mixPath, out ch, out rate); }
            catch (Exception ex) { output.WriteLine($"verify-take: cannot read {mixPath}: {ex.Message}"); return 2; }
            var r = Scan(samples, ch, rate, mixPath);
            output.Write(r.Report);
            foreach (var line in SessionLogContext(mixPath))
                output.WriteLine(line);
            return r.HasSplice ? 1 : 0;
        }

        /// <summary>
        /// Informational only (never affects the verdict): if a session log
        /// sits next to the mix file, surface starvation/resync lines so the
        /// next SPLICE reads as device-starvation vs engine damage.
        /// </summary>
        private static List<string> SessionLogContext(string mixPath)
        {
            var out_ = new List<string>();
            string? logPath = null;
            try
            {
                const string suffix = "-mix.wav";
                string name = Path.GetFileName(mixPath);
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    logPath = Path.Combine(Path.GetDirectoryName(mixPath) ?? "",
                        string.Concat(name.AsSpan(0, name.Length - suffix.Length), ".txt"));
            }
            catch { return out_; }
            if (logPath == null || !File.Exists(logPath)) return out_;
            List<string> hits = new();
            try
            {
                foreach (var line in File.ReadLines(logPath))
                {
                    string l = line.ToLowerInvariant();
                    if (l.Contains("starvation") || l.Contains("resync #") || l.Contains("lagwatch=lag"))
                    {
                        hits.Add(line.Length > 220 ? line.Substring(line.Length - 220) : line);
                        if (hits.Count >= 5) break;
                    }
                }
            }
            catch { return out_; }
            if (hits.Count == 0) return out_;
            out_.Add("session-log context (informational, verdict unaffected):");
            foreach (var h in hits) out_.Add("  " + h.Trim());
            return out_;
        }
    }
}
