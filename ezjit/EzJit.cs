using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EzJit
{
    static class EzJit
    {
        public static string EzJitExeDirectory
        {
            get
            {
                return Path.GetDirectoryName(typeof(Program).Assembly.Location);
            }
        }

        public static string ScratchDirectory
        {
            get
            {
                return Path.Combine(EzJitExeDirectory, ".ezjit_scratch");
            }
        }

        public static string GetConfigurationPath()
        {
            return Path.Combine(EzJitExeDirectory, "ezjit.config.json");
        }

        public static string CreateScratchDirectory()
        {
            var scratchPath = ScratchDirectory;
            try
            {
                Directory.CreateDirectory(scratchPath);
            }
            catch { }
            return scratchPath;
        }

        public static void ClearScratchDirectory()
        {
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(ScratchDirectory))
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

        public const int NumberOfMethodsToProcess = 10000;
        public const int NumberOfMethodsToPrint = 100;
    }
}
