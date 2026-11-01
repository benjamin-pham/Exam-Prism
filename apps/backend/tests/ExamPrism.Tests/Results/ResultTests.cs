namespace ExamPrism.Tests.Results;

using Common.Handlers;
using Common.Results;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Xunit;

public record ProbeVoidCommand;

public sealed class ProbeVoidCommandHandler : IHandler<ProbeVoidCommand>
{
    public Task<Result> Handle(ProbeVoidCommand request, CancellationToken ct = default) =>
        Task.FromResult(Result.Success());
}

public class ResultTests
{
    [Fact]
    public void Success_And_Failure_Invariants_Hold_And_InvalidCombinationsThrow()
    {
        var success = Result.Success();
        success.IsSuccess.Should().BeTrue();
        success.IsFailure.Should().BeFalse();
        success.Error.Should().Be(Error.None);

        var failError = Error.NotFound("USER_NOT_FOUND", "User not found.");
        var failure = Result.Failure(failError);
        failure.IsSuccess.Should().BeFalse();
        failure.IsFailure.Should().BeTrue();
        failure.Error.Should().Be(failError);

        var actSuccessWithNonEmptyError = () => new ResultSubclassProbe(true, failError);
        actSuccessWithNonEmptyError.Should().Throw<ArgumentException>()
            .WithMessage("*Successful result cannot contain an error*");

        var actFailureWithEmptyError = () => new ResultSubclassProbe(false, Error.None);
        actFailureWithEmptyError.Should().Throw<ArgumentException>()
            .WithMessage("*Failed result must contain a non-empty error*");

        var actSuccessWithNull = () => Result.Success<string>(null!);
        actSuccessWithNull.Should().Throw<ArgumentNullException>();

        var failedTyped = Result.Failure<string>(failError);
        failedTyped.IsFailure.Should().BeTrue();
        var actReadValueOnFailed = () => failedTyped.Value;
        actReadValueOnFailed.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot access Value of a failed Result*");
    }

    [Fact]
    public void ImplicitOperators_ConvertValueAndErrorCleanly()
    {
        Result<int> ok = 42;
        ok.IsSuccess.Should().BeTrue();
        ok.Value.Should().Be(42);

        var conflictError = Error.Conflict("DUP", "Resource already exists.");
        Result<int> fail = conflictError;
        fail.IsFailure.Should().BeTrue();
        fail.Error.Type.Should().Be(ErrorType.Conflict);
        fail.Error.Code.Should().Be("DUP");
        fail.Error.Message.Should().Be("Resource already exists.");

        var unauthError = Error.Unauthorized("UNAUTH", "Denied.");
        Result nonGenericFail = unauthError;
        nonGenericFail.IsFailure.Should().BeTrue();
        nonGenericFail.Error.Type.Should().Be(ErrorType.Unauthorized);
        nonGenericFail.Error.Code.Should().Be("UNAUTH");
    }

    [Fact]
    public async Task NonGenericResult_ImplementsIWolverineReturnType_AndDoesNotCascadeOnNonGenericInvokeAsync()
    {
        typeof(IWolverineReturnType).IsAssignableFrom(typeof(Result)).Should().BeTrue();

        var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<ProbeVoidCommandHandler>();
            })
            .Build();

        await host.StartAsync();

        var bus = host.Services.GetService(typeof(IMessageBus)) as IMessageBus;
        bus.Should().NotBeNull();

        var act = async () => await bus!.InvokeAsync(new ProbeVoidCommand());
        await act.Should().NotThrowAsync();

        await host.StopAsync();
    }

    private sealed class ResultSubclassProbe : Result
    {
        public ResultSubclassProbe(bool isSuccess, Error error) : base(isSuccess, error) { }
    }
}
