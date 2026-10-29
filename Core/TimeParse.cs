using System.Globalization;
using System.Text.RegularExpressions;

namespace DoomGuy.Core;

/// <summary>Reads reminder phrases: "stand up in 30m", "call mum at 18:00", "water every 1h", "rent tomorrow 9am".</summary>
public static class TimeParse
{
    private static readonly Regex Part = new(@"(\d+(?:\.\d+)?)\s*([a-z]+)", RegexOptions.IgnoreCase);
    private static readonly Regex Clock = new(@"^(\d{1,2})(?:[:.](\d{2}))?\s*(am|pm)?$", RegexOptions.IgnoreCase);

    public static TimeSpan? Duration(string s)
    {
        s = s.Trim().ToLowerInvariant().Replace(" and ", " ").Replace(",", " ");
        s = Regex.Replace(s, @"^(an?|one)\s+", "1 ");
        s = Regex.Replace(s, @"^half an? hour$", "30m");
        if (s.Length == 0) return null;
        // Every character other than spaces must belong to a number+unit pair.
        if (Part.Replace(s, "").Trim().Length > 0) return null;
        double total = 0;
        foreach (Match m in Part.Matches(s))
        {
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double unit = m.Groups[2].Value switch
            {
                "s" or "sec" or "secs" or "second" or "seconds" => 1,
                "m" or "min" or "mins" or "minute" or "minutes" => 60,
                "h" or "hr" or "hrs" or "hour" or "hours" => 3600,
                "d" or "day" or "days" => 86400,
                "w" or "wk" or "week" or "weeks" => 604800,
                _ => -1,
            };
            if (unit < 0) return null;
            total += n * unit;
        }
        return total > 0 ? TimeSpan.FromSeconds(total) : null;
    }

    /// <summary>The next moment a clock time comes round ("17:30", "5pm", "9", "noon").</summary>
    public static DateTime? At(string s, DateTime now, bool tomorrow = false)
    {
        s = s.Trim().ToLowerInvariant();
        if (s == "noon") s = "12:00";
        if (s == "midnight") s = "0:00";
        var m = Clock.Match(s);
        if (!m.Success)
        {
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var dt) && s.Any(char.IsDigit))
                return dt;
            return null;
        }
        int h = int.Parse(m.Groups[1].Value), min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (min > 59 || h > 23) return null;
        var ampm = m.Groups[3].Value;
        if (ampm == "pm" && h < 12) h += 12;
        if (ampm == "am" && h == 12) h = 0;

        var day = tomorrow ? now.Date.AddDays(1) : now.Date;
        var t = day.AddHours(h).AddMinutes(min);
        if (tomorrow) return t;
        if (t > now) return t;
        // "at 5" with no am/pm means the next 5 o'clock, morning or evening.
        if (ampm.Length == 0 && h < 12 && t.AddHours(12) > now) return t.AddHours(12);
        return t.AddDays(1);
    }

    /// <summary>Reads a "when" phrase on its own.</summary>
    public static bool When(string p, DateTime now, out DateTime due, out TimeSpan? every)
    {
        due = default; every = null;
        p = p.Trim().ToLowerInvariant();
        if (p.Length == 0) return false;

        if (p.StartsWith("every ") || p.StartsWith("daily"))
        {
            var rest = p.StartsWith("daily") ? "day " + p[5..].Trim() : p[6..].Trim();
            var daily = Regex.Match(rest, @"^(day|morning)(?:\s+(?:at\s+)?(.+))?$");
            if (daily.Success)
            {
                every = TimeSpan.FromDays(1);
                var t = daily.Groups[2].Success ? At(daily.Groups[2].Value, now) : (daily.Groups[1].Value == "morning" ? At("9am", now) : now.AddDays(1));
                if (t == null) return false;
                due = t.Value;
                return true;
            }
            var d = Duration(rest);
            if (d == null || d.Value.TotalSeconds < 10) return false;
            every = d;
            due = now + d.Value;
            return true;
        }
        if (p.StartsWith("in "))
        {
            var d = Duration(p[3..]);
            if (d == null) return false;
            due = now + d.Value;
            return true;
        }
        if (p.StartsWith("tomorrow"))
        {
            var rest = Regex.Replace(p[8..].Trim(), @"^at\s+", "");
            var t = rest.Length == 0 ? now.Date.AddDays(1).AddHours(9) : At(rest, now, tomorrow: true);
            if (t == null) return false;
            due = t.Value;
            return true;
        }
        if (p.StartsWith("at ") || p.StartsWith("@"))
        {
            var t = At(p.StartsWith("@") ? p[1..] : p[3..], now);
            if (t == null) return false;
            due = t.Value;
            return true;
        }
        var bare = Duration(p);
        if (bare != null) { due = now + bare.Value; return true; }
        if (Regex.IsMatch(p, @"^\d{1,2}([:.]\d{2}\s*(am|pm)?|\s*(am|pm))$"))
        {
            var t = At(p, now);
            if (t == null) return false;
            due = t.Value;
            return true;
        }
        return false;
    }

    /// <summary>Splits "text + when" in either order, preferring the longest "when" phrase.</summary>
    public static bool Reminder(string input, DateTime now, out string text, out DateTime due, out TimeSpan? every)
    {
        text = ""; due = default; every = null;
        var words = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int k = words.Length - 1; k >= 1; k--)
        {
            if (When(string.Join(' ', words[^k..]), now, out due, out every))
            {
                text = string.Join(' ', words[..^k]);
                return true;
            }
        }
        for (int k = words.Length - 1; k >= 1; k--)
        {
            if (When(string.Join(' ', words[..k]), now, out due, out every))
            {
                text = string.Join(' ', words[k..]);
                return true;
            }
        }
        return false;
    }

    public static string Short(TimeSpan t)
    {
        if (t.TotalSeconds < 0) t = TimeSpan.Zero;
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}S";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}M{(t.TotalMinutes < 10 ? $"{t.Seconds:00}S" : "")}";
        if (t.TotalHours < 24) return t.Minutes == 0 ? $"{(int)t.TotalHours}H" : $"{(int)t.TotalHours}H{t.Minutes:00}M";
        return $"{(int)t.TotalDays}D{t.Hours}H";
    }
}
