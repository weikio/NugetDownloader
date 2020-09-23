using System;

namespace Weikio.NugetDownloader
{
    public class PackageNotFoundException : Exception
    {
        public PackageNotFoundException(string message)
            : base(message)
        {
        }
    }
}
