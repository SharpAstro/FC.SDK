using System.Globalization;
using LALR.CC;
using LALR.CC.LexicalGrammar;

namespace FC.SDK.Scripting;

/// <summary>
/// Source text to <see cref="CameraScript"/>, through the tables LALR.CC's source generator
/// pre-bakes from <c>ScriptGrammar.lalr.yaml</c>.
/// </summary>
internal static class ScriptParser
{
    internal static CameraScript Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var parser = ScriptGrammar.BuildParser(new AstBuilder());
        try
        {
            using var lexer = BytesLexer.FromString(source, ScriptGrammar.BuildLexer());
            using var tokens = new SyncLATokenIterator(lexer);

            // trimReductions off: every production has an action, and with trimming on the parser
            // folds single-child reductions away before their action runs.
            var result = parser.ParseInput(tokens, debugger: null, trimReductions: false);
            if (result.IsError || result.Content is not List<Stmt> statements)
                throw new ScriptSyntaxException(result.Position.Line, result.Position.Column, "the script does not parse");

            // Before anything runs: a mistyped name must not be found out three shutter actuations
            // into a run.
            CheckNames(statements, new HashSet<string>(StringComparer.Ordinal));

            return new CameraScript(statements, source.Split('\n'));
        }
        catch (ParseErrorException ex)
        {
            var found = ex.OffendingToken is { } token && token.ID >= 0
                ? $"'{token.Content}'"
                : "the end of the script";
            // The end of the input has no position of its own; the last line is where the reader
            // needs to look.
            var (line, column) = ex.Position.IsKnown
                ? (ex.Position.Line, ex.Position.Column)
                : (source.TrimEnd().Count(c => c == '\n') + 1, 1);
            throw new ScriptSyntaxException(line, column, $"unexpected {found}; expected {Expected(ex.ExpectedSymbolIds)}");
        }
        catch (LexerException ex)
        {
            throw new ScriptSyntaxException(
                ex.Position.Line, ex.Position.Column, $"unexpected character '{(char)ex.OffendingByte}'");
        }
    }

    /// <summary>
    /// Every name must be a constant or a variable some earlier <c>let</c> or enclosing <c>for</c>
    /// defined. Statements run in source order with no branches, so "earlier in the text" is
    /// exactly "defined by the time it runs".
    /// </summary>
    private static void CheckNames(IReadOnlyList<Stmt> statements, HashSet<string> defined)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStmt s:
                    Check(s.Value);
                    defined.Add(s.Name);
                    break;
                case SetStmt s:
                    Check(s.Code);
                    Check(s.Value);
                    break;
                case ExpectStmt s:
                    Check(s.Condition);
                    break;
                case LogStmt s:
                    Check(s.Value);
                    break;
                case StartLiveViewStmt { Output: { } output }:
                    Check(output);
                    break;
                case FramesStmt { Minimum: { } minimum }:
                    Check(minimum);
                    break;
                case ForStmt s:
                    foreach (var item in s.Items) Check(item);
                    var added = defined.Add(s.Variable);
                    // A `let` inside the body stays defined after it, as it does when run.
                    CheckNames(s.Body, defined);
                    if (added) defined.Remove(s.Variable);
                    break;
            }
        }

        void Check(Expr expr)
        {
            switch (expr)
            {
                case NameExpr e when !defined.Contains(e.Name) && !ScriptConstants.TryGet(e.Name, out _):
                    throw new ScriptSyntaxException(e.Line, 1, $"'{e.Name}' is not defined{Suggest(e.Name)}");
                case ReadExpr e:
                    Check(e.Code);
                    break;
                case NotExpr e:
                    Check(e.Operand);
                    break;
                case BinaryExpr e:
                    Check(e.Left);
                    Check(e.Right);
                    break;
            }
        }
    }

    /// <summary>The same name with different casing or under another enum, if there is one.</summary>
    private static string Suggest(string name)
    {
        var member = name[(name.LastIndexOf('.') + 1)..];
        var near = ScriptConstants.Names
            .Where(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("." + member, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        return near.Length is 0 ? "" : $". Did you mean {string.Join(" or ", near)}?";
    }

    private static string Expected(IReadOnlyList<int> symbolIds)
    {
        var symbols = ScriptGrammar.Schema.Symbols;
        var names = symbolIds
            .Select(id => id < 0 ? "the end of the script" : id < symbols.Count ? Describe(symbols[id]) : "?")
            .Distinct()
            .ToArray();
        return names.Length switch
        {
            0 => "something else",
            1 => names[0],
            _ => "one of " + string.Join(", ", names),
        };

        static string Describe(string symbol) => symbol switch
        {
            "duration" => "a duration (3s, 500ms)",
            "number" => "a number",
            "ident" or "qualified" => "a name",
            "string" => "a \"string\"",
            _ => $"'{symbol}'",
        };
    }

    /// <summary>
    /// One method per production. Each returns the node (or list) its parent's method then finds in
    /// the matching <c>ArgN.Content</c>; a token's content is its lexeme.
    /// </summary>
    private sealed class AstBuilder : ScriptGrammar.IVisitor<object>
    {
        public object Visit(ScriptGrammar.Script node) => node.Arg0.Content;

        public object Visit(ScriptGrammar.StmtsOne node) => new List<Stmt> { (Stmt)node.Arg0.Content };

        public object Visit(ScriptGrammar.StmtsCons node) => Cons((Stmt)node.Arg0.Content, (List<Stmt>)node.Arg1.Content);

        public object Visit(ScriptGrammar.ExprsOne node) => new List<Expr> { (Expr)node.Arg0.Content };

        public object Visit(ScriptGrammar.ExprsCons node) => Cons((Expr)node.Arg0.Content, (List<Expr>)node.Arg2.Content);

        public object Visit(ScriptGrammar.LetStmt node) =>
            new LetStmt(Line(node.Arg0), Text(node.Arg1), (Expr)node.Arg3.Content);

        public object Visit(ScriptGrammar.SetStmt node) =>
            new SetStmt(Line(node.Arg0), (Expr)node.Arg1.Content, (Expr)node.Arg3.Content);

        public object Visit(ScriptGrammar.ExpectStmt node) => new ExpectStmt(Line(node.Arg0), (Expr)node.Arg1.Content);

        public object Visit(ScriptGrammar.LogStmt node) => new LogStmt(Line(node.Arg0), (Expr)node.Arg1.Content);

        public object Visit(ScriptGrammar.StartLiveView node) => new StartLiveViewStmt(Line(node.Arg0), null);

        public object Visit(ScriptGrammar.StartLiveViewWith node) =>
            new StartLiveViewStmt(Line(node.Arg0), (Expr)node.Arg1.Content);

        public object Visit(ScriptGrammar.StopLiveView node) => new StopLiveViewStmt(Line(node.Arg0));

        public object Visit(ScriptGrammar.FramesStmt node) =>
            new FramesStmt(Line(node.Arg0), Duration(node.Arg1), null);

        public object Visit(ScriptGrammar.FramesMinStmt node) =>
            new FramesStmt(Line(node.Arg0), Duration(node.Arg1), (Expr)node.Arg3.Content);

        public object Visit(ScriptGrammar.ReleaseStmt node) => new ReleaseStmt(Line(node.Arg0));

        public object Visit(ScriptGrammar.WaitObjectStmt node) => new WaitObjectStmt(Line(node.Arg0), Duration(node.Arg1));

        public object Visit(ScriptGrammar.ConfirmStmt node) => new ConfirmStmt(Line(node.Arg0), Unquote(node.Arg1));

        public object Visit(ScriptGrammar.SleepStmt node) => new SleepStmt(Line(node.Arg0), Duration(node.Arg1));

        public object Visit(ScriptGrammar.ForStmt node) =>
            new ForStmt(Line(node.Arg0), Text(node.Arg1), (List<Expr>)node.Arg4.Content, (List<Stmt>)node.Arg7.Content);

        // Pass-throughs between precedence levels.
        public object Visit(ScriptGrammar.ExprCmp node) => node.Arg0.Content;
        public object Visit(ScriptGrammar.CmpOr node) => node.Arg0.Content;
        public object Visit(ScriptGrammar.OrAnd node) => node.Arg0.Content;
        public object Visit(ScriptGrammar.AndUnary node) => node.Arg0.Content;
        public object Visit(ScriptGrammar.UnaryPrimary node) => node.Arg0.Content;
        public object Visit(ScriptGrammar.ParenExpr node) => node.Arg1.Content;

        public object Visit(ScriptGrammar.CmpEq node) => Binary(BinaryOp.Eq, node.Arg0, node.Arg2);
        public object Visit(ScriptGrammar.CmpNe node) => Binary(BinaryOp.Ne, node.Arg0, node.Arg2);
        public object Visit(ScriptGrammar.CmpLt node) => Binary(BinaryOp.Lt, node.Arg0, node.Arg2);
        public object Visit(ScriptGrammar.CmpGt node) => Binary(BinaryOp.Gt, node.Arg0, node.Arg2);
        public object Visit(ScriptGrammar.CmpLe node) => Binary(BinaryOp.Le, node.Arg0, node.Arg2);
        public object Visit(ScriptGrammar.CmpGe node) => Binary(BinaryOp.Ge, node.Arg0, node.Arg2);
        public object Visit(ScriptGrammar.BitOr node) => Binary(BinaryOp.Or, node.Arg0, node.Arg2);
        public object Visit(ScriptGrammar.BitAnd node) => Binary(BinaryOp.And, node.Arg0, node.Arg2);

        public object Visit(ScriptGrammar.BitNot node) => new NotExpr(Line(node.Arg0), (Expr)node.Arg1.Content);

        public object Visit(ScriptGrammar.ReadExpr node) => new ReadExpr(Line(node.Arg0), (Expr)node.Arg1.Content);

        public object Visit(ScriptGrammar.NumberLit node)
        {
            var text = Text(node.Arg0);
            var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!uint.TryParse(
                    hex ? text.AsSpan(2) : text, hex ? NumberStyles.HexNumber : NumberStyles.None,
                    CultureInfo.InvariantCulture, out var value))
                throw new ScriptSyntaxException(
                    Line(node.Arg0), node.Arg0.Position.Column, $"'{text}' does not fit in 32 bits");
            return new NumberExpr(Line(node.Arg0), value);
        }

        public object Visit(ScriptGrammar.StringLit node) => new StringExpr(Line(node.Arg0), Unquote(node.Arg0));

        public object Visit(ScriptGrammar.NameRef node) => new NameExpr(Line(node.Arg0), Text(node.Arg0));

        public object Visit(ScriptGrammar.QualifiedRef node) => new NameExpr(Line(node.Arg0), Text(node.Arg0));

        public object Visit(ScriptGrammar.AskExpr node) => new AskExpr(Line(node.Arg0), Unquote(node.Arg1));

        private static BinaryExpr Binary(BinaryOp op, Item left, Item right)
        {
            var l = (Expr)left.Content;
            return new BinaryExpr(l.Line, op, l, (Expr)right.Content);
        }

        private static List<T> Cons<T>(T head, List<T> tail)
        {
            var list = new List<T>(tail.Count + 1) { head };
            list.AddRange(tail);
            return list;
        }

        private static int Line(Item token) => token.Position.Line;

        private static string Text(Item token) => (string)token.Content;

        private static string Unquote(Item token) => Text(token)[1..^1];

        private static TimeSpan Duration(Item token)
        {
            var text = Text(token);
            return text.EndsWith("ms", StringComparison.Ordinal)
                ? TimeSpan.FromMilliseconds(double.Parse(text.AsSpan(0, text.Length - 2), CultureInfo.InvariantCulture))
                : TimeSpan.FromSeconds(double.Parse(text.AsSpan(0, text.Length - 1), CultureInfo.InvariantCulture));
        }
    }
}
