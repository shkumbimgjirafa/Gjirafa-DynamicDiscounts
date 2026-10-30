namespace PricingTool.Data.Services;

/// <summary>
/// Thrown by <see cref="PricingRunOrchestrator.ExecuteRunAsync"/> when another run already holds the global
/// single-flight guard. It is its own type because the scheduler must treat it differently from a run that
/// FAILED: a busy engine leaves the layer's slot due, so the next tick picks it up, while a failed run consumes
/// its slot so it can't re-run every minute.
///
/// <para>The two used to be told apart by <see cref="InvalidOperationException"/>, and a failed run can throw
/// that too — SqlBulkCopy raises it for any value its destination column can't hold. So a failing layer read as
/// a busy engine, its slot was never consumed, and the scheduler re-ran it back-to-back: on 2026-09-24/25 both
/// MK layers ran ~50 times in a row on one SKU's junk shelf price. Still derives from
/// InvalidOperationException, so existing catches of the base type keep working.</para>
/// </summary>
public sealed class RunAlreadyInProgressException : InvalidOperationException
{
    public RunAlreadyInProgressException() : base("A pricing run is already in progress.") { }
}
