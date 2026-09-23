using System.IdentityModel.Tokens.Jwt;
using BooklyHub.Application.Security;
using BooklyHub.Domain.Entities.Identity;
using BooklyHub.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BooklyHub.UnitTests.Security;

public class SecurityTests
{
    private readonly PasswordHasher _passwordHasher = new();
    private readonly JwtTokenGenerator _jwtTokenGenerator;

    public SecurityTests()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "Testing_Super_Secret_Key_For_BooklyHub_SaaS_2026_1234567890!",
            ["Jwt:Issuer"] = "BooklyHub",
            ["Jwt:Audience"] = "BooklyHubClients",
            ["Jwt:ExpirationMinutes"] = "30"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        _jwtTokenGenerator = new JwtTokenGenerator(configuration);
    }

    [Fact]
    public void HashPassword_ShouldGenerateSecureSaltedHash()
    {
        var password = "SecurePassword123!";
        var hash1 = _passwordHasher.HashPassword(password);
        var hash2 = _passwordHasher.HashPassword(password);

        hash1.Should().NotBeNullOrWhiteSpace();
        hash2.Should().NotBeNullOrWhiteSpace();
        hash1.Should().NotBe(hash2); // Different salts!
    }

    [Fact]
    public void VerifyPassword_WithCorrectPassword_ShouldReturnTrue()
    {
        var password = "CorrectHorseBatteryStaple!";
        var hash = _passwordHasher.HashPassword(password);

        var isValid = _passwordHasher.VerifyPassword(password, hash);
        isValid.Should().BeTrue();
    }

    [Fact]
    public void VerifyPassword_WithWrongPassword_ShouldReturnFalse()
    {
        var password = "CorrectPassword!";
        var wrongPassword = "WrongPassword!";
        var hash = _passwordHasher.HashPassword(password);

        var isValid = _passwordHasher.VerifyPassword(wrongPassword, hash);
        isValid.Should().BeFalse();
    }

    [Fact]
    public void GenerateAccessToken_ShouldIncludeTenantIdAndPermissions()
    {
        var tenantId = Guid.NewGuid();
        var user = new User
        {
            TenantId = tenantId,
            Email = "doctor@clinic.com",
            FirstName = "John",
            LastName = "Doe"
        };

        var roles = new[] { Roles.Staff };
        var permissions = new[] { Permissions.Appointments.Read, Permissions.Appointments.Create };

        var tokenString = _jwtTokenGenerator.GenerateAccessToken(user, roles, permissions);
        tokenString.Should().NotBeNullOrWhiteSpace();

        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(tokenString);

        token.Claims.Should().Contain(c => c.Type == "tenant_id" && c.Value == tenantId.ToString());
        token.Claims.Should().Contain(c => (c.Type == System.Security.Claims.ClaimTypes.Role || c.Type == "role") && c.Value == Roles.Staff);
        token.Claims.Should().Contain(c => c.Type == "permission" && c.Value == Permissions.Appointments.Read);
        token.Claims.Should().Contain(c => c.Type == "permission" && c.Value == Permissions.Appointments.Create);
    }

    [Fact]
    public void RolePermissions_DefaultMappings_ShouldBeConsistent()
    {
        var staffPermissions = Permissions.GetDefaultPermissionsForRole(Roles.Staff);
        staffPermissions.Should().Contain(Permissions.Appointments.Read);
        staffPermissions.Should().NotContain(Permissions.Tenancy.Manage);
        staffPermissions.Should().NotContain(Permissions.Reports.Read);

        var adminPermissions = Permissions.GetDefaultPermissionsForRole(Roles.PlatformAdmin);
        adminPermissions.Should().Contain(Permissions.Tenancy.Manage);
        adminPermissions.Should().Contain(Permissions.Reports.Read);
    }
}
