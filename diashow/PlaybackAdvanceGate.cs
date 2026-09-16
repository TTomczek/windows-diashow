namespace diashow;

internal sealed class PlaybackAdvanceGate
{
    private TaskCompletionSource _resumed = CreateCompletedSource();

    public PlaybackAdvanceGate(bool paused = false)
    {
        if (paused)
            Pause();
    }

    public void Pause()
    {
        if (!_resumed.Task.IsCompleted)
            return;

        _resumed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume() => _resumed.TrySetResult();

    public Task WaitUntilResumedAsync(CancellationToken cancellationToken) =>
        _resumed.Task.WaitAsync(cancellationToken);

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
