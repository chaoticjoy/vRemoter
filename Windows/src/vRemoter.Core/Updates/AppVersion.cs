namespace vRemoter.Core.Updates;

public readonly struct AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    public int[] Numbers { get; }
    public string[]? Prerelease { get; }

    public AppVersion(string rawValue)
    {
        string cleaned = rawValue.Trim().TrimStart('v', 'V');
        string[] parts = cleaned.Split('-', 2);
        var parsed = parts[0]
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out int val) ? val : 0)
            .ToList();
        while (parsed.Count < 3)
        {
            parsed.Add(0);
        }
        Numbers = parsed.ToArray();
        Prerelease = parts.Length > 1
            ? parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries)
            : null;
    }

    public int CompareTo(AppVersion other)
    {
        int count = Math.Max(Numbers.Length, other.Numbers.Length);
        for (int i = 0; i < count; i++)
        {
            int left = i < Numbers.Length ? Numbers[i] : 0;
            int right = i < other.Numbers.Length ? other.Numbers[i] : 0;
            if (left != right) return left.CompareTo(right);
        }

        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, not null) => 1,
            (not null, null) => -1,
            (var left, var right) => ComparePrerelease(left!, right!)
        };
    }

    private static int ComparePrerelease(string[] left, string[] right)
    {
        int count = Math.Max(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            if (i >= left.Length) return -1;
            if (i >= right.Length) return 1;

            bool leftNum = int.TryParse(left[i], out int li);
            bool rightNum = int.TryParse(right[i], out int ri);
            int cmp = (leftNum, rightNum) switch
            {
                (true, true) => li.CompareTo(ri),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(left[i], right[i], StringComparison.OrdinalIgnoreCase)
            };
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    public bool Equals(AppVersion other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);
    public override int GetHashCode() => CompareTo(default);

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;
    public static bool operator ==(AppVersion left, AppVersion right) => left.Equals(right);
    public static bool operator !=(AppVersion left, AppVersion right) => !left.Equals(right);
}

