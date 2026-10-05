using System.IdentityModel.Tokens.Jwt;
using Pulse.Application.Auth;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Multi-tenancy Phase 1a: the caller's organization travels from the engineer row, into the access
/// token, onto every database connection as app.current_org_id — and the org columns are required.
/// </summary>
[Collection("Integration")]
public class OrganizationContextTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public OrganizationContextTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task The_access_token_carries_the_engineers_organization()
    {
        var engineer = await SeedEngineerAsync("org_claim@pulse.io");

        var token = await LoginTokenAsync("org_claim@pulse.io");

        var claim = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
            .Single(c => c.Type == PulseClaimTypes.OrganizationId);
        claim.Value.Should().Be(engineer.OrganizationId.ToString());
        claim.Value.Should().Be(Organization.DefaultId.ToString());
    }

    [Fact]
    public async Task The_interceptor_stamps_the_organization_onto_the_connection()
    {
        var orgId = Guid.NewGuid();

        (await CurrentOrgIdAsync(new FixedRlsContext("engineer", Guid.NewGuid().ToString(), orgId.ToString())))
            .Should().Be(orgId);
    }

    [Fact]
    public async Task No_organization_reads_back_as_null_not_an_error()
    {
        (await CurrentOrgIdAsync(new FixedRlsContext("service", "", ""))).Should().BeNull();
    }

    [Theory]
    [InlineData("teams")]
    [InlineData("engineers")]
    [InlineData("projects")]
    public async Task Organization_id_can_no_longer_be_null(string table)
    {
        await using var conn = new NpgsqlConnection(Factory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table AND column_name = 'organization_id'
            """;
        cmd.Parameters.AddWithValue("table", table);

        (await cmd.ExecuteScalarAsync()).Should().Be("NO");
    }

    private async Task<Guid?> CurrentOrgIdAsync(IRlsContext rls)
    {
        var options = new DbContextOptionsBuilder<PulseDbContext>()
            .UseNpgsql(Factory.ConnectionString)
            .AddInterceptors(new RlsConnectionInterceptor(rls))
            .Options;
        await using var db = new PulseDbContext(options);

        return await db.Database
            .SqlQueryRaw<Guid?>("select app.current_org_id() as \"Value\"")
            .SingleAsync();
    }

    private sealed class FixedRlsContext(string role, string userId, string orgId) : IRlsContext
    {
        public (string Role, string UserId, string OrganizationId) Resolve() => (role, userId, orgId);
    }
}
