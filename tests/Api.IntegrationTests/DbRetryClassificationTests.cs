using Api.Data;

namespace Api.IntegrationTests;

// Unit coverage for the data-layer retry invariant. SqlException is sealed with no
// public constructor, so Db.IsRetryableForNumber(number, kind, opened) was extracted
// (internal test seam, exposed via InternalsVisibleTo) and IsRetryable now delegates
// to it per SqlError. Behavior is unchanged; these tests pin the matrix:
//   - pre-open (!opened): any transient (safe OR ambiguous) is retryable, both kinds.
//   - reads: any transient is retryable.
//   - writes: only "definitely did not take effect" (safe) errors are retryable;
//     ambiguous mid-command errors are NOT (retrying could double-apply the write).
//   - unknown error numbers are never retryable once a command was sent.
public class DbRetryClassificationTests
{
    // 1205 = deadlock victim — a SAFE transient (server rolled it back): retry reads AND writes.
    [Fact]
    public void Safe_transient_is_retryable_for_read_and_write_when_opened()
    {
        Assert.True(Db.IsRetryableForNumber(1205, Db.RetryKind.Read, opened: true));
        Assert.True(Db.IsRetryableForNumber(1205, Db.RetryKind.Write, opened: true));
    }

    // -2 (client timeout) and 10054 (connection reset) are AMBIGUOUS mid-command errors:
    // retryable for reads, NOT for writes once a command was sent.
    [Theory]
    [InlineData(-2)]
    [InlineData(10054)]
    public void Ambiguous_transient_is_retryable_for_read_not_write_when_opened(int number)
    {
        Assert.True(Db.IsRetryableForNumber(number, Db.RetryKind.Read, opened: true));
        Assert.False(Db.IsRetryableForNumber(number, Db.RetryKind.Write, opened: true));
    }

    // Before the connection opened, nothing was sent — any transient is retryable for
    // either kind, including the ambiguous ones (they were never executed).
    [Theory]
    [InlineData(1205)]
    [InlineData(-2)]
    [InlineData(10054)]
    public void Any_transient_is_retryable_when_not_opened(int number)
    {
        Assert.True(Db.IsRetryableForNumber(number, Db.RetryKind.Read, opened: false));
        Assert.True(Db.IsRetryableForNumber(number, Db.RetryKind.Write, opened: false));
    }

    // An error number in neither transient set is never retryable once opened
    // (e.g. 547 FK violation, 2627 unique-key violation — deterministic failures).
    [Theory]
    [InlineData(547)]
    [InlineData(2627)]
    [InlineData(99999)]
    public void Unknown_error_is_never_retryable_when_opened(int number)
    {
        Assert.False(Db.IsRetryableForNumber(number, Db.RetryKind.Read, opened: true));
        Assert.False(Db.IsRetryableForNumber(number, Db.RetryKind.Write, opened: true));
    }
}
