namespace InatBestiary.Services;

// Compares strings so that embedded numbers sort by numeric value:
// test-2.jpg < test-11.jpg, photo-1 < photo-10 < photo-20
public sealed class NaturalSortComparer : IComparer<string>
{
    public static readonly NaturalSortComparer Instance = new();
    private NaturalSortComparer() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return  1;

        int xi = 0, yi = 0;
        while (xi < x.Length && yi < y.Length)
        {
            if (char.IsDigit(x[xi]) && char.IsDigit(y[yi]))
            {
                long xn = 0, yn = 0;
                while (xi < x.Length && char.IsDigit(x[xi])) xn = xn * 10 + (x[xi++] - '0');
                while (yi < y.Length && char.IsDigit(y[yi])) yn = yn * 10 + (y[yi++] - '0');
                int cmp = xn.CompareTo(yn);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = char.ToUpperInvariant(x[xi]).CompareTo(char.ToUpperInvariant(y[yi]));
                if (cmp != 0) return cmp;
                xi++; yi++;
            }
        }

        return (x.Length - xi).CompareTo(y.Length - yi);
    }
}
