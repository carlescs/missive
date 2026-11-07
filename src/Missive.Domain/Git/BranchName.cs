using System.Diagnostics.CodeAnalysis;

namespace Missive.Domain.Git;

/// <summary>
/// A name git accepts for a new branch (the rules of <c>git check-ref-format --branch</c>), and
/// that can't be mistaken for a command-line option.
/// </summary>
public sealed record BranchName
{
    private BranchName(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string? text, [NotNullWhen(true)] out BranchName? name, [NotNullWhen(false)] out string? error)
    {
        name = null;
        var trimmed = text?.Trim();
        error = Validate(trimmed);
        if (error is null)
        {
            name = new BranchName(trimmed!);
        }

        return name is not null;
    }

    private static string? Validate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "The branch needs a name.";
        }

        if (text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
        {
            return "Branch names cannot contain spaces or any of ~ ^ : ? * [ \\";
        }

        if (text == "@" || text == "HEAD")
        {
            return $"'{text}' cannot be used as a branch name.";
        }

        if (text.StartsWith('-') || text.StartsWith('/') || text.StartsWith('.'))
        {
            return "Branch names cannot start with -, / or .";
        }

        if (text.EndsWith('/') || text.EndsWith('.') || text.EndsWith(".lock", StringComparison.Ordinal))
        {
            return "Branch names cannot end with /, . or .lock.";
        }

        if (text.Contains("..", StringComparison.Ordinal)
            || text.Contains("//", StringComparison.Ordinal)
            || text.Contains("/.", StringComparison.Ordinal)
            || text.Contains("@{", StringComparison.Ordinal)
            || text.Contains(".lock/", StringComparison.Ordinal))
        {
            return "Branch names cannot contain .., //, /., @{ or .lock/.";
        }

        return null;
    }

    public override string ToString() => Value;
}
