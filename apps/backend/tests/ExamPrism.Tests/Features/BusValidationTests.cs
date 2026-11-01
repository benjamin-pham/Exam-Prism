namespace ExamPrism.Tests.Features;

using Common.Handlers;
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

public sealed class ProbeValidatedCommandHandler : IHandler
{
    public static Task<ProbeValidatedResult> Handle(ProbeValidatedCommand command) =>
        Task.FromResult(new ProbeValidatedResult(command.Code));
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

        var invalidAct = () => bus.InvokeAsync<ProbeValidatedResult>(new ProbeValidatedCommand(""));
        await invalidAct.Should().ThrowAsync<ValidationException>();

        var validResult = await bus.InvokeAsync<ProbeValidatedResult>(new ProbeValidatedCommand("VALID_CODE"));
        validResult.Should().NotBeNull();
        validResult.Code.Should().Be("VALID_CODE");

        await host.StopAsync();
    }

    [Fact]
    public void ProductionAuthCommands_HaveRegisteredValidators()
    {
        var services = new ServiceCollection();
        services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);
        var provider = services.BuildServiceProvider();

        provider.GetService<IValidator<ExamPrism.Features.Auth.Register.RegisterCommand>>().Should().NotBeNull();
        provider.GetService<IValidator<ExamPrism.Features.Auth.Login.LoginCommand>>().Should().NotBeNull();
    }
}

