using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgenticRoslynTool;

/// <summary>
/// Runs the symbol retry parse in a child process, so nothing that parse does can end the
/// run. Owns the process lifecycle; <see cref="SourceParsing"/> owns the parse itself.
/// </summary>
/// <remarks>
/// <para>
/// The retry parses code the first parse skipped. Roslyn's parser and lexer recurse, so a
/// deep nest there overflows the stack, which no catch can stop. Review found such nests in
/// parentheses, generic arguments, interpolated strings, and <c>is not</c> chains, and each
/// in-process guard caught one shape and missed the next. A stack overflow, a stall, or any
/// crash in the child ends only the child. Its file is skipped unjudged, and the run goes on.
/// </para>
/// <para>
/// The tool starts itself with the hidden verb <see cref="Verb"/>. The symbols and the file
/// text go over standard input, because 256 long symbol names can outgrow a command line.
/// Both output streams are read while the child runs, so a full pipe cannot stall it. A child
/// that runs past the limit is killed with its children. Decision 33.
/// </para>
/// </remarks>
internal static class SymbolRetry
{
    /// <summary>The hidden verb the child runs. It is not in the help text.</summary>
    internal const string Verb = "__symbol-retry";

    /// <summary>How long the child may run before it is killed and its file skipped.</summary>
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(30);

    // Bounds every wait on a child that already exited or was killed.
    private static readonly TimeSpan StreamDrainLimit = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>What the child found.</summary>
    internal enum Outcome
    {
        /// <summary>The file parses with its symbols defined.</summary>
        Parses,

        /// <summary>The file still has errors with its symbols defined.</summary>
        Broken,

        /// <summary>The child crashed, ran past its limit, or could not start.</summary>
        Unjudged,
    }

    /// <summary>
    /// Parses <paramref name="text"/> with <paramref name="symbols"/> defined, in a child
    /// process that may run for <paramref name="timeLimit"/>, or <see cref="TimeLimit"/>.
    /// </summary>
    internal static Outcome Run(string text, IReadOnlyList<string> symbols, TimeSpan? timeLimit = null)
    {
        if (StartInfo() is not { } start)
        {
            return Outcome.Unjudged;
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return Outcome.Unjudged;
            }

            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            try
            {
                using var input = process.StandardInput;
                input.Write(symbols.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                foreach (var symbol in symbols)
                {
                    input.Write(symbol + "\n");
                }

                input.Write(text);
            }
            catch (IOException)
            {
                // The child died before it read everything. Its exit code says why.
            }

            if (!process.WaitForExit(timeLimit ?? TimeLimit))
            {
                Kill(process);
                return Outcome.Unjudged;
            }

            // Nothing reads what the child printed. A crash trace would only repeat the stack.
            Drain(error);
            Drain(output);
            return process.ExitCode switch
            {
                0 => Outcome.Parses,
                1 => Outcome.Broken,
                _ => Outcome.Unjudged,
            };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return Outcome.Unjudged;
        }
    }

    /// <summary>
    /// The child's side: reads the symbols and the text from <paramref name="input"/> and
    /// returns 0 when the text parses with them defined, or 1 when it does not.
    /// </summary>
    internal static int RunChild(TextReader input)
    {
        var count = int.Parse(input.ReadLine() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var symbols = new string[count];
        for (var i = 0; i < count; i++)
        {
            symbols[i] = input.ReadLine() ?? string.Empty;
        }

        return SourceParsing.ParsesWithSymbols(input.ReadToEnd(), symbols) ? 0 : 1;
    }

    // Starts the same build that is running. Under "dotnet AgenticRoslynTool.dll", and under
    // a test host, the process is the dotnet host, so the tool's own assembly goes first.
    // Installed as a tool, the process is the tool's own launcher.
    private static ProcessStartInfo? StartInfo()
    {
        var host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        var start = new ProcessStartInfo(host)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(SymbolRetry).Assembly.Location);
        }

        start.ArgumentList.Add(Verb);
        return start;
    }

    // Kill throws when the child exits on its own first, or when part of the tree cannot be
    // ended. The file is skipped either way.
    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
        }

        process.WaitForExit(StreamDrainLimit);
    }

    private static void Drain(Task<string> stream)
    {
        try
        {
            stream.Wait(StreamDrainLimit);
        }
        catch (AggregateException)
        {
        }
    }
}
