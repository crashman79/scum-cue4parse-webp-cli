using System;
using System.Linq;

namespace CUE4ParseCLI
{
    /// <summary>
    /// TUI Entry Point - Forces TUI mode for testing
    /// 
    /// To use this entry point, set StartupObject in csproj:
    ///   &lt;StartupObject&gt;CUE4ParseCLI.ProgramTUI&lt;/StartupObject&gt;
    /// 
    /// Or just run normally (TUI is default for interactive terminals)
    /// </summary>
    /// <summary>
    /// TUI Entry Point - Forces TUI mode
    /// Build with: -p:StartupObject=CUE4ParseCLI.ProgramTUI
    /// </summary>
    class ProgramTUI
    {
        static void Main(string[] args)
        {
            // Force TUI mode by adding --tui flag (overrides auto-detection)
            // Remove any conflicting flags first
            var cleanedArgs = args.Where(a => a != "--gui" && a != "--tui" && a != "--no-ui").ToArray();
            var tuiArgs = new[] { "--tui" }.Concat(cleanedArgs).ToArray();
            
            // Call the main Program's Run method with --tui flag
            Program.Run(tuiArgs);
        }
    }
}
