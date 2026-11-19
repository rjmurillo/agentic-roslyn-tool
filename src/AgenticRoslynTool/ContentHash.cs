using System.Security.Cryptography;

namespace AgenticRoslynTool;

/// <summary>
/// The content hash a manifest row carries for its input: <c>sha256:</c> followed by 64
/// lowercase hexadecimal digits, computed over the raw bytes of the file as the run read
/// them. Raw bytes, not decoded text, so a change of encoding, byte order mark, or newline
/// style counts as a change. The plan was reviewed against those bytes, and the content
/// phase re-emits every one of those properties.
/// </summary>
/// <remarks>
/// The format is load bearing in the same way the manifest column names are. The content
/// phase compares the planned value against a fresh one with an ordinal comparison, so the
/// written form must be canonical: one prefix, one case, one length.
/// </remarks>
internal static class ContentHash
{
    /// <summary>
    /// Opens every refusal this type produces. A caller can match on it to tell a stale plan
    /// from any other skip.
    /// </summary>
    internal const string StalePlanPrefix = "stale plan: ";

    private const string AlgorithmPrefix = "sha256:";

    private const int HexLength = 64;

    /// <summary>Hashes the raw bytes of an input file into the manifest form.</summary>
    /// <param name="bytes">The full byte contents of the file, exactly as read from disk.</param>
    /// <returns>The hash in <c>sha256:</c> plus lowercase hexadecimal form.</returns>
    public static string Compute(ReadOnlySpan<byte> bytes) => AlgorithmPrefix + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Returns true when <paramref name="value"/> is a hash this version could have written.
    /// Anything else, including an empty value, a truncated one, uppercase digits, or another
    /// algorithm's prefix, is not comparable and must refuse rather than match by accident.
    /// </summary>
    public static bool IsWellFormed(string? value)
    {
        return value is not null
            && value.Length == AlgorithmPrefix.Length + HexLength
            && value.StartsWith(AlgorithmPrefix, StringComparison.Ordinal)
            && value.AsSpan(AlgorithmPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    /// <summary>
    /// Compares the hash a plan recorded against the hash of the bytes a later phase read.
    /// </summary>
    /// <param name="planned">The value from the manifest row. May be missing or malformed.</param>
    /// <param name="actual">The hash of the bytes just read, from <see cref="Compute"/>.</param>
    /// <returns>Null when the two match, otherwise the reason to record on a <c>skipped</c> row.</returns>
    public static string? DescribeMismatch(string? planned, string actual)
    {
        if (string.IsNullOrEmpty(planned))
        {
            return StalePlanPrefix + "the manifest row carries no contentHash; re-run --phase plan";
        }

        if (!IsWellFormed(planned))
        {
            return StalePlanPrefix + $"the manifest row carries a malformed contentHash '{Truncate(planned)}'; re-run --phase plan";
        }

        return string.Equals(planned, actual, StringComparison.Ordinal)
            ? null
            : StalePlanPrefix + $"input changed after the plan was written (planned {planned}, found {actual}); re-run --phase plan";
    }

    // A hand-edited cell can hold anything, including a whole pasted file. The reason is one
    // CSV field and one JSON string, so it quotes enough to recognize and no more.
    private static string Truncate(string value) => value.Length <= 80 ? value : value[..80] + "...";
}
