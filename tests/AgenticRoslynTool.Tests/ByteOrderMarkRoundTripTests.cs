using System.Text;
using Xunit;

namespace AgenticRoslynTool.Tests;

// A file that starts with a byte order mark keeps it on every file a split writes. The
// UTF-8 case once lost it: the encoding chosen on read had no preamble, so the write
// added none, and every touched file showed a whole file diff.
public sealed class ByteOrderMarkRoundTripTests : ContentHashTestBase
{
    public static TheoryData<string> Marked() => ["utf-8", "utf-16le", "utf-16be"];

    public static TheoryData<string> Unchanged() =>
        ["utf-8", "utf-16le", "utf-16be", "utf-8 crlf", "utf-8 no mark", "utf-8 no mark crlf"];

    [Theory]
    [MemberData(nameof(Unchanged))]
    public void ReadThenWrite_ReturnsTheSameBytes(string kind)
    {
        var bytes = SourceBytes(kind);
        var path = Path.Combine(Workspace.Root, "Same.cs");

        var source = EncodedSource.FromBytes(bytes);
        WorkspaceWriter.WriteEncoded(path, source.Text, source);

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [MemberData(nameof(Marked))]
    public void Split_KeepsTheMark_OnTheKeptFileAndTheNewFile(string kind)
    {
        var (encoding, mark) = Lookup(kind);
        var plain = Workspace.WriteFile(Path.Combine("plain", "Foo.cs"), TwoTypes);
        var marked = Path.Combine(Workspace.Root, "marked", "Foo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(marked)!);
        File.WriteAllBytes(marked, [.. mark, .. encoding.GetBytes(TwoTypes)]);
        Plan(plain, marked);

        var rows = Content(plain, marked);

        Assert.All(rows, row => Assert.Equal("split", row.Status));
        foreach (var name in new[] { "Foo.cs", "Bar.cs" })
        {
            var expected = File.ReadAllText(Path.Combine(Workspace.Root, "plain", name), new UTF8Encoding(false, true));
            var written = File.ReadAllBytes(Path.Combine(Workspace.Root, "marked", name));
            Assert.Equal([.. mark, .. encoding.GetBytes(expected)], written);
        }
    }

    [Fact]
    public void Split_OfAFileWithNoMark_WritesNoMark()
    {
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        Plan(source);

        Assert.Equal("split", Assert.Single(Content(source)).Status);

        foreach (var name in new[] { "Foo.cs", "Bar.cs" })
        {
            var written = File.ReadAllBytes(Path.Combine(Workspace.Root, name));
            Assert.NotEqual(0xEF, written[0]);
            Assert.Equal((byte)'n', written[0]);
        }
    }

    private static byte[] SourceBytes(string kind)
    {
        var crlf = kind.EndsWith(" crlf", StringComparison.Ordinal);
        var text = crlf ? TwoTypes.Replace("\n", "\r\n", StringComparison.Ordinal) : TwoTypes;
        var name = crlf ? kind[..^" crlf".Length] : kind;
        var (encoding, mark) = name == "utf-8 no mark" ? (new UTF8Encoding(false), []) : Lookup(name);
        return [.. mark, .. encoding.GetBytes(text)];
    }

    // Each encoding writes no mark of its own, so the test adds exactly one.
    private static (Encoding Encoding, byte[] Mark) Lookup(string kind) => kind switch
    {
        "utf-8" => (new UTF8Encoding(false), [0xEF, 0xBB, 0xBF]),
        "utf-16le" => (new UnicodeEncoding(false, false), [0xFF, 0xFE]),
        "utf-16be" => (new UnicodeEncoding(true, false), [0xFE, 0xFF]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown encoding case"),
    };
}
