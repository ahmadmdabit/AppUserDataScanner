using System.Threading;
using System.Threading.Channels;

namespace AppUserDataScanner.Scanner;

/// <summary>
/// <para>
/// Tracks the state of BFS traversal to detect completion.
/// Uses a two-counter system:
///   itemsInChannel: items currently in the channel (queued but not yet read)
///   itemsBeingProcessed: items currently being processed by workers
/// </para>
/// <para>
/// Completion occurs when BOTH counters reach zero simultaneously.
/// This prevents the "credit/debit mismatch" bug where directories that
/// don't enqueue children (matched, skip-listed, max-depth) drain the counter
/// prematurely.
/// </para>
/// </summary>
internal sealed class WorkTracker
{
    private int itemsInChannel;
    private int itemsBeingProcessed;
    private readonly ChannelWriter<(string Path, int Depth)> writer;
    private bool markedComplete;

    internal WorkTracker(ChannelWriter<(string Path, int Depth)> writer)
    {
        this.writer = writer;
    }

    /// <summary>
    /// Approximate remaining work for display purposes.
    /// Not perfectly accurate due to race conditions, but good enough for UI.
    /// </summary>
    internal int RemainingWork =>
        Interlocked.CompareExchange(ref itemsInChannel, 0, 0) +
        Interlocked.CompareExchange(ref itemsBeingProcessed, 0, 0);

    /// <summary>
    /// Exact number of items currently in the channel queue.
    /// </summary>
    internal int ItemsInChannel => Interlocked.CompareExchange(ref itemsInChannel, 0, 0);

    /// <summary>
    /// Called when an item is added to the channel.
    /// </summary>
    internal void OnItemQueued() => Interlocked.Increment(ref itemsInChannel);

    /// <summary>
    /// Called when a worker reads an item from the channel.
    /// Transfers count from channel to being-processed.
    /// </summary>
    internal void OnItemDequeued()
    {
        Interlocked.Decrement(ref itemsInChannel);
        Interlocked.Increment(ref itemsBeingProcessed);
    }

    /// <summary>
    /// Called when a worker finishes processing an item.
    /// Checks if both counters are zero and completes the channel if so.
    /// </summary>
    internal void OnItemCompleted()
    {
        Interlocked.Decrement(ref itemsBeingProcessed);
        TryCompleteIfDone();
    }

    private void TryCompleteIfDone()
    {
        // Both counters must be zero AND we haven't already completed
        if (itemsInChannel == 0 && itemsBeingProcessed == 0 && !markedComplete)
        {
            // Use a compare-exchange pattern to ensure only one completion
            if (!Interlocked.CompareExchange(ref markedComplete, true, false))
            {
                writer.TryComplete();
            }
        }
    }
}