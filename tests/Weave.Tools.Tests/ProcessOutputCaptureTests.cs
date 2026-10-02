using Weave.Invocations.Processes;

namespace Weave.Tools.Tests;

public sealed class ProcessOutputCaptureTests
{
    [Theory]
    [InlineData(65_536, false)]
    [InlineData(65_537, true)]
    public async Task Start_BoundedCapture_DrainsToEndOnDedicatedThread(int length, bool exceeded)
    {
        using var reader = new TrackingReader(new string('x', length));
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var captured = await ProcessOutputCapture.Start(reader, overflow)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        captured.ShouldBe(new string('x', Math.Min(length, 65_536)));
        overflow.Task.IsCompletedSuccessfully.ShouldBe(exceeded);
        reader.SawEnd.ShouldBeTrue();
        reader.WasThreadPool.ShouldBeFalse();
    }

    [Fact]
    public async Task Start_ReaderFails_SignalsFailureAndObservesReaderException()
    {
        using var reader = new FailingReader();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var readFailure = await Should.ThrowAsync<IOException>(() => ProcessOutputCapture.Start(reader, stop));
        var stopFailure = await Should.ThrowAsync<IOException>(() => stop.Task);

        readFailure.Message.ShouldBe("read-failure-marker");
        stopFailure.ShouldBeSameAs(readFailure);
    }

    private sealed class TrackingReader(string text) : StringReader(text)
    {
        public bool WasThreadPool { get; private set; }
        public bool SawEnd { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            WasThreadPool |= Thread.CurrentThread.IsThreadPoolThread;
            var read = base.Read(buffer, index, count);
            SawEnd |= read == 0;
            return read;
        }
    }

    private sealed class FailingReader : TextReader
    {
        public override int Read(char[] buffer, int index, int count) => throw new IOException("read-failure-marker");
    }
}
