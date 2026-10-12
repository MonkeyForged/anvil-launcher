namespace Anvil.Core.Logging;

/// <summary>
/// Schreibt Log-Zeilen in eine Datei und rotiert größenbasiert:
/// anvil.log → .log.1 → .log.2 → … (die älteste fällt weg).
/// IO-Fehler dürfen die App nie zum Absturz bringen — der Writer
/// degradiert still und versucht es beim nächsten Eintrag erneut.
/// </summary>
public sealed class RotatingFileWriter : IDisposable
{
    private readonly string _directory;
    private readonly string _baseFileName;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;

    public RotatingFileWriter(
        string directory,
        string baseFileName = "anvil.log",
        long maxFileSizeBytes = 5_242_880,
        int maxArchiveFiles = 3)
    {
        _directory = directory;
        _baseFileName = baseFileName;
        MaxFileSizeBytes = maxFileSizeBytes;
        MaxArchiveFiles = maxArchiveFiles;
    }

    public string CurrentFilePath => Path.Combine(_directory, _baseFileName);

    public long MaxFileSizeBytes { get; }

    public int MaxArchiveFiles { get; }

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            try
            {
                EnsureWriter();
                if (_writer is null)
                {
                    return; // degradiert — nächster Eintrag versucht es erneut
                }

                if (_writer.BaseStream.Length > MaxFileSizeBytes)
                {
                    Rotate();
                    EnsureWriter();
                }

                _writer?.WriteLine(line);
            }
            catch (IOException)
            {
                DisposeWriter(); // degradieren, beim nächsten Eintrag neu versuchen
            }
            catch (UnauthorizedAccessException)
            {
                DisposeWriter();
            }
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var stream = new FileStream(
                CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream) { AutoFlush = true };
        }
        catch (IOException)
        {
            _writer = null;
        }
        catch (UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    private void Rotate()
    {
        DisposeWriter();
        try
        {
            var oldest = ArchivePath(MaxArchiveFiles);
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = MaxArchiveFiles - 1; i >= 1; i--)
            {
                var source = ArchivePath(i);
                if (File.Exists(source))
                {
                    File.Move(source, ArchivePath(i + 1));
                }
            }

            if (File.Exists(CurrentFilePath))
            {
                File.Move(CurrentFilePath, ArchivePath(1));
            }
        }
        catch (IOException)
        {
            // Rotation ist best-effort: im Zweifel in der bestehenden Datei weiterschreiben.
        }
    }

    private string ArchivePath(int index)
        => Path.Combine(_directory, $"{_baseFileName}.{index}");

    private void DisposeWriter()
    {
        _writer?.Dispose();
        _writer = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposeWriter();
        }
    }
}
