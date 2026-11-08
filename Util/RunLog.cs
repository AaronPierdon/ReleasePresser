using System.Diagnostics;
using System.Text;

namespace Pressing.Core.Util;

/// <summary>
/// One detailed, timestamped log for a run: every stage, every online lookup, every external tool, every file
/// operation, each with how long it took. Shown live on the Log page and written to
/// %APPDATA%\Pressing\runs\&lt;run&gt;-log.txt. At the end it adds a timing summary (where the time went, slowest steps).
/// </summary>
public static class RunLog
{
    private static readonly object Lock = new();
    private static StreamWriter? _file;
    private static Action<string>? _sink;
    private static readonly Stopwatch Clock = new();
    private static readonly List<(string Category, string What, TimeSpan Took)> Timings = new();
    private static readonly Dictionary<string, TimeSpan> Waits = new();

    public static string? FilePath { get; private set; }

    /// <summary>Starts logging a run (appends if the file exists, e.g. analysis then apply of the same run).</summary>
    public static void Begin(string filePath, Action<string>? sink)
    {
        lock (Lock)
        {
            End();
            FilePath = filePath;
            _sink = sink;
            Timings.Clear();
            Waits.Clear();
            Clock.Restart();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                _file = new StreamWriter(filePath, append: true, Encoding.UTF8) { AutoFlush = true };
            }
            catch { _file = null; }
        }
    }

    public static void End()
    {
        lock (Lock)
        {
            try { _file?.Dispose(); } catch { }
            _file = null;
            _sink = null;
        }
    }

    /// <summary>Writes one line: "14:02:11.384  [category]  message".</summary>
    public static void Write(string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {("[" + category + "]").PadRight(10)} {message}";
        Action<string>? sink;
        lock (Lock)
        {
            try { _file?.WriteLine(line); } catch { }
            sink = _sink;
        }
        sink?.Invoke(line);
    }

    /// <summary>Writes a block of text as-is (used for the timing summary).</summary>
    public static void WriteRaw(string text)
    {
        foreach (var l in text.TrimEnd().Split('\n')) Write("summary", l.TrimEnd('\r'));
    }

    /// <summary>Times a step: logs "▶ what" now (unless logStart is false) and "✓ what — 1.23 s" when disposed.</summary>
    public static Timer Time(string category, string what, bool logStart = true) => new(category, what, logStart);

    /// <summary>Records a duration measured elsewhere (e.g. an HTTP call) for the summary.</summary>
    public static void Record(string category, string what, TimeSpan took)
    {
        lock (Lock) Timings.Add((category, what, took));
    }

    /// <summary>Time spent waiting on purpose (rate limits, retry back-off), per reason.</summary>
    public static void RecordWait(string reason, TimeSpan waited)
    {
        if (waited <= TimeSpan.Zero) return;
        lock (Lock) Waits[reason] = Waits.GetValueOrDefault(reason) + waited;
    }

    public static string Ms(TimeSpan t) => t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.00} s" : $"{t.TotalMilliseconds:0} ms";

    /// <summary>Where the time went: totals per stage and per kind of work, waits, and the slowest single steps.</summary>
    public static string Summary()
    {
        List<(string Category, string What, TimeSpan Took)> t;
        Dictionary<string, TimeSpan> w;
        TimeSpan total;
        lock (Lock) { t = Timings.ToList(); w = new(Waits); total = Clock.Elapsed; }
        var sb = new StringBuilder();
        sb.AppendLine("════════════════════ TIMING SUMMARY ════════════════════");
        sb.AppendLine($"Total wall time: {Ms(total)}");

        var stages = t.Where(x => x.Category == "stage").ToList();
        if (stages.Count > 0)
        {
            sb.AppendLine("Stages:");
            foreach (var s in stages) sb.AppendLine($"   {s.What,-34} {Ms(s.Took),10}   {Pct(s.Took, total)}");
        }

        sb.AppendLine("By kind of work (count · total · average · slowest):");
        foreach (var g in t.Where(x => x.Category != "stage").GroupBy(x => x.Category).OrderByDescending(g => g.Sum(x => x.Took.Ticks)))
        {
            var sum = TimeSpan.FromTicks(g.Sum(x => x.Took.Ticks));
            var max = g.MaxBy(x => x.Took);
            sb.AppendLine($"   {g.Key,-14} {g.Count(),6} ×   {Ms(sum),10}   avg {Ms(TimeSpan.FromTicks(sum.Ticks / g.Count())),9}   max {Ms(max.Took)} ({Short(max.What)})");
        }

        if (w.Count > 0)
        {
            sb.AppendLine("Deliberate waiting (rate limits / retry back-off; added up across albums worked on at once, so it can exceed the wall time):");
            foreach (var (k, v) in w.OrderByDescending(x => x.Value)) sb.AppendLine($"   {k,-34} {Ms(v),10}   {Pct(v, total)}");
        }

        sb.AppendLine("Slowest single steps:");
        foreach (var x in t.Where(x => x.Category != "stage").OrderByDescending(x => x.Took).Take(15))
            sb.AppendLine($"   {Ms(x.Took),10}   [{x.Category}] {Short(x.What, 110)}");
        sb.AppendLine("════════════════════════════════════════════════════════");
        return sb.ToString();
    }

    private static string Pct(TimeSpan part, TimeSpan total) => total.TotalMilliseconds > 0 ? $"{part.TotalMilliseconds / total.TotalMilliseconds:P0}" : "";
    private static string Short(string s, int max = 60) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public sealed class Timer : IDisposable
    {
        private readonly string _cat, _what;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private bool _done;
        public string Result { get; set; } = "";

        internal Timer(string cat, string what, bool logStart)
        {
            _cat = cat; _what = what;
            if (logStart) Write(cat, "▶ " + what);
        }

        public TimeSpan Elapsed => _sw.Elapsed;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _sw.Stop();
            Record(_cat, _what, _sw.Elapsed);
            Write(_cat, $"✓ {_what} — {Ms(_sw.Elapsed)}{(Result.Length > 0 ? "  · " + Result : "")}");
        }
    }
}
