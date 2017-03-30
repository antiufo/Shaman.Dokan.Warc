using System;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Shaman.Dokan
{
    class WarcProgram
    {
        static int Main(string[] args)
        {
            var file = args.FirstOrDefault(x => !x.StartsWith("-"));
            if (file == null || !(file.EndsWith(".cdx") || file.EndsWith(".cdx.gz")))
            {
                Console.WriteLine("Must specify a .cdx or .cdx.gz file.");
                return 1;
            }
            var mountpoint = new WarcFs(file).MountSimple(4);
            if (args.Contains("--open"))
                Process.Start(mountpoint);
            new TaskCompletionSource<bool>().Task.Wait();
            return 0;
        }
    }
}
