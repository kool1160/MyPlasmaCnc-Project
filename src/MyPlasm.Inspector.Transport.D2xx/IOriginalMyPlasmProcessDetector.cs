using System.Diagnostics;

namespace MyPlasm.Inspector.Transport.D2xx;

public interface IOriginalMyPlasmProcessDetector
{
    bool IsRunning();
}

public sealed class OriginalMyPlasmProcessDetector : IOriginalMyPlasmProcessDetector
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
