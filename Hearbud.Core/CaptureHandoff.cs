// Phase 4 (v2 port, L0/L1): the audio-thread handoff. Bounded channels with
// DropOldest never block capture; the count check approximates drops for
// health (exact under the single-writer/single-reader topology we use).

using System.Threading;
using System.Threading.Channels;

namespace Hearbud
{
    public static class CaptureHandoff
    {
        public static Channel<T> Create<T>(int capacity) =>
            Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

        /// <summary>
        /// Hands a block to the mixer. Never blocks: when full, the oldest block
        /// is discarded and <paramref name="dropped"/> is incremented.
        /// Returns false only if the channel is completed.
        /// </summary>
        public static bool TryWrite<T>(Channel<T> channel, T item, int capacity, ref long dropped)
        {
            if (channel.Reader.Count >= capacity)
                Interlocked.Increment(ref dropped);
            return channel.Writer.TryWrite(item);
        }
    }
}
