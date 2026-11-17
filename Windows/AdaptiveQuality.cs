using System.Text.RegularExpressions;

namespace MonoRelay.Windows;

internal sealed class AdaptiveQuality
{
    private static readonly Regex FrameReport = new(@"^INFO: ([0-9]{1,4}) fps(?: \(\+([0-9]{1,4}) frames skipped\))?$");
    internal const string Id = "auto";
    internal int Index { get; private set; } = 1;
    private int warmup = 3;
    private int strained;
    private int stable;
    private bool mayRaise = true;

    internal void BeginStream()
    {
        warmup = 3;
        strained = 0;
        stable = 0;
    }

    internal bool Sample(int rendered, int skipped, int cap, int lastIndex)
    {
        if (warmup > 0)
        {
            warmup--;
            return false;
        }
        strained = skipped >= 3 && skipped * 5 >= rendered + skipped ? strained + 1 : 0;
        stable = mayRaise && skipped == 0 && rendered * 4 >= cap * 3 ? stable + 1 : 0;
        if (Index > 0 && strained >= 4) Index--;
        else if (Index < lastIndex && stable >= 45) Index++;
        else return false;
        mayRaise = false;
        BeginStream();
        return true;
    }

    internal static (int Rendered, int Skipped)? Parse(string? line)
    {
        if (line is null) return null;
        var match = FrameReport.Match(line);
        if (!match.Success) return null;
        return (int.Parse(match.Groups[1].Value),
                match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0);
    }
}
