using PairShare.Services;
using Xunit;

namespace PairShare.Tests;

public class FileNamesTests
{
    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\evil.exe", "evil.exe")]
    [InlineData("C:\\Users\\me\\report.docx", "report.docx")]
    [InlineData(".bashrc", "bashrc")]
    [InlineData("notes.txt...", "notes.txt")]
    [InlineData("  spaced  .txt  ", "spaced  .txt")]
    [InlineData("a<b>c:d\"e|f?g*.txt", "a_b_c_d_e_f_g_.txt")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    [InlineData("evil\u202Etxt.exe", "eviltxt.exe")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT1.tar.gz", "_LPT1.tar.gz")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("", "file")]
    [InlineData("...", "file")]
    [InlineData(null, "file")]
    [InlineData("héllo wörld 📷.png", "héllo wörld 📷.png")]
    public void Sanitize(string? input, string expected) => Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void Long_names_are_shortened_but_keep_their_extension()
    {
        var result = FileNames.Sanitize(new string('a', 500) + ".jpeg");

        Assert.Equal(FileNames.MaxLength, result.Length);
        Assert.EndsWith(".jpeg", result);
    }

    [Fact]
    public void Unique_adds_a_counter_when_the_name_is_taken()
    {
        var dir = Directory.CreateTempSubdirectory("pairshare-names-").FullName;
        try
        {
            Assert.Equal(Path.Combine(dir, "a.txt"), FileNames.Unique(dir, "a.txt"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "");
            Assert.Equal(Path.Combine(dir, "a (1).txt"), FileNames.Unique(dir, "a.txt"));
            File.WriteAllText(Path.Combine(dir, "a (1).txt"), "");
            Assert.Equal(Path.Combine(dir, "a (2).txt"), FileNames.Unique(dir, "a.txt"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
