using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace TargetCheck
{
    /// <summary>
    /// Validates the Harmony targets and API surface the GameBridge mod
    /// depends on, against the installed dedicated server's Managed folder.
    /// Also prints the detected game version. Exits non-zero when any
    /// required target is missing, so CI and the Makefile can gate on it.
    ///
    /// Uses System.Reflection.Metadata (no code execution, no assembly
    /// loading), so Unity assemblies are safe to inspect on any platform.
    /// </summary>
    internal static class Program
    {
        private const string Usage =
            "usage: targetcheck [GAME_DIR]\n" +
            "\n" +
            "Validates the Harmony targets and API surface the GameBridge mod\n" +
            "depends on against a 7 Days to Die dedicated server install.\n" +
            "\n" +
            "Arguments:\n" +
            "  GAME_DIR        install to inspect; defaults to the Steam path\n" +
            "                  $HOME/.local/share/Steam/steamapps/common/7 Days to Die\n" +
            "                  Dedicated Server (USERPROFILE on Windows)\n" +
            "\n" +
            "Options:\n" +
            "  -h, --help      show this help and exit\n" +
            "\n" +
            "Exit codes:\n" +
            "  0  every required target is present\n" +
            "  1  at least one required target is missing\n" +
            "  2  usage error, or no server install found under GAME_DIR\n" +
            "\n" +
            "The found-target report goes to stdout, failures to stderr.\n";

        private static int _failures;

        private static int Main(string[] args)
        {
            if (args.Any(a => a == "-h" || a == "--help"))
            {
                // Help is the only argument it accepts. Answering it while
                // swallowing a mistyped flag would report the usage text and
                // exit 0 for "targetcheck --bogus --help", where every tool
                // in tools/ rejects the unknown option with exit 2.
                if (args.Length > 1)
                {
                    Console.Error.Write("targetcheck: --help takes no other argument\n" + Usage);
                    return 2;
                }
                Console.Write(Usage);
                return 0;
            }
            if (args.Length > 1)
            {
                Console.Error.Write("targetcheck: expected at most one GAME_DIR argument\n" + Usage);
                return 2;
            }
            if (args.Length == 1 && args[0].StartsWith("-", StringComparison.Ordinal))
            {
                // A mistyped flag is not a path: reporting it as a missing
                // install would send the reader looking for a directory named
                // after the flag. -h/--help is handled above, so anything left
                // starting with "-" is unknown.
                Console.Error.Write("targetcheck: unknown option: " + args[0] + "\n" + Usage);
                return 2;
            }

            string? gameDir = args.Length > 0 ? args[0] : null;
            if (gameDir == null)
            {
                gameDir = Path.Combine(DefaultSteamRoot(), "7 Days to Die Dedicated Server");
            }

            string managed = Path.Combine(gameDir, "7DaysToDieServer_Data", "Managed");
            string asmCSharp = Path.Combine(managed, "Assembly-CSharp.dll");
            if (!File.Exists(asmCSharp))
            {
                // Prefixed with the tool name, not FAIL: a missing install is
                // a usage error, and a "FAIL:" line means a target check ran
                // and did not find what the bridge binds to.
                Console.Error.WriteLine("targetcheck: Assembly-CSharp.dll not found under " + managed);
                Console.Error.WriteLine("targetcheck: pass the dedicated server install as GAME_DIR " +
                                        "(make bridge-check GAME_DIR=...), or install the server first");
                return 2;
            }

            PEReader? reader = OpenAssembly(asmCSharp);
            if (reader == null)
            {
                return 2;
            }
            using (reader)
            {
                // Reading the metadata block, and every check below that
                // walks it, throws on a malformed image or metadata heap
                // rather than returning partial data. A corrupt game assembly
                // must fail as a documented usage error (exit 2) instead of a
                // .NET stack trace.
                try
                {
                    return RunChecks(reader.GetMetadataReader(), managed, asmCSharp);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("targetcheck: cannot read the game assemblies under " + managed +
                                            ": " + ex.Message);
                    return 2;
                }
            }
        }

        private static int RunChecks(MetadataReader md, string managed, string asmCSharp)
        {
            ReportGameVersion(md, asmCSharp);

            CheckType(md, "GameManager", t =>
            {
                CheckMethod(md, t, "Update", "void()", isStatic: false);
                CheckProperty(md, t, "IsDedicatedServer", isStatic: true);
                CheckFieldOrProperty(md, t, "Instance", isStatic: true);
                CheckProperty(md, t, "World", isStatic: false);
                CheckMethod(md, t, "ChatMessageServer", "void(ClientInfo, EChatType, int, string, List`1<int>, EMessageSender, BbCodeSupportMode)", isStatic: false);
                CheckMethod(md, t, "RequestToSpawnPlayer", "void(ClientInfo, int, PlayerProfile, int)", isStatic: false);
            });

            CheckType(md, "ClientInfo", t =>
            {
                CheckFieldOrProperty(md, t, "playerName", isStatic: false);
                CheckFieldOrProperty(md, t, "entityId", isStatic: false);
            });

            // Bot servant entity APIs (BotServant.cs).
            CheckType(md, "World", t =>
            {
                CheckFieldOrProperty(md, t, "Entities", isStatic: false);
                CheckMethod(md, t, "GetEntity", "Entity(int)", isStatic: false);
                CheckMethod(md, t, "SpawnEntityInWorld", "void(Entity)", isStatic: false);
                CheckMethod(md, t, "GetWorldTime", "ulong()", isStatic: false);
            });

            CheckType(md, "Entity", t =>
            {
                CheckFieldOrProperty(md, t, "entityId", isStatic: false);
                CheckFieldOrProperty(md, t, "position", isStatic: false);
                CheckMethod(md, t, "SetPosition", "void(Vector3, bool)", isStatic: false);
                CheckMethod(md, t, "SetRotation", "void(Vector3)", isStatic: false);
            });

            CheckType(md, "EntityAlive", t =>
            {
                CheckProperty(md, t, "Health", isStatic: false);
                CheckFieldOrProperty(md, t, "equipment", isStatic: false);
                CheckFieldOrProperty(md, t, "Buffs", isStatic: false);
                CheckMethod(md, t, "SetDead", "void()", isStatic: false);
                CheckMethod(md, t, "IsDead", "bool()", isStatic: false);
                CheckMethod(md, t, "DamageEntity", "int(DamageSource, int, bool, float)", isStatic: false);
            });

            // Parachute glide buff (BotServant.ApplyGlideBuff): applied while
            // the glide flag is armed so the landing is safe.
            CheckType(md, "EntityBuffs", t =>
            {
                CheckMethod(md, t, "AddBuff", "BuffStatus(string, int, bool, bool, float)", isStatic: false);
                CheckMethod(md, t, "RemoveBuff", "void(string, int, bool)", isStatic: false);
                CheckMethod(md, t, "HasBuff", "bool(string)", isStatic: false);
            });

            // Parachute glide sense (BotServant.WearsGlider): a worn item
            // whose ItemClass carries the glider tag sets the sense v4
            // wearing_glider bit.
            CheckType(md, "Equipment", t =>
            {
                CheckMethod(md, t, "GetItems", "ItemValue[]()", isStatic: false);
            });

            CheckType(md, "ItemValue", t =>
            {
                CheckMethod(md, t, "IsEmpty", "bool()", isStatic: false);
                CheckProperty(md, t, "ItemClass", isStatic: false);
            });

            CheckType(md, "ItemClass", t =>
            {
                CheckMethod(md, t, "HasAnyTags", "bool(FastTags`1<Global>)", isStatic: false);
            });

            CheckType(md, "EntityFactory", t =>
            {
                // BotServant spawns through this exact overload.
                CheckMethod(md, t, "CreateEntity", "Entity(int, Vector3, Vector3)", isStatic: true);
            });

            CheckType(md, "EntityClass", t =>
            {
                CheckMethod(md, t, "FromString", "int(string)", isStatic: true);
            });

            CheckType(md, "ConsoleCmdAbstract", t =>
            {
                // The bridge's CmdWasm overrides these exact (lowercase) names;
                // the PascalCase legacy wrappers on the same class are not used.
                CheckMethod(md, t, "getCommands", "string[]()", isStatic: false);
                CheckMethod(md, t, "getDescription", "string()", isStatic: false);
                CheckMethod(md, t, "getHelp", "string()", isStatic: false);
                CheckMethod(md, t, "Execute", "void(List`1<string>, CommandSenderInfo)", isStatic: false);
            });

            CheckType(md, "CommandSenderInfo", t =>
            {
                // CmdWasm names the sender of a load, reload, or unload in the
                // server log, so both the local-console flag and the remote
                // client are load-bearing after a game update.
                CheckFieldOrProperty(md, t, "IsLocalGame", isStatic: false);
                CheckFieldOrProperty(md, t, "RemoteClientInfo", isStatic: false);
            });

            CheckType(md, "SdtdConsole", t =>
            {
                CheckMethod(md, t, "Output", "void(string)", isStatic: false);
            });

            // SdtdConsole is reached through the Unity singleton pattern.
            CheckType(md, "SingletonMonoBehaviour`1", t =>
            {
                CheckFieldOrProperty(md, t, "Instance", isStatic: true);
            });

            CheckType(md, "IModApi", t =>
            {
                Console.WriteLine("  IModApi found at " + FullName(md, t) + " (" + TypeKind(t) + ")");
            });

            CheckEnumMember(md, "EChatType", "Global");
            CheckEnumMember(md, "EMessageSender", "Server");
            CheckEnumMember(md, "BbCodeSupportMode", "NotSupported");

            // The game logger lives in LogLibrary.dll, not Assembly-CSharp.
            string logLibrary = Path.Combine(managed, "LogLibrary.dll");
            if (File.Exists(logLibrary))
            {
                using var peLog = OpenAssembly(logLibrary);
                if (peLog == null)
                {
                    return 2;
                }
                var mdLog = peLog.GetMetadataReader();
                CheckType(mdLog, "Log", t =>
                {
                    CheckMethod(mdLog, t, "Out", "void(string)", isStatic: true);
                    CheckMethod(mdLog, t, "Warning", "void(string)", isStatic: true);
                    CheckMethod(mdLog, t, "Error", "void(string)", isStatic: true);
                });
            }
            else
            {
                Fail("LogLibrary.dll not found under " + managed);
            }

            Console.WriteLine();
            if (_failures == 0)
            {
                Console.Error.WriteLine("RESULT: all required targets present");
                return 0;
            }
            Console.Error.WriteLine("RESULT: " + _failures + " required target(s) missing");
            return 1;
        }

        private static void CheckEnumMember(MetadataReader md, string typeName, string memberName)
        {
            foreach (var handle in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(handle);
                if (md.GetString(t.Name) != typeName)
                {
                    continue;
                }
                bool isEnum = false;
                foreach (var f in t.GetFields())
                {
                    var field = md.GetFieldDefinition(f);
                    if (md.GetString(field.Name) == "value__")
                    {
                        isEnum = true;
                        break;
                    }
                }
                Console.WriteLine("== " + FullName(md, t) + " (enum=" + isEnum + ") ==");
                if (!isEnum)
                {
                    Fail(typeName + " is not an enum");
                    return;
                }
                foreach (var f in t.GetFields())
                {
                    var field = md.GetFieldDefinition(f);
                    if (md.GetString(field.Name) == memberName)
                    {
                        Console.WriteLine("  OK enum member " + memberName);
                        return;
                    }
                }
                var available = new List<string>();
                foreach (var f in t.GetFields())
                {
                    available.Add(md.GetString(md.GetFieldDefinition(f).Name));
                }
                Fail("enum " + typeName + " has no member " + memberName + " (available: " + string.Join(", ", available) + ")");
                return;
            }
            Fail("enum " + typeName + " not found");
        }

        /// <summary>
        /// Steam's library root on this platform: Program Files (x86) under
        /// Windows, the user profile under Linux, and Application Support
        /// under macOS, which keeps its own per-user data there and has no
        /// .local/share. Resolved through the framework's own folder API
        /// rather than an environment variable, so a Windows machine without
        /// HOME still finds its install and the not-found message names a real
        /// path on every platform.
        /// </summary>
        private static string DefaultSteamRoot()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(programFilesX86))
                {
                    return Path.Combine(programFilesX86, "Steam", "steamapps", "common");
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    return Path.Combine(localAppData, "Steam", "steamapps", "common");
                }
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(
                string.IsNullOrEmpty(home) ? "." : home,
                ".local", "share", "Steam", "steamapps", "common");
        }

        private static void ReportGameVersion(MetadataReader md, string asmPath)
        {
            var name = md.GetAssemblyDefinition();
            var culture = md.GetString(name.Culture);
            Console.WriteLine("Assembly-CSharp: " + md.GetString(name.Name) + " v" + name.Version + " (culture " + culture + ")");
            Console.WriteLine("File: " + asmPath);
            Console.WriteLine();
        }

        private static void CheckType(MetadataReader md, string name, Action<TypeDefinition> body)
        {
            foreach (var handle in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(handle);
                if (md.GetString(t.Name) == name)
                {
                    Console.WriteLine("== " + FullName(md, t) + " ==");
                    body(t);
                    Console.WriteLine();
                    return;
                }
            }
            Fail("type " + name + " not found");
        }

        private static void CheckMethod(MetadataReader md, TypeDefinition type, string name, string expectedSignature, bool isStatic)
        {
            var seen = new List<string>();
            foreach (var handle in type.GetMethods())
            {
                var m = md.GetMethodDefinition(handle);
                if (md.GetString(m.Name) != name)
                {
                    continue;
                }
                bool actualStatic = (m.Attributes & MethodAttributes.Static) != 0;
                if (actualStatic != isStatic)
                {
                    continue;
                }
                string sig = DecodeSignature(m);
                seen.Add(sig);
                if (sig == expectedSignature)
                {
                    Console.WriteLine("  OK " + (isStatic ? "static " : "inst ") + name + sig);
                    return;
                }
            }
            if (seen.Count > 0)
            {
                Fail("no " + (isStatic ? "static " : "inst ") + name + " overload with signature " + expectedSignature + " on " +
                     FullName(md, type) + " (found: " + string.Join("; ", seen) + ")");
                return;
            }
            Fail("method " + name + " (static=" + isStatic + ") not found on " + FullName(md, type));
        }

        private static void CheckProperty(MetadataReader md, TypeDefinition type, string name, bool isStatic)
        {
            foreach (var handle in type.GetProperties())
            {
                var p = md.GetPropertyDefinition(handle);
                if (md.GetString(p.Name) == name)
                {
                    bool ok = true;
                    var accessors = p.GetAccessors();
                    ok &= CheckAccessor(md, accessors.Getter, isStatic);
                    ok &= CheckAccessor(md, accessors.Setter, isStatic);
                    Console.WriteLine("  " + (ok ? "OK " : "MISMATCH ") + (isStatic ? "static " : "inst ") + "property " + name + (ok ? "" : " (static flag mismatch)"));
                    if (!ok)
                    {
                        Fail("property " + name + " static flag mismatch");
                    }
                    return;
                }
            }
            Fail("property " + name + " not found on " + FullName(md, type));
        }

        private static void CheckFieldOrProperty(MetadataReader md, TypeDefinition type, string name, bool isStatic)
        {
            foreach (var handle in type.GetFields())
            {
                var f = md.GetFieldDefinition(handle);
                if (md.GetString(f.Name) == name)
                {
                    bool actualStatic = (f.Attributes & FieldAttributes.Static) != 0;
                    Console.WriteLine("  " + (actualStatic == isStatic ? "OK " : "MISMATCH ") + "field " + name + " (" + (actualStatic ? "static" : "inst") + ")");
                    if (actualStatic != isStatic)
                    {
                        Fail("field " + name + " static flag mismatch");
                    }
                    return;
                }
            }
            CheckProperty(md, type, name, isStatic);
        }

        private static string FullName(MetadataReader md, TypeDefinition t)
        {
            string ns = md.GetString(t.Namespace);
            return ns.Length == 0 ? md.GetString(t.Name) : ns + "." + md.GetString(t.Name);
        }

        private static string TypeKind(TypeDefinition t)
        {
            return (t.Attributes & TypeAttributes.Interface) != 0 ? "interface" : "class";
        }

        /// <summary>
        /// Opens a PE image for metadata-only reading, or reports the real
        /// reason on stderr and returns null. A truncated, corrupt, or
        /// unreadable assembly is a usage-level problem (exit 2), not a crash:
        /// an unhandled BadImageFormatException here would print a .NET stack
        /// trace and exit with a code the Makefile does not document. The
        /// file handle is closed on every path, including a failed reader
        /// construction.
        /// </summary>
        private static PEReader? OpenAssembly(string path)
        {
            FileStream? stream;
            try
            {
                stream = File.OpenRead(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("targetcheck: cannot read " + path + ": " + ex.Message);
                return null;
            }
            try
            {
                return new PEReader(stream);
            }
            catch (Exception ex)
            {
                stream.Dispose();
                Console.Error.WriteLine("targetcheck: " + path + " is not a readable .NET assembly: " +
                                        ex.Message + "; point GAME_DIR at a real dedicated server install");
                return null;
            }
        }

        private static void Fail(string what)
        {
            _failures++;
            Console.Error.WriteLine("  FAIL: " + what);
        }

        /// <summary>
        /// Decodes a method signature into a compact "Name(T1, T2, ...)"
        /// string using type names only. Covers the primitive and class
        /// types the bridge targets; unknown types are rendered as "?".
        /// </summary>
        private static string DecodeSignature(MethodDefinition m)
        {
            var sig = m.DecodeSignature(new SignatureDecoder(), new object());
            return sig.ReturnType + "(" + string.Join(", ", sig.ParameterTypes.ToArray()) + ")";
        }

        private static bool CheckAccessor(MetadataReader md, MethodDefinitionHandle handle, bool isStatic)
        {
            if (handle.IsNil)
            {
                return true;
            }
            var m = md.GetMethodDefinition(handle);
            return (m.Attributes & MethodAttributes.Static) != 0 == isStatic;
        }

        private sealed class SignatureDecoder : ISignatureTypeProvider<string, object>
        {
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
            public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
            public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
            public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetPinnedType(string elementType) => elementType;
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPrimitiveType(PrimitiveTypeCode typeCode)
            {
                switch (typeCode)
                {
                    case PrimitiveTypeCode.Void: return "void";
                    case PrimitiveTypeCode.Boolean: return "bool";
                    case PrimitiveTypeCode.Char: return "char";
                    case PrimitiveTypeCode.SByte: return "sbyte";
                    case PrimitiveTypeCode.Byte: return "byte";
                    case PrimitiveTypeCode.Int16: return "short";
                    case PrimitiveTypeCode.UInt16: return "ushort";
                    case PrimitiveTypeCode.Int32: return "int";
                    case PrimitiveTypeCode.UInt32: return "uint";
                    case PrimitiveTypeCode.Int64: return "long";
                    case PrimitiveTypeCode.UInt64: return "ulong";
                    case PrimitiveTypeCode.Single: return "float";
                    case PrimitiveTypeCode.Double: return "double";
                    case PrimitiveTypeCode.String: return "string";
                    case PrimitiveTypeCode.Object: return "object";
                    default: return "?";
                }
            }

            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                var t = reader.GetTypeDefinition(handle);
                return reader.GetString(t.Name);
            }

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var t = reader.GetTypeReference(handle);
                return reader.GetString(t.Name);
            }

            public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            {
                return "?";
            }
        }
    }
}
