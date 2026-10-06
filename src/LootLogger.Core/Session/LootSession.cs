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
    private readonly string? _autosavePath;
    private DateTime? _captureStartedUtc;
    private TimeSpan _activeBefore;

    public LootSession(DateTime startedUtc, string? autosaveFolder)
    {
        StartedUtc = startedUtc;
        if (autosaveFolder is not null)
        {
            Directory.CreateDirectory(autosaveFolder);
            _autosavePath = Path.Combine(autosaveFolder, CsvExporter.DefaultFileName(startedUtc));
        }
    }

    public DateTime StartedUtc { get; }

    public string? AutosavePath => _autosavePath;

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
        Append(CsvExporter.LootRow(entry));
    }

    public void Add(KillEntry entry)
    {
        _kills.Add(entry);
        Append(CsvExporter.KillRow(entry));
    }

    private void Append(string row)
    {
        if (_autosavePath is null)
        {
            return;
        }

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
}
