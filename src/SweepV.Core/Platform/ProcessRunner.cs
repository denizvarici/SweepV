using System.Diagnostics;

namespace SweepV.Core.Platform
{
    public readonly record struct ProcessResult(int ExitCode, string Output)
    {
        public bool Succeeded => ExitCode == 0;
    }

    /// <summary>Runs a console tool hidden and captures its output.</summary>
    public static class ProcessRunner
    {
        public static ProcessResult Run(string exe, IEnumerable<string> args, CancellationToken cancellationToken = default)
        {
            var info = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);

            Process? process;
            try
            {
                process = Process.Start(info);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return new ProcessResult(-1, ex.Message);
            }
            if (process is null)
                return new ProcessResult(-1, string.Empty);

            using (process)
            {
                // Read both pipes concurrently so the child can't block on a full buffer.
                var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                try
                {
                    process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    throw;
                }
                return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
            }
        }

        public static string SystemTool(string exeName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName);
    }
}
