using System.Diagnostics;
using System.Text;

namespace PricingTool.Agent.ClaudeCode;

/// <summary>One child process to run: the arguments are passed as a list (no shell, no quoting by hand)
/// and the prompt goes on stdin, so neither ends up on a command line.</summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string StandardInput,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, TimeSpan Elapsed);

/// <summary>Seam for tests: the client never starts a process except through this.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default);
}

/// <summary>
/// Runs a process with an explicit environment (the parent's is cleared first), UTF-8 stdin without a
/// BOM, both output streams read concurrently, and the whole process tree killed on timeout.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), ct);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(spec.Timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }
        sw.Stop();

        return new ProcessResult(process.ExitCode, await stdout, await stderr, timedOut, sw.Elapsed);
    }
}
