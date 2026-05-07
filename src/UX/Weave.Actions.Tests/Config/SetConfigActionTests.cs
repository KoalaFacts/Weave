using Weave.Actions.Config;
using Weave.Actions.Context;

namespace Weave.Actions.Tests.Config;

public sealed class SetConfigActionTests
{
    [Fact]
    public async Task ExecuteAsync_KnownKeyAndValue_CallsWriterAndReturnsCanonical()
    {
        var writer = new RecordingWriter();
        var action = new SetConfigAction(writer);

        var result = await action.ExecuteAsync(new SetConfigInput("siloPath", "/opt/weave"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Key.ShouldBe("siloPath");
        result.Value.Value.ShouldBe("/opt/weave");
        writer.Calls.Count.ShouldBe(1);
        writer.Calls[0].ShouldBe(("siloPath", "/opt/weave"));
    }

    [Theory]
    [InlineData("SILOPATH")]
    [InlineData("Silopath")]
    [InlineData("siloPath")]
    public async Task ExecuteAsync_KeyMatchIsCaseInsensitive_PersistsCanonicalCasing(string key)
    {
        var writer = new RecordingWriter();
        var action = new SetConfigAction(writer);

        var result = await action.ExecuteAsync(new SetConfigInput(key, "/x"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        // Writer always sees the canonical-case key from ConfigKeys.Writable.
        writer.Calls[0].Key.ShouldBe("siloPath");
        result.Value!.Key.ShouldBe("siloPath");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownKey_ReturnsValidationFailedWithKeyList()
    {
        var writer = new RecordingWriter();
        var action = new SetConfigAction(writer);

        var result = await action.ExecuteAsync(new SetConfigInput("nope", "x"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        // Lists every writable key so the user knows what's accepted.
        foreach (var key in ConfigKeys.Writable)
            result.Failure.Message.ShouldContain(key);
        writer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_DefaultPort_ValidInteger_Succeeds()
    {
        var writer = new RecordingWriter();
        var action = new SetConfigAction(writer);

        var result = await action.ExecuteAsync(new SetConfigInput("defaultPort", "9404"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        writer.Calls[0].ShouldBe(("defaultPort", "9404"));
    }

    [Theory]
    [InlineData("nine-thousand")]
    [InlineData("9404.5")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    public async Task ExecuteAsync_DefaultPort_InvalidValue_ReturnsValidationFailed(string value)
    {
        var writer = new RecordingWriter();
        var action = new SetConfigAction(writer);

        var result = await action.ExecuteAsync(new SetConfigInput("defaultPort", value), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        result.Failure.Message.ShouldContain(value);
        writer.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_SiloPath_BlankValue_ReturnsValidationFailed(string value)
    {
        var writer = new RecordingWriter();
        var action = new SetConfigAction(writer);

        var result = await action.ExecuteAsync(new SetConfigInput("siloPath", value), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        writer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WriterThrowsIOException_ReturnsInternal()
    {
        var writer = new ThrowingWriter(new IOException("disk full"));
        var action = new SetConfigAction(writer);

        var result = await action.ExecuteAsync(new SetConfigInput("siloPath", "/x"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Internal);
        result.Failure.Message.ShouldContain("disk full");
    }

    [Fact]
    public async Task ExecuteAsync_WriterThrowsUnauthorized_ReturnsUnauthorized()
    {
        var writer = new ThrowingWriter(new UnauthorizedAccessException("denied"));
        var action = new SetConfigAction(writer);

        var result = await action.ExecuteAsync(new SetConfigInput("siloPath", "/x"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Reason.ShouldBe(ActionFailureReason.Unauthorized);
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var action = new SetConfigAction(new RecordingWriter());

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankKey_Throws(string key)
    {
        var action = new SetConfigAction(new RecordingWriter());

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new SetConfigInput(key, "x"), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_NullValue_Throws()
    {
        var action = new SetConfigAction(new RecordingWriter());

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(new SetConfigInput("siloPath", null!), CancellationToken.None));
    }

    private sealed class RecordingWriter : ISystemConfigWriter
    {
        public List<(string Key, string Value)> Calls { get; } = [];

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Calls.Add((key, value));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingWriter : ISystemConfigWriter
    {
        private readonly Exception _exception;

        public ThrowingWriter(Exception exception)
        {
            _exception = exception;
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken) => throw _exception;
    }
}
