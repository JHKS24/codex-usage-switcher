using System.Buffers;

namespace CodexDesktopUsageSwitcher.Windows.Infrastructure;

// Forward-only JSONL line reader over a stream, the single source of truth for byte-level
// line splitting shared by both history readers (guardrail D3). It splits on '\n' (0x0A) —
// which can never occur inside a multi-byte UTF-8 sequence, so lines stay intact — and hands
// each line to the caller as ReadOnlyMemory<byte> for JsonDocument.Parse, avoiding the
// per-line string allocation and UTF-8 decode the old StreamReader.ReadLine path paid.
//
// Each returned line EXCLUDES the trailing '\n' (a trailing '\r' is left for the caller to
// ignore, exactly like the old TrimEnd('\r')). The final UNTERMINATED line (a file not ending
// in '\n') is intentionally withheld: ParsedBytes always points just past the last consumed
// '\n', so a resume from ParsedBytes re-reads that partial line once an append completes it —
// no double counting (verdict V2). A leading UTF-8 BOM at offset 0 is skipped to match the
// old StreamReader's BOM handling.
//
// The memory handed back is valid only until the next TryReadLine call; callers must parse it
// (and dispose the JsonDocument) before reading the next line.
internal sealed class JsonlLineReader : IDisposable
{
    private const int InitialCapacity = 64 * 1024;
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly Stream _stream;
    private byte[] _buf;
    private int _lineStart;     // index in _buf of the next unconsumed line
    private int _len;           // count of valid bytes in _buf
    private long _absLineStart; // absolute stream offset of _buf[_lineStart]
    private bool _eof;

    public JsonlLineReader(Stream stream, long startOffset)
    {
        _stream = stream;
        _absLineStart = startOffset;
        _buf = ArrayPool<byte>.Shared.Rent(InitialCapacity);
        if (startOffset == 0)
        {
            SkipBom();
        }
    }

    // Offset just past the last consumed '\n' — i.e. the start of the pending (withheld)
    // line. Resume the next range from exactly this offset.
    public long ParsedBytes => _absLineStart;

    public bool TryReadLine(out ReadOnlyMemory<byte> line)
    {
        while (true)
        {
            var nl = _buf.AsSpan(_lineStart, _len - _lineStart).IndexOf((byte)'\n');
            if (nl >= 0)
            {
                line = _buf.AsMemory(_lineStart, nl); // excludes '\n'
                var consumed = nl + 1;
                _lineStart += consumed;
                _absLineStart += consumed;
                return true;
            }

            if (_eof)
            {
                line = default;
                return false;
            }

            Fill();
        }
    }

    // Index of the first byte that is not ASCII JSON whitespace (space/tab/CR/LF), or -1 for an
    // all-whitespace span. Real JSONL only uses ASCII whitespace, so this is the byte-level
    // equivalent of the old start check; it deliberately does NOT strip the wider Unicode set
    // string.Trim() recognized (U+00A0, U+FEFF, U+2028/9, U+3000), which never appears here.
    public static int FirstNonWhitespace(ReadOnlySpan<byte> s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var b = s[i];
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return i;
            }
        }

        return -1;
    }

    private void SkipBom()
    {
        while (_len < Bom.Length && !_eof)
        {
            Fill();
        }

        if (_len >= Bom.Length && _buf.AsSpan(0, Bom.Length).SequenceEqual(Bom))
        {
            _lineStart = Bom.Length;
            _absLineStart += Bom.Length;
        }
    }

    private void Fill()
    {
        // Slide the pending bytes to the front so a long line has room to grow.
        if (_lineStart > 0)
        {
            var pending = _len - _lineStart;
            if (pending > 0)
            {
                Array.Copy(_buf, _lineStart, _buf, 0, pending);
            }

            _len = pending;
            _lineStart = 0;
        }

        if (_len == _buf.Length)
        {
            Grow();
        }

        var n = _stream.Read(_buf, _len, _buf.Length - _len);
        if (n == 0)
        {
            _eof = true;
        }
        else
        {
            _len += n;
        }
    }

    private void Grow()
    {
        var bigger = ArrayPool<byte>.Shared.Rent(_buf.Length * 2);
        Array.Copy(_buf, 0, bigger, 0, _len);
        ArrayPool<byte>.Shared.Return(_buf);
        _buf = bigger;
    }

    public void Dispose()
    {
        if (_buf.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buf);
            _buf = [];
        }
    }
}
