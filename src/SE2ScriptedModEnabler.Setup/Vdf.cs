using System;
using System.Collections.Generic;
using System.Text;

namespace SE2ScriptedModEnabler.Setup;

/// <summary>
/// A node in a Valve KeyValues document: either a leaf with a <see cref="Value"/> or a
/// container with <see cref="Children"/>.
/// </summary>
public sealed class VdfNode(string name)
{
    public string Name { get; } = name;
    public string? Value { get; internal set; }
    public List<VdfNode> Children { get; } = [];

    /// <summary>First child with this name, case-insensitively. Keys repeat in VDF.</summary>
    public VdfNode? this[string key]
    {
        get
        {
            foreach (var child in Children)
                if (string.Equals(child.Name, key, StringComparison.OrdinalIgnoreCase))
                    return child;
            return null;
        }
    }

    public string? ValueOf(string key) => this[key]?.Value;
}

/// <summary>
/// A reader for Valve's KeyValues text format — <c>libraryfolders.vdf</c> and
/// <c>appmanifest_*.acf</c>.
///
/// Deliberately permissive: the goal is to find one or two keys in a file Valve owns
/// and may extend, so unknown structure is carried rather than rejected. It is not a
/// writer, because nothing here ever writes a Steam file.
/// </summary>
public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode("");
        var stack = new Stack<VdfNode>();
        stack.Push(root);

        var i = 0;
        string? pendingKey = null;

        while (true)
        {
            SkipTrivia(text, ref i);
            if (i >= text.Length) break;

            var c = text[i];

            if (c == '}')
            {
                i++;
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
                continue;
            }

            if (c == '{')
            {
                i++;
                // An unnamed block cannot be addressed later, but skipping it would
                // desynchronise the brace depth, so it is kept anonymously.
                var node = new VdfNode(pendingKey ?? "");
                stack.Peek().Children.Add(node);
                stack.Push(node);
                pendingKey = null;
                continue;
            }

            var token = ReadToken(text, ref i);

            if (pendingKey is null)
            {
                pendingKey = token;
                continue;
            }

            stack.Peek().Children.Add(new VdfNode(pendingKey) { Value = token });
            pendingKey = null;
        }

        return root;
    }

    private static void SkipTrivia(string text, ref int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] is not ('\n' or '\r')) i++;
                continue;
            }
            return;
        }
    }

    private static string ReadToken(string text, ref int i)
    {
        if (text[i] != '"')
        {
            // Unquoted token: runs to the next whitespace or brace.
            var begin = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}')) i++;
            return text[begin..i];
        }

        i++;   // opening quote
        var sb = new StringBuilder();
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                sb.Append(text[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    var other => other,
                });
            }
            else
            {
                sb.Append(text[i]);
            }
            i++;
        }
        if (i < text.Length) i++;   // closing quote
        return sb.ToString();
    }
}
