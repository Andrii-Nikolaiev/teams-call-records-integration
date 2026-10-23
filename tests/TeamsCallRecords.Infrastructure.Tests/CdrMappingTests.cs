using Microsoft.EntityFrameworkCore;
using TeamsCallRecords.Core;
using TeamsCallRecords.Infrastructure.Data;
using TeamsCallRecords.Infrastructure.Data.Entities;
using TeamsCallRecords.Infrastructure.Tests.Support;

namespace TeamsCallRecords.Infrastructure.Tests;

public class CdrMappingTests
{
    private const string Now = "2026-10-20T18:00:00.0000000Z";

    public static IEnumerable<object[]> Samples() => Builders.RoundTripSamples().Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task RoundTrip_SavedAndRead_EqualsSource(Cdr source)
    {
        using var db = TestDb.Create();
        await db.Gate.RunAsync(async (ctx, ct) =>
        {
            ctx.Cdrs.Add(CdrMapping.ToRow(source, Now));
            await ctx.SaveChangesAsync(ct);
        }, CancellationToken.None);

        var row = await db.ReadAsync(ctx => ctx.Cdrs.AsNoTracking().SingleAsync());
        Builders.AssertEqual(source, CdrMapping.ToCdr(row));
    }

    [Fact]
    public async Task Ticks_ArePreservedWithoutLoss()
    {
        using var db = TestDb.Create();
        var start = DateTimeOffset.Parse("2026-10-20T09:00:00.1234567Z");
        var source = Builders.Cdr(start: start);

        await db.Gate.RunAsync(async (ctx, ct) =>
        {
            ctx.Cdrs.Add(CdrMapping.ToRow(source, Now));
            await ctx.SaveChangesAsync(ct);
        }, CancellationToken.None);

        var row = await db.ReadAsync(ctx => ctx.Cdrs.AsNoTracking().SingleAsync());
        Assert.Equal("2026-10-20T09:00:00.1234567Z", row.StartUtc);
        Assert.Equal(start.UtcTicks, CdrMapping.ToCdr(row).StartUtc.UtcTicks);
    }

    [Fact]
    public async Task NullCaller_WritesBothColumnsNull()
    {
        using var db = TestDb.Create();
        var source = Builders.Cdr(caller: null, callee: null);

        await db.Gate.RunAsync(async (ctx, ct) =>
        {
            ctx.Cdrs.Add(CdrMapping.ToRow(source, Now));
            await ctx.SaveChangesAsync(ct);
        }, CancellationToken.None);

        var row = await db.ReadAsync(ctx => ctx.Cdrs.AsNoTracking().SingleAsync());
        Assert.Null(row.CallerType);
        Assert.Null(row.CallerId);
        Assert.Null(row.CalleeType);
        Assert.Null(row.CalleeId);
        var back = CdrMapping.ToCdr(row);
        Assert.Null(back.Caller);
        Assert.Null(back.Callee);
    }

    [Fact]
    public async Task Modalities_AreJsonArray()
    {
        using var db = TestDb.Create();
        var source = Builders.Cdr(modalities: new[] { "audio", "video" });

        await db.Gate.RunAsync(async (ctx, ct) =>
        {
            ctx.Cdrs.Add(CdrMapping.ToRow(source, Now));
            await ctx.SaveChangesAsync(ct);
        }, CancellationToken.None);

        var row = await db.ReadAsync(ctx => ctx.Cdrs.AsNoTracking().SingleAsync());
        Assert.Equal("[\"audio\",\"video\"]", row.Modalities);
        Assert.Equal(new[] { "audio", "video" }, CdrMapping.ToCdr(row).Modalities);
    }

    [Fact]
    public void CopyOnUpdate_KeepsSeqAndCreated()
    {
        var existing = CdrMapping.ToRow(Builders.Cdr(version: 1), "2026-10-20T18:00:00.0000000Z");
        existing.CdrSeq = 1;
        var source = CdrMapping.ToRow(
            Builders.Cdr(version: 2, durationSeconds: 120, status: CdrStatus.Failed, terminationReason: "x"),
            "2026-10-20T19:00:00.0000000Z");
        source.CdrSeq = 99;

        CdrMapping.CopyOnUpdate(existing, source, "2026-10-20T20:00:00.0000000Z");

        Assert.Equal(1, existing.CdrSeq);
        Assert.Equal("2026-10-20T18:00:00.0000000Z", existing.CreatedUtc);
        Assert.Equal(2, existing.SourceVersion);
        Assert.Equal(120, existing.DurationS);
        Assert.Equal("failed", existing.Status);
        Assert.Equal("x", existing.TerminationReason);
        Assert.Equal("2026-10-20T20:00:00.0000000Z", existing.UpdatedUtc);
    }

    [Fact]
    public void ToRow_SetsCreatedAndUpdated()
    {
        var row = CdrMapping.ToRow(Builders.Cdr(), Now);
        Assert.Equal(Now, row.CreatedUtc);
        Assert.Equal(Now, row.UpdatedUtc);
        Assert.Equal(0, row.CdrSeq);
    }
}
