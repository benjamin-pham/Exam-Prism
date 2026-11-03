namespace ExamPrism.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity;

public class PlatformRole : IdentityRole<Guid>
{
    public PlatformRole() { }
    public PlatformRole(string roleName) : base(roleName) { }
}
