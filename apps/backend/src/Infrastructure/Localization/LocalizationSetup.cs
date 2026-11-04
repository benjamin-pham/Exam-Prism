namespace ExamPrism.Infrastructure.Localization;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

public static class LocalizationSetup
{
    public static readonly IReadOnlyList<string> SupportedCultures = ["vi-VN", "vi", "en-US", "en"];

    public static IServiceCollection AddAppLocalization(this IServiceCollection services)
    {
        services.AddLocalization(options => options.ResourcesPath = "Common/Localization/Resources");

        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.SetDefaultCulture("vi-VN")
                   .AddSupportedCultures([.. SupportedCultures])
                   .AddSupportedUICultures([.. SupportedCultures]);
        });

        return services;
    }

    public static IApplicationBuilder UseAppLocalization(this IApplicationBuilder app)
    {
        return app.UseRequestLocalization();
    }
}
