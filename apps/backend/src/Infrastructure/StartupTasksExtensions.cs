namespace ExamPrism.Infrastructure;

using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.Builder;

public static class StartupTasksExtensions
{
    public static async Task InitializeInfrastructureAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        await app.InitializeIdentityAsync(cancellationToken);
        await app.InitializeMultiTenancyAsync(cancellationToken);
    }
}
