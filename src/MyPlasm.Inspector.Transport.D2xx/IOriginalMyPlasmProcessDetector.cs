using System.Diagnostics;

namespace MyPlasm.Inspector.Transport.D2xx;

internal interface IOriginalMyPlasmProcessDetector
{
    bool IsRunning();
}

internal sealed class OriginalMyPlasmProcessDetector : IOriginalMyPlasmProcessDetector
{
    public bool IsRunning()
    {
        Process[] processes = Process.GetProcessesByName("MyPlasmCNC");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}
