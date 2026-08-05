using System.IO;
using System.Text;
using Xunit;

namespace AgenticRoslynTool.Tests;

// CsvFieldReader replaced Microsoft.VisualBasic.FileIO.TextFieldParser during the port.
// Its parity with quoted-field semantics is load-bearing for both input CSVs and the
// manifest round-trip, so these tests pin the well-known quoting rules.
public sealed class CsvFieldReaderTests
{
    [Fact]
    public void ReadsSimpleUnquotedFields()
    {
        var fields = ReadSingleRecord("a,b,c\n");
        Assert.Equal(new[] { "a", "b", "c" }, fields);
    }

    [Fact]
    public void QuotedFieldContainingComma_IsOneField()
    {
        var fields = ReadSingleRecord("\"a,b\",c\n");
        Assert.Equal(new[] { "a,b", "c" }, fields);
    }

    [Fact]
    public void DoubledQuote_InsideQuotedField_DecodesToSingleQuote()
    {
        var fields = ReadSingleRecord("\"he said \"\"hi\"\"\",tail\n");
        Assert.Equal(new[] { "he said \"hi\"", "tail" }, fields);
    }

    [Fact]
    public void EmbeddedNewline_InsideQuotedField_StaysInTheField()
    {
        var fields = ReadSingleRecord("\"line1\nline2\",next\n");
        Assert.Equal(new[] { "line1\nline2", "next" }, fields);
    }

    [Fact]
    public void EmbeddedCarriageReturnLf_InsideQuotedField_StaysInTheField()
    {
        var fields = ReadSingleRecord("\"line1\r\nline2\",next\n");
        Assert.Equal(new[] { "line1\r\nline2", "next" }, fields);
    }

    [Fact]
    public void MultipleRecords_AreReturnedInOrder()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("t.csv", "a,b\n\"x,y\",z\n");
        using var reader = new CsvFieldReader(path);
        Assert.Equal(new[] { "a", "b" }, reader.ReadFields());
        Assert.Equal(new[] { "x,y", "z" }, reader.ReadFields());
        Assert.True(reader.EndOfData);
    }

    [Fact]
    public void EmptyFile_ReturnsNullOnFirstRead()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("t.csv", string.Empty);
        using var reader = new CsvFieldReader(path);
        Assert.Null(reader.ReadFields());
        Assert.True(reader.EndOfData);
    }

    private static string[] ReadSingleRecord(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "art-csv-" + System.Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            using var reader = new CsvFieldReader(path);
            var fields = reader.ReadFields();
            Assert.NotNull(fields);
            return fields!;
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
