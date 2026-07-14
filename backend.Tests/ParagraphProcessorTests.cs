using Lingofix.Backend.Documents;
using Xunit;

namespace Lingofix.Backend.Tests;

/// <summary>
/// Guards the checkpoint resume invariant for parallel batch processing: the persisted
/// "completed batches" count is a positional resume index (work.Skip), so it must only
/// ever advance across a contiguous prefix of finished batches. If it counted every
/// out-of-order completion, resuming an interrupted run would Skip past — and thus
/// silently drop — batches that never actually ran. That is the defect that let a
/// large document come back with the LLM's corrections missing from the output.
/// </summary>
public class ParagraphProcessorTests
{
    [Fact]
    public void ContiguousPrefix_DoesNotAdvancePastAGap()
    {
        // Batch 0 done, batch 1 still running, batches 2-3 already finished.
        var flags = new[] { true, false, true, true };

        // Resume index must stay at 1: batch 1 is unprocessed and must not be skipped.
        Assert.Equal(1, ParagraphProcessor.AdvanceContiguousPrefix(flags, 0));
    }

    [Fact]
    public void ContiguousPrefix_AdvancesAsTheGapFills()
    {
        var flags = new[] { true, false, true, true };
        var prefix = ParagraphProcessor.AdvanceContiguousPrefix(flags, 0);
        Assert.Equal(1, prefix);

        // The straggler finishes; the prefix now jumps across all remaining done batches.
        flags[1] = true;
        prefix = ParagraphProcessor.AdvanceContiguousPrefix(flags, prefix);
        Assert.Equal(4, prefix);
    }

    [Fact]
    public void ContiguousPrefix_AllDoneReachesTotal()
    {
        var flags = new[] { true, true, true };
        Assert.Equal(3, ParagraphProcessor.AdvanceContiguousPrefix(flags, 0));
    }

    [Fact]
    public void ContiguousPrefix_NothingDoneStaysAtZero()
    {
        var flags = new[] { false, false };
        Assert.Equal(0, ParagraphProcessor.AdvanceContiguousPrefix(flags, 0));
    }

    [Fact]
    public void ContiguousPrefix_EmptyIsZero()
    {
        Assert.Equal(0, ParagraphProcessor.AdvanceContiguousPrefix(System.Array.Empty<bool>(), 0));
    }
}
