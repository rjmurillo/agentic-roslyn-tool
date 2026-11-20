using System.Diagnostics;

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

    // Bounds every wait on a git that already exited or was killed.
    private static readonly TimeSpan StreamDrainLimit = TimeSpan.FromSeconds(5);

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
    /// <exception cref="InvalidOperationException">Thrown when <c>git mv</c> altered the file's byte contents.</exception>
    internal void ApplyRenameOnly(string originalPath, byte[] originalBytes, string keptPath)
    {
        RunGitMove(originalPath, keptPath);
        var movedBytes = File.ReadAllBytes(keptPath);
        if (!originalBytes.SequenceEqual(movedBytes))
        {
            RunGitMove(keptPath, originalPath);
            throw new InvalidOperationException($"git mv changed file content for {originalPath}");
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
    /// Runs <c>git mv</c> from the configured repository root and throws with stderr and stdout attached on failure.
    /// </summary>
    /// <remarks>
    /// Each path is its own argument, so no quote or backslash in a file name can be read as
    /// argument syntax. The <c>--</c> keeps a path from being read as an option. Both output
    /// streams are drained while git runs, so a full pipe cannot stall it. A git that runs
    /// past the time limit is killed with its children, and every wait after the kill is
    /// bounded too, so a stuck descendant holding a pipe cannot hang the run.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the git process cannot be started or exits with a non-zero code.</exception>
    /// <exception cref="TimeoutException">Thrown when git does not exit within the time limit and is killed.</exception>
    internal void RunGitMove(string source, string target)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repoRoot,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("mv");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add(target);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start git mv");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(_gitTimeout))
        {
            Kill(process);
            throw new TimeoutException(
                $"git mv did not exit within {_gitTimeout.TotalSeconds} seconds and was killed. " +
                "A killed git can leave an index.lock file in the repository's git directory. " +
                "Remove it before the next run.");
        }

        if (process.ExitCode != 0)
        {
            try
            {
                Task.WaitAll([error, output], StreamDrainLimit);
            }
            catch (AggregateException)
            {
                // A failed read leaves that stream out of the message. The exit code still fails the row.
            }

            throw new InvalidOperationException("git mv failed: " + Drained(error) + Drained(output));
        }
    }

    // Kill can throw when the process exits on its own between the timed wait and the
    // kill. That race still ends in a dead process, so the timeout message stands.
    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        process.WaitForExit(StreamDrainLimit);
    }

    private static string Drained(Task<string> stream) => stream.IsCompletedSuccessfully ? stream.Result : string.Empty;

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
