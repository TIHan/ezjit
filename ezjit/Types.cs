using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EzJit
{
    sealed record EzJitConfiguration(string RuntimeRepoPath, string FuzzlynRepoPath, string PerfViewExePath);

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

    sealed class TimeStampRange
    {
        public double Start { get; set; }
        public string StartEventName { get; set; }

        public double End { get; set; }
        public string EndEventName { get; set; }
    }
}
