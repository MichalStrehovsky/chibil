using System.Text;

namespace Chilink;

internal sealed class ElfLinkerScript(string path, string text)
{
    private int _offset;
    private string _token;
    private bool _quoted;
    private readonly List<string> _inputs = new();

    public static IReadOnlyList<string> Read(string path, Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
        try
        {
            return new ElfLinkerScript(path, reader.ReadToEnd()).Read();
        }
        catch (DecoderFallbackException)
        {
            throw new LinkException($"{path}: invalid UTF-8 linker script");
        }
    }

    private LinkException Error(string message) => new($"{path}: linker script: {message}");

    private IReadOnlyList<string> Read()
    {
        Next();
        while (_token != null)
        {
            if (Take(";"))
                continue;
            if (Take("OUTPUT_FORMAT"))
            {
                Expect("(");
                while (_token != null && (_quoted || _token != ")"))
                    Next();
                Expect(")");
            }
            else if (Take("INPUT") || Take("GROUP"))
                ReadInputs(0);
            else
                throw Error($"unsupported command '{_token}'");
        }
        if (_inputs.Count == 0)
            throw Error("expected INPUT or GROUP with at least one input");
        return _inputs;
    }

    private void ReadInputs(int depth)
    {
        if (depth >= 64)
            throw Error("AS_NEEDED nesting limit exceeded");
        Expect("(");
        bool any = false;
        while (_token != null && (_quoted || _token != ")"))
        {
            if (Take("AS_NEEDED"))
                ReadInputs(depth + 1);
            else
            {
                if (!_quoted && _token is "(" or "," or ";")
                    throw Error($"expected a library filename, found '{_token}'");
                if (_token.Length == 0)
                    throw Error("empty library filename");
                _inputs.Add(_token);
                Next();
            }
            any = true;
            if (Take(",") && (_token == null || !_quoted && _token == ")"))
                throw Error("expected a library filename after ','");
        }
        if (!any)
            throw Error("empty input list");
        Expect(")");
    }

    private bool Take(string value)
    {
        if (_quoted || _token != value)
            return false;
        Next();
        return true;
    }

    private void Expect(string value)
    {
        if (!Take(value))
            throw Error($"expected '{value}', found '{_token ?? "end of file"}'");
    }

    private void Next()
    {
        while (_offset < text.Length)
        {
            if (char.IsWhiteSpace(text[_offset]))
                _offset++;
            else if (text.AsSpan(_offset).StartsWith("/*", StringComparison.Ordinal))
            {
                int end = text.IndexOf("*/", _offset + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw Error("unterminated comment");
                _offset = end + 2;
            }
            else
                break;
        }
        _quoted = false;
        if (_offset == text.Length)
        {
            _token = null;
            return;
        }
        int start = _offset++;
        if (text[start] == '"')
        {
            int end = text.IndexOf('"', _offset);
            if (end < 0)
                throw Error("unterminated quoted filename");
            _quoted = true;
            _token = text[_offset..end];
            _offset = end + 1;
        }
        else if ("(),;".Contains(text[start]))
            _token = text[start].ToString();
        else
        {
            while (_offset < text.Length && !char.IsWhiteSpace(text[_offset]) &&
                !"(),;\"".Contains(text[_offset]) &&
                !text.AsSpan(_offset).StartsWith("/*", StringComparison.Ordinal))
                _offset++;
            _token = text[start.._offset];
        }
    }
}
