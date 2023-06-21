using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.Specialized;

using Spectre.Console;
using Spectre.Console.Cli;
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
using CsvHelper;
using CsvHelper.Configuration;
using static EzJit.Program.AnalyzeEtlDiffCommand;
using System.Runtime.InteropServices;

namespace EzJit
{
    static class Program
    {

        static EzJitConfiguration LoadConfiguration()
        {
            return JsonSerializer.Deserialize<EzJitConfiguration>(File.ReadAllText(EzJit.GetConfigurationPath()));
        }

        static void SaveConfiguration(EzJitConfiguration config)
        {
            File.WriteAllText(EzJit.GetConfigurationPath(), JsonSerializer.Serialize(config));
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

                var coreRoot = CoreRun.GetCoreRootPath(Configuration.RuntimeRepoPath, settings.Architecture, settings.Configuration);
                var (corerunExe, _, corerunExeArgs, corerunExeEnvVars) = CoreRun.GetArguments(coreRoot, settings.DotNetExeOrDllPath, settings.Arguments);

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
                var batFilePath = Path.Combine(EzJit.ScratchDirectory, "run.bat");
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
                    var (jitMethods, managedCalls, nativeCalls) = EtlProcessing.ProcessEtl(etlFilePath + ".zip", true, true, -1, new TimeStampRange(), Path.Combine(coreRoot, "\\PDB"));

                    jitMethods = jitMethods.OrderByDescending(x => x.Time).Take(EzJit.NumberOfMethodsToPrint).ToList();
                    managedCalls = managedCalls.OrderByDescending(x => x.ExclusivePercent).Take(EzJit.NumberOfMethodsToPrint).ToList();
                    nativeCalls = nativeCalls.OrderByDescending(x => x.ExclusivePercent).Take(EzJit.NumberOfMethodsToPrint).ToList();

                    AnsiConsole.WriteLine("");
                    PrintTopSlowestJittedMethods(jitMethods);
                    AnsiConsole.WriteLine("");
                    PrintTopSlowestManagedMethodCalls(managedCalls);
                    AnsiConsole.WriteLine("");
                    PrintTopSlowestNativeMethodCalls(nativeCalls);
                }

                return exitCode;
            }
        }

        static void SaveCsvData<T>(string path, IEnumerable<T> data)
        {
            var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                NewLine = Environment.NewLine,
                ShouldQuote = new ShouldQuote(_ => true)
            };

            using (var writer = new StreamWriter(path))
            using (var csv = new CsvWriter(writer, csvConfig))
            {
                csv.WriteRecords(data);
            }
        }

        static void PrintTopSlowestJittedMethods(List<JitMethodData> jitMethods)
        {
            AnsiConsole.MarkupLine("[purple]Top Slowest Jitted Methods[/]");

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

        static void PrintTopSlowestManagedMethodCalls(List<MethodCallData> methodCalls)
        {
            AnsiConsole.MarkupLine("[purple]Top Slowest Managed Method Calls[/]");

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

        static void PrintTopSlowestNativeMethodCalls(List<MethodCallData> methodCalls)
        {
            AnsiConsole.MarkupLine("[purple]Top Slowest Native Method Calls[/]");

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

        static void PrintTopSlowestJittedMethodsDiff(List<JitMethodDataDiff> jitMethods)
        {
            AnsiConsole.MarkupLine("[purple]Top Slowest Jitted Methods - Diffs[/]");

            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddRow(new string[] { "Name", "Jit Time(ms) Diff" });

            foreach (var jitMethod in jitMethods)
            {
                grid.AddRow(new string[] { Markup.Escape(jitMethod.FullyQualifiedName), jitMethod.TimeDiff.ToString("F04", CultureInfo.InvariantCulture) });
            }

            AnsiConsole.Write(grid);
        }

        static void PrintTopSlowestManagedMethodCallsDiff(List<MethodCallDataDiff> methodCalls)
        {
            AnsiConsole.MarkupLine("[purple]Top Slowest Managed Method Calls - Diffs[/]");

            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddRow(new string[] { "Name", "Exc % Diff", "Exc Diff", "Inc % Diff", "Inc Diff" });

            foreach (var call in methodCalls)
            {
                grid.AddRow(new string[] { Markup.Escape(call.Name), call.ExclusivePercentDiff.ToString("F04", CultureInfo.InvariantCulture), call.ExclusiveCountDiff.ToString("F04", CultureInfo.InvariantCulture), call.InclusivePercentDiff.ToString("F04", CultureInfo.InvariantCulture), call.InclusiveCountDiff.ToString("F04", CultureInfo.InvariantCulture) });
            }

            AnsiConsole.Write(grid);
        }

        static void PrintTopSlowestNativeMethodCallsDiff(List<MethodCallDataDiff> methodCalls)
        {
            AnsiConsole.MarkupLine("[purple]Top Slowest Native Method Calls - Diffs[/]");

            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddRow(new string[] { "Name", "Exc % Diff", "Exc Diff", "Inc % Diff", "Inc Diff" });

            foreach (var call in methodCalls)
            {
                grid.AddRow(new string[] { Markup.Escape(call.Name), call.ExclusivePercentDiff.ToString("F04", CultureInfo.InvariantCulture), call.ExclusiveCountDiff.ToString("F04", CultureInfo.InvariantCulture), call.InclusivePercentDiff.ToString("F04", CultureInfo.InvariantCulture), call.InclusiveCountDiff.ToString("F04", CultureInfo.InvariantCulture) });
            }

            AnsiConsole.Write(grid);
        }

        public static ValidationResult ValidateAnalyzeEtl<T>(CommandContext context, T settings, Func<CommandContext, T, ValidationResult> validate) where T : AnalyzeEtlCommand.Settings
        {
            if (settings.Start != 0 && !string.IsNullOrWhiteSpace(settings.StartEventName))
            {
                return ValidationResult.Error("'--start' and '--start-event' cannot be used at the same time.");
            }

            if (settings.End != double.MaxValue && !string.IsNullOrWhiteSpace(settings.EndEventName))
            {
                return ValidationResult.Error("'--end' and '--end-event' cannot be used at the same time.");
            }

            return validate(context, settings);
        }

        public class AnalyzeEtlDiffCommand : Command<AnalyzeEtlDiffCommand.Settings>
        {
            public class Settings : AnalyzeEtlCommand.Settings
            {
                [CommandArgument(2, "<base-etl/etl.zip>")]
                public string BaseEtlFilePath { get; set; }

                [CommandArgument(3, "<base-process-id>")]
                public int BaseProcessId { get; set; }

                [CommandOption("--percent")]
                [Description("Calculate diffs by percentage difference.")]
                public bool UsePercent { get; set; }
            }

            public override ValidationResult Validate(CommandContext context, Settings settings)
            {
                return ValidateAnalyzeEtl(context, settings, base.Validate);
            }

            public override int Execute(CommandContext context, Settings settings)
            {
                var range = new TimeStampRange();
                range.Start = settings.Start;
                range.End = settings.End;
                range.StartEventName = settings.StartEventName;
                range.EndEventName = settings.EndEventName;
                var (jitMethods, managedCalls, nativeCalls) = EtlProcessing.ProcessEtl(settings.EtlFilePath, false, false, settings.ProcessId, range, string.Empty);
                var (jitMethodsBase, managedCallsBase, nativeCallsBase) = EtlProcessing.ProcessEtl(settings.BaseEtlFilePath, false, false, settings.BaseProcessId, range, string.Empty);

                var jitMethodsDiff = new List<JitMethodDataDiff>();
                var managedCallsDiff = new List<MethodCallDataDiff>();
                var nativeCallsDiff = new List<MethodCallDataDiff>();

                var jitMethodsLookup = new Dictionary<string, JitMethodData>();
                foreach (var x in jitMethods)
                {
                    jitMethodsLookup[x.FullyQualifiedName] = x;
                }
                foreach (var y in jitMethodsBase)
                {
                    if (jitMethodsLookup.TryGetValue(y.FullyQualifiedName, out var x) && x.IsValid && y.IsValid)
                    {
                        var data = new JitMethodDataDiff();
                        data.FullyQualifiedName = x.FullyQualifiedName;
                        if (settings.UsePercent)
                        {
                            data.TimeDiff = (Math.Abs(y.Time - x.Time) / y.Time) * 100;
                        }
                        else
                        {
                            data.TimeDiff = y.Time - x.Time;
                        }
                        jitMethodsDiff.Add(data);
                    }
                }

                var managedCallsLookup = new Dictionary<string, MethodCallData>();
                foreach (var x in managedCalls)
                {
                    managedCallsLookup[x.Name] = x;
                }
                foreach (var y in managedCallsBase)
                {
                    if (managedCallsLookup.TryGetValue(y.Name, out var x) && x.IsValid && y.IsValid)
                    {
                        var data = new MethodCallDataDiff();
                        data.Name = x.Name;
                        if (settings.UsePercent)
                        {
                            data.ExclusivePercentDiff = (Math.Abs(y.ExclusivePercent - x.ExclusivePercent) / y.ExclusivePercent) * (double)100;
                            data.ExclusiveCountDiff = (Math.Abs((double)y.ExclusiveCount - (double)x.ExclusiveCount) / (double)y.ExclusiveCount) * (double)100;
                            data.InclusivePercentDiff = (Math.Abs(y.InclusivePercent - x.InclusivePercent) / y.InclusivePercent) * (double)100;
                            data.InclusiveCountDiff = (Math.Abs((double)y.InclusiveCount - (double)x.InclusiveCount) / (double)y.InclusiveCount) * (double)100;
                        }
                        else
                        {
                            data.ExclusivePercentDiff = y.ExclusivePercent - x.ExclusivePercent;
                            data.ExclusiveCountDiff = y.ExclusiveCount - x.ExclusiveCount;
                            data.InclusivePercentDiff = y.InclusivePercent - x.InclusivePercent;
                            data.InclusiveCountDiff = y.InclusiveCount - x.InclusiveCount;
                        }
                        managedCallsDiff.Add(data);
                    }
                }

                var nativeCallsLookup = new Dictionary<string, MethodCallData>();
                foreach (var x in nativeCalls)
                {
                    nativeCallsLookup[x.Name] = x;
                }
                foreach (var y in nativeCallsBase)
                {
                    if (nativeCallsLookup.TryGetValue(y.Name, out var x) && x.IsValid && y.IsValid)
                    {
                        var data = new MethodCallDataDiff();
                        data.Name = x.Name;
                        if (settings.UsePercent)
                        {
                            data.ExclusivePercentDiff = (Math.Abs(y.ExclusivePercent - x.ExclusivePercent) / y.ExclusivePercent) * (double)100;
                            data.ExclusiveCountDiff = (Math.Abs((double)y.ExclusiveCount - (double)x.ExclusiveCount) / (double)y.ExclusiveCount) * (double)100;
                            data.InclusivePercentDiff = (Math.Abs(y.InclusivePercent - x.InclusivePercent) / y.InclusivePercent) * (double)100;
                            data.InclusiveCountDiff = (Math.Abs((double)y.InclusiveCount - (double)x.InclusiveCount) / (double)y.InclusiveCount) * (double)100;
                        }
                        else
                        {
                            data.ExclusivePercentDiff = y.ExclusivePercent - x.ExclusivePercent;
                            data.ExclusiveCountDiff = y.ExclusiveCount - x.ExclusiveCount;
                            data.InclusivePercentDiff = y.InclusivePercent - x.InclusivePercent;
                            data.InclusiveCountDiff = y.InclusiveCount - x.InclusiveCount;
                        }
                        nativeCallsDiff.Add(data);
                    }
                }

                jitMethodsDiff = jitMethodsDiff.OrderByDescending(x => x.TimeDiff).Take(EzJit.NumberOfMethodsToPrint).ToList();
                managedCallsDiff = managedCallsDiff.OrderByDescending(x => x.ExclusivePercentDiff).Take(EzJit.NumberOfMethodsToPrint).ToList();
                nativeCallsDiff = nativeCallsDiff.OrderByDescending(x => x.ExclusivePercentDiff).Take(EzJit.NumberOfMethodsToPrint).ToList();

                if (string.IsNullOrWhiteSpace(settings.OutputCsvPrefix))
                {
                    AnsiConsole.WriteLine("");
                    PrintTopSlowestJittedMethodsDiff(jitMethodsDiff);
                    AnsiConsole.WriteLine("");
                    PrintTopSlowestManagedMethodCallsDiff(managedCallsDiff);
                    AnsiConsole.WriteLine("");
                    PrintTopSlowestNativeMethodCallsDiff(nativeCallsDiff);
                }
                else
                {
                    SaveCsvData(Path.Combine(Environment.CurrentDirectory, $"{settings.OutputCsvPrefix}_JIT_METHODS_DIFF.csv"), jitMethodsDiff);
                    SaveCsvData(Path.Combine(Environment.CurrentDirectory, $"{settings.OutputCsvPrefix}_MANAGED_CALLS_DIFF.csv"), managedCallsDiff);
                    SaveCsvData(Path.Combine(Environment.CurrentDirectory, $"{settings.OutputCsvPrefix}_NATIVE_CALLS_DIFF.csv"), nativeCallsDiff);
                }
                return 0;
            }
        }

        public class AnalyzeEtlCommand : Command<AnalyzeEtlCommand.Settings>
        {
            public class Settings : CommandSettings
            {
                [CommandArgument(0, "<etl/etl.zip>")]
                public string EtlFilePath { get; set; }

                [CommandArgument(1, "<process-id>")]
                public int ProcessId { get; set; }

                [CommandOption("--start")]
                [Description("Filter events by time-stamps greater-than or equal to this value.")]
                [DefaultValue((double)0)]
                public double Start { get; set; }

                [CommandOption("--start-event")]
                [Description("Filter events by time-stamps greater-than or equal to the time-stamp of first occurence of the given event.")]
                public string StartEventName { get; set; }

                [CommandOption("--end")]
                [Description("Filter events by time-stamps less-than or equal to this value.")]
                [DefaultValue(Double.MaxValue)]
                public double End { get; set; }

                [CommandOption("--end-event")]
                [Description("Filter events by time-stamps less-than or equal to the time-stamp of first occurence of the given event.")]
                public string EndEventName { get; set; }

                [CommandOption("--output-csv-prefix")]
                [Description("Output analysis data with the given prefix for the output path of each CSV file. The directory the files are created in is the one EzJit was from run.")]
                public string OutputCsvPrefix { get; set; }
            }

            public override ValidationResult Validate(CommandContext context, Settings settings)
            {
                return ValidateAnalyzeEtl(context, settings, base.Validate);
            }

            public override int Execute(CommandContext context, Settings settings)
            {
                var range = new TimeStampRange();
                range.Start = settings.Start;
                range.End = settings.End;
                range.StartEventName = settings.StartEventName;
                range.EndEventName = settings.EndEventName;
                var (jitMethods, managedCalls, nativeCalls) = EtlProcessing.ProcessEtl(settings.EtlFilePath, false, false, settings.ProcessId, range, string.Empty);

                jitMethods = jitMethods.OrderByDescending(x => x.Time).Take(EzJit.NumberOfMethodsToPrint).ToList();
                managedCalls = managedCalls.OrderByDescending(x => x.ExclusivePercent).Take(EzJit.NumberOfMethodsToPrint).ToList();
                nativeCalls = nativeCalls.OrderByDescending(x => x.ExclusivePercent).Take(EzJit.NumberOfMethodsToPrint).ToList();

                if (string.IsNullOrEmpty(settings.OutputCsvPrefix))
                {
                    AnsiConsole.WriteLine("");
                    PrintTopSlowestJittedMethods(jitMethods);
                    AnsiConsole.WriteLine("");
                    PrintTopSlowestManagedMethodCalls(managedCalls);
                    AnsiConsole.WriteLine("");
                    PrintTopSlowestNativeMethodCalls(nativeCalls);
                }
                else
                {
                    SaveCsvData(Path.Combine(Environment.CurrentDirectory, $"{settings.OutputCsvPrefix}_JIT_METHODS.csv"), jitMethods);
                    SaveCsvData(Path.Combine(Environment.CurrentDirectory, $"{settings.OutputCsvPrefix}_MANAGED_CALLS.csv"), managedCalls);
                    SaveCsvData(Path.Combine(Environment.CurrentDirectory, $"{settings.OutputCsvPrefix}_NATIVE_CALLS.csv"), nativeCalls);
                }
                return 0;
            }
        }

        static int ExecuteCoreRun(AbstractCoreRunCommandSettings settings, string coreRoot)
        {
            try
            {
                var (corerunExe, dotNetExeOrDll, args, envVars) = CoreRun.GetArguments(coreRoot, settings.DotNetExeOrDllPath, settings.Arguments);
                var collect = !string.IsNullOrWhiteSpace(settings.CollectPath);
                if (collect)
                {
                    var jitName = "clrjit.dll";
                    var scratchPath = EzJit.CreateScratchDirectory();
                    envVars.Add(("SuperPMIShimLogPath", scratchPath));
                    envVars.Add(("SuperPMIShimPath", Path.Combine(coreRoot, jitName)));
                    envVars.Add(("DOTNET_JitName", "superpmi-shim-collector.dll"));
                }

                var methToDump = string.Empty;
                if (!string.IsNullOrWhiteSpace(settings.DisasmMethod))
                {
                    methToDump = settings.DisasmMethod;
                }
                if (!string.IsNullOrWhiteSpace(settings.DumpMethod))
                {
                    methToDump = settings.DumpMethod;
                }

                if (!string.IsNullOrWhiteSpace(settings.DumpMethod))
                {
                    envVars.Add(("DOTNET_JitDump", methToDump));
                    envVars.Add(("DOTNET_JitDiffableDasm", "1"));
                    envVars.Add(("DOTNET_JitStdOutFile", Path.Combine(Environment.CurrentDirectory, "dump_meth.txt")));
                }

                if (!string.IsNullOrWhiteSpace(settings.DisasmMethod) && string.IsNullOrWhiteSpace(settings.DumpMethod))
                {
                    envVars.Add(("DOTNET_JitDisasm", methToDump));
                    envVars.Add(("DOTNET_JitDiffableDasm", "1"));
                    envVars.Add(("DOTNET_JitStdOutFile", Path.Combine(Environment.CurrentDirectory, "dump_meth.txt")));
                }

                if (!string.IsNullOrWhiteSpace(settings.AltJit))
                {
                    if (!string.IsNullOrWhiteSpace(methToDump))
                    {
                        envVars.Add(("DOTNET_AltJit", methToDump));
                    }
                    envVars.Add(("DOTNET_AltJitName", settings.AltJit));
                }

                envVars.Add(("DOTNET_TieredCompilation", Convert.ToInt32(settings.Tier).ToString()));
                envVars.Add(("DOTNET_TieredPGO", Convert.ToInt32(settings.Pgo).ToString()));
                envVars.Add(("DOTNET_ReadyToRun", "1"));
                envVars.Add(("DOTNET_TieredPGO_InstrumentOnlyHotCode", "0"));
                envVars.Add(("DOTNET_TC_CallCountingDelayMs", "0"));

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
                            $"\"{EzJit.ScratchDirectory}\\*.mc\"",
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


        public abstract class AbstractCoreRunCommandSettings : CommandSettings
        {
            public abstract string DotNetExeOrDllPath { get; set; }

            public abstract string[] Arguments { get; set; }

            [CommandOption("--collect")]
            public string CollectPath { get; set; }

            [CommandOption("--dump-meth")]
            public string DumpMethod { get; set; }

            [CommandOption("--disasm-meth")]
            public string DisasmMethod { get; set; }

            [CommandOption("--alt-jit")]
            public string AltJit { get; set; }

            [CommandOption("--tier")]
            [DefaultValue(true)]
            public bool Tier { get; set; }

            [CommandOption("--pgo")]
            [DefaultValue(false)]
            public bool Pgo { get; set; }
        }

        public class CoreRunCustomCommand : Command<CoreRunCustomCommand.Settings>
        {
            public class Settings : AbstractCoreRunCommandSettings
            {
                [CommandArgument(0, "<core_root>")]
                public string CoreRoot { get; set; }

                [CommandArgument(1, "<exe/dll>")]
                public override string DotNetExeOrDllPath { get; set; }

                [CommandArgument(2, "[args]")]
                public override string[] Arguments { get; set; }
            }

            public override int Execute(CommandContext context, Settings settings)
            {
                return ExecuteCoreRun(settings, settings.CoreRoot);
            }
        }

        public class CoreRunCommand : Command<CoreRunCommand.Settings>
        {
            public class Settings : AbstractCoreRunCommandSettings
            {
                [CommandArgument(0, "<arch>")]
                public string Architecture { get; set; }

                [CommandArgument(1, "<config>")]
                public string Configuration { get; set; }

                [CommandArgument(2, "<exe/dll>")]
                public override string DotNetExeOrDllPath { get; set; }

                [CommandArgument(3, "[args]")]
                public override string[] Arguments { get; set; }
            }

            public override int Execute(CommandContext context, Settings settings)
            {
                CheckRuntimeRepositoryConfiguration();
                PrintConfiguration();

                return ExecuteCoreRun(settings, CoreRun.GetCoreRootPath(Configuration.RuntimeRepoPath, settings.Architecture, settings.Configuration));
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
            EzJit.ClearScratchDirectory();
            Configuration = LoadConfiguration();

            var app = new CommandApp();
            app.Configure(config =>
            {
                config.AddCommand<CoreRunCommand>("run");
                config.AddCommand<CoreRunCustomCommand>("corerun");

                config.AddCommand<TraceCommand>("trace").WithExample(new string[] { "trace --providers \"ClrPrivate,PaintDotNetTrace\" \"paintdotnet.etl\" x64 release \"C:\\Program Files\\paint.net\\paintdotnet.dll\" /returnOnShownTime" });
                config.AddCommand<AnalyzeEtlCommand>("analyze-etl").WithExample(new string[] { "analyze-etl \"paintdotnet.etl.zip\" 17180 --start-event \"PaintDotNetTrace/AppStarted\" --end-event \"PaintDotNetTrace/AppReady\"" });
                config.AddCommand<AnalyzeEtlDiffCommand>("analyze-etl-diff");
            });

            try
            {
                return app.Run(args);
            }
            finally
            {
                EzJit.ClearScratchDirectory();
            }
        }
    }
}