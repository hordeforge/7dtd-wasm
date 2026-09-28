using System.Collections.Generic;
using Xunit;

/// <summary>
/// Tests that assert on what the code under test wrote to the log shim.
/// xUnit runs test classes in parallel, and the shim's warning list is
/// process-wide, so a class that reads it has to run alone rather than
/// clear state another class may be asserting on.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedLogCollection
{
    public const string Name = "shared log shim";
}

/// <summary>
/// Stand-in for the game's LogLibrary.Log, the one game type
/// WasmSettingsProvider touches. The net48 bridge gets the real one from
/// the dedicated install; the net8 suite has no game assemblies, so the
/// warning the provider emits on a failed shared-file reload is captured
/// here instead of being written to a console nobody reads.
/// </summary>
internal static class Log
{
    internal static List<string> Warnings { get; } = new List<string>();

    internal static void Warning(string message)
    {
        Warnings.Add(message);
    }
}
