using System.Text;
using LootLogger.Core.Export;
using LootLogger.Core.Tracking;

namespace LootLogger.Core.Session;

/// <summary>
/// Everything captured since "Nova sessão". Each new row is also appended to a CSV file
/// right away, so a crash or a closed window never loses the session.
/// Not thread safe: use from the UI thread.
/// </summary>
public sealed class LootSession
{
    private readonly List<LootEntry> _loot = [];
    private readonly List<KillEntry> _kills = [];
    private readonly string? _autosaveFolder;
    private string? _autosavePath;
    private DateTime? _captureStartedUtc;
    private TimeSpan _activeBefore;

    public LootSession(DateTime startedUtc, string? autosaveFolder)
    {
        StartedUtc = startedUtc;
        if (autosaveFolder is not null)
        {
            Directory.CreateDirectory(autosaveFolder);
            _autosaveFolder = autosaveFolder;
        }
    }

    public DateTime StartedUtc { get; }

    /// <summary>The CSV file, named when the first row is written; null before that.</summary>
    public string? AutosavePath => _autosavePath;

    /// <summary>Who is running the program; goes into the file name if known before the first row.</summary>
    public string? Owner { get; set; }

    public IReadOnlyList<LootEntry> Loot => _loot;

    public IReadOnlyList<KillEntry> Kills => _kills;

    public int TotalItems => _loot.Sum(l => l.Quantity);

    public long TotalValue => _loot.Sum(l => l.TotalValue);

    public int ItemsSince(DateTime utc) => _loot.Where(l => l.UtcTime >= utc).Sum(l => l.Quantity);

    public bool IsCapturing => _captureStartedUtc is not null;

    public TimeSpan ActiveTime(DateTime utcNow) =>
        _activeBefore + (_captureStartedUtc is { } started ? utcNow - started : TimeSpan.Zero);

    public void MarkCaptureStarted(DateTime utcNow) => _captureStartedUtc ??= utcNow;

    public void MarkCaptureStopped(DateTime utcNow)
    {
        if (_captureStartedUtc is { } started)
        {
            _activeBefore += utcNow - started;
            _captureStartedUtc = null;
        }
    }

    public void Add(LootEntry entry)
    {
        _loot.Add(entry);
        Append(CsvExporter.LootRow(entry), entry.UtcTime);
    }

    public void Add(KillEntry entry)
    {
        _kills.Add(entry);
        Append(CsvExporter.KillRow(entry), entry.UtcTime);
    }

    private void Append(string row, DateTime utcTime)
    {
        if (_autosaveFolder is null)
        {
            return;
        }

        // Named after the first thing recorded, not when the program opened: that is when the fight started.
        _autosavePath ??= UniquePath(_autosaveFolder, CsvExporter.DefaultFileName(utcTime, Owner));

        try
        {
            var isNew = !File.Exists(_autosavePath);
            var text = (isNew ? CsvExporter.Header + "\r\n" : string.Empty) + row + "\r\n";
            File.AppendAllText(_autosavePath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: isNew));
        }
        catch (IOException)
        {
            // The file may be open in Excel; the row stays in memory and goes into the next export.
        }
    }

    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}");
        }

        return path;
    }
}
