using System.Diagnostics;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Delays background work until no user activity has been reported for the idle period.
/// Long-running inference is allowed to finish; the next photo waits for idle.
/// </summary>
public sealed class UserActivityGate : IBackgroundActivityGate
{
    private readonly TimeSpan _idlePeriod;
    private long _lastActivityTimestamp;

    public UserActivityGate(TimeSpan? idlePeriod = null)
    {
        _idlePeriod = idlePeriod ?? TimeSpan.FromSeconds(5);
        NotifyUserActivity();
    }

    public void NotifyUserActivity()
    {
        Interlocked.Exchange(
            ref _lastActivityTimestamp,
            Stopwatch.GetTimestamp());
    }

    public async Task WaitForIdleAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var lastActivity = Interlocked.Read(ref _lastActivityTimestamp);
            var elapsed = Stopwatch.GetElapsedTime(lastActivity);
            var remaining = _idlePeriod - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining, cancellationToken);
        }
    }
}
