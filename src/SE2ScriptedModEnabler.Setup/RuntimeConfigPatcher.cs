using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SE2ScriptedModEnabler.Setup;

public enum PatchOutcome
{
    /// <summary>Our path was appended to an existing DEV_PLUGINS value.</summary>
    Added,

    /// <summary>A DEV_PLUGINS key was created because the file had none.</summary>
    KeyInserted,

    /// <summary>A stale entry for our DLL pointed somewhere else and was corrected.</summary>
    Replaced,

    /// <summary>Our exact path was already there. Nothing written.</summary>
    AlreadyPresent,

    /// <summary>Our entry was spliced out.</summary>
    Removed,

    /// <summary>There was nothing of ours to remove.</summary>
    NotPresent,

    /// <summary>Valid JSON in a shape this tool will not edit. Left alone.</summary>
    Unsupported,

    /// <summary>Not parseable as the runtimeconfig it claims to be. Left alone.</summary>
    Invalid,
}

/// <param name="Content">The whole file as it should be written, or null if nothing changed.</param>
/// <param name="Segments">DEV_PLUGINS as the game will split it, after the change.</param>
public sealed record PatchResult(
    PatchOutcome Outcome,
    byte[]? Content,
    string Detail,
    IReadOnlyList<string> Segments)
{
    public bool Changed => Content is not null;
    public bool Ok => Outcome is not (PatchOutcome.Unsupported or PatchOutcome.Invalid);
}

/// <summary>
/// Adds and removes one entry in the game's <c>DEV_PLUGINS</c> runtime config property.
///
/// <para><b>This is a byte splice, never a round trip.</b> The file is located by
/// scanning with <see cref="Utf8JsonReader"/>, which reports the exact byte span of the
/// value, and only that span is rewritten. Every other byte — key order, two-space
/// indentation, CRLF line endings, the absent trailing newline — comes through
/// untouched. Re-emitting the document with <c>JsonNode</c> would be far less code and
/// would break two things that matter: the diff Steam has to reconcile on update grows
/// from one line to the whole file, and uninstall could no longer restore the original
/// bytes, only something equivalent to them.</para>
///
/// <para>Uninstall therefore removes our segment surgically rather than restoring the
/// backup. A backup is still taken, but between install and uninstall Keen may have
/// legitimately changed this file, and stamping an old copy over their change would
/// turn an uninstall into a downgrade.</para>
///
/// <para>Mutation works on the raw, still-escaped segment text so that untouched
/// segments are preserved byte for byte. That is only sound while the raw text splits
/// on <c>;</c> the same way the decoded text does, which is checked rather than
/// assumed — <c>\u003b</c> would divide the two, and the answer then is to refuse.</para>
/// </summary>
public static class RuntimeConfigPatcher
{
    public const string Key = "DEV_PLUGINS";

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>DEV_PLUGINS split the way PluginHost.LoadPlugins splits it.</summary>
    public static IReadOnlyList<string> ReadSegments(byte[] content)
    {
        if (!TryLocate(content, out var loc, out _) || loc.RawValue is null) return [];
        return Split(Unescape(loc.RawValue));
    }

    public static PatchResult Add(byte[] original, string pluginPath)
    {
        var problem = ValidatePluginPath(pluginPath);
        if (problem is not null)
            return new PatchResult(PatchOutcome.Unsupported, null, problem, ReadSegments(original));

        if (!TryLocate(original, out var loc, out var error, out var failure))
            return new PatchResult(failure, null, error, []);

        var escaped = Escape(pluginPath);
        var leaf = LeafName(pluginPath);

        if (loc.RawValue is null)
        {
            // No DEV_PLUGINS at all — a shape the shipping file has never had, but a
            // future build might, and inserting the key is well defined.
            var (at, text) = loc.InsertionFor($"\"{Key}\": \"{escaped}\",");
            var inserted = Splice(original, at, 0, text);
            return Verify(inserted, PatchOutcome.KeyInserted,
                $"created {Key} with {pluginPath}", [pluginPath]);
        }

        var raw = SplitRawChecked(loc.RawValue, out var mismatch);
        if (mismatch is not null)
            return new PatchResult(PatchOutcome.Unsupported, null, mismatch, ReadSegments(original));

        var ours = IndexOfLeaf(raw, leaf);

        if (ours >= 0 && Unescape(raw[ours]) == pluginPath)
            return new PatchResult(PatchOutcome.AlreadyPresent, null,
                $"{pluginPath} is already in {Key}", raw.ConvertAll(Unescape));

        string detail;
        PatchOutcome outcome;
        if (ours >= 0)
        {
            detail = $"replaced a stale {leaf} entry ({Unescape(raw[ours])}) with {pluginPath}";
            outcome = PatchOutcome.Replaced;
            raw[ours] = escaped;
        }
        else
        {
            detail = $"appended {pluginPath} to {Key}";
            outcome = PatchOutcome.Added;
            raw.Add(escaped);
        }

        var patched = Splice(original, loc.RawStart, loc.RawByteLength, string.Join(";", raw));
        return Verify(patched, outcome, detail, raw.ConvertAll(Unescape));
    }

    /// <summary>
    /// Remove every entry whose filename is <paramref name="dllFileName"/>, which also
    /// cleans up entries left by an install that lived somewhere else.
    ///
    /// <para>Only the value is edited; the key itself is never deleted, even if
    /// <see cref="Add"/> is what created it. As a pure function of the file this cannot
    /// tell an empty <c>DEV_PLUGINS</c> that Keen shipped from one it left behind, and
    /// the two are identical to the game — <c>LoadPlugins</c> splits with
    /// <c>RemoveEmptyEntries</c>. Guessing wrong would mean deleting a key we did not
    /// add, so it leaves the empty string.</para>
    /// </summary>
    public static PatchResult Remove(byte[] original, string dllFileName)
    {
        if (!TryLocate(original, out var loc, out var error, out var failure))
            return new PatchResult(failure, null, error, []);

        if (loc.RawValue is null)
            return new PatchResult(PatchOutcome.NotPresent, null, $"no {Key} key in this file", []);

        var raw = SplitRawChecked(loc.RawValue, out var mismatch);
        if (mismatch is not null)
            return new PatchResult(PatchOutcome.Unsupported, null, mismatch, ReadSegments(original));

        var kept = new List<string>();
        var dropped = new List<string>();
        foreach (var segment in raw)
        {
            var decoded = Unescape(segment);
            if (segment.Length > 0 && string.Equals(LeafName(decoded), dllFileName, StringComparison.OrdinalIgnoreCase))
                dropped.Add(decoded);
            else
                kept.Add(segment);
        }

        if (dropped.Count == 0)
            return new PatchResult(PatchOutcome.NotPresent, null,
                $"no {dllFileName} entry in {Key}", raw.ConvertAll(Unescape));

        var patched = Splice(original, loc.RawStart, loc.RawByteLength, string.Join(";", kept));
        return Verify(patched, PatchOutcome.Removed,
            $"removed {string.Join(", ", dropped)} from {Key}", kept.ConvertAll(Unescape));
    }

    /// <summary>Null if the path is one the game could actually load.</summary>
    public static string? ValidatePluginPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "plugin path is empty";

        // PluginHost.LoadPlugins splits the value on ';', so a path containing one
        // would silently become two paths, neither of which exists.
        if (path.Contains(';'))
            return $"plugin path contains ';', which {Key} uses as its separator: {path}";

        foreach (var c in path)
            if (c < ' ')
                return "plugin path contains a control character";

        // A /mnt/c/... path is the failure with no symptom: Assembly.LoadFrom finds
        // nothing, PluginHost logs nothing, and the game starts up looking normal.
        if (!HostPaths.LooksLikeWindowsPath(path))
            return $"plugin path must be a Windows path such as C:\\..., got: {path}";

        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return $"plugin path must name a .dll, got: {path}";

        return null;
    }

    public static string LeafName(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    // ---- locating the value ------------------------------------------------------

    /// <param name="RawValue">The still-escaped text between the quotes, or null if absent.</param>
    /// <param name="RawStart">Byte index of the first byte inside the quotes.</param>
    /// <param name="RawByteLength">Length in bytes, which is not the length in chars.</param>
    private readonly record struct Location(
        string? RawValue,
        int RawStart,
        int RawByteLength,
        int InsertAt,
        string InsertWhitespace,
        bool ConfigPropertiesEmpty)
    {
        /// <summary>Where to put a brand new key, and the text to put there.</summary>
        public (int At, string Text) InsertionFor(string keyValueText) =>
            ConfigPropertiesEmpty
                ? (InsertAt, InsertWhitespace + keyValueText.TrimEnd(',') + InsertWhitespace)
                : (InsertAt, keyValueText + InsertWhitespace);
    }

    private static bool TryLocate(byte[] content, out Location location, out string error) =>
        TryLocate(content, out location, out error, out _);

    private static bool TryLocate(byte[] content, out Location location, out string error,
        out PatchOutcome failure)
    {
        location = default;
        failure = PatchOutcome.Unsupported;

        // Everything below is in byte offsets into `content`. Utf8JsonReader reports
        // byte offsets, the splice is a byte operation, and a single non-ASCII
        // character anywhere earlier in the file would desynchronise char indices
        // from byte ones — silently, and only on the machines that have one.
        var bom = HasBom(content) ? Bom.Length : 0;

        var reader = new Utf8JsonReader(content.AsSpan(bom),
            new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var names = new string?[8];
        var depth = 0;
        var inConfig = false;
        var sawKey = false;
        int brace = -1, firstProperty = -1, closeBrace = -1, configKey = -1;
        int valueStart = -1, valueEnd = -1;

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        if (depth >= 1 && depth <= names.Length) names[depth - 1] = reader.GetString();
                        if (depth == 2 && names[0] == "runtimeOptions" && names[1] == "configProperties")
                            configKey = (int)reader.TokenStartIndex + bom;
                        if (inConfig && depth == 3)
                        {
                            if (firstProperty < 0) firstProperty = (int)reader.TokenStartIndex + bom;
                            if (names[2] == Key) sawKey = true;
                        }
                        break;

                    case JsonTokenType.StartObject:
                        depth++;
                        if (depth == 3 && names[0] == "runtimeOptions" && names[1] == "configProperties")
                        {
                            inConfig = true;
                            brace = (int)reader.TokenStartIndex + bom;
                        }
                        break;

                    case JsonTokenType.StartArray:
                        depth++;
                        break;

                    case JsonTokenType.EndObject:
                        if (depth == 3 && inConfig)
                        {
                            closeBrace = (int)reader.TokenStartIndex + bom;
                            inConfig = false;
                        }
                        depth--;
                        break;

                    case JsonTokenType.EndArray:
                        depth--;
                        break;

                    case JsonTokenType.String:
                        if (inConfig && depth == 3 && names[2] == Key)
                        {
                            valueStart = (int)reader.TokenStartIndex + bom;
                            valueEnd = (int)reader.BytesConsumed + bom;
                        }
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            error = $"not valid JSON: {ex.Message}";
            failure = PatchOutcome.Invalid;
            return false;
        }

        if (brace < 0)
        {
            error = "not a recognisable runtimeconfig.json: no runtimeOptions.configProperties object";
            return false;
        }

        if (sawKey && valueStart < 0)
        {
            error = $"{Key} is present but is not a string; refusing to edit it";
            return false;
        }

        if (valueStart >= 0)
        {
            // TokenStartIndex is the opening quote and BytesConsumed is one past the
            // closing quote, so the raw, still-escaped value is what lies between.
            var rawStart = valueStart + 1;
            var rawLength = valueEnd - valueStart - 2;
            var raw = Encoding.UTF8.GetString(content, rawStart, rawLength);
            location = new Location(raw, rawStart, rawLength, rawStart, "", false);
            error = "";
            return true;
        }

        var empty = firstProperty < 0;
        string whitespace;
        int insertAt;

        if (!empty)
        {
            // Reuse the exact newline and indent the file already uses between the
            // brace and its first property.
            whitespace = Encoding.UTF8.GetString(content, brace + 1, firstProperty - brace - 1);
            insertAt = firstProperty;
        }
        else
        {
            if (closeBrace < 0 || configKey < 0)
            {
                error = "configProperties is empty and its layout could not be read";
                return false;
            }
            var newline = IndexOfCrLf(content) >= 0 ? "\r\n" : "\n";
            var column = configKey - (Array.LastIndexOf(content, (byte)'\n', configKey) + 1);
            whitespace = newline + new string(' ', Math.Max(column, 0) + 2);
            insertAt = brace + 1;
        }

        location = new Location(null, -1, 0, insertAt, whitespace, empty);
        error = "";
        return true;
    }

    // ---- byte and escape plumbing ------------------------------------------------

    private static int IndexOfCrLf(byte[] content)
    {
        for (var i = 0; i + 1 < content.Length; i++)
            if (content[i] == (byte)'\r' && content[i + 1] == (byte)'\n') return i;
        return -1;
    }

    private static bool HasBom(byte[] content) =>
        content.Length >= 3 && content[0] == Bom[0] && content[1] == Bom[1] && content[2] == Bom[2];

    private static byte[] Splice(byte[] original, int start, int length, string replacement)
    {
        var insert = Encoding.UTF8.GetBytes(replacement);
        var result = new byte[original.Length - length + insert.Length];
        Buffer.BlockCopy(original, 0, result, 0, start);
        Buffer.BlockCopy(insert, 0, result, start, insert.Length);
        Buffer.BlockCopy(original, start + length, result, start + insert.Length,
            original.Length - start - length);
        return result;
    }

    /// <summary>
    /// Last line of defence: never hand back bytes that are not valid JSON, or that do
    /// not read back as the segments we meant to write.
    /// </summary>
    private static PatchResult Verify(byte[] patched, PatchOutcome outcome, string detail,
        IReadOnlyList<string> expected)
    {
        try
        {
            // JsonDocument.Parse treats a UTF-8 BOM as a syntax error rather than
            // skipping it, so give it the same view of the bytes the reader had.
            var skip = HasBom(patched) ? Bom.Length : 0;
            using var _ = JsonDocument.Parse(patched.AsMemory(skip));
        }
        catch (JsonException ex)
        {
            return new PatchResult(PatchOutcome.Invalid, null,
                $"the edit would have produced invalid JSON ({ex.Message}); nothing written", []);
        }

        var actual = ReadSegments(patched);
        if (actual.Count != expected.Count)
            return new PatchResult(PatchOutcome.Invalid, null,
                $"the edit did not read back as expected ({actual.Count} entries, wanted {expected.Count}); nothing written",
                actual);

        for (var i = 0; i < actual.Count; i++)
            if (actual[i] != expected[i])
                return new PatchResult(PatchOutcome.Invalid, null,
                    $"the edit did not read back as expected at entry {i}; nothing written", actual);

        return new PatchResult(outcome, patched, detail, actual);
    }

    private static List<string> Split(string value) =>
        value.Length == 0 ? [] : [.. value.Split(';')];

    /// <summary>
    /// Split the raw value, refusing when raw and decoded do not agree on where the
    /// separators are — an escaped <c>\u003b</c> would be a separator to the game but
    /// not to a raw split, and editing under that mismatch would corrupt the value.
    /// </summary>
    private static List<string> SplitRawChecked(string raw, out string? mismatch)
    {
        var rawParts = Split(raw);
        var decodedParts = Split(Unescape(raw));

        mismatch = rawParts.Count == decodedParts.Count
            ? null
            : $"{Key} uses escaped semicolons; refusing to edit it automatically";

        return rawParts;
    }

    private static string Escape(string value)
    {
        var json = JsonSerializer.Serialize(value);
        return json[1..^1];
    }

    private static string Unescape(string raw)
    {
        try { return JsonSerializer.Deserialize<string>("\"" + raw + "\"") ?? ""; }
        catch (JsonException) { return raw; }
    }

    private static int IndexOfLeaf(List<string> rawSegments, string leaf)
    {
        for (var i = 0; i < rawSegments.Count; i++)
        {
            if (rawSegments[i].Length == 0) continue;
            if (string.Equals(LeafName(Unescape(rawSegments[i])), leaf, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
