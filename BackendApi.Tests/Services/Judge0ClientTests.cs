using System.IO.Compression;
using System.Text;
using BackendApi.Contracts;
using BackendApi.Services;

namespace BackendApi.Tests.Services;

// SEK-01: focused unit tests for Judge0Client's additional-files zip/flatten helper,
// independent of a live Judge0 instance (see RunAsync's own remarks on why the client
// itself isn't unit-tested against a real submission).
public class Judge0ClientTests
{
    private static Dictionary<string, string> ReadZipEntries(string base64Zip)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(base64Zip));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = new Dictionary<string, string>();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            entries[entry.FullName] = reader.ReadToEnd();
        }
        return entries;
    }

    [Fact]
    public void BuildAdditionalFilesZip_ReturnsNull_WhenNoAdditionalFiles()
    {
        var result = Judge0Client.BuildAdditionalFilesZip([]);
        Assert.Null(result);
    }

    [Fact]
    public void BuildAdditionalFilesZip_FlattensNestedPathsToBasename()
    {
        var files = new List<CodeFileDto>
        {
            new("src/helper.py", "python", "x = 1"),
            new("lib/util.py", "python", "y = 2"),
        };

        var zip = Judge0Client.BuildAdditionalFilesZip(files);
        var entries = ReadZipEntries(zip!);

        Assert.Equal(2, entries.Count);
        Assert.Equal("x = 1", entries["helper.py"]);
        Assert.Equal("y = 2", entries["util.py"]);
    }

    [Fact]
    public void BuildAdditionalFilesZip_DisambiguatesCollidingBasenames()
    {
        var files = new List<CodeFileDto>
        {
            new("src/helper.py", "python", "x = 1"),
            new("lib/helper.py", "python", "y = 2"),
        };

        var zip = Judge0Client.BuildAdditionalFilesZip(files);
        var entries = ReadZipEntries(zip!);

        Assert.Equal(2, entries.Count);
        Assert.Contains("helper.py", entries.Keys);
        Assert.Contains("helper_1.py", entries.Keys);
    }
}
