namespace PlaylistDl.App.Services;

/// <summary>Raised when a prepared job turns out to have nothing left to download.</summary>
/// <remarks>
/// Its own type, because an unattended check treats this as the quiet outcome while every
/// other failure — a backend that is gone, a source that cannot be resolved — must still be
/// reported and counted.
/// </remarks>
public sealed class NothingToDownloadException : InvalidOperationException
{
    public NothingToDownloadException(string message)
        : base(message)
    {
    }

    public NothingToDownloadException()
    {
    }

    public NothingToDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
