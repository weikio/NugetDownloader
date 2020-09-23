using System;
using NuGet.Common;
using NuGet.Configuration;

namespace Weikio.NugetDownloader
{
    public class MachineWideSettings : IMachineWideSettings
    {
        private readonly Lazy<ISettings> _settings;

        public MachineWideSettings()
        {
            _settings = new Lazy<ISettings>(() =>
            {
                var baseDirectory = NuGetEnvironment.GetFolderPath(NuGetFolderPath.MachineWideConfigDirectory);
                return global::NuGet.Configuration.Settings.LoadMachineWideSettings(baseDirectory);
            });
        }

        public ISettings Settings => _settings.Value;
    }
}
