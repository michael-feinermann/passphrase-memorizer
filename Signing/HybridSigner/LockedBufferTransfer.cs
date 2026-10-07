using System.Runtime.ExceptionServices;
using KalynaArchiver.Services;

namespace KeepVaultMac.Packaging;

/// <summary>
/// Transfers ownership of one locked result only after every temporary secret
/// and descriptor has been released successfully. A cleanup failure turns the
/// whole operation into a failure and erases the would-be result as well.
/// </summary>
internal static class LockedBufferTransfer
{
    /// <summary>
    /// Completes an operation only after every cleanup action has been
    /// attempted. The original operation failure keeps its stack trace when it
    /// is the only failure; otherwise it is retained alongside every cleanup
    /// failure in one aggregate.
    /// </summary>
    internal static void CompleteVoid(
        Exception? operationFailure,
        string failureMessage,
        params Action[] cleanupActions)
    {
        var failures = new List<Exception>();
        if (operationFailure is not null)
        {
            failures.Add(operationFailure);
        }

        foreach (Action cleanupAction in cleanupActions)
        {
            try
            {
                cleanupAction();
            }
            catch (Exception cleanupFailure)
            {
                failures.Add(cleanupFailure);
            }
        }

        if (failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failureMessage, failures);
    }

    internal static LockedSensitiveBuffer Complete(
        LockedSensitiveBuffer? result,
        Exception? operationFailure,
        string failureMessage,
        LockedSensitiveBuffer?[] sensitiveTemporaries,
        IDisposable?[] resources)
    {
        var failures = new List<Exception>();
        if (operationFailure is not null)
        {
            failures.Add(operationFailure);
            TryZeroAndDispose(
                sensitiveTemporaries.Concat([result]).ToArray(),
                failures);
            TryDispose(resources, failures);
            ThrowFailures(operationFailure, failures, failureMessage);
        }

        // Close ordinary resources first. If that fails, no secret temporary
        // has been unlocked yet, so every secret including the result can be
        // erased as one failed operation.
        TryDispose(resources, failures);
        if (failures.Count != 0)
        {
            TryZeroAndDispose(
                sensitiveTemporaries.Concat([result]).ToArray(),
                failures);
            throw new AggregateException(failureMessage, failures);
        }

        // Only temporaries are released on the success path. If their unlock
        // fails, the result is still locked and is erased before it can become
        // unreachable.
        TryZeroAndDispose(sensitiveTemporaries, failures);
        if (failures.Count != 0)
        {
            TryZeroAndDispose([result], failures);
            throw new AggregateException(failureMessage, failures);
        }

        return result
            ?? throw new InvalidOperationException(
                "A locked-buffer operation completed without producing its result.");
    }

    private static void TryZeroAndDispose(
        LockedSensitiveBuffer?[] buffers,
        List<Exception> failures)
    {
        try
        {
            SecureMemory.ZeroAndDisposeAll(buffers);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
    }

    private static void TryDispose(
        IDisposable?[] resources,
        List<Exception> failures)
    {
        try
        {
            SecureMemory.DisposeAll(resources);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
    }

    private static void ThrowFailures(
        Exception operationFailure,
        IReadOnlyCollection<Exception> failures,
        string failureMessage)
    {
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        throw new AggregateException(failureMessage, failures);
    }
}
