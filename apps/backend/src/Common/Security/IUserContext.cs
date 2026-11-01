namespace Common.Security;

public interface IUserContext
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
}
