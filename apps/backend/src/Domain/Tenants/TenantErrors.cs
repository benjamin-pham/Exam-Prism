namespace ExamPrism.Domain.Tenants;

using Common.Results;

public static class TenantErrors
{
    public static Error TenantNotFound() =>
        Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.");

    public static Error SlugAlreadyTaken() =>
        Error.Conflict("SLUG_ALREADY_TAKEN", "This organization subdomain is already taken or reserved.");

    public static Error InvalidSlug() =>
        Error.Validation("Slug", "INVALID_SLUG", "The organization subdomain slug is invalid.");

    public static Error NameRequired() =>
        Error.Validation("Name", "REQUIRED", "Organization name is required.");

    public static Error NameTooLong() =>
        Error.Validation("Name", "MAX_LENGTH", "Organization name must not exceed 200 characters.");

    public static Error InvalidStatusTransition(string message) =>
        Error.Conflict("INVALID_TENANT_STATUS_TRANSITION", message);
}

