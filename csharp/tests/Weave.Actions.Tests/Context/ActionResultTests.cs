using Weave.Actions.Context;

namespace Weave.Actions.Tests.Context;

public sealed class ActionResultTests
{
    [Fact]
    public void Success_ExposesValue_AndNullFailure()
    {
        var result = ActionResult.Success("hello");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("hello");
        result.Failure.ShouldBeNull();
    }

    [Fact]
    public void Failed_ExposesFailure_AndDefaultValue()
    {
        var failure = ActionFailure.SiloUnreachable("offline");
        var result = ActionResult.Failed<string>(failure);

        result.IsSuccess.ShouldBeFalse();
        result.Value.ShouldBeNull();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        result.Failure.Message.ShouldBe("offline");
    }

    [Fact]
    public void ActionFailure_FactoryMethods_SetReasonsCorrectly()
    {
        ActionFailure.SiloUnreachable("x").Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        ActionFailure.ValidationFailed("x").Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        ActionFailure.NotFound("x").Reason.ShouldBe(ActionFailureReason.NotFound);
        ActionFailure.Conflict("x").Reason.ShouldBe(ActionFailureReason.Conflict);
        ActionFailure.Unauthorized("x").Reason.ShouldBe(ActionFailureReason.Unauthorized);
        ActionFailure.Cancelled().Reason.ShouldBe(ActionFailureReason.Cancelled);
        ActionFailure.Internal("x").Reason.ShouldBe(ActionFailureReason.Internal);
    }

    [Fact]
    public void Cancelled_DefaultsToStandardMessage()
    {
        ActionFailure.Cancelled().Message.ShouldBe("Operation cancelled.");
    }
}
