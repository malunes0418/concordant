using Concordant.Shared;
using Concordant.Sync.Native;
using Concordant.Values;

namespace Concordant.Core.Tests;

/// <summary>
/// Cross-TFM / golden compatibility checks. Behavior and wire bytes must match on net8.0 and net10.0.
/// </summary>
public sealed class CompatibilityTests
{
    private static readonly string FixturesDir = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Sync");

    [Fact]
    public void Golden_seed42_round_trips_byte_identical()
    {
        byte[] expected = LoadHexFixture("golden-seed42-fullstate.hex");
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(99) });
        Assert.Equal(ApplyStatus.Integrated, doc.ApplyUpdate(expected).Status);
        Assert.Equal(expected, doc.EncodeFullState());
    }

    [Fact]
    public void Golden_map_only_fixture_matches()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(77) });
        doc.Transact(tx =>
        {
            SharedMap map = tx.GetOrCreateMap("cfg");
            map.Set("enabled", ConcordantScalar.Bool(true));
            map.Set("count", ConcordantScalar.Int64(42));
        });

        byte[] actual = doc.EncodeFullState();
        byte[] expected = LoadHexFixture("golden-seed77-map.hex");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Empty_update_header_is_stable()
    {
        byte[] expected = LoadHexFixture("golden-empty-update.hex");
        Assert.Equal(20, expected.Length); // header(16) + opCount(4)
        Assert.Equal("CNCR"u8.ToArray(), expected.AsSpan(0, 4).ToArray());
        Assert.Equal(NativeWireFormat.CurrentVersion, BitConverter.ToUInt16(expected, 4));
    }

    [Fact]
    public void Fingerprint_is_deterministic_for_fixed_session()
    {
        string a = BuildFingerprint();
        string b = BuildFingerprint();
        Assert.Equal(a, b);
        Assert.Contains("roots=notes:Text.;", a, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_tfm_is_supported()
    {
        string tfm = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        Assert.True(
            tfm.Contains(".NET 8", StringComparison.Ordinal)
            || tfm.Contains(".NET 10", StringComparison.Ordinal)
            || tfm.Contains(".NET 9", StringComparison.Ordinal),
            $"Unexpected runtime: {tfm}");
    }

    private static string BuildFingerprint()
    {
        using var doc = new ConcordantDocument(new ConcordantDocumentOptions { WriterSession = SessionId.FromSeed(42) });
        doc.Transact(tx => tx.GetOrCreateText("notes").Insert(0, "Hi"));
        return doc.VisibleFingerprint();
    }

    private static byte[] LoadHexFixture(string fileName)
    {
        string path = Path.Combine(FixturesDir, fileName);
        Assert.True(File.Exists(path), $"Missing fixture {path}");
        string hex = File.ReadAllText(path).Trim().Replace(" ", "", StringComparison.Ordinal);
        return Convert.FromHexString(hex);
    }
}
