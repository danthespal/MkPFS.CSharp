// PS5PkgTool oracle runner: builds one package with PS5PkgTool's ProsperoPkgTool engine.
// Usage: PPTOracle <source dir> <output.pkg> <stored|auto> <content id> <passcode> <seed hex>
using System.Globalization;
using PS5PKGTool.Core.Builders;

if (args.Length != 6)
{
    Console.Error.WriteLine("usage: PPTOracle <source dir> <output.pkg> <stored|auto> <content id> <passcode> <seed hex>");
    return 2;
}

try
{
    SonyDebugPackageBuildResult result = await SonyDebugPackageBuilder.CreateFromDirectoryAsync(args[0], args[1], new SonyDebugPackageBuildOptions
    {
        ContentId = args[3],
        Passcode = args[4],
        Seed = Convert.FromHexString(args[5]),
        Compression = args[2] == "stored" ? Ps5InnerCompression.Stored : Ps5InnerCompression.Auto,
        KrakenThreads = 1,
        Log = message => Console.WriteLine("log: " + message),
    });
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ok {result.PackageSize}"));
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"error {ex.GetType().Name}: {ex.Message}");
    return 1;
}
