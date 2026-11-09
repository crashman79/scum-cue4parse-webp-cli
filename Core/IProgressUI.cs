using System;

namespace CUE4ParseCLI.Core
{
    /// <summary>
    /// Shared interface for progress reporting across GUI and TUI implementations
    /// All interface-related code should implement this, while extraction logic uses only this interface
    /// </summary>
    public interface IProgressUI : IDisposable
    {
        // Statistics properties
        long Progress { get; set; }
        long TotalFiles { get; set; }
        long JsonCount { get; set; }
        long TextureSuccess { get; set; }
        long TextureTotal { get; set; }
        long MeshCount { get; set; }
        long SoundCount { get; set; }
        long ErrorCount { get; set; }
        double Rate { get; set; }
        TimeSpan ETA { get; set; }
        TimeSpan Elapsed { get; set; }
        double MemoryGB { get; set; }
        double DiskSpaceGB { get; set; }
        int GcTriggers { get; set; }
        bool ResumeMode { get; set; }
        long EstimatedTotalOutputs { get; set; }
        bool HasEstimate { get; set; }
        long JsonSkipped { get; set; }
        long TextureSkipped { get; set; }
        long MeshSkipped { get; set; }
        long SoundSkipped { get; set; }
        string OutputDirectory { get; set; }
        string StatusMessage { get; set; }
        
        // Async WebP conversion (optional - some UIs may not need this)
        long WebPQueued { get; set; }
        long WebPCompleted { get; set; }
        bool WebPAsyncEnabled { get; set; }
        
        // Event methods
        void AddExport(string message);
        void AddWarning(string message);
        void AddError(string message);
        
        // Type-specific export methods (for color coding in TUI, etc.)
        void AddJsonExport(string fileName);
        void AddTextureExport(string fileName);
        void AddMeshExport(string fileName);
        void AddSoundExport(string fileName);
        void AddIconExport(string fileName);
        
        // Update/refresh UI
        void Update();
        void RequestUpdate();
        void Stop();
        
        // Cancellation support (for GUI)
        System.Threading.CancellationTokenSource? CancellationSource { get; set; }
    }
}