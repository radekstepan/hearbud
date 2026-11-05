// macOS MP3 backend: LAME via P/Invoke, float input straight from NAudio.
//
// LAME 3.100 is LGPL (https://sourceforge.net/projects/lame/files/lame/3.100/);
// MP3 patents expired in April 2017, so no royalties apply. The dylib stays a
// separate bundled binary (never linked in) to satisfy LGPL relinking; see
// tools/lame/build-lame.sh to regenerate it from the vendored tarball.
//
// Expected bundled-dylib layout (wired by Hearbud.Core.csproj — this file
// only resolves it at runtime):
//
//   <app-dir>/
//     Hearbud.Core.dll
//     runtimes/
//       osx-arm64/native/libmp3lame.dylib   <- thin arm64 slice
//       osx-x64/native/libmp3lame.dylib     <- thin x64 slice
//
// That is the standard .NET RuntimeTargets layout: the csproj ships each
// architecture's dylib under runtimes/<rid>/native/ and the resolver below
// picks the slice matching the process architecture. System-wide
// libmp3lame (e.g. Homebrew) is used only as a last-resort fallback.

using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Hearbud
{
    public sealed class LameMp3Encoder : IAudioEncoder
    {
        // Logical name resolved by ResolveLame below; never loaded by name.
        private const string LibName = "mp3lame";

        // Samples per channel per encode() call. MP3 buffer sizing follows the
        // lame.h rule: mp3buf_size >= 1.25 * nsamples + 7200.
        private const int SamplesPerChunk = 8192;

        static LameMp3Encoder()
        {
            // One resolver per assembly: if the Hearbud assembly ever needs a
            // second native dependency, merge its probing into ResolveLame
            // instead of registering another resolver (last one wins).
            NativeLibrary.SetDllImportResolver(typeof(LameMp3Encoder).Assembly, ResolveLame);
        }

        private static IntPtr ResolveLame(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != LibName)
            {
                return IntPtr.Zero; // Not ours: fall through to default probing.
            }

            // This backend only targets macOS; on other OSes fall through so
            // the runtime raises the usual DllNotFoundException.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return IntPtr.Zero;
            }

            string rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "osx-arm64",
                Architecture.X64 => "osx-x64",
                _ => "osx-arm64", // Unknown arch: try arm64 first, fallbacks below still run.
            };

            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "runtimes", rid, "native", "libmp3lame.dylib"),
                // Dev-loop convenience: dylib dropped next to the binaries.
                Path.Combine(baseDir, "libmp3lame.dylib"),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    try
                    {
                        return NativeLibrary.Load(candidate);
                    }
                    catch (Exception)
                    {
                        // Wrong arch or otherwise unloadable: try the next candidate.
                    }
                }
            }

            // Last resort: a system-wide install (e.g. `brew install lame`).
            // Returns IntPtr.Zero when absent, letting the runtime throw.
            if (NativeLibrary.TryLoad("libmp3lame.0.dylib", assembly, searchPath, out IntPtr system)
                || NativeLibrary.TryLoad("libmp3lame.dylib", assembly, searchPath, out system))
            {
                return system;
            }

            return IntPtr.Zero;
        }

        public bool EncodeMp3(string mixWavPath, string mp3Path, int bitrateKbps)
        {
            if (string.IsNullOrWhiteSpace(mixWavPath))
            {
                throw new ArgumentException("Mix WAV path is empty.", nameof(mixWavPath));
            }

            if (!File.Exists(mixWavPath))
            {
                throw new FileNotFoundException("Mix WAV not found.", mixWavPath);
            }

            if (string.IsNullOrWhiteSpace(mp3Path))
            {
                throw new ArgumentException("MP3 output path is empty.", nameof(mp3Path));
            }

            if (bitrateKbps <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bitrateKbps), "Bitrate must be positive.");
            }

            string? outDir = Path.GetDirectoryName(Path.GetFullPath(mp3Path));
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            using var reader = new WaveFileReader(mixWavPath);
            int channels = reader.WaveFormat.Channels;
            if (channels < 1 || channels > 2)
            {
                throw new NotSupportedException($"LAME supports mono/stereo only; input has {channels} channels.");
            }

            // Float samples, interleaved, nominally +/-1 full scale — exactly
            // what lame_encode_buffer_ieee_float wants. No 16-bit step.
            var samples = new WaveToSampleProvider(reader);

            IntPtr gfp = lame_init();
            if (gfp == IntPtr.Zero)
            {
                throw new InvalidOperationException("lame_init returned NULL (out of memory).");
            }

            try
            {
                lame_set_in_samplerate(gfp, reader.WaveFormat.SampleRate);
                lame_set_num_channels(gfp, channels);
                lame_set_brate(gfp, bitrateKbps);
                if (lame_init_params(gfp) < 0)
                {
                    throw new InvalidOperationException(
                        $"lame_init_params failed (rate={reader.WaveFormat.SampleRate}, channels={channels}, brate={bitrateKbps}).");
                }

                int mp3BufSize = (int)(SamplesPerChunk * 1.25) + 7200;
                var mp3Buf = new byte[mp3BufSize];
                var interleaved = new float[SamplesPerChunk * channels];
                var left = new float[SamplesPerChunk];
                var right = new float[SamplesPerChunk];

                using var outStream = new FileStream(mp3Path, FileMode.Create, FileAccess.Write, FileShare.None);
                int read;
                while ((read = samples.Read(interleaved.AsSpan())) > 0)
                {
                    int frames = read / channels;
                    if (channels == 2)
                    {
                        for (int i = 0; i < frames; i++)
                        {
                            left[i] = interleaved[i * 2];
                            right[i] = interleaved[i * 2 + 1];
                        }
                    }
                    else
                    {
                        Array.Copy(interleaved, left, frames);
                        Array.Copy(interleaved, right, frames);
                    }

                    int encoded = lame_encode_buffer_ieee_float(gfp, left, right, frames, mp3Buf, mp3BufSize);
                    if (encoded < 0)
                    {
                        throw new InvalidOperationException($"lame_encode_buffer_ieee_float failed ({encoded}).");
                    }

                    if (encoded > 0)
                    {
                        outStream.Write(mp3Buf, 0, encoded);
                    }
                }

                int flushed = lame_encode_flush(gfp, mp3Buf, mp3BufSize);
                if (flushed < 0)
                {
                    throw new InvalidOperationException($"lame_encode_flush failed ({flushed}).");
                }

                if (flushed > 0)
                {
                    outStream.Write(mp3Buf, 0, flushed);
                }
            }
            finally
            {
                lame_close(gfp);
            }

            var info = new FileInfo(mp3Path);
            return info.Exists && info.Length > 0;
        }

        // --- LAME 3.100 C ABI (lame.h); Cdecl on both macOS architectures. ---

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_init")]
        private static extern IntPtr lame_init();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_set_in_samplerate")]
        private static extern int lame_set_in_samplerate(IntPtr gfp, int rate);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_set_num_channels")]
        private static extern int lame_set_num_channels(IntPtr gfp, int channels);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_set_brate")]
        private static extern int lame_set_brate(IntPtr gfp, int brate);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_init_params")]
        private static extern int lame_init_params(IntPtr gfp);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_encode_buffer_ieee_float")]
        private static extern int lame_encode_buffer_ieee_float(
            IntPtr gfp,
            [In] float[] pcmL,
            [In] float[] pcmR,
            int nsamples,
            [Out] byte[] mp3buf,
            int mp3bufSize);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_encode_flush")]
        private static extern int lame_encode_flush(IntPtr gfp, [Out] byte[] mp3buf, int size);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lame_close")]
        private static extern int lame_close(IntPtr gfp);
    }
}
