using System.Globalization;
using System.Text;

namespace Chandlery.Services;

/// <summary>A name used inside an expression: a field (<c>draftAft</c>), or a part of a grid (<c>measured.meas.b1.f</c>, <c>measured.*.b1.f</c> for a column, <c>measured.meas.*</c> for a row, <c>measured</c> for all of it).</summary>
public sealed record FormRef(string Head, IReadOnlyList<string> Rest)
{
    public string Text => Rest.Count == 0 ? Head : Head + "." + string.Join(".", Rest);

    /// <summary>For a grid reference: the row id or <c>*</c>.</summary>
    public string Row => Rest.Count == 0 ? "*" : Rest[0];

    /// <summary>For a grid reference: the cell id (which may itself contain dots, like <c>b1.f</c>) or <c>*</c>.</summary>
    public string Cell => Rest.Count < 2 ? "*" : string.Join(".", Rest.Skip(1));
}

/// <summary>What an expression reads its names from. A field gives one value (or none), a column, row or grid gives a list.</summary>
public interface IFormScope
{
    object? Resolve(FormRef reference);
}

/// <summary>
/// The small closed language the rules of a form are written in: numbers, text in quotes, names, <c>+ - * /</c>, comparisons, <c>and or not</c>, parentheses and
/// <c>sum count any min max round</c>. It is parsed and evaluated here and never run as code, so a form can compute and do nothing else. The length, depth and
/// size are limited so that a definition can never hang a vessel.
/// A missing answer makes an arithmetic result missing (never zero); a division by zero is missing too.
/// </summary>
public sealed class FormExpression
{
    public const int MaxLength = 300;
    public const int MaxDepth = 20;
    public const int MaxNodes = 200;

    private static readonly Dictionary<string, (int Min, int Max)> Functions = new()
    {
        ["sum"] = (1, 20), ["count"] = (1, 20), ["any"] = (1, 20), ["min"] = (1, 20), ["max"] = (1, 20), ["round"] = (1, 2),
    };

    private abstract record Node;
    private sealed record Num(decimal Value) : Node;
    private sealed record Str(string Value) : Node;
    private sealed record Bool(bool Value) : Node;
    private sealed record Ref(FormRef Reference) : Node;
    private sealed record Unary(string Op, Node Operand) : Node;
    private sealed record Binary(string Op, Node Left, Node Right) : Node;
    private sealed record Call(string Name, IReadOnlyList<Node> Args) : Node;

    private readonly Node _root;
    public IReadOnlyList<FormRef> References { get; }
    public string Text { get; }

    private FormExpression(string text, Node root, IReadOnlyList<FormRef> references) => (Text, _root, References) = (text, root, references);

    public static bool TryParse(string? text, out FormExpression? expression, out string error)
    {
        expression = null;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "The expression is empty."; return false; }
        if (text.Length > MaxLength) { error = $"The expression is longer than {MaxLength} characters."; return false; }
        try
        {
            var parser = new Parser(text);
            var root = parser.ParseAll();
            expression = new FormExpression(text, root, parser.References);
            return true;
        }
        catch (FormatException ex) { error = ex.Message; return false; }
    }

    public static FormExpression Parse(string text)
        => TryParse(text, out var expression, out var error) ? expression! : throw new FormatException(error);

    // ---- evaluating ----

    public object? Evaluate(IFormScope scope) => Eval(_root, scope);

    public bool EvaluateBool(IFormScope scope) => Truthy(Eval(_root, scope));

    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        decimal d => d != 0,
        string s => s != "",
        List<object?> list => list.Any(Truthy),
        _ => true,
    };

    private static object? Eval(Node node, IFormScope scope)
    {
        switch (node)
        {
            case Num n: return n.Value;
            case Str s: return s.Value;
            case Bool b: return b.Value;
            case Ref r: return scope.Resolve(r.Reference);
            case Unary { Op: "not" } u: return !Truthy(Eval(u.Operand, scope));
            case Unary u: return Eval(u.Operand, scope) is decimal d ? -d : null;
            case Binary { Op: "and" } b: return Truthy(Eval(b.Left, scope)) && Truthy(Eval(b.Right, scope));
            case Binary { Op: "or" } b: return Truthy(Eval(b.Left, scope)) || Truthy(Eval(b.Right, scope));
            case Binary b: return Operate(b.Op, Eval(b.Left, scope), Eval(b.Right, scope));
            case Call c: return CallFunction(c.Name, c.Args.Select(a => Eval(a, scope)).ToList());
            default: return null;
        }
    }

    private static object? Operate(string op, object? left, object? right)
    {
        switch (op)
        {
            case "+" or "-" or "*" or "/":
                if (left is not decimal a || right is not decimal b) return null;
                try
                {
                    return op switch { "+" => a + b, "-" => a - b, "*" => a * b, _ => b == 0 ? null : (object)(a / b) };
                }
                catch (OverflowException) { return null; }
            case "=": return Same(left, right);
            case "!=": return !Same(left, right);
            default:
                var order = Order(left, right);
                if (order is null) return false;
                return op switch { "<" => order < 0, "<=" => order <= 0, ">" => order > 0, _ => order >= 0 };
        }
    }

    private static bool Same(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is decimal a && right is decimal b) return a == b;
        return string.Equals(Plain(left), Plain(right), StringComparison.Ordinal);
    }

    private static int? Order(object? left, object? right)
    {
        if (left is decimal a && right is decimal b) return a.CompareTo(b);
        if (left is string s && right is string t) return Math.Sign(string.CompareOrdinal(s, t));   // ISO dates and times order correctly as text
        return null;
    }

    private static string Plain(object? value) => value switch
    {
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        null => "",
        _ => value.ToString() ?? "",
    };

    /// <summary>Every single value inside a value or list, leaving out what is missing or empty.</summary>
    private static IEnumerable<object> Flatten(object? value)
    {
        if (value is List<object?> list)
        {
            foreach (var item in list)
                foreach (var inner in Flatten(item)) yield return inner;
        }
        else if (value is not null && value is not "") yield return value;
    }

    private static object? CallFunction(string name, List<object?> args)
    {
        var all = args.SelectMany(Flatten).ToList();
        var numbers = all.OfType<decimal>().ToList();
        switch (name)
        {
            case "sum": return numbers.Sum();
            case "count": return (decimal)all.Count;
            case "any": return all.Any(v => v is not false);
            case "min": return numbers.Count == 0 ? null : numbers.Min();
            case "max": return numbers.Count == 0 ? null : numbers.Max();
            case "round":
                if (args[0] is not decimal value) return null;
                var places = args.Count > 1 && args[1] is decimal p ? (int)Math.Clamp(p, 0, 10) : 0;
                return Math.Round(value, places, MidpointRounding.AwayFromZero);
            default: return null;
        }
    }

    // ---- parsing ----

    private sealed class Parser
    {
        private readonly string _text;
        private int _at;
        private int _depth;
        private int _nodes;
        public List<FormRef> References { get; } = [];

        public Parser(string text) => _text = text;

        public Node ParseAll()
        {
            var node = ParseOr();
            SkipSpace();
            if (_at < _text.Length) throw Fail($"Unexpected \"{_text[_at]}\".");
            return node;
        }

        private FormatException Fail(string message) => new($"{message} (at position {_at + 1})");

        private void SkipSpace() { while (_at < _text.Length && char.IsWhiteSpace(_text[_at])) _at++; }

        private T Count<T>(T node) where T : Node
        {
            if (++_nodes > MaxNodes) throw Fail($"The expression is too large (more than {MaxNodes} parts).");
            return node;
        }

        private bool Peek(string token)
        {
            SkipSpace();
            return string.CompareOrdinal(_text, _at, token, 0, token.Length) == 0;
        }

        private bool PeekWord(string word)
        {
            SkipSpace();
            if (string.Compare(_text, _at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            var end = _at + word.Length;
            return end >= _text.Length || !(char.IsLetterOrDigit(_text[end]) || _text[end] == '_' || _text[end] == '.');
        }

        private Node Deeper(Func<Node> parse)
        {
            if (++_depth > MaxDepth) throw Fail($"The expression is nested deeper than {MaxDepth} levels.");
            try { return parse(); }
            finally { _depth--; }
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (PeekWord("or")) { _at += 2; left = Count(new Binary("or", left, ParseAnd())); }
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (PeekWord("and")) { _at += 3; left = Count(new Binary("and", left, ParseNot())); }
            return left;
        }

        private Node ParseNot()
        {
            if (PeekWord("not")) { _at += 3; return Count(new Unary("not", Deeper(ParseNot))); }
            return ParseComparison();
        }

        private Node ParseComparison()
        {
            var left = ParseSum();
            foreach (var op in new[] { "<=", ">=", "!=", "<", ">", "=" })
                if (Peek(op))
                {
                    _at += op.Length;
                    return Count(new Binary(op, left, ParseSum()));
                }
            return left;
        }

        private Node ParseSum()
        {
            var left = ParseProduct();
            while (true)
            {
                if (Peek("+")) { _at++; left = Count(new Binary("+", left, ParseProduct())); }
                else if (Peek("-")) { _at++; left = Count(new Binary("-", left, ParseProduct())); }
                else return left;
            }
        }

        private Node ParseProduct()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Peek("*")) { _at++; left = Count(new Binary("*", left, ParseUnary())); }
                else if (Peek("/")) { _at++; left = Count(new Binary("/", left, ParseUnary())); }
                else return left;
            }
        }

        private Node ParseUnary()
        {
            if (Peek("-")) { _at++; return Count(new Unary("-", Deeper(ParseUnary))); }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipSpace();
            if (_at >= _text.Length) throw Fail("The expression ends too soon.");
            var c = _text[_at];

            if (c == '(')
            {
                _at++;
                var inner = Deeper(ParseOr);
                SkipSpace();
                if (_at >= _text.Length || _text[_at] != ')') throw Fail("A closing bracket is missing.");
                _at++;
                return inner;
            }
            if (char.IsDigit(c) || c == '.') return ParseNumber();
            if (c is '"' or '\'') return ParseString(c);
            if (char.IsLetter(c) || c == '_') return ParseName();
            throw Fail($"Unexpected \"{c}\".");
        }

        private Node ParseNumber()
        {
            var start = _at;
            while (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '.')) _at++;
            var text = _text[start.._at];
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) throw Fail($"\"{text}\" is not a number.");
            return Count(new Num(value));
        }

        private Node ParseString(char quote)
        {
            _at++;
            var text = new StringBuilder();
            while (_at < _text.Length && _text[_at] != quote) text.Append(_text[_at++]);
            if (_at >= _text.Length) throw Fail("A quote is not closed.");
            _at++;
            return Count(new Str(text.ToString()));
        }

        private Node ParseName()
        {
            var start = _at;
            while (_at < _text.Length && (char.IsLetterOrDigit(_text[_at]) || _text[_at] == '_')) _at++;
            var head = _text[start.._at];

            if (_at < _text.Length && _text[_at] == '(')
            {
                var name = head.ToLowerInvariant();
                if (!Functions.TryGetValue(name, out var arity)) throw Fail($"\"{head}\" is not a function ({string.Join(", ", Functions.Keys)}).");
                _at++;
                var args = new List<Node>();
                SkipSpace();
                if (_at < _text.Length && _text[_at] == ')') _at++;
                else
                {
                    while (true)
                    {
                        args.Add(Deeper(ParseOr));
                        SkipSpace();
                        if (_at < _text.Length && _text[_at] == ',') { _at++; continue; }
                        if (_at < _text.Length && _text[_at] == ')') { _at++; break; }
                        throw Fail("A bracket or a comma is missing.");
                    }
                }
                if (args.Count < arity.Min || args.Count > arity.Max) throw Fail($"{name}() takes {(arity.Min == arity.Max ? arity.Min.ToString() : $"{arity.Min} to {arity.Max}")} value(s), not {args.Count}.");
                return Count(new Call(name, args));
            }

            var lower = head.ToLowerInvariant();
            if (lower == "true") return Count(new Bool(true));
            if (lower == "false") return Count(new Bool(false));
            if (lower is "and" or "or" or "not") throw Fail($"\"{head}\" is a reserved word and cannot be used as a name.");

            var rest = new List<string>();
            while (_at < _text.Length && _text[_at] == '.')
            {
                _at++;
                if (_at < _text.Length && _text[_at] == '*') { _at++; rest.Add("*"); continue; }
                var partStart = _at;
                while (_at < _text.Length && (char.IsLetterOrDigit(_text[_at]) || _text[_at] == '_')) _at++;
                if (partStart == _at) throw Fail("A name is missing after a dot.");
                rest.Add(_text[partStart.._at]);
            }
            var reference = new FormRef(head, rest);
            References.Add(reference);
            return Count(new Ref(reference));
        }
    }
}
