using System;
using System.Collections.Generic;
using System.Text;

namespace Hearbud
{
    /// <summary>
    /// UI-thread click/lag diagnostics for glitchy-input reports (e.g. checkboxes
    /// needing several clicks, dropdowns not opening). Best-effort only: never
    /// throws, never shows UI. Logging discipline: transitions only, WARN on lag
    /// spikes at most at 1 Hz with thread/tick context, input ring tail-capped.
    /// </summary>
    public sealed class InputEventRing
    {
        private readonly Queue<string> _queue = new();
        private readonly int _capacity;
        private long _total;

        public InputEventRing(int capacity = 50)
        {
            _capacity = Math.Clamp(capacity, 1, 200);
        }

        public void Record(string evt)
        {
            if (string.IsNullOrEmpty(evt)) return;
            lock (_queue)
            {
                _total++;
                _queue.Enqueue(evt);
                while (_queue.Count > _capacity) _queue.Dequeue();
            }
        }

        public long Total
        {
            get { lock (_queue) return _total; }
        }

        public int Kept
        {
            get { lock (_queue) return _queue.Count; }
        }

        /// <summary>Returns up to the last <paramref name="n"/> events, oldest first.</summary>
        public string[] Tail(int n)
        {
            lock (_queue)
            {
                int take = Math.Clamp(n, 0, _queue.Count);
                if (take == 0) return Array.Empty<string>();
                var arr = _queue.ToArray();
                var tail = new string[take];
                Array.Copy(arr, arr.Length - take, tail, 0, take);
                return tail;
            }
        }
    }

    /// <summary>
    /// Pure lag math for the 1 Hz UI-thread watchdog. The caller measures
    /// <c>elapsedMs</c> between expected ticks; this tracks max + spike count.
    /// </summary>
    public sealed class LagTracker
    {
        public int Samples { get; private set; }
        public double MaxLagMs { get; private set; }
        public int SpikeCount { get; private set; }

        /// <returns>Signed lag in ms (positive = tick arrived late).</returns>
        public double Observe(double elapsedMs, double expectedMs, double spikeThresholdMs = 250.0)
        {
            double lag = elapsedMs - expectedMs;
            Samples++;
            if (lag > MaxLagMs) MaxLagMs = lag;
            if (lag > spikeThresholdMs) SpikeCount++;
            return lag;
        }
    }

    /// <summary>
    /// One-shot environment report (OS, rendering tier, remote/tablet/animation
    /// state, DPI) written once at startup to distinguish a blocked UI thread
    /// from system-wide input/rendering issues. Every probe is guarded.
    /// </summary>
    public static class UiDiagnostics
    {
        public static string SourceName(object? src)
        {
            try
            {
                if (src is System.Windows.FrameworkElement fe)
                    return Truncate($"{fe.GetType().Name} Name={fe.Name}", 80);
                if (src != null)
                    return Truncate(src.GetType().Name, 80);
            }
            catch { }
            return "null";
        }

        public static string BuildEnvReport(System.Windows.Window? window = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"utc={DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"os={Safe(() => Environment.OSVersion.VersionString)} " +
                $"64bitProcess={Environment.Is64BitProcess} 64bitOs={Environment.Is64BitOperatingSystem} " +
                $"cpus={Environment.ProcessorCount} clr={Safe(() => Environment.Version.ToString())}");
            sb.AppendLine($"renderTier=0x{Safe(() => System.Windows.Media.RenderCapability.Tier.ToString("X8"))} " +
                $"tierLevel={Safe(() => (System.Windows.Media.RenderCapability.Tier >> 16).ToString())} " +
                $"renderMode={Safe(() => System.Windows.Media.RenderOptions.ProcessRenderMode.ToString())}");
            sb.AppendLine($"remote={Safe(() => System.Windows.SystemParameters.IsRemoteSession.ToString())} " +
                $"menuAnim={Safe(() => System.Windows.SystemParameters.MenuAnimation.ToString())} " +
                $"menuFade={Safe(() => System.Windows.SystemParameters.MenuFadeEnabled.ToString())} " +
                $"clientAnim={Safe(() => System.Windows.SystemParameters.ClientAreaAnimation.ToString())} " +
                $"mousePresent={Safe(() => System.Windows.SystemParameters.MousePresent.ToString())} " +
                $"swapButtons={Safe(() => System.Windows.SystemParameters.SwapButtons.ToString())} " +
                $"screen={Safe(() => System.Windows.SystemParameters.PrimaryScreenWidth.ToString())}x{Safe(() => System.Windows.SystemParameters.PrimaryScreenHeight.ToString())}");
            sb.AppendLine($"tablets={Safe(() =>
            {
                var devs = System.Windows.Input.Tablet.TabletDevices;
                var parts = new List<string>();
                foreach (var d in devs)
                {
                    try { parts.Add($"{d.Type}:{d.Name}"); } catch { parts.Add("?"); }
                }
                return $"{devs.Count}[{string.Join(",", parts)}]";
            })}");
            if (window != null)
            {
                sb.AppendLine($"dpi={Safe(() =>
                {
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                    return $"{dpi.DpiScaleX:F2}x{dpi.DpiScaleY:F2}";
                })} tid={Environment.CurrentManagedThreadId}");
            }
            else
            {
                sb.AppendLine($"tid={Environment.CurrentManagedThreadId}");
            }
            return sb.ToString();
        }

        private static string Safe(Func<string> probe)
        {
            try { return probe() ?? "n/a"; }
            catch { return "n/a"; }
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max);
    }
}
