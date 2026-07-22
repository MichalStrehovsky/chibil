using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkTests : ChibiTestBase
{
    [Fact]
    public void RejectsUnsupportedLinkOption()
    {
        Chilink.ChilinkException error = Assert.Throws<Chilink.ChilinkException>(
            () => new Chilink.Driver().Run(["/incremental"]));
        Assert.Contains("unsupported option", error.Message);
    }

    [Fact]
    public void RejectsUnsupportedMachine()
    {
        Chilink.ChilinkException error = Assert.Throws<Chilink.ChilinkException>(
            () => new Chilink.Driver().Run(["/machine:x86"]));
        Assert.Contains("supports x64 only", error.Message);
    }

    [Fact]
    public void LinksSingleObject()
    {
        Compile("""
            int main(void) {
                return 42;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void LinksCrossObjectCall()
    {
        Compile("""
            int answer(void);
            int main(void) {
                return answer();
            }
            """)
        .Compile("""
            int answer(void) {
                return 37;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 37);
    }

    [Fact]
    public void OptRefRemovesUnusedFunctionSection()
    {
        LinkResult result = Compile("""
            int dead(void) {
                return 1;
            }
            int main(void) {
                return 0;
            }
            """, ["-ffunction-sections"])
        .Link(["/entry:main", "/subsystem:console", "/opt:ref"]);

        Assert.DoesNotContain("dead", GetMethodNames(result));
        result.RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void OptRefKeepsSharedFunctionSection()
    {
        LinkResult result = Compile("""
            int dead(void) {
                return 1;
            }
            int main(void) {
                return 0;
            }
            """)
        .Link(["/entry:main", "/subsystem:console", "/opt:ref"]);

        Assert.Contains("dead", GetMethodNames(result));
        result.RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void LinksReadOnlyStringLiteralData()
    {
        Compile("""
            int main(void) {
                return "abc"[1];
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 98);
    }

    [Fact]
    public void LinksMutableInitializedGlobal()
    {
        LinkResult result = Compile("""
            int value = 10;
            int main(void) {
                value += 32;
                return value;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"]);

        AssertTransformedGlobal(result, "value");
        Assert.Contains(".cctor", GetMethodNames(result));
        result.RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void LinksZeroInitializedCommonGlobal()
    {
        LinkResult result = Compile("""
            int value;
            int main(void) {
                value = 42;
                return value;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"]);

        AssertTransformedGlobal(result, "value");
        Assert.DoesNotContain(".cctor", GetMethodNames(result));
        result.RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void LinksInitializedDataPointerRelocation()
    {
        LinkResult result = Compile("""
            char data[] = "AB";
            char *value = &data[1];
            int main(void) {
                return *value;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"]);

        AssertTransformedGlobal(result, "data");
        AssertTransformedGlobal(result, "value");
        Assert.Contains(".cctor", GetMethodNames(result));
        result.RunAndCheck(exitCode: 66);
    }

    [Fact]
    public void LinksZeroInitializedBssGlobal()
    {
        Compile("""
            int value;
            int main(void) {
                value = 42;
                return value;
            }
            """, ["-fdata-sections"])
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void LinksInitializedAggregateGlobal()
    {
        Compile("""
            struct Pair {
                int first;
                int second;
            };
            struct Pair value = { 19, 23 };
            int main(void) {
                return value.first + value.second;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void LinksCrossObjectDataRelocation()
    {
        Compile("""
            extern int value;
            int *pointer = &value;
            int main(void) {
                return *pointer;
            }
            """)
        .Compile("""
            int value = 42;
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void LinksInitializedStaticLocal()
    {
        Compile("""
            int next(void) {
                static int value = 40;
                value++;
                return value;
            }
            int main(void) {
                return next() + next() - 40;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 43);
    }

    [Fact]
    public void StrongGlobalDefinitionOverridesCommonDefinition()
    {
        Compile("""
            int value;
            int main(void) {
                return value;
            }
            """)
        .Compile("""
            int value = 42;
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void ReadOnlyStrongDefinitionOverridesCommonDefinition()
    {
        Compile("""
            int value;
            int main(void) {
                return value;
            }
            """)
        .Compile("""
            const int value = 42;
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void RejectsIncompatibleCommonDefinitions()
    {
        Chilink.ChilinkException error = Assert.Throws<Chilink.ChilinkException>(() =>
            Compile("""
                int values[1];
                int main(void) {
                    return values[0];
                }
                """)
            .Compile("""
                int values[2];
                """)
            .Link(["/entry:main", "/subsystem:console"]));

        Assert.Contains("incompatible field definitions", error.Message);
    }

    [Fact]
    public void CrossInputCommonAliasesBindAfterCanonicalFieldsArePlanned()
    {
        Compile("""
            int left = 19;
            int right;
            int sum(void) {
                return left + right;
            }
            """)
        .Compile("""
            int left;
            int right = 23;
            int sum(void);
            int main(void) {
                return sum();
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void RejectsFunctionPointerInitializerRelocation()
    {
        Chilink.ChilinkException error = Assert.Throws<Chilink.ChilinkException>(() =>
            Compile("""
                int target(void) {
                    return 42;
                }
                int (*pointer)(void) = target;
                int main(void) {
                    return pointer();
                }
                """)
            .Link(["/entry:main", "/subsystem:console"]));

        Assert.Contains("function/vtfixup", error.Message);
    }

    [Fact]
    public void FoldsReadOnlyStringLiteralComdats()
    {
        Compile("""
            char *other(void);
            char *local(void) {
                return "shared";
            }
            int main(void) {
                return local() == other() ? 0 : 1;
            }
            """, ["-fdata-sections"])
        .Compile("""
            char *other(void) {
                return "shared";
            }
            """, ["-fdata-sections"])
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 0);
    }

    [Fact]
    public void LinksManagedAggregateMetadata()
    {
        Compile("""
            struct Pair {
                int first;
                int second;
            };

            int main(void) {
                struct Pair value;
                value.first = 19;
                value.second = 23;
                return value.first + value.second;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void LinksAggregateAcrossTranslationUnits()
    {
        LinkResult result = Compile("""
            struct Value {
                int number;
            };
            int read_value(struct Value *value);
            int main(void) {
                struct Value value;
                value.number = 42;
                return read_value(&value);
            }
            """)
        .Compile("""
            struct Value {
                int number;
            };
            int read_value(struct Value *value) {
                return value->number;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"]);

#if FIELD_BACKED_AGGREGATES
        Assert.Equal(1, GetFieldNames(result).Count(name => name == "number"));
#endif
        result.RunAndCheck(exitCode: 42);
    }

    [Fact]
    public void LinksAsm2ObjCrt()
    {
        Compile("""
            int main(int argc, char **argv, char **envp) {
                return argc;
            }
            """)
        .AddCrt()
        .Link(["/entry:mainCRTStartup", "/subsystem:console", "/opt:ref"])
        .RunAndCheck(exitCode: 1);
    }

    [Fact]
    public void LinksMsvcPureObject()
    {
        Compile("""
            int __clrcall answer(void);
            int main(void) {
                return answer();
            }
            """)
        .MsvcPureCompile("""
            extern "C" int answer(void) {
                return 42;
            }
            """)
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }

    private static string[] GetMethodNames(LinkResult result)
    {
        using var stream = File.OpenRead(result.ExePath);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        return metadata.MethodDefinitions
            .Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name))
            .ToArray();
    }

    private static string[] GetFieldNames(LinkResult result)
    {
        using var stream = File.OpenRead(result.ExePath);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        return metadata.FieldDefinitions
            .Select(handle => metadata.GetString(metadata.GetFieldDefinition(handle).Name))
            .ToArray();
    }

    private static void AssertTransformedGlobal(LinkResult result, string name)
    {
        using var stream = File.OpenRead(result.ExePath);
        using var pe = new PEReader(stream);
        MetadataReader metadata = pe.GetMetadataReader();
        FieldDefinition field = metadata.FieldDefinitions
            .Select(metadata.GetFieldDefinition)
            .Single(field => metadata.GetString(field.Name) == name);

        Assert.Equal(0, field.GetRelativeVirtualAddress());
        Assert.False((field.Attributes & FieldAttributes.HasFieldRVA) != 0);
    }
}
