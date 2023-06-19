using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EzJit
{
    static class EtlProcessing
    {
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

            // Pre-process to get time-stamps for the first occurence of the start and/or end events.
            if (timeStampRange != null && !string.IsNullOrWhiteSpace(timeStampRange.StartEventName) || !string.IsNullOrWhiteSpace(timeStampRange.EndEventName))
            {
                using (var source = new ETWTraceEventSource(etlFilePath))
                {
                    source.Dynamic.All += AllEvents;
                    source.Process();
                }
            }

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

            void AllEvents(TraceEvent obj)
            {
                if (!string.IsNullOrWhiteSpace(timeStampRange.StartEventName) && timeStampRange.Start == 0 && obj.FullEventName() == timeStampRange.StartEventName)
                {
                    timeStampRange.Start = obj.TimeStampRelativeMSec;
                }

                if (!string.IsNullOrWhiteSpace(timeStampRange.EndEventName) && timeStampRange.End == double.MaxValue && obj.FullEventName() == timeStampRange.EndEventName)
                {
                    timeStampRange.End = obj.TimeStampRelativeMSec;
                }
            }

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

        public static (List<JitMethodData>, List<MethodCallData> managedCalls, List<MethodCallData> nativeCalls) ProcessEtl(string etlOrEtlZipFilePath, bool canHideMethodSignature, bool isCoreRun, int processId, TimeStampRange timeStampRange, string pdbDir)
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
    }
}
