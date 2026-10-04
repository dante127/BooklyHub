using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using BooklyHub.Application.Scheduling;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Services;
using BooklyHub.Domain.Entities.StaffMembers;
using BooklyHub.Domain.Entities.Tenancy;
using BooklyHub.Infrastructure.Data;
using BooklyHub.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BooklyHub.IntegrationTests.Performance;

/// <summary>
/// PERF-09. The availability guard reads one location's occupancy for a forward window, and the schema had no
/// index leading with (TenantId, LocationId): the optimizer intersected three indexes and paid clustered key
/// lookups to answer a tomorrow check. These two tests pin the shape that measurement picked — location equality
/// prefix, EndAtUtc as the single seekable range, the projection in INCLUDE — and the property that made it win:
/// the guard's own statement reaches Appointments through one access path and reads a fraction of the table.
/// The read test drives the real service over the real connection so a query change that stops being covered
/// fails here instead of quietly costing pages in production.
/// </summary>
public class OccupancyIndexTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const string IndexName = "IX_Appointments_Tenant_Location_EndAt";
    private const int RowsPerLocation = 4_000;

    private readonly BooklyHubWebApplicationFactory _factory;

    public OccupancyIndexTests(BooklyHubWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Graph(Guid Tenant, Guid Location, Guid Service, Guid Staff);

    [Fact]
    public async Task Occupancy_index_must_lead_with_the_location_seek_on_the_forward_bound_and_cover_the_projection()
    {
        var (keys, included) = await ReadIndexShapeAsync();

        keys.Should().NotBeEmpty($"{IndexName} is not in the schema");
        keys.Should().Equal("TenantId", "LocationId", "EndAtUtc");
        included.Should().BeEquivalentTo("StartAtUtc", "StaffId", "Status");
    }

    [Fact]
    public async Task Occupancy_read_must_reach_appointments_through_one_access_path_and_read_a_fraction_of_the_table()
    {
        var graph = await SeedAsync();

        var clusteredPages = await ScalarAsync<long>("""
            SELECT SUM(CAST(au.total_pages AS bigint))
            FROM sys.allocation_units AS au
            JOIN sys.partitions AS p ON p.partition_id = au.container_id
            WHERE p.object_id = OBJECT_ID('dbo.Appointments') AND p.index_id = 1 AND au.type_desc = 'IN_ROW_DATA'
            """);

        var accesses = await ReadOccupancyIoAsync(graph);

        accesses.Should().HaveCount(1,
            "one CheckSlotAsync holds exactly one occupancy read; a second access path means the optimizer went back to intersecting");

        var (scanCount, logicalReads) = accesses[0];

        scanCount.Should().Be(1,
            $"the read must be served by {IndexName} alone; anything above 1 means key lookups or index intersection came back");
        logicalReads.Should().BeLessThan(clusteredPages / 4,
            $"the read costs {logicalReads} pages while the table holds {clusteredPages}; a covering seek on the forward range must not touch a quarter of it");
    }

    /// <summary>Key columns in ordinal order, then the INCLUDE set.</summary>
    private async Task<(string[] Keys, string[] Included)> ReadIndexShapeAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("""
            SELECT c.name, CAST(ic.key_ordinal AS int), CAST(ic.is_included_column AS int)
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.Appointments') AND i.name = @name
            """, connection);
        command.Parameters.AddWithValue("@name", IndexName);

        var keys = new List<(int Ordinal, string Name)>();
        var included = new List<string>();

        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (reader.GetInt32(2) == 1)
                {
                    included.Add(reader.GetString(0));
                }
                else
                {
                    keys.Add((reader.GetInt32(1), reader.GetString(0)));
                }
            }
        }

        return (keys.OrderBy(k => k.Ordinal).Select(k => k.Name).ToArray(), included.ToArray());
    }

    /// <summary>
    /// Runs the guard on a connection that reports every page it touches, so the assertion is about the statement
    /// production sends rather than a hand-written copy of it. The context's connection is opened first and kept
    /// open, because the statistics notice belongs to the session that turned them on.
    /// </summary>
    private async Task<IReadOnlyList<(int ScanCount, long LogicalReads)>> ReadOccupancyIoAsync(Graph graph)
    {
        var notices = new ConcurrentQueue<string>();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BooklyHub.Application.Common.Interfaces.ITenantContext>()
            .SetTenant(graph.Tenant, isPlatformAdmin: false);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var availability = scope.ServiceProvider.GetRequiredService<IAvailabilityService>();

        await db.Database.OpenConnectionAsync();
        try
        {
            var sqlConnection = (SqlConnection)db.Database.GetDbConnection();
            sqlConnection.InfoMessage += (_, e) => notices.Enqueue(e.Message);

            await db.Database.ExecuteSqlRawAsync("SET STATISTICS IO ON");

            var slot = DateTime.UtcNow.Date.AddDays(1).AddHours(10);
            await availability.CheckSlotAsync(graph.Tenant, graph.Location, graph.Service, graph.Staff,
                slot, slot.AddMinutes(30));

            await db.Database.ExecuteSqlRawAsync("SET STATISTICS IO OFF");
        }
        finally
        {
            db.Database.CloseConnection();
        }

        var text = string.Join(Environment.NewLine, notices);
        return Regex.Matches(text, @"Table 'Appointments'\. Scan count (\d+), logical reads (\d+)")
            .Cast<Match>()
            .Select(m => (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                          long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var scalar = await command.ExecuteScalarAsync();
        return scalar is T typed ? typed : throw new InvalidOperationException($"unexpected scalar '{scalar}'");
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>
    /// Two locations for one tenant, each holding <see cref="RowsPerLocation"/> appointments spread over four
    /// years, because the cost the index avoids is proportional to how far back the location's own history goes.
    /// </summary>
    private async Task<Graph> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var otherLocationId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var tenant = new Tenant(tenantId, "Occupancy Index Tenant", $"occ-{tenantId:N}", "UTC")
            {
                Settings = new TenantSetting(tenantId)
                {
                    MinBookingNoticeMinutes = 10,
                    MaxAdvanceBookingDays = 730,
                    SlotIntervalMinutes = 30
                }
            };
            db.Tenants.Add(tenant);

            foreach (var (id, name) in new[] { (locationId, "Measured"), (otherLocationId, "Neighbour") })
            {
                db.Locations.Add(new Location
                {
                    Id = id,
                    TenantId = tenantId,
                    Name = name,
                    Address = "1 Main St",
                    City = "Damascus",
                    Country = "SY",
                    TimeZoneId = "UTC"
                });
            }

            db.Services.Add(new Service
            {
                Id = serviceId,
                TenantId = tenantId,
                Name = "Procedure",
                DurationMinutes = 30,
                Price = 100.00m,
                BufferBeforeMinutes = 0,
                BufferAfterMinutes = 0
            });

            var staff = new Staff
            {
                Id = staffId,
                TenantId = tenantId,
                LocationId = locationId,
                FirstName = "Nurse",
                LastName = "One",
                Email = $"nurse-{tenantId:N}@occ.test"
            };
            staff.StaffServices.Add(new StaffService { TenantId = tenantId, StaffId = staffId, ServiceId = serviceId });
            db.StaffMembers.Add(staff);

            db.Customers.Add(new Customer
            {
                Id = customerId,
                TenantId = tenantId,
                FirstName = "Patient",
                LastName = "One",
                Email = $"patient-{tenantId:N}@occ.test"
            });

            await db.SaveChangesAsync();
        }

        await using var connection = await OpenAsync();
        foreach (var location in new[] { locationId, otherLocationId })
        {
            await using var command = new SqlCommand("""
                ;WITH tally AS (
                    SELECT TOP (@rows) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
                    FROM sys.columns a CROSS JOIN sys.columns b)
                INSERT INTO Appointments
                    (Id, TenantId, LocationId, ServiceId, StaffId, CustomerId, RecurringAppointmentId,
                     StartAtUtc, EndAtUtc, DurationMinutes, Price, Currency, Status,
                     CreatedAtUtc, CreatedBy, LastModifiedAtUtc, LastModifiedBy)
                SELECT NEWID(), @tenant, @location, @service, @staff, @customer, NULL,
                       s.StartAtUtc, DATEADD(minute, 30, s.StartAtUtc), 30, 100.00, 'SYP',
                       CASE WHEN s.n % 10 = 0 THEN 6 ELSE 2 END,
                       @now, NULL, NULL, NULL
                FROM tally
                CROSS APPLY (
                    SELECT tally.n,
                           DATEADD(minute, (tally.n % 18) * 30 + 540,
                                   DATEADD(day, (tally.n % 1460) - 730, CAST(CAST(@now AS date) AS datetime2))) AS StartAtUtc
                ) AS s
                """, connection);
            command.Parameters.AddWithValue("@rows", RowsPerLocation);
            command.Parameters.AddWithValue("@tenant", tenantId);
            command.Parameters.AddWithValue("@location", location);
            command.Parameters.AddWithValue("@service", serviceId);
            command.Parameters.AddWithValue("@staff", staffId);
            command.Parameters.AddWithValue("@customer", customerId);
            command.Parameters.AddWithValue("@now", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync();
        }

        await using var statistics = new SqlCommand("UPDATE STATISTICS dbo.Appointments WITH FULLSCAN;", connection);
        await statistics.ExecuteNonQueryAsync();

        return new Graph(tenantId, locationId, serviceId, staffId);
    }
}
