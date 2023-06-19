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
        sealed class JitMethodData
        {
            public string FullyQualifiedName { get; set; }

            public double StartTime { get; set; }

            public double EndTime { get; set; }

            public double Time
            {
                get
                {
                    return EndTime - StartTime;
                }
            }

        }

        sealed class MethodCallData
        {
            public string Name { get; set; }
            public double ExclusivePercent { get; set; }
            public int ExclusiveCount { get; set; }
            public double InclusivePercent { get; set; }
            public int InclusiveCount { get; set; }
        }

        sealed record EzJitConfiguration(string RuntimeRepoPath, string FuzzlynRepoPath, string PerfViewExePath);

        sealed class TimeStampRange
        {
            public double Start { get; set; }

            public double End { get; set; }
        }

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

        static async Task<(int exitCode, string stdOut, string stdError)> Exec(string filePath, string[] args, (string name, string value)[] envVars, bool runAsAdmin)
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
                return new (1, string.Empty, ex.Message);
            }
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

        static (List<JitMethodData>, List<MethodCallData> managedCalls, List<MethodCallData> nativeCalls) ProcessEtl(string etlOrEtlZipFilePath, bool canHideMethodSignature, bool isCoreRun, int processId, TimeStampRange timeStampRange, string pdbDir)
        {
            if (Path.GetExtension(etlOrEtlZipFilePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var zipEtlPath = Path.GetFullPath(etlOrEtlZipFilePath);
                var nonZipEtlPath = etlOrEtlZipFilePath.Replace(".zip", "", StringComparison.OrdinalIgnoreCase);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipEtlPath, Path.GetDirectoryName(zipEtlPath), true);

                try
                {
                    return ProcessEtlCore(nonZipEtlPath, canHideMethodSignature, isCoreRun, processId, timeStampRange, pdbDir);
                }
                finally
                {
                    try
                    {
                        File.Delete(nonZipEtlPath);
                    }
                    catch { }
                }
            }

            var etlPath = Path.GetFullPath(etlOrEtlZipFilePath);
            return ProcessEtlCore(etlPath, canHideMethodSignature, isCoreRun, processId, timeStampRange, pdbDir);
        }

        static (List<JitMethodData>, List<MethodCallData> managedCalls, List<MethodCallData> nativeCalls) ProcessEtlCore(string etlFilePath, bool canHideMethodSignature, bool isCoreRun, int processId, TimeStampRange timeStampRange, string pdbDir)
        {
            TextWriter SymbolLookupMessages = new StringWriter();
            var symbolPath = new SymbolPath(SymbolPath.SymbolPathFromEnvironment).Add(SymbolPath.MicrosoftSymbolServerPath).Add(pdbDir);
            SymbolReader symbolReader = new SymbolReader(SymbolLookupMessages, symbolPath.ToString());
            symbolReader.Options = SymbolReaderOptions.CacheOnly;

            // By default the symbol reader will NOT read PDBs from 'unsafe' locations (like next to the EXE)  
            // because hackers might make malicious PDBs.   If you wish ignore this threat, you can override this
            // check to always return 'true' for checking that a PDB is 'safe'.  
            symbolReader.SecurityCheck = (path => true);

            var methods = new List<JitMethodData>();
            var methodLookup = new Dictionary<long, JitMethodData>();

            var managedCalls = new List<MethodCallData>();
            var nativeCalls = new List<MethodCallData>();

            using (var source = new ETWTraceEventSource(etlFilePath))
            {
                source.Clr.MethodJittingStarted += Clr_MethodJittingStarted;
                source.Clr.MethodLoadVerbose += Clr_MethodLoadVerbose;

                var traceLog = TraceLog.OpenOrConvert(etlFilePath, new TraceLogOptions() { ConversionLog = Console.Out });

                TraceProcess p;
                if (isCoreRun)
                {
                    p = traceLog.Processes.First(x => x.CommandLine.Contains("corerun"));
                }
                else
                {
                    p = traceLog.Processes.FirstOrDefault(x => x.ProcessID == processId);
                }

                var evts = p.EventsInProcess.Filter(x =>
                {
                    if (timeStampRange != null)
                    {
                        if (x.TimeStampRelativeMSec < timeStampRange.Start || x.TimeStampRelativeMSec > timeStampRange.End)
                            return false;
                    }

                    var callStack = x.CallStack();
                    if (callStack != null)
                    {
                        var mname = callStack.CodeAddress.ModuleName;
                        if (mname.Contains(".") || mname.StartsWith("ManagedModule"))
                        {
                            return true;
                        }
                    }
                    return false;
                });

                var callTree = new CallTree(ScalingPolicyKind.TimeMetric);
                var stackSource = new TraceEventStackSource(evts);
                callTree.StackSource = stackSource;

                foreach (var call in callTree.ByIDSortedExclusiveMetric().Take(20))
                {
                    managedCalls.Add(new MethodCallData() { Name = call.Name, ExclusivePercent = call.ExclusiveMetricPercent, ExclusiveCount = (int)call.ExclusiveCount, InclusivePercent = call.InclusiveMetricPercent, InclusiveCount = (int)call.InclusiveCount });
                }

                var evts2 = p.EventsInProcess.Filter(x =>
                {
                    if (timeStampRange != null)
                    {
                        if (x.TimeStampRelativeMSec < timeStampRange.Start || x.TimeStampRelativeMSec > timeStampRange.End)
                            return false;
                    }

                    var callStack = x.CallStack();
                    if (callStack != null)
                    {
                        var mname = callStack.CodeAddress.ModuleName;
                        if (!mname.Contains(".") && !mname.StartsWith("ManagedModule"))
                        {
                            ResolveNativeCode(callStack, symbolReader);
                            return true;
                        }
                    }
                    return false;
                });

                var callTree2 = new CallTree(ScalingPolicyKind.TimeMetric);
                var stackSource2 = new TraceEventStackSource(evts2);
                callTree2.StackSource = stackSource2;

                foreach (var call in callTree2.ByIDSortedExclusiveMetric().Take(20))
                {
                    nativeCalls.Add(new MethodCallData() { Name = call.Name, ExclusivePercent = call.ExclusiveMetricPercent, ExclusiveCount = (int)call.ExclusiveCount, InclusivePercent = call.InclusiveMetricPercent, InclusiveCount = (int)call.InclusiveCount });
                }

                source.Process();
            }

            return (methods.Where(x => x.EndTime != 0).ToList(), managedCalls, nativeCalls);

            void Clr_MethodJittingStarted(MethodJittingStartedTraceData data)
            {
                if (timeStampRange != null)
                {
                    if (data.TimeStampRelativeMSec < timeStampRange.Start || data.TimeStampRelativeMSec > timeStampRange.End)
                        return;
                }

                var m = new JitMethodData();

                var index = data.MethodSignature.IndexOf('(');
                if (index >= 0)
                {
                    var name = data.MethodNamespace + "::" + data.MethodName;

                    if (canHideMethodSignature)
                    {
                        m.FullyQualifiedName = name;
                    }
                    else 
                    {
                        m.FullyQualifiedName = data.MethodSignature.Insert(index, name);
                    }
                    m.StartTime = data.TimeStampRelativeMSec;
                    m.EndTime = 0;
                    methodLookup[data.MethodID] = m;
                    methods.Add(m);
                }
            }

            void Clr_MethodLoadVerbose(MethodLoadUnloadVerboseTraceData data)
            {
                if (timeStampRange != null)
                {
                    if (data.TimeStampRelativeMSec < timeStampRange.Start || data.TimeStampRelativeMSec > timeStampRange.End)
                        return;
                }

                JitMethodData m;
                if (methodLookup.TryGetValue(data.MethodID, out m))
                {
                    m.EndTime = data.TimeStampRelativeMSec;
                }
            }
        }

        static HashSet<ModuleFileIndex> moduleSymbolsLoaded = new();
        /// <summary>
        /// Because it is expensive and often unnecessary, lookup of native symbols needs to be explicitly requested.  
        /// Here we do this for every frame in the stack.     Note that this is not needed for JIT compiled managed code. 
        /// </summary>
        static void ResolveNativeCode(TraceCallStack callStack, SymbolReader symbolReader)
        {
            var codeAddress = callStack.CodeAddress;
            if (codeAddress.Method == null)
            {
                var moduleFile = codeAddress.ModuleFile;
                if (moduleFile == null)
                {
                    Trace.WriteLine(string.Format("Could not find module for Address 0x{0:x}", codeAddress.Address));
                }
                else
                {
                    if (moduleSymbolsLoaded.Add(moduleFile.ModuleFileIndex))
                        codeAddress.CodeAddresses.LookupSymbolsForModule(symbolReader, moduleFile);
                }
            }
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
            //    args.Add("/Providers:\"ClrPrivate\"");
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

                (int exitCode, string stdOut, string stdErr) = Exec(perfViewExe, args.ToArray(), new (string, string)[] { }, true).Result;

                if (settings.CanAnalyze)
                {
                    var (jitMethods, managedCalls, nativeCalls) = ProcessEtl(etlFilePath + ".zip", true, true, -1, null, Path.Combine(coreRoot, "\\PDB"));
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

                [CommandOption("--start")]
                [Description("Filter events by time-stamps greater-than or equal to this value.")]
                [DefaultValue((double)0)]
                public double Start { get; set; }

                [CommandOption("--end")]
                [Description("Filter events by time-stamps less-than or equal to this value.")]
                [DefaultValue(Double.MaxValue)]
                public double End { get; set; }

                [CommandOption("--hide-meth-sig")]
                [Description("Hide method signatures.")]
                public bool CanHideMethodSignature { get; set; }
            }

            public override int Execute(CommandContext context, Settings settings)
            {
                var range = new TimeStampRange();
                range.Start = settings.Start;
                range.End = settings.End;
                var (jitMethods, managedCalls, nativeCalls) = ProcessEtl(settings.EtlFilePath, settings.CanHideMethodSignature, false, settings.ProcessId, range, string.Empty);
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

                    (int exitCode, string stdOut, string stdErr) = Exec(corerunExe, args.ToArray(), envVars.ToArray(), false).Result;

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

                        (int exitCode2, string stdOut2, string stdErr2) = Exec(mcsExe, mcsExeArgs, new (string, string)[] { }, false).Result;
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
                config.AddCommand<TraceCommand>("trace");
                config.AddCommand<AnalyzeEtlCommand>("analyze-etl");
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