using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EzJit
{
    static class CoreRun
    {
        public static string GetCoreRootPath(string runtimeRepoPath, string arch, string config)
        {
            var coreRootPath = $"artifacts\\tests\\coreclr\\windows.{arch}.{config}\\Tests\\Core_Root";
            return Path.Combine(runtimeRepoPath, coreRootPath);
        }

        public static (string corerunExe, string dotNetExeOrDll, List<string> args, List<(string, string)> envVars)
            GetArguments(string coreRoot, string dotNetExeOrDll, string[] arguments)
        {
            var corerunExe = Path.Combine(coreRoot, "corerun.exe");
            dotNetExeOrDll = Path.GetFullPath(dotNetExeOrDll);

            if (!Directory.Exists(coreRoot))
            {
                AnsiConsole.MarkupLine($"[red]'Core_Root' path not found: \"{coreRoot}\"[/]");
                throw new ArgumentException("'Core_Root' path not found.");
            }

            if (!File.Exists(corerunExe))
            {
                AnsiConsole.MarkupLine($"[red]'corerun.exe' file not found: \"{corerunExe}\"[/]");
                throw new ArgumentException("'corerun.exe' file not found.");
            }

            if (!File.Exists(dotNetExeOrDll))
            {
                AnsiConsole.MarkupLine($"[red]\"{dotNetExeOrDll}\" file not found.[/]");
                throw new ArgumentException($"\"{dotNetExeOrDll}\" file not found.");
            }

            var envVars = new List<(string, string)>();
            envVars.Add(("CORE_LIBRARIES", Path.GetDirectoryName(dotNetExeOrDll)));
            envVars.Add(("CORE_ROOT", coreRoot));

            var args = new List<string>();
            args.Add($"\"{dotNetExeOrDll}\"");
            if (arguments != null)
            {
                args.AddRange(arguments);
            }

            return (corerunExe, dotNetExeOrDll, args, envVars);
        }
    }
}
