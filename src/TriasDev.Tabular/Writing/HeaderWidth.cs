using System.Globalization;
using System.Text;

namespace TriasDev.Tabular;

/// <summary>
/// How wide a column must be for its header to be read in full: the lower bound every workbook
/// column's width is raised to, in the characters <see cref="WriteColumn.Width"/> is in.
/// </summary>
/// <remarks>
/// <para>
/// A writer that streams cannot measure its data, but it knows the header when the sheet begins, and
/// a column narrower than its header cannot be read: nobody can tell what it holds. So the header sets
/// the floor, and the declared width only ever raises it.
/// </para>
/// <para>
/// The measure errs wide, never narrow. The width unit is the default font's digit — Calibri 11 in
/// the xlsx we write, Liberation Sans 10 (Arial's metrics) where LibreOffice opens our ods — about 7
/// pixels either way. A character is measured by its advance in Helvetica, whose metrics Arial and
/// Liberation Sans share and which runs wider than Calibri: the wider of its regular and bold advance,
/// so a bold header style needs no case of its own. The em is taken at 11 points, the larger of the
/// two default sizes, 14⅔ pixels, so that 477 thousandths of it make a 7-pixel character: Helvetica at
/// 11 points is wider than Calibri at 11 and than itself at 10, so either program fits the header.
/// (Measured against Calibri and Liberation Sans, a 10-point em left a CJK header 0.7 pixels short in
/// Excel.) Outside ASCII the shapes are unknown: a lowercase letter counts as an "m", anything else as
/// a whole em — a CJK ideograph is one em, two characters, as terminals count it. A text element is
/// one character, so a letter and its combining accent or an emoji sequence counts once. Then one
/// character for the cell's own margins, and three for the filter's drop-down button (about 17
/// pixels) when the header carries one.
/// </para>
/// <para>
/// A header of several lines is as wide as its longest line. A header whose style wraps is as wide as
/// its longest word: wrapping is how a long header sits over a narrow column, and the bound must not
/// undo it.
/// </para>
/// </remarks>
internal static class HeaderWidth
{
    /// <summary>Thousandths of an em that make one character: 7 pixels of an 11-point, 14⅔-pixel em.</summary>
    private const double ThousandthsPerCharacter = 7_000 / (11 * 96 / 72d);

    /// <summary>The cell's margins, left and right.</summary>
    private const double Padding = 1;

    /// <summary>The auto-filter's drop-down button, about 17 pixels in Excel and LibreOffice: 21 is room for it.</summary>
    private const double FilterButton = 3;

    /// <summary>A lowercase letter outside ASCII, as wide as the widest ASCII one ("m" in bold).</summary>
    private const int OtherLowercase = 889;

    /// <summary>Any other character outside ASCII: a whole em.</summary>
    private const int OtherCharacter = 1000;

    /// <summary>
    /// The advance of each character from ' ' to '~', in thousandths of an em: the wider of Helvetica's
    /// and Helvetica-Bold's (Adobe's published font metrics).
    /// </summary>
    private static ReadOnlySpan<short> Ascii =>
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, // space to /
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611, // 0 to ?
        1015, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, // @ to O
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556, // P to _
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, // ` to o
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584, // p to ~
    ];

    /// <summary>
    /// The width a column needs to show <paramref name="header"/> in full, at most
    /// <paramref name="widest"/>, rounded up to whole characters so that ods, which rounds widths, never
    /// rounds it down.
    /// </summary>
    /// <param name="header">The header text.</param>
    /// <param name="wrap">Whether the header's style wraps its text, so that only its longest word must fit.</param>
    /// <param name="filter">Whether the header carries the auto-filter's drop-down button.</param>
    /// <param name="widest">The widest column the formats hold.</param>
    public static double Of(string header, bool wrap, bool filter, double widest)
    {
        int longest = 0;
        int run = 0;
        ReadOnlySpan<char> rest = header;

        while (!rest.IsEmpty)
        {
            int length = StringInfo.GetNextTextElementLength(rest);
            char first = rest[0];

            // "\r\n" is one text element, so a line break of either kind ends the run once.
            if (first is '\n' or '\r' || (wrap && char.IsWhiteSpace(first)))
            {
                longest = Math.Max(longest, run);
                run = 0;
            }
            else
            {
                run += Advance(rest[..length]);
            }

            rest = rest[length..];
        }

        longest = Math.Max(longest, run);
        double characters = (longest / ThousandthsPerCharacter) + Padding + (filter ? FilterButton : 0);

        return Math.Min(Math.Ceiling(characters), widest);
    }

    private static int Advance(ReadOnlySpan<char> element)
    {
        char first = element[0];

        if (first is >= ' ' and <= '~')
        {
            return Ascii[first - ' '];
        }

        if (first == '\t')
        {
            return Ascii[0];
        }

        return Rune.DecodeFromUtf16(element, out Rune rune, out _) == System.Buffers.OperationStatus.Done && Rune.IsLower(rune)
            ? OtherLowercase
            : OtherCharacter;
    }
}
