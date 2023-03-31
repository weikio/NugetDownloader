using System;
using System.Threading.Tasks;
using NuGet.Common;

namespace Weikio.NugetDownloader
{
    public class ConsoleLogger : LoggerBase
    {
        public override void Log(ILogMessage message)
        {
            switch (message.Level)
            {
                case LogLevel.Debug:
                    Console.WriteLine($"DEBUG - {message}");
                    break;

                case LogLevel.Verbose:
                    Console.WriteLine($"VERBOSE - {message}");
                    break;

                case LogLevel.Information:
                    Console.WriteLine($"INFORMATION - {message}");
                    break;

                case LogLevel.Minimal:
                    Console.WriteLine($"MINIMAL - {message}");
                    break;

                case LogLevel.Warning:
                    Console.WriteLine($"WARNING - {message}");
                    break;

                case LogLevel.Error:
                    Console.WriteLine($"ERROR - {message}");
                    break;
                
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);

            return Task.CompletedTask;
        }
    }
}
