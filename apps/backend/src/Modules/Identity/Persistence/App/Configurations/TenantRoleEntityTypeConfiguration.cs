namespace ExamPrism.Infrastructure.Persistence.App.Configurations;

using ExamPrism.Infrastructure.Persistence.Identity.Configurations;
using Microsoft.AspNetCore.Identity;

public sealed class TenantRoleEntityTypeConfiguration : IdentityRoleEntityTypeConfiguration<IdentityRole<Guid>>
{
}
