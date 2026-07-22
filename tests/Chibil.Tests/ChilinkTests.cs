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
}
