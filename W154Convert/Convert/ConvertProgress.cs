using System.Diagnostics;

namespace W154Convert;

sealed class ConvertProgress
{
    readonly TextWriter _log;
    readonly bool _tty;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly int? _total;
    int _done;
    int _lastWidth;
    long _lastDrawMs = -1_000;

    public ConvertProgress(TextWriter log, int? totalFrames)
    {
        _log = log;
        _total = totalFrames is > 0 ? totalFrames : null;
        _tty = ReferenceEquals(log, Console.Out) && !Console.IsOutputRedirected;
    }

    public void Report(int done)
    {
        _done = done;
        long now = _clock.ElapsedMilliseconds;
        if (now - _lastDrawMs < 100)
            return;
        Draw(final: false);
    }

    public void Finish()
    {
        Draw(final: true);
        if (_tty)
            BreakLine();
    }

    public void BreakLine()
    {
        if (_lastDrawMs < 0)
            return;
        _log.WriteLine();
        _lastDrawMs = -1;
    }

    void Draw(bool final)
    {
        string text = FormatLine(_done, _total, _clock.Elapsed, Columns(), final);
        if (_tty)
        {
            if (text.Length < _lastWidth)
                text = text.PadRight(_lastWidth);
            _lastWidth = text.TrimEnd().Length;
            _log.Write('\r');
            _log.Write(text);
            _log.Flush();
        }
        else if (final || _clock.ElapsedMilliseconds - _lastDrawMs >= 1000)
        {
            _log.WriteLine(text);
        }
        else
        {
            return;
        }

        _lastDrawMs = _clock.ElapsedMilliseconds;
    }

    static int Columns()
    {
        try
        {
            if (Console.IsOutputRedirected)
                return 80;
            return Math.Max(48, Console.WindowWidth - 1);
        }
        catch (IOException)
        {
            return 80;
        }
    }

    public static string FormatLine(int done, int? total, TimeSpan elapsed, int columns, bool final)
    {
        string passed = "passou " + Format(elapsed);
        if (total is not int goal)
            return $"quadros {done}   {passed}";

        if (final)
            goal = Math.Max(done, 1);
        int shown = Math.Min(Math.Max(done, 0), goal);
        int percent = final ? 100 : (int)(shown * 100L / goal);
        string rest = done <= 0
            ? "resta --:--"
            : done >= goal || final
                ? "resta 00:00"
                : "resta " + Format(TimeSpan.FromSeconds(elapsed.TotalSeconds / done * (goal - done)));
        string label = $"{percent,3}%  {done}/{goal}  {passed}  {rest}";
        int bar = Math.Clamp(columns - label.Length - 3, 10, 40);
        int fill = final ? bar : (int)Math.Round(bar * (shown / (double)goal));
        fill = Math.Clamp(fill, 0, bar);
        return $"[{new string('#', fill)}{new string('-', bar - fill)}] {label}";
    }

    static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        int totalSeconds = (int)value.TotalSeconds;
        int hours = totalSeconds / 3600;
        int minutes = totalSeconds % 3600 / 60;
        int seconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
    }
}
