using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

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
        private static int _failures;

        private static int Main(string[] args)
        {
            string? gameDir = args.Length > 0 ? args[0] : null;
            if (gameDir == null)
            {
                string? home = Environment.GetEnvironmentVariable("HOME");
                gameDir = Path.Combine(home ?? ".", ".local", "share", "Steam", "steamapps", "common", "7 Days to Die Dedicated Server");
            }

            string managed = Path.Combine(gameDir, "7DaysToDieServer_Data", "Managed");
            string asmCSharp = Path.Combine(managed, "Assembly-CSharp.dll");
            if (!File.Exists(asmCSharp))
            {
                Console.Error.WriteLine("FAIL: Assembly-CSharp.dll not found under " + managed);
                return 2;
            }

            using var pe = new PEReader(File.OpenRead(asmCSharp));
            var md = pe.GetMetadataReader();

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
                CheckMethod(md, t, "SetDead", "void()", isStatic: false);
                CheckMethod(md, t, "IsDead", "bool()", isStatic: false);
                CheckMethod(md, t, "DamageEntity", "int(DamageSource, int, bool, float)", isStatic: false);
            });

            CheckType(md, "EntityFactory", t =>
            {
                CheckMethod(md, t, "SetupEntityCreationData", "EntityCreationData(int, Vector3)", isStatic: true);
                CheckMethod(md, t, "CreateEntity", "Entity(EntityCreationData)", isStatic: true);
            });

            CheckType(md, "EntityClass", t =>
            {
                CheckMethod(md, t, "FromString", "int(string)", isStatic: true);
            });

            CheckType(md, "GameTimer", t =>
            {
                CheckProperty(md, t, "Instance", isStatic: true);
                CheckFieldOrProperty(md, t, "ticks", isStatic: false);
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

            CheckType(md, "SdtdConsole", t =>
            {
                CheckMethod(md, t, "Output", "void(string)", isStatic: false);
                CheckMethod(md, t, "ExecuteSync", "List`1<string>(string, ClientInfo)", isStatic: false);
            });

            // SdtdConsole is reached through the Unity singleton pattern.
            CheckType(md, "SingletonMonoBehaviour`1", t =>
            {
                CheckFieldOrProperty(md, t, "Instance", isStatic: true);
            });

            CheckType(md, "IModApi", t =>
            {
                Console.WriteLine("  IModApi found at " + FullName(md, t) + " (" + TypeKind(md, t) + ")");
            });

            CheckEnumMember(md, "EChatType", "Global");
            CheckEnumMember(md, "EMessageSender", "Server");
            CheckEnumMember(md, "BbCodeSupportMode", "NotSupported");

            // The game logger lives in LogLibrary.dll, not Assembly-CSharp.
            string logLibrary = Path.Combine(managed, "LogLibrary.dll");
            if (File.Exists(logLibrary))
            {
                using var peLog = new PEReader(File.OpenRead(logLibrary));
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
                Console.WriteLine("RESULT: all required targets present");
                return 0;
            }
            Console.WriteLine("RESULT: " + _failures + " required target(s) missing");
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
                string sig = DecodeSignature(md, m);
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

        private static string TypeKind(MetadataReader md, TypeDefinition t)
        {
            return (t.Attributes & TypeAttributes.Interface) != 0 ? "interface" : "class";
        }

        private static void Fail(string what)
        {
            _failures++;
            Console.WriteLine("  FAIL: " + what);
        }

        /// <summary>
        /// Decodes a method signature into a compact "Name(T1, T2, ...)"
        /// string using type names only. Covers the primitive and class
        /// types the bridge targets; unknown types are rendered as "?".
        /// </summary>
        private static string DecodeSignature(MetadataReader md, MethodDefinition m)
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
