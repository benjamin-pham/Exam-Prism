namespace Tests;

using FluentAssertions;
using NSubstitute;
using Xunit;

public class NSubstituteTests
{
    public interface ITestService
    {
        string GetMessage(string name);
        int Calculate(int x, int y);
        Task<bool> ProcessAsync(string payload);
    }

    [Fact]
    public async Task NSubstitute_And_FluentAssertions_WorkTogether()
    {
        // Arrange
        var service = Substitute.For<ITestService>();
        service.GetMessage("Alice").Returns("Hello Alice");
        service.Calculate(Arg.Any<int>(), Arg.Any<int>()).Returns(42);
        service.ProcessAsync("ok").Returns(Task.FromResult(true));

        // Act
        var message = service.GetMessage("Alice");
        var calcResult = service.Calculate(10, 20);
        var processResult = await service.ProcessAsync("ok");

        // Assert with FluentAssertions
        message.Should().Be("Hello Alice");
        calcResult.Should().Be(42);
        processResult.Should().BeTrue();

        // Verify with NSubstitute
        service.Received(1).GetMessage("Alice");
        service.Received(1).Calculate(10, 20);
        await service.Received(1).ProcessAsync("ok");
    }
}

