using Concordant.Shared;
using Concordant.Sync;
using Concordant.Sync.Native;
using Concordant.Values;

namespace Concordant.Core.Tests;

public sealed class SyncCodecTests
{
    private static readonly string FixturesDir = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Sync");

    [Fact]
    public void Golden_full_state_bytes_match_fixture()
    {
        using ConcordantDocument doc = CreateGoldenDocument();
        byte[] actual = doc.EncodeFullState();
        byte[] expected = LoadHexFixture("golden-seed42-fullstate.hex");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Golden_empty_delta_bytes_match_fixture()
    {
        using ConcordantDocument doc = CreateGoldenDocument();
        byte[] actual = doc.EncodeUpdateSince(doc.StateVector);
        byte[] expected = LoadHexFixture("golden-empty-update.hex");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Full_state_and_empty_state_vector_delta_are_equivalent_after_apply()
    {
        using ConcordantDocument source = CreateGoldenDocument();
        byte[] full = source.EncodeFullState();
        byte[] delta = source.EncodeUpdateSince(new Dictionary<SessionId, ulong>());

        using var fromFull = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(100) });
        using var fromDelta = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(101) });

        Assert.Equal(ApplyStatus.Integrated, fromFull.ApplyUpdate(full).Status);
        Assert.Equal(ApplyStatus.Integrated, fromDelta.ApplyUpdate(delta).Status);
        Assert.Equal(source.VisibleFingerprint(), fromFull.VisibleFingerprint());
        Assert.Equal(source.VisibleFingerprint(), fromDelta.VisibleFingerprint());
        Assert.Equal("Hi", fromFull.GetText("notes").ToString());
    }

    [Fact]
    public void Checkpoint_plus_log_recovers_with_fresh_writer_session()
    {
        using var primary = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(7) });
        primary.Transact(tx => tx.GetOrCreateText("t").Insert(0, "ab"));
        IReadOnlyDictionary<SessionId, ulong> checkpointSv = new Dictionary<SessionId, ulong>(primary.StateVector);
        byte[] checkpoint = primary.EncodeFullState();

        primary.Transact(tx => tx.GetOrCreateText("t").Insert(2, "c"));
        byte[] log = primary.EncodeUpdateSince(checkpointSv);

        using ConcordantDocument recovered = ConcordantDocument.CreateFromCheckpoint(
            checkpoint,
            new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(99) });

        Assert.NotEqual(SessionId.FromSeed(7), recovered.SessionId);
        Assert.Equal(SessionId.FromSeed(99), recovered.SessionId);
        Assert.Equal("ab", recovered.GetText("t").ToString());

        Assert.Equal(ApplyStatus.Integrated, recovered.ApplyUpdate(log).Status);
        Assert.Equal(primary.VisibleFingerprint(), recovered.VisibleFingerprint());
        Assert.Equal("abc", recovered.GetText("t").ToString());
    }

    [Fact]
    public void Duplicate_and_reordered_delivery_converge()
    {
        using var a = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        using var b = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(2) });

        a.Transact(tx => tx.GetOrCreateText("t").Insert(0, "A"));
        b.Transact(tx => tx.GetOrCreateText("t").Insert(0, "B"));

        byte[] updateA = a.EncodeUpdateSince(new Dictionary<SessionId, ulong>());
        byte[] updateB = b.EncodeUpdateSince(new Dictionary<SessionId, ulong>());

        using var r1 = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(10) });
        using var r2 = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(11) });

        Assert.Equal(ApplyStatus.Integrated, r1.ApplyUpdate(updateA).Status);
        Assert.Equal(ApplyStatus.Integrated, r1.ApplyUpdate(updateB).Status);
        Assert.Equal(ApplyStatus.Duplicate, r1.ApplyUpdate(updateA).Status);

        Assert.Equal(ApplyStatus.Integrated, r2.ApplyUpdate(updateB).Status);
        Assert.Equal(ApplyStatus.Integrated, r2.ApplyUpdate(updateA).Status);
        Assert.Equal(ApplyStatus.Duplicate, r2.ApplyUpdate(updateB).Status);

        Assert.Equal(r1.VisibleFingerprint(), r2.VisibleFingerprint());
    }

    [Fact]
    public void Unsupported_version_rejects_without_mutation()
    {
        using ConcordantDocument doc = CreateGoldenDocument();
        string before = doc.VisibleFingerprint();
        byte[] bytes = doc.EncodeFullState();
        bytes[4] = 99; // version LSB
        bytes[5] = 0;

        using var target = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(3) });
        string emptyFp = target.VisibleFingerprint();
        ApplyResult result = target.ApplyUpdate(bytes);

        Assert.Equal(ApplyStatus.Rejected, result.Status);
        Assert.Equal(ApplyRejectReason.UnsupportedVersion, result.Reason);
        Assert.Equal(99, result.CodecVersion);
        Assert.False(result.Retryable);
        Assert.Equal(emptyFp, target.VisibleFingerprint());
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    [Fact]
    public void Malformed_update_rejects_with_zero_partial_mutation()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(5) });
        doc.Transact(tx => tx.GetOrCreateText("t").Insert(0, "ok"));
        string before = doc.VisibleFingerprint();

        byte[] junk = "CNCR"u8.ToArray().Concat(new byte[] { 1, 0, 1, 0 }).Concat(new byte[20]).ToArray();
        // Corrupt: claim huge op count
        junk[16] = 0xFF;
        junk[17] = 0xFF;
        junk[18] = 0xFF;
        junk[19] = 0x7F;

        ApplyResult result = doc.ApplyUpdate(junk);
        Assert.Equal(ApplyStatus.Rejected, result.Status);
        Assert.Equal(before, doc.VisibleFingerprint());
        Assert.Equal("ok", doc.GetText("t").ToString());
    }

    [Fact]
    public void Oversized_update_is_quota_rejected()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions
        {
            WriterSession = SessionId.FromSeed(8),
            MaxUpdateBytes = 8,
        });
        string before = doc.VisibleFingerprint();
        byte[] bytes = LoadHexFixture("golden-empty-update.hex");
        ApplyResult result = doc.ApplyUpdate(bytes);
        Assert.Equal(ApplyStatus.Rejected, result.Status);
        Assert.Equal(ApplyRejectReason.QuotaExceeded, result.Reason);
        Assert.True(result.Retryable);
        Assert.Equal(before, doc.VisibleFingerprint());
    }

    [Fact]
    public void Pending_dependencies_include_missing_ranges_metadata()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(1) });
        SessionId remote = SessionId.FromSeed(2);
        var op2 = new ConcordantOperation.RootDeclare("t", RootKind.Text)
        {
            Id = new OpId(remote, 2),
            Lamport = 1,
            LamportSource = null,
        };

        byte[] bytes = NativeUpdateCodec.Instance.Encode(new[] { op2 }, UpdateEncodeKind.Update);
        ApplyResult result = doc.ApplyUpdate(bytes);

        Assert.Equal(ApplyStatus.PendingDependencies, result.Status);
        Assert.True(result.Retryable);
        Assert.NotNull(result.MissingRanges);
        Assert.Contains(result.MissingRanges!, r => r.Session == remote && r.FromClockInclusive == 1 && r.ToClockInclusive == 1);
        Assert.NotNull(result.StateVector);
        Assert.Equal(1, result.CodecVersion);
    }

    [Fact]
    public void ApplyUpdate_result_carries_state_vector_on_integrate()
    {
        using var src = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(20) });
        src.Transact(tx => tx.GetOrCreateMap("m").Set("k", ConcordantScalar.Int64(1)));
        byte[] update = src.EncodeUpdateSince(new Dictionary<SessionId, ulong>());

        using var dst = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(21) });
        ApplyResult result = dst.ApplyUpdate(update);
        Assert.Equal(ApplyStatus.Integrated, result.Status);
        Assert.NotNull(result.StateVector);
        Assert.Equal(dst.StateVector[src.SessionId], result.StateVector![src.SessionId]);
        Assert.Equal(1, result.CodecVersion);
        Assert.Equal(0u, result.RequiredFeatures);
    }

    [Fact]
    public void Codec_never_mutates_store_custom_codec_still_revalidated()
    {
        var passthrough = new RecordingCodec();
        using var doc = new ConcordantDocument(
            new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(30) },
            passthrough);

        doc.Transact(tx => tx.GetOrCreateText("t").Insert(0, "z"));
        byte[] encoded = doc.EncodeFullState();
        Assert.True(passthrough.EncodeCalls > 0);

        using var other = new ConcordantDocument(
            new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(31) },
            passthrough);
        Assert.Equal(ApplyStatus.Integrated, other.ApplyUpdate(encoded).Status);
        Assert.True(passthrough.DecodeCalls > 0);
        Assert.Equal("z", other.GetText("t").ToString());
    }

    [Fact]
    public void CreateFromCheckpoint_rejects_non_checkpoint_kind()
    {
        using var src = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(40) });
        src.Transact(tx => tx.GetOrCreateText("t").Insert(0, "x"));
        byte[] update = src.EncodeUpdateSince(new Dictionary<SessionId, ulong>());

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            ConcordantDocument.CreateFromCheckpoint(update, new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(41) }));
        Assert.Contains("checkpoint", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wire_bytes_are_stable_across_encode_rounds()
    {
        using ConcordantDocument doc = CreateGoldenDocument();
        byte[] a = doc.EncodeFullState();
        byte[] b = doc.EncodeFullState();
        Assert.Equal(a, b);

        using var roundTrip = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(50) });
        Assert.Equal(ApplyStatus.Integrated, roundTrip.ApplyUpdate(a).Status);
        Assert.Equal(a, roundTrip.EncodeFullState());
    }

    private static ConcordantDocument CreateGoldenDocument()
    {
        var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(42) });
        doc.Transact(tx =>
        {
            SharedText text = tx.GetOrCreateText("notes");
            text.Insert(0, "Hi");
            SharedMap map = tx.GetOrCreateMap("meta");
            map.Set("n", ConcordantScalar.Null);
            map.Set("b", ConcordantScalar.Bool(true));
            map.Set("i", ConcordantScalar.Int64(-7));
            map.Set("f", ConcordantScalar.Float64(1.5));
            map.Set("s", ConcordantScalar.String("x"));
        });
        return doc;
    }

    private static byte[] LoadHexFixture(string fileName)
    {
        string path = Path.Combine(FixturesDir, fileName);
        Assert.True(File.Exists(path), $"Missing fixture {path}");
        string hex = File.ReadAllText(path).Trim().Replace(" ", "", StringComparison.Ordinal);
        return Convert.FromHexString(hex);
    }

    private sealed class RecordingCodec : IUpdateCodec
    {
        public int EncodeCalls { get; private set; }

        public int DecodeCalls { get; private set; }

        public byte[] Encode(IReadOnlyList<ConcordantOperation> operations, UpdateEncodeKind kind)
        {
            EncodeCalls++;
            return NativeUpdateCodec.Instance.Encode(operations, kind);
        }

        public CodecDecodeResult Decode(ReadOnlySpan<byte> bytes, CodecDecodeLimits limits)
        {
            DecodeCalls++;
            return NativeUpdateCodec.Instance.Decode(bytes, limits);
        }
    }
}
