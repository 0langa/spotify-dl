using System.IO;

namespace PlaylistDl.App.Services;

public static class RunLogReader
{
    public static string Read(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return "No run log exists yet. Start a download or analysis, then refresh.";
        }
        catch (DirectoryNotFoundException)
        {
            return "No run log exists yet. Start a download or analysis, then refresh.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return $"Run log cannot be read right now: {exception.Message}";
        }
    }
}
