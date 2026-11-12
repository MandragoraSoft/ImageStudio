using ImageStudio.Services;

namespace ImageStudio.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void Pipe_name_is_unique_per_windows_session_for_the_same_user()
    {
        const string sid = "S-1-5-21-123456789-123456789-123456789-1001";
        var sessionOne = SingleInstanceCoordinator.CreateNames(sid, 1);
        var sessionTwo = SingleInstanceCoordinator.CreateNames(sid, 2);

        Assert.Equal(sessionOne.MutexName, sessionTwo.MutexName);
        Assert.StartsWith(@"Local\", sessionOne.MutexName);
        Assert.NotEqual(sessionOne.PipeName, sessionTwo.PipeName);
        Assert.Equal(sessionOne.PipeName, SingleInstanceCoordinator.CreateNames(sid, 1).PipeName);
    }

    [Fact]
    public void A_second_instance_sends_activation_and_the_primary_delivers_it_after_window_ready()
    {
        var objectName = $"ImageStudio.Tests.{Guid.NewGuid():N}";
        using var primary = new SingleInstanceCoordinator(objectName);
        using var second = new SingleInstanceCoordinator(objectName);
        using var activated = new ManualResetEventSlim();

        Assert.True(primary.AcquireOrActivate(TimeSpan.FromSeconds(2)));
        primary.SetActivationHandler(action => action(), activated.Set);

        var secondIsPrimary = true;
        Exception? secondFailure = null;
        var secondThread = new Thread(() =>
        {
            try { secondIsPrimary = second.AcquireOrActivate(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { secondFailure = ex; }
        }) { IsBackground = true };
        secondThread.Start();
        Assert.True(secondThread.Join(TimeSpan.FromSeconds(5)));
        if (secondFailure is not null) throw secondFailure;
        Assert.False(secondIsPrimary);
        Assert.False(activated.IsSet);

        primary.MarkWindowReady();

        Assert.True(activated.Wait(TimeSpan.FromSeconds(2)));
    }
}
