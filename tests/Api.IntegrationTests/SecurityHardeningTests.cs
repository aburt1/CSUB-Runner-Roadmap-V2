using Api.Auth;
using Api.Controllers;
using Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.IntegrationTests;

// Unit tests for the production fail-safe guards (no server/DB needed).
public class SecurityHardeningTests
{
    private static IConfiguration Config(string? jwtSecret) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Secret"] = jwtSecret })
            .Build();

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static readonly IHostEnvironment Production = new FakeEnv("Production");
    private static readonly IHostEnvironment Development = new FakeEnv("Development");

    [Fact]
    public void Missing_jwt_secret_throws() =>
        Assert.Throws<InvalidOperationException>(() => new JwtService(Config(null), Development));

    [Fact]
    public void Weak_default_jwt_secret_in_production_throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            new JwtService(Config("change-me-in-production-0123456789-abcdefghijklmnop"), Production));

    [Fact]
    public void Short_jwt_secret_in_production_throws() =>
        Assert.Throws<InvalidOperationException>(() => new JwtService(Config("too-short-secret"), Production));

    [Fact]
    public void Strong_jwt_secret_in_production_is_accepted()
    {
        var svc = new JwtService(Config("Zq" + new string('K', 44)), Production);
        Assert.False(string.IsNullOrEmpty(svc.IssueAdminToken("1", "sysadmin", "a@b.c", "Admin")));
    }

    [Fact]
    public void Dev_default_jwt_secret_is_allowed_in_development() =>
        Assert.NotNull(new JwtService(Config("dev-only-jwt-secret-change-me-0123456789-abcdef"), Development));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin123")]
    [InlineData("short")]
    [InlineData("CHANGE_ME_Strong_Admin_Pass_1!")]
    public void Weak_admin_passwords_are_rejected(string? password) =>
        Assert.True(Seeder.IsWeakAdminPassword(password));

    [Theory]
    [InlineData("a-genuinely-strong-passphrase-2026")]
    [InlineData("Str0ng!Passw0rd#here")]
    public void Strong_admin_passwords_are_accepted(string password) =>
        Assert.False(Seeder.IsWeakAdminPassword(password));

    // Break-glass with a weak/placeholder password is fail-closed in Production: the
    // endpoint behaves as if unconfigured (404) so a guessable emergency credential can
    // never grant sysadmin. The weak-password path returns before touching DB/HttpContext.
    [Fact]
    public async Task Break_glass_with_weak_password_in_production_is_disabled()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalLogin:Username"] = "localadmin",
                ["LocalLogin:Password"] = "admin123", // weak -> rejected in Production
            })
            .Build();
        var controller = new AdminAuthController(
            null!, null!, null!, config, Production, NullLogger<AdminAuthController>.Instance);

        var result = await controller.LocalLogin(
            new AdminAuthController.LocalLoginRequest("localadmin", "admin123"));

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
