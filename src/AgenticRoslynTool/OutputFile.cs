namespace AgenticRoslynTool;

/// <summary>
/// The full text of a planned output file, ready to be written to disk. Verification
/// runs against this in-memory representation before any write happens.
/// </summary>
/// <param name="Path">Absolute path where the file will be written.</param>
/// <param name="Type">The top-level type that owns this output.</param>
/// <param name="Text">
/// The final serialized text, including the injected <c>--require-header</c> when one
/// was supplied. This is what is written to disk and what the header-prefix check in
/// <c>VerifyOutputs</c> is applied to.
/// </param>
/// <param name="BodyText">
/// The output text before any <c>--require-header</c> injection. Line conservation
/// counting uses this so that an injected header cannot mask a dropped source line.
/// </param>
internal sealed record OutputFile(string Path, TopLevelType Type, string Text, string BodyText);
