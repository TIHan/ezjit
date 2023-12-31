﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration.Attributes;

namespace EzJit
{
    sealed record EzJitConfiguration(string RuntimeRepoPath, string FuzzlynRepoPath, string PerfViewExePath);

    sealed class JitMethodData
    {
        [Index(0)]
        [Name("name")]
        public string FullyQualifiedName { get; set; }

        [Ignore]
        public double StartTime { get; set; }

        [Ignore]
        public double EndTime { get; set; }

        [Index(1)]
        [Name("time")]
        public double Time
        {
            get
            {
                return EndTime - StartTime;
            }
        }

        [Index(2)]
        [Name("codegen-size")]
        public double CodeGenSize { get; set; }

        [Index(3)]
        [Name("tier")]
        public string Tier { get; set; }

        [Ignore]
        public bool IsValid { get { return Time != 0; } }
    }

    sealed class JitMethodDataDiff
    {
        [Index(0)]
        [Name("name")]
        public string FullyQualifiedName { get; set; }

        [Index(1)]
        [Name("time")]
        public double TimeDiff { get; set; }

        [Index(2)]
        [Name("codegen-size")]
        public double CodeGenSizeDiff { get; set; }

        [Index(3)]
        [Name("tier")]
        public string Tier { get; set; }
    }

    sealed class MethodCallData
    {
        [Index(0)]
        [Name("name")]
        public string Name { get; set; }

        [Index(1)]
        [Name("excp")]
        public double ExclusivePercent { get; set; }

        [Index(2)]
        [Name("exc")]
        public int ExclusiveCount { get; set; }

        [Index(3)]
        [Name("incp")]
        public double InclusivePercent { get; set; }

        [Index(4)]
        [Name("inc")]
        public int InclusiveCount { get; set; }

        [Ignore]
        public bool IsValid { get { return ExclusivePercent != 0 && ExclusiveCount != 0 && InclusiveCount != 0 && InclusivePercent != 0; } }
    }

    sealed class MethodCallDataDiff
    {
        [Index(0)]
        [Name("name")]
        public string Name { get; set; }

        [Index(1)]
        [Name("excp")]
        public double ExclusivePercentDiff { get; set; }

        [Index(2)]
        [Name("exc")]
        public double ExclusiveCountDiff { get; set; }

        [Index(3)]
        [Name("incp")]
        public double InclusivePercentDiff { get; set; }

        [Index(4)]
        [Name("inc")]
        public double InclusiveCountDiff { get; set; }

        [Index(5)]
        [Name("time-spent")]
        public double TimeSpent { get; set; }
    }

    sealed class TimeStampRange
    {
        public double Start { get; set; }
        public string StartEventName { get; set; }

        public double End { get; set; }
        public string EndEventName { get; set; }

        public bool IsDefault
        {
            get
            {
                return this.Start == 0 && this.End == double.MaxValue;
            }
        }
    }
}
