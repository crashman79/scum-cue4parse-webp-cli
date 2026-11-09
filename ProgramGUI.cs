using System;
using System.Linq;

namespace CUE4ParseCLI
{
    /// <summary>
    /// GUI Entry Point - Forces GUI mode for testing
    /// 
    /// To use this entry point, set StartupObject in csproj:
    ///   &lt;StartupObject&gt;CUE4ParseCLI.ProgramGUI&lt;/StartupObject&gt;
    /// 
    /// Or run with: dotnet run -- --gui [other args]
    /// </summary>
    /// <summary>
    /// GUI Entry Point - Forces GUI mode
    /// Build with: -p:StartupObject=CUE4ParseCLI.ProgramGUI
    /// </summary>
    class ProgramGUI
    {
        static void Main(string[] args)
        {
            // Force GUI mode by prepending --gui flag if not already present
            var cleanedArgs = args.Where(a => a != "--gui" && a != "--tui" && a != "--no-ui").ToArray();
            var guiArgs = new[] { "--gui" }.Concat(cleanedArgs).ToArray();
            
            // Call the main Program's Run method with GUI flag
            Program.Run(guiArgs);
        }
    }
}
