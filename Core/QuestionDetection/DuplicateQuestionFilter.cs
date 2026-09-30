using System.Text.RegularExpressions;

namespace ConversationAssistant.Core.QuestionDetection;

public sealed partial class DuplicateQuestionFilter(TimeSpan? cooldown = null)
{
    private readonly object _gate = new();
    private readonly TimeSpan _cooldown = cooldown ?? TimeSpan.FromSeconds(45);
    private string _last = "";
    private DateTimeOffset _lastTime;

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonLetters();

    public bool Accept(string question, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var normalized = NonLetters().Replace(question.ToLowerInvariant(), "");
        if (normalized.Length == 0) throw new ArgumentException("Question has no words.", nameof(question));
        lock (_gate)
        {
            var recent = timestamp >= _lastTime && timestamp - _lastTime < _cooldown;
            var prefix = normalized.StartsWith(_last, StringComparison.Ordinal) ||
                         _last.StartsWith(normalized, StringComparison.Ordinal);
            var similar = Similarity(normalized, _last) >= .82;
            if (recent && _last.Length > 0 && (prefix || similar)) return false;
            _last = normalized;
            _lastTime = timestamp;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) { _last = ""; _lastTime = default; }
    }

    private static double Similarity(string a, string b)
    {
        if (b.Length == 0) return 0;
        // Keep normalization work bounded even for unusually long STT segments.
        a = a[..Math.Min(a.Length, 256)];
        b = b[..Math.Min(b.Length, 256)];
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return 1d - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
}
