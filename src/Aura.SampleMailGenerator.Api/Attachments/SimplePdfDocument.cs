using System.Globalization;
using System.Text;

namespace Aura.SampleMailGenerator.Api.Attachments;

/// <summary>
/// Minimal, dependency-free PDF writer for generated test documents: headings, wrapped paragraphs, key/value
/// lines and simple tables over A4 pages. Uses the standard Helvetica fonts (WinAnsi encoding) so nothing is
/// embedded and output renders identically on Windows, macOS and Linux containers. Produces real text-layer
/// PDFs, which is what broker submissions mostly are; scanned/image PDFs need a different generator.
/// </summary>
public sealed class SimplePdfDocument
{
    private const double PageWidth = 595.28;   // A4 in points
    private const double PageHeight = 841.89;
    private const double Margin = 56;
    private const double ContentWidth = PageWidth - 2 * Margin;
    private const double LineSpacing = 1.35;

    private readonly List<StringBuilder> _pages = [];
    private readonly string _title;
    private StringBuilder _current = null!;
    private double _y;

    public SimplePdfDocument(string title)
    {
        _title = title;
        NewPage();
    }

    public SimplePdfDocument Heading(string text, double size = 15)
    {
        Space(size * 0.4);
        WriteWrapped(text, size, bold: true, x: Margin, width: ContentWidth);
        Space(size * 0.3);
        return this;
    }

    public SimplePdfDocument Paragraph(string text, double size = 10, bool bold = false)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            WriteWrapped(line, size, bold, Margin, ContentWidth);
        }
        Space(size * 0.5);
        return this;
    }

    /// <summary>A label in bold with its value aligned in a second column.</summary>
    public SimplePdfDocument Field(string label, string value, double size = 10, double labelWidth = 170)
    {
        var valueLines = Wrap(value, size, bold: false, ContentWidth - labelWidth);
        EnsureSpace(LineHeight(size) * Math.Max(1, valueLines.Count));
        DrawText(label, Margin, _y, size, bold: true);
        foreach (var line in valueLines)
        {
            DrawText(line, Margin + labelWidth, _y, size, bold: false);
            _y -= LineHeight(size);
        }
        return this;
    }

    /// <summary>A table with a bold header row. Column widths are fractions of the content width.</summary>
    public SimplePdfDocument Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, IReadOnlyList<double> columnFractions, double size = 9)
    {
        const double cellPadding = 4;
        var widths = columnFractions.Select(f => f * ContentWidth).ToArray();

        void Row(IReadOnlyList<string> cells, bool bold)
        {
            var wrapped = cells.Select((c, i) => Wrap(c, size, bold, widths[i] - 2 * cellPadding)).ToList();
            var rowHeight = wrapped.Max(w => Math.Max(1, w.Count)) * LineHeight(size) + cellPadding;
            EnsureSpace(rowHeight);

            var top = _y + LineHeight(size) - size * 0.2;
            var x = Margin;
            for (var i = 0; i < wrapped.Count; i++)
            {
                var lineY = _y;
                foreach (var line in wrapped[i])
                {
                    DrawText(line, x + cellPadding, lineY, size, bold);
                    lineY -= LineHeight(size);
                }
                x += widths[i];
            }
            _y -= rowHeight;
            DrawRule(top - rowHeight, bold ? 0.8 : 0.3);
        }

        Row(headers, bold: true);
        foreach (var row in rows)
        {
            Row(row, bold: false);
        }
        Space(size * 0.8);
        return this;
    }

    public SimplePdfDocument Space(double points)
    {
        _y -= points;
        return this;
    }

    public byte[] ToBytes()
    {
        // Object layout: 1 catalog, 2 page tree, 3 Helvetica, 4 Helvetica-Bold, 5 info, then (page, contents) pairs.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", _pages.Select((_, i) => $"{6 + i * 2} 0 R"))}] /Count {_pages.Count} >>",
            FontDictionary("Helvetica", bold: false),
            FontDictionary("Helvetica-Bold", bold: true),
            $"<< /Title ({EscapeText(_title)}) /Producer (Aura Sample Mail Generator) /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z) >>",
        };

        for (var i = 0; i < _pages.Count; i++)
        {
            var stream = _pages[i].ToString();
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(PageWidth)} {Num(PageHeight)}] " +
                        $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {7 + i * 2} 0 R >>");
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }

        // Every byte written is ASCII (non-ASCII text is octal-escaped), so string length equals byte length.
        var pdf = new StringBuilder("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R /Info 5 0 R >>\n")
           .Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    // Explicit /Widths: viewers' built-in base-14 metrics often lack the Euro glyph, which makes "€" overlap the
    // following digit. Supplying widths for every code also guarantees layout matches our own measurements.
    private static string FontDictionary(string baseFont, bool bold)
    {
        var widths = string.Join(" ", Enumerable.Range(FirstChar, LastChar - FirstChar + 1).Select(code => GlyphWidth(code, bold)));
        return $"<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont} /Encoding /WinAnsiEncoding " +
               $"/FirstChar {FirstChar} /LastChar {LastChar} /Widths [{widths}] >>";
    }

    private void NewPage()
    {
        _current = new StringBuilder();
        _pages.Add(_current);
        _y = PageHeight - Margin;
    }

    private void EnsureSpace(double height)
    {
        if (_y - height < Margin)
        {
            NewPage();
        }
    }

    private void WriteWrapped(string text, double size, bool bold, double x, double width)
    {
        var lines = Wrap(text, size, bold, width);
        if (lines.Count == 0)
        {
            _y -= LineHeight(size);
            return;
        }

        foreach (var line in lines)
        {
            EnsureSpace(LineHeight(size));
            DrawText(line, x, _y, size, bold);
            _y -= LineHeight(size);
        }
    }

    private void DrawText(string text, double x, double y, double size, bool bold)
    {
        _current.Append("BT /").Append(bold ? "F2" : "F1").Append(' ').Append(Num(size)).Append(" Tf ")
                .Append(Num(x)).Append(' ').Append(Num(y)).Append(" Td (").Append(EscapeText(text)).Append(") Tj ET\n");
    }

    private void DrawRule(double y, double weight)
    {
        _current.Append(Num(weight)).Append(" w ").Append(Num(Margin)).Append(' ').Append(Num(y)).Append(" m ")
                .Append(Num(PageWidth - Margin)).Append(' ').Append(Num(y)).Append(" l S\n");
    }

    private static double LineHeight(double size) => size * LineSpacing;

    private static List<string> Wrap(string text, double size, bool bold, double width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (current.Length > 0 && MeasureWidth(candidate, size, bold) > width)
            {
                lines.Add(current.ToString());
                current.Clear().Append(word);
            }
            else
            {
                current.Clear().Append(candidate);
            }
        }
        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }
        return lines;
    }

    private static double MeasureWidth(string text, double size, bool bold) =>
        text.Sum(c => GlyphWidth(ToWinAnsi(c), bold)) * size / 1000.0;

    private const int FirstChar = 32;
    private const int LastChar = 255;

    /// <summary>Advance width in 1/1000 em for a WinAnsi code, from the standard Adobe Helvetica AFMs.</summary>
    private static int GlyphWidth(int code, bool bold)
    {
        switch (code)
        {
            case >= 32 and <= 126:
                return (bold ? HelveticaBoldWidths : HelveticaWidths)[code - 32];
            case 0x80: return 556;                       // Euro
            case 0x85 or 0x97: return 1000;              // ellipsis, em dash
            case 0x91 or 0x92 or 0x82: return bold ? 278 : 222;
            case 0x93 or 0x94 or 0x84: return bold ? 500 : 333;
            case 0x95: return 350;                       // bullet
            case 0x96: return 556;                       // en dash
            case 0x99: return 1000;                      // trademark
            case 0xA0: return 278;                       // no-break space
            case >= 0xC0 and <= 0xFF:
                // Accented Latin letters (Irish fadas etc.) share the width of their base letter.
                var baseLetter = ((char)code).ToString().Normalize(System.Text.NormalizationForm.FormD)[0];
                return baseLetter is >= 'A' and <= 'z' ? GlyphWidth(baseLetter, bold) : 556;
            default:
                return 556;
        }
    }

    // Advance widths (1/1000 em) for ASCII 32..126.
    private static readonly int[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly int[] HelveticaBoldWidths =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    /// <summary>Maps a character to its WinAnsi (cp1252) code; unsupported characters become '?'.</summary>
    private static int ToWinAnsi(char c) => c switch
    {
        '€' => 0x80, '‚' => 0x82, '„' => 0x84, '…' => 0x85, '‘' => 0x91, '’' => 0x92,
        '“' => 0x93, '”' => 0x94, '•' => 0x95, '–' => 0x96, '—' => 0x97, '™' => 0x99,
        >= '\u00a0' and <= '\u00ff' => c,
        >= ' ' and <= '~' => c,
        _ => '?',
    };

    /// <summary>Escapes a string for a PDF literal, mapping to WinAnsi (cp1252) and octal-escaping non-ASCII bytes.</summary>
    private static string EscapeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var code = ToWinAnsi(c);

            switch (code)
            {
                case '(' or ')' or '\\':
                    sb.Append('\\').Append((char)code);
                    break;
                case > 126:
                    sb.Append('\\').Append(Convert.ToString(code, 8).PadLeft(3, '0'));
                    break;
                default:
                    sb.Append((char)code);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
