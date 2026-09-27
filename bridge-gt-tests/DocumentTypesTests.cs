using Xunit;

/// <summary>
/// The document-type codes, pinned.
///
/// These are not arbitrary constants - each was MEASURED against a live
/// Subiekt GT database, and the reason they exist at all is that five queries
/// previously identified documents by matching an operator-editable number
/// PREFIX. A customer whose numbering template differed matched nothing, and
/// four things broke silently at once: duplicate orders on every retry, stock
/// released twice, an empty order feed, and no order ever marked realized.
///
/// So a change to one of these values is a change to the thing that decides
/// whether a retried order creates a second ZK. If a future install genuinely
/// disagrees, re-run the query in DocumentTypes.cs's header there - do not
/// adjust a number to make a test pass.
/// </summary>
public class DocumentTypesTests
{
    [Fact]
    public void Codes_match_what_was_measured_on_a_live_database()
    {
        Assert.Equal(2, DocumentTypes.Fs);
        Assert.Equal(6, DocumentTypes.Kfs);
        Assert.Equal(11, DocumentTypes.Wz);
        Assert.Equal(12, DocumentTypes.Pw);
        Assert.Equal(13, DocumentTypes.Rw);
        Assert.Equal(16, DocumentTypes.Zk);
        Assert.Equal(21, DocumentTypes.Pa);
    }

    /// <summary>
    /// An order and its warehouse release must never share a code - the ZK
    /// idempotency guard and the WZ idempotency guard both key on
    /// dok_NrPelnyOryg and are told apart by nothing else.
    /// </summary>
    [Fact]
    public void Every_code_is_distinct()
    {
        var codes = new[]
        {
            DocumentTypes.Fs, DocumentTypes.Kfs, DocumentTypes.Wz,
            DocumentTypes.Pw, DocumentTypes.Rw, DocumentTypes.Zk, DocumentTypes.Pa,
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }
}
