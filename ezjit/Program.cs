using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.Specialized;

using Spectre.Console;
using Spectre.Console.Cli;
using static EzJit.Program.RunCommand;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using static EzJit.Program.TraceCommand;
using Microsoft.Diagnostics.Tracing;
using System.Globalization;
using static EzJit.Program.AnalyzeEtlCommand;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using static System.Runtime.InteropServices.JavaScript.JSType;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using System.Reflection;
using System.ComponentModel;

namespace EzJit
{
    static class Program
    {
        static string EzJitExeDirectory
        {
            get
            {
                return Path.GetDirectoryName(typeof(Program).Assembly.Location);
            }
        }

        static string EzJitScratchDirectory
        {
            get
            {
                return Path.Combine(EzJitExeDirectory, ".ezjit_scratch");
            }
        }

        static string GetConfigurationPath()
        {
            return Path.Combine(EzJitExeDirectory, "ezjit.config.json");
        }

        static string CreateScratchDirectory()
        {
            var scratchPath = EzJitScratchDirectory;
            try
            {
                Directory.CreateDirectory(scratchPath);
            }
            catch { }
            return scratchPath;
        }

        static void ClearScratchDirectory()
        {
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(EzJitScratchDirectory))
                {
                    try
                    {
                        File.Delete(filePath);
                    }
                    catch { }
                }
            }
            catch { }
        }

        static EzJitConfiguration LoadConfiguration()
        {
            return JsonSerializer.Deserialize<EzJitConfiguration>(File.ReadAllText(GetConfigurationPath()));
        }

        static void SaveConfiguration(EzJitConfiguration config)
        {
            File.WriteAllText(GetConfigurationPath(), JsonSerializer.Serialize(config));
        }


        static string GetCoreRootPath(string arch, string config)
        {
            var coreRootPath = $"artifacts\\tests\\coreclr\\windows.{arch}.{config}\\Tests\\Core_Root";
            return Path.Combine(Configuration.RuntimeRepoPath, coreRootPath);
        }

        static (string corerunExe, string dotNetExeOrDll, List<string> args, List<(string, string)> envVars) 
            GetRunArguments(string architecture, string configuration, string dotNetExeOrDllPath, string[] arguments)
        {
            var coreRoot = GetCoreRootPath(architecture, configuration);
            var corerunExe = Path.Combine(coreRoot, "corerun.exe");
            var dotNetExeOrDll = Path.GetFullPath(dotNetExeOrDllPath);

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

        public class TraceCommand : Command<TraceCommand.Settings>
        {
            public class Settings : CommandSettings
            {
                [CommandArgument(0, "<output-etl>")]
                public string EtlFilePath { get; set; }

                [CommandArgument(1, "<arch>")]
                public string Architecture { get; set; }

                [CommandArgument(2, "<config>")]
                public string Configuration { get; set; }

                [CommandArgument(3, "<exe/dll>")]
                public string DotNetExeOrDllPath { get; set; }

                [CommandArgument(4, "[args]")]
                public string[] Arguments { get; set; }

                [CommandOption("--analyze")]
                [Description("Analyze ETL.")]
                public bool CanAnalyze { get; set; }

                [CommandOption("--providers")]
                [Description("ETW providers. Comma-delimited")]
                public string Providers { get; set; }
            }

            public override int Execute(CommandContext context, Settings settings)
            {
                CheckRuntimeRepositoryConfiguration();
                CheckPerfViewExeConfiguration();
                PrintConfiguration();

                var (corerunExe, _, corerunExeArgs, corerunExeEnvVars) = GetRunArguments(settings.Architecture, settings.Configuration, settings.DotNetExeOrDllPath, settings.Arguments);
                var coreRoot = Path.GetDirectoryName(corerunExe);

                var etlFilePath = settings.EtlFilePath;

                var perfViewExe = Configuration.PerfViewExePath;
                var args = new List<string>();
                args.Add($"/DataFile:\"{etlFilePath}\"");
                args.Add("/BufferSizeMB:256");
                args.Add("/StackCompression");
                args.Add("/CircularMB:4096");
                args.Add("/CpuSampleMSec:0.125");
                args.Add("/Process:\"corerun\"");
                if (!string.IsNullOrWhiteSpace(settings.Providers))
                {
                    args.Add($"/Providers:\"{settings.Providers}\"");
                }
                args.Add("/ClrEvents:GC,Binder,Security,AppDomainResourceManagement,Contention,Exception,Threading,JITSymbols,Type,GCHeapSurvivalAndMovement,GCHeapAndTypeNames,Stack,ThreadTransfer,Codesymbols,Compilation");
                args.Add("/NoGui");
                args.Add("/NoNGenRundown");
                args.Add("run");

                // Begin Bat
                var batFilePath = Path.Combine(EzJitScratchDirectory, "run.bat");
                var bat = new StringBuilder();
                foreach(var (key, value) in corerunExeEnvVars)
                {
                    bat.AppendLine($"set {key}={value}");
                }
                bat.Append($"\"{corerunExe}\" ");
                foreach(var arg in corerunExeArgs)
                {
                    bat.Append($"{arg} ");
                }
                bat.AppendLine();
                File.WriteAllText(batFilePath, bat.ToString());
                args.Add($"\"{batFilePath}\"");
                // End Bat

                (int exitCode, string stdOut, string stdErr) = ExternalProcess.Exec(perfViewExe, args.ToArray(), new (string, string)[] { }, true).Result;

                if (settings.CanAnalyze)
                {
                    var (jitMethods, managedCalls, nativeCalls) = EtlProcessing.ProcessEtl(etlFilePath + ".zip", true, true, -1, null, Path.Combine(coreRoot, "\\PDB"));
                    AnsiConsole.WriteLine("");
                    PrintTop20SlowestJittedMethods(jitMethods);
                    AnsiConsole.WriteLine("");
                    PrintTop20SlowestManagedMethodCalls(managedCalls);
                    AnsiConsole.WriteLine("");
                    PrintTop20SlowestNativeMethodCalls(nativeCalls);
                }

                return exitCode;
            }
        }

        static void PrintTop20SlowestJittedMethods(List<JitMethodData> jitMethods)
        {
            jitMethods = jitMethods.OrderByDescending(x => x.Time).Take(20).ToList();

            AnsiConsole.MarkupLine("[purple]Top 20 Slowest Jitted Methods[/]");

            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddRow(new string[] { "Name", "Jit Time(ms)" });

            foreach (var jitMethod in jitMethods)
            {
                grid.AddRow(new string[] { Markup.Escape(jitMethod.FullyQualifiedName), jitMethod.Time.ToString("F04", CultureInfo.InvariantCulture) });
            }

            AnsiConsole.Write(grid);
        }

        static void PrintTop20SlowestManagedMethodCalls(List<MethodCallData> methodCalls)
        {
            methodCalls = methodCalls.OrderByDescending(x => x.ExclusivePercent).Take(20).ToList();

            AnsiConsole.MarkupLine("[purple]Top 20 Slowest Managed Method Calls[/]");

            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddRow(new string[] { "Name", "Exc %", "Exc", "Inc %", "Inc" });

            foreach (var call in methodCalls)
            {
                grid.AddRow(new string[] { Markup.Escape(call.Name), call.ExclusivePercent.ToString("F04", CultureInfo.InvariantCulture), call.ExclusiveCount.ToString(), call.InclusivePercent.ToString("F04", CultureInfo.InvariantCulture), call.InclusiveCount.ToString() });
            }

            AnsiConsole.Write(grid);
        }

        static void PrintTop20SlowestNativeMethodCalls(List<MethodCallData> methodCalls)
        {
            methodCalls = methodCalls.OrderByDescending(x => x.ExclusivePercent).Take(20).ToList();

            AnsiConsole.MarkupLine("[purple]Top 20 Slowest Native Method Calls[/]");

            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddRow(new string[] { "Name", "Exc %", "Exc", "Inc %", "Inc" });

            foreach (var call in methodCalls)
            {
                grid.AddRow(new string[] { Markup.Escape(call.Name), call.ExclusivePercent.ToString("F04", CultureInfo.InvariantCulture), call.ExclusiveCount.ToString(), call.InclusivePercent.ToString("F04", CultureInfo.InvariantCulture), call.InclusiveCount.ToString() });
            }

            AnsiConsole.Write(grid);
        }

        public class AnalyzeEtlCommand : Command<AnalyzeEtlCommand.Settings>
        {
            public class Settings : CommandSettings
            {
                [CommandArgument(0, "<etl/etl.zip>")]
                public string EtlFilePath { get; set; }

                [CommandArgument(1, "<process-id>")]
                public int ProcessId { get; set; }

                [CommandOption("-s|--start")]
                [Description("Filter events by time-stamps greater-than or equal to this value.")]
                [DefaultValue((double)0)]
                public double Start { get; set; }

                [CommandOption("--start-event")]
                [Description("Filter events by time-stamps greater-than or equal to the time-stamp of first occurence of the given event.")]
                public string StartEventName { get; set; }

                [CommandOption("-e|--end")]
                [Description("Filter events by time-stamps less-than or equal to this value.")]
                [DefaultValue(Double.MaxValue)]
                public double End { get; set; }

                [CommandOption("--end-event")]
                [Description("Filter events by time-stamps less-than or equal to the time-stamp of first occurence of the given event.")]
                public string EndEventName { get; set; }

                [CommandOption("--hide-meth-sig")]
                [Description("Hide method signatures.")]
                public bool CanHideMethodSignature { get; set; }
            }

            public override ValidationResult Validate(CommandContext context, Settings settings)
            {
                if (settings.Start != 0 && !string.IsNullOrWhiteSpace(settings.StartEventName))
                {
                    return ValidationResult.Error("'--start' and '--start-event' cannot used at the same time.");
                }

                if (settings.End != double.MaxValue && !string.IsNullOrWhiteSpace(settings.EndEventName))
                {
                    return ValidationResult.Error("'--end' and '--end-event' cannot used at the same time.");
                }

                return base.Validate(context, settings);
            }

            public override int Execute(CommandContext context, Settings settings)
            {
                var range = new TimeStampRange();
                range.Start = settings.Start;
                range.End = settings.End;
                range.StartEventName = settings.StartEventName;
                range.EndEventName = settings.EndEventName;
                var (jitMethods, managedCalls, nativeCalls) = EtlProcessing.ProcessEtl(settings.EtlFilePath, settings.CanHideMethodSignature, false, settings.ProcessId, range, string.Empty);
                AnsiConsole.WriteLine("");
                PrintTop20SlowestJittedMethods(jitMethods);
                AnsiConsole.WriteLine("");
                PrintTop20SlowestManagedMethodCalls(managedCalls);
                AnsiConsole.WriteLine("");
                PrintTop20SlowestNativeMethodCalls(nativeCalls);
                return 0;
            }
        }

        public class RunCommand : Command<RunCommand.Settings>
        {
            public class Settings : CommandSettings
            {
                [CommandArgument(0, "<arch>")]
                public string Architecture { get; set; }

                [CommandArgument(1, "<config>")]
                public string Configuration { get; set; }

                [CommandArgument(2, "<exe/dll>")]
                public string DotNetExeOrDllPath { get; set; }

                [CommandArgument(3, "[args]")]
                public string[] Arguments { get; set; }

                [CommandOption("--collect")]
                public string CollectPath { get; set; }
            }


            public override int Execute(CommandContext context, Settings settings)
            {
                CheckRuntimeRepositoryConfiguration();
                PrintConfiguration();

                try
                {
                    var (corerunExe, dotNetExeOrDll, args, envVars) = GetRunArguments(settings.Architecture, settings.Configuration, settings.DotNetExeOrDllPath, settings.Arguments);
                    var coreRoot = Path.GetDirectoryName(corerunExe);

                    var collect = !string.IsNullOrWhiteSpace(settings.CollectPath);

                    if (collect)
                    {
                        var jitName = "clrjit.dll";
                        var scratchPath = CreateScratchDirectory();
                        envVars.Add(("SuperPMIShimLogPath", scratchPath));
                        envVars.Add(("SuperPMIShimPath", Path.Combine(coreRoot, jitName)));
                        envVars.Add(("DOTNET_JitName", "superpmi-shim-collector.dll"));
                    }

                    (int exitCode, string stdOut, string stdErr) = ExternalProcess.Exec(corerunExe, args.ToArray(), envVars.ToArray(), false).Result;

                    // Use mcs.exe to cleanup and combine.
                    if (collect)
                    {
                        var mcsExe = Path.Combine(coreRoot, "mcs.exe");
                        var mcsExeArgs =
                            new string[]
                            {
                            "-merge",
                            $"\"{settings.CollectPath}\"",
                            $"\"{EzJitScratchDirectory}\\*.mc\"",
                            "-recursive",
                            "-dedup",
                            "-thin"
                            };

                        (int exitCode2, string stdOut2, string stdErr2) = ExternalProcess.Exec(mcsExe, mcsExeArgs, new (string, string)[] { }, false).Result;
                    }

                    return exitCode;
                }
                catch (ArgumentException)
                {
                    return 1;
                }
            }
        }

        static string DeterminePathColor(string path)
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                return "green";
            }
            return "gray";
        }

        static void PrintConfiguration()
        {
            AnsiConsole.WriteLine("");

            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();

            grid.AddRow(new string[] { $"[{DeterminePathColor(Configuration.RuntimeRepoPath)}]Runtime Repository:[/]", $"\"{Configuration.RuntimeRepoPath}\"" });
            grid.AddRow(new string[] { $"[{DeterminePathColor(Configuration.FuzzlynRepoPath)}]Fuzzlyn Repository:[/]", $"\"{Configuration.FuzzlynRepoPath}\"" });
            grid.AddRow(new string[] { $"[{DeterminePathColor(Configuration.PerfViewExePath)}]PerfView.exe:[/]", $"\"{Configuration.PerfViewExePath}\"" });

            AnsiConsole.Write(grid);

            AnsiConsole.WriteLine("");
        }

        static void CheckRuntimeRepositoryConfiguration()
        {
            var isRuntimeRepoEmpty = string.IsNullOrWhiteSpace(Configuration.RuntimeRepoPath);
            var runtimeRepoExists = Directory.Exists(Configuration.RuntimeRepoPath);
            while (isRuntimeRepoEmpty || !runtimeRepoExists)
            {
                if (!isRuntimeRepoEmpty && !runtimeRepoExists)
                {
                    AnsiConsole.MarkupLine($"[red]Runtime repository path, \"{Configuration.RuntimeRepoPath}\", does not exist.[/]");
                }

                Configuration = Configuration with { RuntimeRepoPath = AnsiConsole.Ask<string>("[red]Runtime repository path is required, [bold red]please enter the absolute path:[/][/]") };

                isRuntimeRepoEmpty = string.IsNullOrWhiteSpace(Configuration.RuntimeRepoPath);
                runtimeRepoExists = Directory.Exists(Configuration.RuntimeRepoPath);
            }

            SaveConfiguration(Configuration);
        }

        static void CheckPerfViewExeConfiguration()
        {
            var isEmpty = string.IsNullOrWhiteSpace(Configuration.PerfViewExePath);
            var exists = File.Exists(Configuration.PerfViewExePath);
            while (isEmpty || !exists)
            {
                if (!isEmpty && !exists)
                {
                    AnsiConsole.MarkupLine($"[red]PerfView.exe path, \"{Configuration.PerfViewExePath}\", does not exist.[/]");
                }

                Configuration = Configuration with { PerfViewExePath = AnsiConsole.Ask<string>("[red]PerfView.exe path is required, [bold red]please enter the absolute path:[/][/]") };

                isEmpty = string.IsNullOrWhiteSpace(Configuration.PerfViewExePath);
                exists = File.Exists(Configuration.PerfViewExePath);
            }

            SaveConfiguration(Configuration);
        }

        static EzJitConfiguration Configuration { get; set; }

        static int Main(string[] args)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                AnsiConsole.MarkupLine("[red]EzJit only supports Windows.");
                return 1;
            }

            // Init
            ClearScratchDirectory();
            Configuration = LoadConfiguration();

            var app = new CommandApp();
            app.Configure(config =>
            {
                config.AddCommand<RunCommand>("run");
                config.AddCommand<TraceCommand>("trace").WithExample(new string[] { "trace --providers \"ClrPrivate,PaintDotNetTrace\" \"paintdotnet.etl\" x64 release \"C:\\Program Files\\paint.net\\paintdotnet.dll\" /returnOnShownTime" });
                config.AddCommand<AnalyzeEtlCommand>("analyze-etl").WithExample(new string[] { "analyze-etl \"paintdotnet.etl.zip\" 17180 --start-event \"PaintDotNetTrace/AppStarted\" --end-event \"PaintDotNetTrace/AppReady\"" });
            });

            try
            {
                return app.Run(args);
            }
            finally
            {
                ClearScratchDirectory();
            }
        }
    }
}