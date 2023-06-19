using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EzJit
{
    static class ExternalProcess
    {
        public static async Task<(int exitCode, string stdOut, string stdError)> Exec(string filePath, string[] args, (string name, string value)[] envVars, bool runAsAdmin)
        {
            var startInfo = new ProcessStartInfo();
            startInfo.FileName = filePath;
            startInfo.Arguments = string.Join(' ', args);
            if (!runAsAdmin)
            {
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            }
            startInfo.RedirectStandardOutput = !runAsAdmin;
            startInfo.RedirectStandardError = !runAsAdmin;
            startInfo.UseShellExecute = runAsAdmin;

            if (runAsAdmin)
            {
                startInfo.Verb = "runas";
            }

            foreach (var (name, value) in envVars)
            {
                startInfo.EnvironmentVariables[name] = value;
            }

            try
            {
                if (runAsAdmin)
                {
                    using (var proc = Process.Start(startInfo))
                    {
                        if (proc == null)
                            throw new Exception($"Failed to execute '{startInfo.FileName} {startInfo.Arguments}'.");

                        proc.WaitForExit();
                        var exitCode = proc.ExitCode;
                        return (exitCode, string.Empty, string.Empty);
                    }
                }
                else
                {
                    using (AutoResetEvent outputWaitHandle = new AutoResetEvent(false))
                    using (AutoResetEvent errorWaitHandle = new AutoResetEvent(false))
                    using (var proc = Process.Start(startInfo))
                    {
                        if (proc == null)
                            throw new Exception($"Failed to execute '{startInfo.FileName} {startInfo.Arguments}'.");

                        var stdOut = new StringBuilder();
                        var stdErr = new StringBuilder();

                        proc.OutputDataReceived += (_, e) =>
                        {
                            if (e.Data == null)
                            {
                                outputWaitHandle.Set();
                            }
                            else
                            {
                                AnsiConsole.WriteLine(e.Data);
                                stdOut.AppendLine(e.Data);
                            }
                        };

                        proc.ErrorDataReceived += (_, e) =>
                        {
                            if (e.Data == null)
                            {
                                errorWaitHandle.Set();
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Data)}[/]");
                                stdErr.AppendLine(e.Data);
                            }
                        };

                        proc.BeginOutputReadLine();
                        proc.BeginErrorReadLine();

                        await proc.WaitForExitAsync();
                        outputWaitHandle.WaitOne();
                        errorWaitHandle.WaitOne();

                        var exitCode = proc.ExitCode;
                        return (exitCode, stdOut.ToString(), stdErr.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                return new(1, string.Empty, ex.Message);
            }
        }
    }
}
