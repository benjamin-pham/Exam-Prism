namespace ExamPrism.Tests.Infrastructure;

using System.Globalization;
using Common.Localization;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using global::Tests.Infrastructure;
using Xunit;

public class LocalizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public LocalizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("vi-VN", "Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau.")]
    [InlineData("en-US", "An unexpected error occurred. Please try again later.")]
    public void StringLocalizer_ReturnsCorrectLocalizedMessage_ForCulture(string cultureName, string expected)
    {
        using var scope = _factory.Services.CreateScope();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResources>>();

        var culture = new CultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var message = localizer["InternalServerError"].Value;

        message.Should().Be(expected);
    }

    [Fact]
    public void RequestLocalization_HasDefaultCultureOfVietnamese()
    {
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;

        options.DefaultRequestCulture.Culture.Name.Should().Be("vi-VN");
        options.DefaultRequestCulture.UICulture.Name.Should().Be("vi-VN");
        options.SupportedCultures.Should().Contain(c => c.Name == "vi-VN");
        options.SupportedCultures.Should().Contain(c => c.Name == "en-US");
        options.SupportedUICultures.Should().Contain(c => c.Name == "vi-VN");
        options.SupportedUICultures.Should().Contain(c => c.Name == "en-US");
    }
}
