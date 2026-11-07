public static class InventoryHelpers
{
    // Normalize a store name / ID to a canonical store ID
    // "Seattle", "SEA", "seattle", "Seatle" all mean SEA
    public static string NormalizeStoreId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var normalized = input.Trim().ToLowerInvariant();

        var known = new Dictionary<string, string>
        {
            ["portland"] = "POR",
            ["por"] = "POR",
            ["seattle"] = "SEA",
            ["sea"] = "SEA",
            ["beaverton"] = "BEA",
            ["bea"] = "BEA",
        };

        if (known.TryGetValue(normalized, out var exact))
        {
            return exact;
        }

        // Fuzzy match for typos: 1-2 edits away from a known city
        string[] cities = { "portland", "seattle", "beaverton" };
        var best = cities
            .Select(c => new { City = c, Distance = Levenshtein(normalized, c) })
            .OrderBy(x => x.Distance)
            .First();

        if (best.Distance <= 2)
        {
            return known[best.City];
        }

        return input.Trim().ToUpperInvariant();
    }

    public static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
