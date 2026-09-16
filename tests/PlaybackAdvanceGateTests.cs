namespace diashow.Tests;

public sealed class PlaybackAdvanceGateTests
{
    [Fact]
    public async Task Resume_releases_an_image_advance_that_expired_while_paused()
    {
        var gate = new PlaybackAdvanceGate(paused: true);
        var wait = gate.WaitUntilResumedAsync(CancellationToken.None);

        Assert.False(wait.IsCompleted);

        gate.Resume();

        await wait;
    }
}
