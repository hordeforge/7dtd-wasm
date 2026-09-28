using System.Collections.Generic;

/// <summary>
/// Stand-in for the game's global <c>Log</c>, which the bridge sources use
/// unqualified. The net8 test project has no game assembly, so the bridge
/// files linked into it (see HordeForge.WasmHost.Tests.csproj) need one;
/// the lines are collected so a test can assert on what was reported.
/// </summary>
internal static class Log
{
    private static readonly List<string> Lines = new List<string>();

    public static IReadOnlyList<string> Captured => Lines;

    public static void Clear() => Lines.Clear();

    public static void Out(string message) => Lines.Add("OUT " + message);

    public static void Warning(string message) => Lines.Add("WARN " + message);

    public static void Error(string message) => Lines.Add("ERR " + message);
}
