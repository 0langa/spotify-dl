using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class FolderPickerPathTests
{
    [Fact]
    public void CanonicalizesExistingDirectoryBeforePassingItToWindowsShell()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"playlistdl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var slashPath = directory.Replace(Path.DirectorySeparatorChar, '/');

            Assert.Equal(Path.GetFullPath(directory), FolderPickerPath.ResolveInitialDirectory(slashPath));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Z:/playlistdl/path/that/does/not/exist")]
    public void IgnoresMissingOrEmptyInitialDirectory(string? path)
    {
        Assert.Null(FolderPickerPath.ResolveInitialDirectory(path));
    }
}
