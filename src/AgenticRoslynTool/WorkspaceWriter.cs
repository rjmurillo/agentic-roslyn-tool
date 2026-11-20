namespace AgenticRoslynTool;

/// <summary>
/// The single place this tool touches the working tree. Every rename runs through
/// <c>git mv</c> so history follows the file, and every write preserves the source
/// encoding and BOM so a split does not become a whole-file diff.
/// </summary>
/// <remarks>
/// Split out of <see cref="FileSplitter"/> because it was the only reason that class
/// referenced <c>System.Diagnostics</c> and <c>File.WriteAllBytes</c>. Planning, building,
/// and verifying an output are now reachable without a real repository behind them.
/// No interface: there is one implementation, and the tests exercise a real temporary
/// git repository, which is a more truthful check than a fake would be.
/// </remarks>
internal sealed class WorkspaceWriter
{
    /// <summary>How long one <c>git mv</c> may run before the tool kills it.</summary>
    internal static readonly TimeSpan DefaultGitTimeout = TimeSpan.FromMinutes(2);

    private readonly string _repoRoot;
    private readonly TimeSpan _gitTimeout;

    /// <summary>Creates a writer bound to the repository <c>git mv</c> runs inside.</summary>
    internal WorkspaceWriter(string repoRoot)
        : this(repoRoot, DefaultGitTimeout)
    {
    }

    /// <summary>Creates a writer with a custom <c>git mv</c> time limit. Tests use a short one.</summary>
    internal WorkspaceWriter(string repoRoot, TimeSpan gitTimeout)
    {
        _repoRoot = repoRoot;
        _gitTimeout = gitTimeout;
    }

    /// <summary>
    /// Runs the git-rename step in isolation and verifies that the file content is
    /// unchanged after the move. If content changed for any reason, the move is
    /// undone before reporting failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <c>git mv</c> fails or altered the file's byte contents.</exception>
    /// <exception cref="TimeoutException">Thrown when <c>git mv</c> does not exit within the time limit.</exception>
    internal void ApplyRenameOnly(string originalPath, byte[] originalBytes, string keptPath)
    {
        RunGitMove(originalPath, keptPath);
        var movedBytes = File.ReadAllBytes(keptPath);
        if (!originalBytes.SequenceEqual(movedBytes))
        {
            var changed = $"git mv changed file content for {originalPath}";
            try
            {
                RunGitMove(keptPath, originalPath);
            }
            catch (Exception undo)
            {
                // Keep the first cause. A failed move back must not hide that git changed the file.
                throw new InvalidOperationException($"{changed}, and moving it back failed: {undo.Message}", undo);
            }

            throw new InvalidOperationException(changed);
        }
    }

    /// <summary>
    /// Writes every planned output file, using the encoding, BOM state, newline
    /// style, and final-newline convention captured from the original source.
    /// On any exception during writing, deletes the newly created sibling files and
    /// restores the original file from the bytes read at the start of processing so
    /// the working tree is left in its pre-run state.
    /// </summary>
    /// <remarks>
    /// This method never moves a file, so there is nothing to undo on that axis. Renames
    /// are their own phase (<see cref="ApplyRenameOnly"/>), and
    /// <see cref="SplitPlanner.GetReadPath"/> refuses to run the content phase for a
    /// renamed row unless the rename already landed, in which case the caller passes the
    /// kept path here and the restore below writes to it. An earlier <c>moved</c> flag
    /// and <c>git mv</c> rollback branch stood here permanently false; it was removed
    /// rather than wired, because wiring it would mean giving this method a rename
    /// responsibility the phase split deliberately took away.
    /// </remarks>
    internal void WriteOutputs(string originalPath, byte[] originalBytes, EncodedSource source, SplitPlan plan, IReadOnlyList<OutputFile> outputs)
    {
        var created = new List<string>();
        try
        {
            foreach (var output in outputs)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(output.Path, originalPath) && !StringComparer.OrdinalIgnoreCase.Equals(output.Path, plan.KeptPath))
                {
                    created.Add(output.Path);
                }

                WriteEncoded(output.Path, output.Text, source);
            }
        }
        catch
        {
            foreach (var path in created.Where(File.Exists))
            {
                File.Delete(path);
            }

            File.WriteAllBytes(originalPath, originalBytes);
            throw;
        }
    }

    /// <summary>
    /// Runs <c>git mv</c> from the configured repository root. Each path is its own
    /// argument, after a <c>--</c> so no path can be read as an option.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when git cannot be started or exits with a non-zero code.</exception>
    /// <exception cref="TimeoutException">Thrown when git does not exit within the time limit and is killed.</exception>
    internal void RunGitMove(string source, string target) =>
        GitCommand.Run(_repoRoot, _gitTimeout, "mv", "--", source, target);

    /// <summary>
    /// Writes the text to disk, preserving the source encoding and BOM. When the
    /// source had no BOM, the output has no BOM either; this is the point where the
    /// tool avoids gratuitous whole-file diffs on re-serialization.
    /// </summary>
    internal static void WriteEncoded(string path, string text, EncodedSource source)
    {
        var body = source.Encoding.GetBytes(text);
        if (!source.EmitPreamble)
        {
            File.WriteAllBytes(path, body);
            return;
        }

        var preamble = source.Encoding.GetPreamble();
        var bytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
        File.WriteAllBytes(path, bytes);
    }
}
