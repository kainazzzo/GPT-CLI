namespace GptCli.Tests.TestDoubles;

internal sealed class TempCwd : IDisposable
{
    private readonly string _previous;
    public string DirectoryPath { get; }

    public TempCwd()
    {
        _previous = Directory.GetCurrentDirectory();
        DirectoryPath = Path.Combine(Path.GetTempPath(), "gptcli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        Directory.SetCurrentDirectory(DirectoryPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.SetCurrentDirectory(_previous);
        }
        catch
        {
            // ignore
        }

        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch
        {
            // ignore leftover temp files
        }
    }
}
