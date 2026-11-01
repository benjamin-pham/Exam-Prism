namespace ExamPrism.Tests.Features;

using Common.Handlers;
using Common.Results;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.FluentValidation;
using Xunit;

public record ProbeValidatedCommand(string Code);

public sealed class ProbeValidatedCommandValidator : AbstractValidator<ProbeValidatedCommand>
{
    public ProbeValidatedCommandValidator()
    {
        RuleFor(x => x.Code).NotEmpty();
    }
}

public sealed class ProbeValidatedCommandHandler : IHandler<ProbeValidatedCommand, ProbeValidatedResult>
{
    public Task<Result<ProbeValidatedResult>> Handle(ProbeValidatedCommand command, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(new ProbeValidatedResult(command.Code)));
}

public record ProbeValidatedResult(string Code);

public class BusValidationTests
{
    [Fact]
    public async Task InvokeAsync_WhenCommandFailsFluentValidation_ThrowsValidationExceptionWithoutHttp()
    {
        var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<ProbeValidatedCommandHandler>();
                opts.UseFluentValidation();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IValidator<ProbeValidatedCommand>, ProbeValidatedCommandValidator>();
            })
            .Build();

        await host.StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        var invalidAct = () => bus.InvokeAsync<Result<ProbeValidatedResult>>(new ProbeValidatedCommand(""));
        await invalidAct.Should().ThrowAsync<ValidationException>();

        var validResult = await bus.InvokeAsync<Result<ProbeValidatedResult>>(new ProbeValidatedCommand("VALID_CODE"));
        validResult.Should().NotBeNull();
        validResult.IsSuccess.Should().BeTrue();
        validResult.Value.Code.Should().Be("VALID_CODE");

        await host.StopAsync();
    }

    [Fact]
    public void ProductionAuthCommands_HaveRegisteredValidators()
    {
        var services = new ServiceCollection();
        services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);
        var provider = services.BuildServiceProvider();

        provider.GetService<IValidator<ExamPrism.Features.Auth.RegisterCommand>>().Should().NotBeNull();
        provider.GetService<IValidator<ExamPrism.Features.Auth.LoginCommand>>().Should().NotBeNull();
    }
}
