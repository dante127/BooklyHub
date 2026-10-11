using System.Text.RegularExpressions;
using BooklyHub.Application.Common.Helpers;
using BooklyHub.Application.Common.Interfaces;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BooklyHub.Application.Tenancy.Commands;

/// <summary>
/// ONB-01: the two writes a client deployment has never had, and the bounds they are judged by.
///
/// Before these commands the only writer of a <c>Tenant</c> row in <c>src</c> was <c>DatabaseSeeder</c>, and the only
/// writer of a <c>User</c> row was the same file, for one platform administrator. So a fresh production database had
/// zero tenants and one credential, and putting a client's clinic into it — or giving that clinic a receptionist —
/// meant running <c>INSERT</c> statements by hand. Every number below is this file's policy, chosen against a column
/// width or against what a reader in this repository actually does with the value, and each refusal is a fact in
/// <c>OnboardingTests</c>.
/// </summary>
public static class OnboardingLimits
{
    /// <summary><c>Tenants.Slug</c> is nvarchar(100) (<c>CoreConfigurations.cs:26</c>).</summary>
    public const int MaxSlugCharacters = 100;

    /// <summary>Below three characters a slug is an abbreviation nobody can read back.</summary>
    public const int MinSlugCharacters = 3;

    /// <summary><c>Tenants.Name</c> is nvarchar(200), <c>Plan</c> nvarchar(50), <c>TimeZoneId</c> nvarchar(100).</summary>
    public const int MaxTenantNameCharacters = 200;
    public const int MaxPlanCharacters = 50;
    public const int MaxTimeZoneCharacters = 100;

    /// <summary><c>Users.Email</c>, <c>FirstName</c> and <c>LastName</c> are 256, 100 and 100 (<c>CoreConfigurations.cs:61-63</c>).</summary>
    public const int MaxEmailCharacters = 256;
    public const int MaxPersonNameCharacters = 100;

    /// <summary>
    /// <c>Users.PhoneNumber</c> is <c>nvarchar(max)</c> — the model gives it no width — so this bound is policy
    /// rather than a truncation guard, taken at the width the two columns beside it do carry.
    /// </summary>
    public const int MaxPhoneNumberCharacters = 100;

    /// <summary>
    /// How many roles one account write may name. Every tenant role in this product is a full permission set of its
    /// own, so an account that needs four has the wrong role design; the bound stops one request minting a principal
    /// whose claims are the union of everything a caller can type.
    /// </summary>
    public const int MaxRolesPerAccount = 3;

    /// <summary>
    /// A slug is a URL segment before it is anything else, so it is lowercase alphanumerics with single hyphens
    /// between words. Compared against the <em>normalized</em> value — see <see cref="Tenant.NormalizeSlug"/>.
    /// </summary>
    public static bool IsSlugShape(string normalizedSlug) =>
        normalizedSlug.Length is >= MinSlugCharacters and <= MaxSlugCharacters &&
        Regex.IsMatch(normalizedSlug, "^[a-z0-9]+(-[a-z0-9]+)*$");
}

public record TenantSettingsDto(
    int MinBookingNoticeMinutes,
    int MaxAdvanceBookingDays,
    int CancellationCutoffHours,
    int ReschedulingCutoffHours,
    int SlotIntervalMinutes,
    bool ConfirmationEmailEnabled,
    int ReminderNoticeHours,
    bool RequireUpfrontPayment);

public record AccountDto(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string? PhoneNumber,
    bool IsActive,
    IReadOnlyList<string> Roles);

public record ProvisionedTenantDto(
    Guid Id,
    string Name,
    string Slug,
    string TimeZoneId,
    string Plan,
    bool IsActive,
    DateTime CreatedAtUtc,
    TenantSettingsDto Settings,
    AccountDto Owner);

/// <summary>
/// Creates a clinic and the first account that owns it, as one write.
///
/// The two are one command because a tenant with no credential is the broken state this finding is about: two routes
/// would leave a caller who created the tenant and lost the second request holding a clinic nobody can sign into,
/// with no route to finish it. The owner's role is not a parameter — it is <c>TenantOwner</c>, the role the product
/// says owns a tenant — so this route cannot be pointed at <c>PlatformAdmin</c>, which is the one role whose claims
/// bypass every permission check in the server.
/// </summary>
public record ProvisionTenantCommand(
    string Name,
    string Slug,
    string TimeZoneId,
    string? Plan,
    string OwnerEmail,
    string OwnerPassword,
    string OwnerFirstName,
    string OwnerLastName,
    string? OwnerPhoneNumber) : IRequest<ProvisionedTenantDto>;

public class ProvisionTenantCommandValidator : AbstractValidator<ProvisionTenantCommand>
{
    public ProvisionTenantCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("A tenant needs a name; it is what an operator's list and every downstream log read.")
            .MaximumLength(OnboardingLimits.MaxTenantNameCharacters)
            .WithMessage($"Tenant name must be {OnboardingLimits.MaxTenantNameCharacters} characters or fewer.");

        // Judged on the value that gets stored, so the shape rule and the unique index cannot disagree about what
        // " Apex-Dental " is.
        RuleFor(x => x.Slug)
            .NotEmpty().WithMessage("A tenant needs a slug.")
            .Must(slug => OnboardingLimits.IsSlugShape(Tenant.NormalizeSlug(slug)))
            .WithMessage($"Slug must be {OnboardingLimits.MinSlugCharacters}-{OnboardingLimits.MaxSlugCharacters} " +
                         "characters of lowercase letters, digits and single hyphens, starting and ending with a letter or digit.");

        RuleFor(x => x.TimeZoneId)
            .NotEmpty().WithMessage("A tenant needs a time zone; every working hour it writes later is local to it.")
            .MaximumLength(OnboardingLimits.MaxTimeZoneCharacters)
            .WithMessage($"Time zone id must be {OnboardingLimits.MaxTimeZoneCharacters} characters or fewer.")
            // The reader substitutes UTC for anything it cannot resolve (TimeZoneHelper.cs:30), so without this rule
            // a typo is not an error — it is a clinic whose whole day of slots is shifted by hours and which is told
            // nothing about it.
            .Must(timeZoneId => TimeZoneHelper.IsKnownTimeZoneId(timeZoneId))
            .WithMessage("Unknown time zone id. Use an id this server can resolve, such as UTC or Asia/Damascus.");

        RuleFor(x => x.Plan!)
            .MaximumLength(OnboardingLimits.MaxPlanCharacters)
            .WithMessage($"Plan must be {OnboardingLimits.MaxPlanCharacters} characters or fewer.");

        AccountShapeRules.For(
            this,
            x => x.OwnerEmail,
            x => x.OwnerPassword,
            x => x.OwnerFirstName,
            x => x.OwnerLastName,
            x => x.OwnerPhoneNumber);
    }
}

public class ProvisionTenantCommandHandler : IRequestHandler<ProvisionTenantCommand, ProvisionedTenantDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;

    public ProvisionTenantCommandHandler(IApplicationDbContext db, IPasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<ProvisionedTenantDto> Handle(ProvisionTenantCommand request, CancellationToken cancellationToken)
    {
        var slug = Tenant.NormalizeSlug(request.Slug);

        // IgnoreQueryFilters, because the unique index on Slug carries no filter (CoreConfigurations.cs:30), so a
        // soft-deleted tenant still owns its slug. A filtered read would answer 201 for a row the database then
        // refuses — a client mistake arriving as a 500.
        if (await _db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Slug == slug, cancellationToken))
        {
            throw new BusinessRuleValidationException(
                "TenantSlugAlreadyTaken",
                $"Another tenant already has the slug '{slug}', including any tenant that has been deleted.");
        }

        var email = AccountShapeRules.NormalizeEmail(request.OwnerEmail);
        await AccountGuards.RequireEmailFreeAsync(_db, email, cancellationToken);

        var ownerRoleId = await AccountGuards.RequireSystemRoleIdAsync(_db, Roles.TenantOwner, cancellationToken);

        var tenant = new Tenant(Guid.NewGuid(), request.Name.Trim(), slug, request.TimeZoneId.Trim(),
            string.IsNullOrWhiteSpace(request.Plan) ? "Standard" : request.Plan.Trim())
        {
            Settings = new TenantSetting(Guid.NewGuid())
        };

        var owner = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = email,
            FirstName = request.OwnerFirstName.Trim(),
            LastName = request.OwnerLastName.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(request.OwnerPhoneNumber) ? null : request.OwnerPhoneNumber.Trim(),
            PasswordHash = _hasher.HashPassword(request.OwnerPassword),
            IsActive = true
        };

        // UserRole.TenantId is left unset exactly as DatabaseSeeder leaves it: nothing in src reads that column, and
        // a row whose shape depends on which code path wrote it is a question for whoever queries it next.
        owner.UserRoles.Add(new UserRole { UserId = owner.Id, RoleId = ownerRoleId });

        _db.Tenants.Add(tenant);
        _db.Users.Add(owner);

        // One save for all four rows: the settings row, the owner and the role link are meaningless without the
        // tenant, and SaveChangesAsync sends them inside one transaction, so a refused write leaves no partial
        // clinic behind. There is deliberately no way to ask for a tenant that cannot be signed into.
        await AccountGuards.SaveAccountWriteAsync(_db, email, slug, cancellationToken);

        return ToDto(tenant, owner, [Roles.TenantOwner]);
    }

    internal static ProvisionedTenantDto ToDto(Tenant tenant, User owner, IReadOnlyList<string> ownerRoles) => new(
        tenant.Id,
        tenant.Name,
        tenant.Slug,
        tenant.TimeZoneId,
        tenant.Plan,
        tenant.IsActive,
        tenant.CreatedAtUtc,
        new TenantSettingsDto(
            tenant.Settings!.MinBookingNoticeMinutes,
            tenant.Settings.MaxAdvanceBookingDays,
            tenant.Settings.CancellationCutoffHours,
            tenant.Settings.ReschedulingCutoffHours,
            tenant.Settings.SlotIntervalMinutes,
            tenant.Settings.ConfirmationEmailEnabled,
            tenant.Settings.ReminderNoticeHours,
            tenant.Settings.RequireUpfrontPayment),
        new AccountDto(owner.Id, owner.Email, owner.FirstName, owner.LastName, owner.PhoneNumber, owner.IsActive, ownerRoles));
}

public record TenantStatusDto(Guid Id, string Slug, bool IsActive);

/// <summary>
/// The only writer of <c>Tenant.IsActive</c>. That column has had a reader since before this file existed —
/// <c>AvailabilityService</c>'s tenant load turns a clinic flagged off into a not-bookable answer, which arrives as
/// an empty day on the public portal — and until now nothing in <c>src</c> could set it, so suspending a client's
/// clinic was an <c>UPDATE</c> in the database.
///
/// What it does not decide is how loud a suspension is. A deactivated tenant's own accounts keep signing in, keep
/// refreshing, and keep reading their rows; the calendar goes quiet and the bookings stop. Whether a suspended
/// tenant should be refused at the door instead is <c>SEC-03</c>'s open question, and this route deliberately leaves
/// it open rather than answer a security policy from a provisioning change.
/// </summary>
public record SetTenantStatusCommand(Guid TenantId, bool IsActive) : IRequest<TenantStatusDto>;

public class SetTenantStatusCommandValidator : AbstractValidator<SetTenantStatusCommand>
{
    public SetTenantStatusCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty().WithMessage("The tenant to switch is not optional.");
    }
}

public class SetTenantStatusCommandHandler : IRequestHandler<SetTenantStatusCommand, TenantStatusDto>
{
    private readonly IApplicationDbContext _db;

    public SetTenantStatusCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<TenantStatusDto> Handle(SetTenantStatusCommand request, CancellationToken cancellationToken)
    {
        // A filtered read: a tenant that was deleted is not a tenant that is switched off, and reviving one through
        // a status route would be a delete route's job.
        var tenant = await _db.Tenants
            .FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Tenant with ID {request.TenantId} was not found.");

        tenant.IsActive = request.IsActive;
        await _db.SaveChangesAsync(cancellationToken);

        return new TenantStatusDto(tenant.Id, tenant.Slug, tenant.IsActive);
    }
}

/// <summary>
/// Creates an account inside the tenant the caller is already standing in.
///
/// There is no <c>TenantId</c> on this command's request body, and that is the finding's other half: <c>User</c> is
/// not an <c>ITenantEntity</c>, so the save-time cross-tenant rejection in <c>ApplicationDbContext.cs:162-169</c>
/// does not run for it — the handler stamping the caller's own resolved tenant is the only thing that scopes the
/// row. A body that could name a tenant would be a body that could name someone else's.
/// </summary>
public record CreateAccountCommand(
    Guid TenantId,
    string Email,
    string Password,
    string FirstName,
    string LastName,
    string? PhoneNumber,
    IReadOnlyList<string> Roles) : IRequest<AccountDto>;

public class CreateAccountCommandValidator : AbstractValidator<CreateAccountCommand>
{
    public CreateAccountCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        AccountShapeRules.For(
            this,
            x => x.Email,
            x => x.Password,
            x => x.FirstName,
            x => x.LastName,
            x => x.PhoneNumber);

        RuleFor(x => x.Roles)
            .NotEmpty().WithMessage("An account with no role can sign in and use nothing, so it is not an account yet.")
            .Must(roles => roles.Count <= OnboardingLimits.MaxRolesPerAccount)
            .WithMessage($"One account may be given at most {OnboardingLimits.MaxRolesPerAccount} roles.")
            .Must(roles => roles.Select(AccountShapeRules.NormalizeRoleName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == roles.Count)
            .WithMessage("A role named twice is not two grants, it is a client that does not know what it asked for.");

        // The one role this route will not put on an account. Its holder is checked before any permission claim is
        // read (PermissionAuthorization.cs:22-26), so a tenant-side route that could name it would be a way to mint
        // a caller that no tenant rule constrains.
        RuleFor(x => x.Roles)
            .Must(roles => roles.All(role => AccountShapeRules.NormalizeRoleName(role) != Roles.PlatformAdmin))
            .WithMessage($"'{Roles.PlatformAdmin}' is not grantable here. It answers every permission check in this " +
                         "server regardless of the claims on the token, so no route that stands inside a tenant may create it.");
    }
}

public class CreateAccountCommandHandler : IRequestHandler<CreateAccountCommand, AccountDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;

    public CreateAccountCommandHandler(IApplicationDbContext db, IPasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<AccountDto> Handle(CreateAccountCommand request, CancellationToken cancellationToken)
    {
        var email = AccountShapeRules.NormalizeEmail(request.Email);
        await AccountGuards.RequireEmailFreeAsync(_db, email, cancellationToken);

        var wanted = request.Roles.Select(AccountShapeRules.NormalizeRoleName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var granted = await AccountGuards.ResolveSystemRolesAsync(_db, wanted, cancellationToken);

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            Email = email,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim(),
            PasswordHash = _hasher.HashPassword(request.Password),
            IsActive = true
        };

        foreach (var (name, roleId) in granted)
        {
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
        }

        _db.Users.Add(user);
        await AccountGuards.SaveAccountWriteAsync(_db, email, slug: null, cancellationToken);

        return new AccountDto(user.Id, user.Email, user.FirstName, user.LastName, user.PhoneNumber, user.IsActive,
            granted.Select(g => g.Name).ToList());
    }
}

public record AccountStatusDto(Guid Id, string Email, bool IsActive);

/// <summary>
/// The only writer of <c>User.IsActive</c>, the column ACT-01 made mean something: an account switched off cannot
/// sign in <em>or</em> refresh. Until this command existed, that rule had a reader and no route, so turning off a
/// departed receptionist was an <c>UPDATE</c>.
///
/// A caller cannot switch off the account they are signed in as. There is no self-service reactivation anywhere in
/// this API, so allowing it would make one misclick an outage for one person and a helpdesk ticket for whoever has
/// to run SQL.
/// </summary>
public record SetAccountStatusCommand(Guid TenantId, Guid UserId, bool IsActive) : IRequest<AccountStatusDto>;

public class SetAccountStatusCommandValidator : AbstractValidator<SetAccountStatusCommand>
{
    public SetAccountStatusCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
    }
}

public class SetAccountStatusCommandHandler : IRequestHandler<SetAccountStatusCommand, AccountStatusDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;

    public SetAccountStatusCommandHandler(IApplicationDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<AccountStatusDto> Handle(SetAccountStatusCommand request, CancellationToken cancellationToken)
    {
        // Both the id and the tenant, in one predicate. Users carries no global tenant filter (it is not an
        // ITenantEntity), so this is the read that decides whether another tenant's account id is a 404 or a 200 —
        // and it is the first of those, which is what every other tenant-scoped write in this API answers.
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == request.UserId && u.TenantId == request.TenantId, cancellationToken)
            ?? throw new NotFoundException($"Account with ID {request.UserId} was not found.");

        if (!request.IsActive && _currentUser.UserId == user.Id)
        {
            throw new BusinessRuleValidationException(
                "CannotDeactivateOwnAccount",
                "This is the account you are signed in with, and nothing here can switch it back on.");
        }

        user.IsActive = request.IsActive;
        await _db.SaveChangesAsync(cancellationToken);

        return new AccountStatusDto(user.Id, user.Email, user.IsActive);
    }
}

/// <summary>
/// The rules two writes share, because an account created as a tenant's owner and an account hired later are the same
/// kind of object and must not be judged by different yardsticks.
/// </summary>
internal static class AccountShapeRules
{
    public static string NormalizeEmail(string email) => email.Trim();

    public static string NormalizeRoleName(string role) => role.Trim();

    /// <summary>
    /// Applies the password rule from the one place it is written (<see cref="PasswordPolicy"/>), rather than a copy
    /// of 12 and 256 here, and the length bounds from the columns.
    /// </summary>
    public static void For<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, string>> email,
        System.Linq.Expressions.Expression<Func<T, string>> password,
        System.Linq.Expressions.Expression<Func<T, string>> firstName,
        System.Linq.Expressions.Expression<Func<T, string>> lastName,
        System.Linq.Expressions.Expression<Func<T, string?>> phoneNumber)
    {
        validator.RuleFor(email)
            .NotEmpty().WithMessage("An account is signed in by its email address, so it has to have one.")
            .MaximumLength(OnboardingLimits.MaxEmailCharacters)
            .WithMessage($"Email must be {OnboardingLimits.MaxEmailCharacters} characters or fewer.")
            .EmailAddress().WithMessage("That is not an email address this server can sign anyone in with.");

        validator.RuleFor(password)
            .NotEmpty()
            .Must(value => PasswordPolicy.RefusalFor(value) is null)
            .WithMessage($"Password must be between {PasswordPolicy.MinLength} and {PasswordPolicy.MaxLength} characters.");

        validator.RuleFor(firstName)
            .NotEmpty().WithMessage("A first name is what a booking list shows.")
            .MaximumLength(OnboardingLimits.MaxPersonNameCharacters)
            .WithMessage($"First name must be {OnboardingLimits.MaxPersonNameCharacters} characters or fewer.");

        validator.RuleFor(lastName)
            .NotEmpty().WithMessage("A last name is what a booking list shows.")
            .MaximumLength(OnboardingLimits.MaxPersonNameCharacters)
            .WithMessage($"Last name must be {OnboardingLimits.MaxPersonNameCharacters} characters or fewer.");

        validator.RuleFor(phoneNumber)
            .MaximumLength(OnboardingLimits.MaxPhoneNumberCharacters)
            .WithMessage($"Phone number must be {OnboardingLimits.MaxPhoneNumberCharacters} characters or fewer.");
    }
}

internal static class AccountGuards
{
    /// <summary>
    /// The rule "one address, one account", written twice on purpose: read here before the write, and enforced by
    /// <c>IX_Users_Email</c> at the write. The read alone cannot close it — two requests that both find the address
    /// free both go on to insert, and the hash costs them both about a tenth of a second, which is a wide enough
    /// window for a client retrying a timeout to land in. The index is the thing that decides, and this turns what
    /// it decides back into the same refusal the read gives, so the loser of the race is told the address was taken
    /// rather than handed a 500 for a collision its own request participated in.
    ///
    /// Only a duplicate on the two indexes named here is translated. Anything else the save throws is a different
    /// failure and has to stay a different failure.
    /// </summary>
    public static async Task SaveAccountWriteAsync(
        IApplicationDbContext db,
        string email,
        string? slug,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            var message = exception.InnerException?.Message ?? exception.Message;

            if (slug is not null && message.Contains("IX_Tenants_Slug", StringComparison.Ordinal))
            {
                throw new BusinessRuleValidationException(
                    "TenantSlugAlreadyTaken",
                    $"Another tenant took the slug '{slug}' while this request was in flight.");
            }

            if (message.Contains("IX_Users_Email", StringComparison.Ordinal))
            {
                throw new BusinessRuleValidationException(
                    "EmailAlreadyRegistered",
                    $"That email was claimed by a write that raced this one, and a sign-in resolves an address to one account.");
            }

            throw;
        }
    }

    /// <summary>
    /// Refuses an address that already names an account <em>anywhere on this server</em>, not merely in this tenant.
    /// That is wider than the schema used to say, and it is the sign-in read that decides: a login resolves one row by
    /// <c>Email</c> alone (<c>AuthController.cs:342</c>), so two tenants holding the same address is not two accounts,
    /// it is one account whose tenant an <c>ORDER BY</c> nobody wrote would choose. The unique index this change adds
    /// is the same rule expressed in the database, for the writes that race this read.
    ///
    /// <c>IgnoreQueryFilters</c> for the same reason the slug check above has it: <c>IX_Users_Email</c> carries no
    /// filter, so a soft-deleted account still owns its address, and a read that ran the <c>!IsDeleted</c> filter would
    /// call such an address free and leave the database to refuse it. The policy is the index's either way — the
    /// pre-check only decides which sentence the caller reads, and "that email already names an account" is the true
    /// one where the race branch would say "a write that raced this one" about a request with no competitor at all.
    /// </summary>
    public static async Task RequireEmailFreeAsync(IApplicationDbContext db, string email, CancellationToken cancellationToken)
    {
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new BusinessRuleValidationException(
                "EmailAlreadyRegistered",
                "That email already names an account on this server, and a sign-in resolves an address to one account.");
        }
    }

    /// <summary>
    /// Resolves one role name to a row. The two extra predicates are not decoration: <c>TenantId IS NULL</c> because
    /// a tenant may make a role of its own and give it any name it likes — the unique index is
    /// <c>(TenantId, Name)</c> — and <c>IsSystemRole</c> because the permission set a name grants comes from the
    /// seeded <c>RolePermission</c> rows, which only system roles have.
    /// </summary>
    public static async Task<Guid> RequireSystemRoleIdAsync(IApplicationDbContext db, string name, CancellationToken cancellationToken)
    {
        var roleId = await FindSystemRoleIdAsync(db, name, cancellationToken);

        return roleId ?? throw new BusinessRuleValidationException(
            "RoleNotSeeded",
            $"This database has no system '{name}' role row, so nothing can be granted it. Run the seeder.");
    }

    public static async Task<IReadOnlyList<(string Name, Guid RoleId)>> ResolveSystemRolesAsync(
        IApplicationDbContext db,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var resolved = new List<(string Name, Guid RoleId)>();

        foreach (var name in names)
        {
            var roleId = await FindSystemRoleIdAsync(db, name, cancellationToken);

            if (roleId is null)
            {
                throw new BusinessRuleValidationException(
                    "RoleNotGrantable",
                    $"'{name}' is not a role this server can grant. It is not a seeded system role of this deployment.");
            }

            resolved.Add((name, roleId.Value));
        }

        return resolved;
    }

    private static async Task<Guid?> FindSystemRoleIdAsync(IApplicationDbContext db, string name, CancellationToken cancellationToken) =>
        await db.Roles
            .IgnoreQueryFilters()
            .Where(r => r.Name == name && r.TenantId == null && r.IsSystemRole)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
