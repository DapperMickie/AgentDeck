using Microsoft.Maui.Platform.Linux;

namespace AgentDeck.Platforms.Linux;

internal class Program
{
    public static void Main(string[] args)
    {
        LinuxApplication.Run(MauiProgram.CreateMauiApp(), args);
    }
}
