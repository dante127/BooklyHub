using System.Globalization;
using System.Text.RegularExpressions;
using BooklyHub.Application.Scheduling;
using BooklyHub.Domain.Entities.Customers;
using BooklyHub.Domain.Entities.Organizations;
using BooklyHub.Domain.Entities.Scheduling;
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
/// PERF-05. The booking guard's holiday read ORs a date range with <see cref="Holiday.RecurringAnnually"/>, and the
/// audit diagnosed that OR as one column short of the key. The measurement disagreed about the remedy: the tenant and
/// the date are already the key, and the read does not need a seekable recurring column — it needs the filter's and the
/// projection's other two columns <em>in</em> the index, because the alternative is a key lookup per row of the tenant's
/// calendar into a clustered index keyed on a random Guid. Measured on the guard's own statement over a 40,040-row
/// table whose tenant held 40 rows: 124 logical reads uncovered, 4 covered. The same replay on a 200,040-row table went
/// further the other way — the optimizer abandoned the index entirely and answered with one clustered scan of every
/// tenant's calendar, 3,045 reads for 10 rows (PERFORMANCE.md §7, DATABASE.md §3.2).
/// The third fact is the one that keeps the first two honest: no other test in this suite reaches the recurring half of
/// the OR, so a covered seek that quietly returned nothing would have looked exactly like a speed-up.
/// </summary>
public class HolidayIndexTests : IClassFixture<BooklyHubWebApplicationFactory>
{
    private const string IndexName = "IX_Holidays_TenantId_Date";
    private const int RowsForMeasuredTenant = 40;
    private const int RecurringRowsForMeasuredTenant = 10;

    /// <summary>The cost the index avoids is the whole table's, so the volume has to be other tenants' rows.</summary>
    private const int FillerTenants = 1_000;

    private readonly BooklyHubWebApplicationFactory _factory;

    public HolidayIndexTests(BooklyHubWebApplicationFactory factory) => _factory = factory;

    private sealed record Graph(Guid Tenant, Guid Location, Guid Service, Guid Staff);

    [Fact]
    public async Task Holiday_index_must_cover_the_two_columns_the_recurring_OR_reads()
    {
        var (keys, included) = await ReadIndexShapeAsync();

        keys.Should().NotBeEmpty($"{IndexName} is not in the schema");
        keys.Should().Equal("TenantId", "Date");
        included.Should().BeEquivalentTo("LocationId", "RecurringAnnually");
    }

    [Fact]
    public async Task Holiday_read_must_cost_the_measured_tenant_not_the_whole_table()
    {
        var graph = await SeedVolumeAsync();

        var clusteredPages = await ScalarAsync<long>("""
            SELECT SUM(CAST(au.total_pages AS bigint))
            FROM sys.allocation_units AS au
            JOIN sys.partitions AS p ON p.partition_id = au.container_id
            WHERE p.object_id = OBJECT_ID('dbo.Holidays') AND p.index_id = 1 AND au.type_desc = 'IN_ROW_DATA'
            """);

        clusteredPages.Should().BeGreaterThan(40,
            $"{FillerTenants} filler tenants were seeded; a table this small would let any plan pass");

        var accesses = await ReadHolidayIoAsync(graph, DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2)));

        accesses.Should().HaveCount(1,
            "one slot check loads the calendar once, so there is exactly one access to Holidays to judge");

        var (scanCount, logicalReads) = accesses[0];

        // Measured at 1 on both sides of the fix, so this is not what discriminates; it catches a future plan that
        // reaches the table twice for one calendar load. The page count below is the one that decides.
        scanCount.Should().Be(1, "one slot check reaches Holidays once, whatever index the optimizer picks");
        logicalReads.Should().BeLessThan(clusteredPages / 20,
            $"the read costs {logicalReads} pages while the table holds {clusteredPages}: the covering seek pays for the " +
            "measured tenant's own leaf pages. Without the two included columns this same statement cost 124 pages on " +
            "this seed, paying a key lookup per row into a clustered index keyed on a random Guid");
    }

    /// <summary>
    /// The seek has to keep answering the recurring half, not just read fewer pages: an annual holiday is matched by
    /// month and day, so it is exactly the row the date-range seek cannot find on its own.
    /// </summary>
    [Fact]
    public async Task Recurring_annual_holiday_must_still_close_its_day()
    {
        var holidayDay = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2));
        var graph = await SeedGraphAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Holidays.Add(new Holiday
            {
                TenantId = graph.Tenant,
                LocationId = graph.Location,
                Name = "Annual closure",
                Date = new DateOnly(holidayDay.Year - 5, holidayDay.Month, holidayDay.Day),
                RecurringAnnually = true
            });
            await db.SaveChangesAsync();
        }

        var closed = await CheckSlotAsync(graph, holidayDay);
        var open = await CheckSlotAsync(graph, holidayDay.AddDays(1));

        closed.Should().Be(SlotUnavailableReason.Closed,
            "a recurring holiday on this month and day closes the day, and the covered seek is what returns it");
        open.Should().NotBe(SlotUnavailableReason.Closed,
            "the next day is not a holiday; if it were, the fact above would be answering every date the same way");
    }

    /// <summary>Runs one slot check through the service, in process, with the tenant bound so the filter lets the read through.</summary>
    private async Task<SlotUnavailableReason?> CheckSlotAsync(Graph graph, DateOnly day)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BooklyHub.Application.Common.Interfaces.ITenantContext>()
            .SetTenant(graph.Tenant, isPlatformAdmin: false);

        var availability = scope.ServiceProvider.GetRequiredService<IAvailabilityService>();
        var start = day.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc);

        var result = await availability.CheckSlotAsync(
            graph.Tenant, graph.Location, graph.Service, graph.Staff, start, start.AddMinutes(30));

        return result.Reason;
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
            WHERE i.object_id = OBJECT_ID('dbo.Holidays') AND i.name = @name
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
    /// The guard's own statement on a session that reports every page it touches, so the number is what production
    /// pays and not a hand-written copy of the query. The connection is opened first and kept open: the statistics
    /// notice belongs to the session that turned them on, and it arrives on that session's next round trip.
    /// </summary>
    private async Task<IReadOnlyList<(int ScanCount, long LogicalReads)>> ReadHolidayIoAsync(Graph graph, DateOnly day)
    {
        var notices = new List<string>();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BooklyHub.Application.Common.Interfaces.ITenantContext>()
            .SetTenant(graph.Tenant, isPlatformAdmin: false);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var availability = scope.ServiceProvider.GetRequiredService<IAvailabilityService>();

        await db.Database.OpenConnectionAsync();
        try
        {
            ((SqlConnection)db.Database.GetDbConnection()).InfoMessage += (_, e) => notices.Add(e.Message);
            await db.Database.ExecuteSqlRawAsync("SET STATISTICS IO ON");

            var start = day.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc);
            await availability.CheckSlotAsync(
                graph.Tenant, graph.Location, graph.Service, graph.Staff, start, start.AddMinutes(30));

            await db.Database.ExecuteSqlRawAsync("SET STATISTICS IO OFF");
        }
        finally
        {
            db.Database.CloseConnection();
        }

        var text = string.Join(Environment.NewLine, notices);
        return Regex.Matches(text, @"Table 'Holidays'\. Scan count (\d+), logical reads (\d+)")
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

    private async Task<Graph> SeedGraphAsync()
    {
        var tenantId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Tenants.Add(new Tenant(tenantId, "Holiday Index Tenant", $"hol-{tenantId:N}", "UTC")
        {
            Settings = new TenantSetting(tenantId)
            {
                MinBookingNoticeMinutes = 10,
                MaxAdvanceBookingDays = 730,
                SlotIntervalMinutes = 30
            }
        });

        db.Locations.Add(new Location
        {
            Id = locationId,
            TenantId = tenantId,
            Name = "Measured",
            Address = "1 Main St",
            City = "Damascus",
            Country = "SY",
            TimeZoneId = "UTC"
        });

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
            Email = $"nurse-{tenantId:N}@holiday.test"
        };
        staff.StaffServices.Add(new StaffService { TenantId = tenantId, StaffId = staffId, ServiceId = serviceId });
        db.StaffMembers.Add(staff);

        db.Customers.Add(new Customer
        {
            Id = customerId,
            TenantId = tenantId,
            FirstName = "Patient",
            LastName = "One",
            Email = $"patient-{tenantId:N}@holiday.test"
        });

        await db.SaveChangesAsync();
        return new Graph(tenantId, locationId, serviceId, staffId);
    }

    /// <summary>
    /// The measured tenant's calendar plus <see cref="FillerTenants"/> other tenants' calendars, because the plan this
    /// index replaces reads the whole table: without the filler there is nothing for a scan and a seek to disagree about.
    /// Rows are calendar-shaped — a recurring share matched by month and day, dated one-offs spread over eight years,
    /// and tenant-wide rows with NULL LocationId.
    /// </summary>
    private async Task<Graph> SeedVolumeAsync()
    {
        var graph = await SeedGraphAsync();

        await using var connection = await OpenAsync();

        await using (var command = new SqlCommand("""
            ;WITH tally AS (
                SELECT TOP (@rows) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
                FROM sys.columns a CROSS JOIN sys.columns b)
            INSERT INTO Holidays (Id, TenantId, LocationId, Name, [Date], RecurringAnnually)
            SELECT NEWID(), @tenant, CASE WHEN tally.n % 7 = 0 THEN NULL ELSE @location END,
                   'H' + CONVERT(varchar(20), tally.n),
                   CASE WHEN tally.n < @recurring
                        THEN DATEADD(day, tally.n % 366, '2021-01-01')
                        ELSE DATEADD(day, (tally.n * 37) % 2920, '2018-01-01') END,
                   CASE WHEN tally.n < @recurring THEN 1 ELSE 0 END
            FROM tally
            """, connection))
        {
            command.Parameters.AddWithValue("@rows", RowsForMeasuredTenant);
            command.Parameters.AddWithValue("@tenant", graph.Tenant);
            command.Parameters.AddWithValue("@location", graph.Location);
            command.Parameters.AddWithValue("@recurring", RecurringRowsForMeasuredTenant);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new SqlCommand("""
            ;WITH filler AS (
                SELECT TOP (@tenants) NEWID() AS tid
                FROM sys.columns a CROSS JOIN sys.columns b)
            INSERT INTO Holidays (Id, TenantId, LocationId, Name, [Date], RecurringAnnually)
            SELECT NEWID(), filler.tid, CASE WHEN x.n % 7 = 0 THEN NULL ELSE @location END,
                   'F' + CONVERT(varchar(20), x.n),
                   DATEADD(day, (x.n * 37) % 2920, '2018-01-01'),
                   CASE WHEN x.n < 10 THEN 1 ELSE 0 END
            FROM filler
            CROSS APPLY (SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
                         FROM sys.columns) AS x
            """, connection))
        {
            command.CommandTimeout = 300;
            command.Parameters.AddWithValue("@tenants", FillerTenants);
            command.Parameters.AddWithValue("@location", graph.Location);
            await command.ExecuteNonQueryAsync();
        }

        await using var statistics = new SqlCommand("UPDATE STATISTICS dbo.Holidays WITH FULLSCAN;", connection);
        await statistics.ExecuteNonQueryAsync();

        return graph;
    }
}
