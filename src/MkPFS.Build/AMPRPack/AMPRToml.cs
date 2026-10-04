using System.Text;
using MkPFS.Core.AMPR;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Serialization;
using Tomlyn.Syntax;

namespace MkPFS.Build.AMPRPack;

/// <summary>
/// TOML 1.0 reader for pack configurations. Syntax and semantic validation (duplicate keys and tables, dotted
/// keys redefined by headers) runs first because the table converter alone is lenient; Python <c>tomllib</c>
/// rejects the same documents, with different message text.
/// </summary>
internal static class AMPRToml
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Read a TOML file (UTF-8, like <c>tomllib.load</c>).</summary>
    /// <param name="path">File path.</param>
    /// <returns>Root table.</returns>
    /// <exception cref="AMPRPackException">The file is not valid UTF-8 or not valid TOML.</exception>
    public static TomlTable Load(string path)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException exc)
        {
            throw new AMPRPackException($"{path} is not valid UTF-8", exc);
        }

        return Parse(text, path);
    }

    /// <summary>Parse TOML text.</summary>
    /// <param name="text">Document.</param>
    /// <param name="sourceName">Name used in diagnostics.</param>
    /// <returns>Root table.</returns>
    /// <exception cref="AMPRPackException">The text is not valid TOML.</exception>
    public static TomlTable Parse(string text, string sourceName)
    {
        DocumentSyntax document = SyntaxParser.Parse(text, sourceName, true);
        if (document.HasErrors)
        {
            DiagnosticMessage first = document.Diagnostics.First(d => d.Kind == DiagnosticMessageKind.Error);
            throw new AMPRPackException(
                $"{first.Message} (at line {first.Span.Start.Line + 1}, column {first.Span.Start.Column + 1})");
        }

        try
        {
            return TomlSerializer.Deserialize<TomlTable>(text, AMPRTomlContext.Default)
                ?? throw new AMPRPackException("configuration root must be a TOML table");
        }
        catch (TomlException exc)
        {
            throw new AMPRPackException(exc.Message, exc);
        }
    }
}

/// <summary>Source-generated Tomlyn metadata (Native AOT).</summary>
[TomlSerializable(typeof(TomlTable))]
internal sealed partial class AMPRTomlContext : TomlSerializerContext
{
}
