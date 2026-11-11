namespace FC.SDK.Scripting;

// The tree the interpreter walks. Built by ScriptParser's visitor at reduction time, so the
// generated ScriptGrammar records (which only carry parser items) never leave the parser.

internal abstract record Expr(int Line);

internal sealed record NumberExpr(int Line, uint Value) : Expr(Line);

internal sealed record StringExpr(int Line, string Value) : Expr(Line);

/// <summary>A variable, a built-in constant, or an <c>Enum.Member</c> name.</summary>
internal sealed record NameExpr(int Line, string Name) : Expr(Line);

/// <summary><c>read code</c>: the property's value on the camera now, past any cache.</summary>
internal sealed record ReadExpr(int Line, Expr Code) : Expr(Line);

/// <summary><c>ask "question"</c>: the operator's yes (1) or no (0).</summary>
internal sealed record AskExpr(int Line, string Prompt) : Expr(Line);

internal sealed record NotExpr(int Line, Expr Operand) : Expr(Line);

internal enum BinaryOp { Eq, Ne, Lt, Gt, Le, Ge, Or, And }

internal sealed record BinaryExpr(int Line, BinaryOp Op, Expr Left, Expr Right) : Expr(Line);

internal abstract record Stmt(int Line);

internal sealed record LetStmt(int Line, string Name, Expr Value) : Stmt(Line);

internal sealed record SetStmt(int Line, Expr Code, Expr Value) : Stmt(Line);

internal sealed record ExpectStmt(int Line, Expr Condition) : Stmt(Line);

internal sealed record LogStmt(int Line, Expr Value) : Stmt(Line);

internal sealed record StartLiveViewStmt(int Line, Expr? Output) : Stmt(Line);

internal sealed record StopLiveViewStmt(int Line) : Stmt(Line);

internal sealed record FramesStmt(int Line, TimeSpan Window, Expr? Minimum) : Stmt(Line);

internal sealed record ReleaseStmt(int Line) : Stmt(Line);

internal sealed record WaitObjectStmt(int Line, TimeSpan Window) : Stmt(Line);

internal sealed record ConfirmStmt(int Line, string Prompt) : Stmt(Line);

internal sealed record SleepStmt(int Line, TimeSpan Duration) : Stmt(Line);

internal sealed record ForStmt(int Line, string Variable, IReadOnlyList<Expr> Items, IReadOnlyList<Stmt> Body)
    : Stmt(Line);

/// <summary>A parsed script: its statements, and its source lines for the transcript.</summary>
public sealed class CameraScript
{
    internal CameraScript(IReadOnlyList<Stmt> statements, string[] lines)
    {
        Statements = statements;
        Lines = lines;
    }

    internal IReadOnlyList<Stmt> Statements { get; }

    internal string[] Lines { get; }

    /// <summary>The trimmed source text of a 1-based line, for reporting a step.</summary>
    internal string LineText(int line) =>
        line >= 1 && line <= Lines.Length ? Lines[line - 1].Trim() : "";

    /// <summary>Parses script source. Throws <see cref="ScriptSyntaxException"/> on the first error.</summary>
    public static CameraScript Parse(string source) => ScriptParser.Parse(source);
}

/// <summary>A script that does not parse: where, and what was expected there.</summary>
public sealed class ScriptSyntaxException(int line, int column, string message)
    : Exception($"line {line}, column {column}: {message}")
{
    /// <summary>1-based line of the offending token.</summary>
    public int Line { get; } = line;

    /// <summary>1-based column of the offending token.</summary>
    public int Column { get; } = column;
}
