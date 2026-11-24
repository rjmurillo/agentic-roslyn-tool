using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
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

            // The write runs on its own task, inside the time limit. A child that hangs before
            // it reads would otherwise block this write on a full pipe with no bound.
            var write = Task.Run(() => WriteInput(process.StandardInput, symbols, text));
            if (!process.WaitForExit(timeLimit ?? TimeLimit))
            {
                Kill(process);
                return Outcome.Unjudged;
            }

            // Nothing reads what the child printed. A crash trace would only repeat the stack.
            Drain(write);
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
        // Exit 2 on input the parent never sends, so it reads as unjudged, never as a result.
        if (!int.TryParse(input.ReadLine(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count))
        {
            return 2;
        }

        var symbols = new string[count];
        for (var i = 0; i < count; i++)
        {
            symbols[i] = input.ReadLine() ?? string.Empty;
        }

        return SourceParsing.ParsesWithSymbols(input.ReadToEnd(), symbols) ? 0 : 1;
    }

    // Starts the same build that is running. When the tool is the entry assembly, the process
    // is either its own launcher (installed tool, apphost), which takes the verb as is, or the
    // dotnet host ("dotnet AgenticRoslynTool.dll", dnx), which needs the assembly first. When
    // something else hosts the tool, such as a test host, which is testhost.exe on Windows,
    // the child runs through the dotnet host the SDK names in DOTNET_HOST_PATH.
    private static ProcessStartInfo? StartInfo()
    {
        var toolIsEntry = Assembly.GetEntryAssembly() == typeof(SymbolRetry).Assembly;
        var host = toolIsEntry ? Environment.ProcessPath : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
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
        if (!toolIsEntry || Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
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

    private static void WriteInput(StreamWriter input, IReadOnlyList<string> symbols, string text)
    {
        try
        {
            using (input)
            {
                input.Write(symbols.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                foreach (var symbol in symbols)
                {
                    input.Write(symbol + "\n");
                }

                input.Write(text);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child died or was killed before it read everything. Its exit says why.
        }
    }

    private static void Drain(Task stream)
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
