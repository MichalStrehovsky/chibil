using System.Buffers.Binary;
using System.Reflection.Metadata.Ecma335;
using Coff;
using Xunit;

namespace Chibil.Tests;

public sealed class ChilinkGlobalTests : ChibiTestBase
{
    private static readonly string[] ConsoleMain = ["/entry:main", "/subsystem:console"];

    [Theory]
    [InlineData("int value;")]
    [InlineData("int value = 0;")]
    [InlineData("int value = 42;")]
    [InlineData("int value[2] = { 20, 22 };")]
    [InlineData("const int target = 42; const int *const value = &target;")]
    public void AddressTakenUnannotatedGlobalsCannotBeConverted(string source)
    {
        var builder = Compile(source + " void *address(void) { return &value; } int main(void) { return address() != 0; }",
            ["-fdata-sections", "-fno-fixed-address-statics"]);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.Contains("FixedAddressValueTypeAttribute", error.Message);
        Assert.Contains("value", error.Message);
        Assert.False(File.Exists(Path.Combine(builder._tempDir, CompilationBuilder.ExecutableName)));
    }

    [Theory]
    [InlineData("int value;")]
    [InlineData("int value = 0;")]
    [InlineData("int value = 41;")]
    [InlineData("static int value = 41;")]
    public void UnannotatedValueOnlyGlobalsCanBeConverted(string source)
    {
        Compile(source + " int main(void) { value = 42; return value; }",
            ["-fdata-sections", "-fno-fixed-address-statics"])
            .Link(ConsoleMain).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyDataRelocationTargetsRequireFixedAddresses(bool reverse)
    {
        const string main = """
            extern int values[2];
            int *pointer = &values[1];
            void collect_globals(void);
            int main(void) { collect_globals(); return *pointer; }
            """;
        const string data = "int values[2] = { 20, 42 };";
        Compile(reverse ? data : main, reverse ? null : ["-fno-fixed-address-statics"])
            .Compile(reverse ? main : data, reverse ? ["-fno-fixed-address-statics"] : null)
            .AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link(ConsoleMain).RunAndCheck(42);
    }

    [Fact]
    public void UnannotatedDataRelocationTargetsStillRequireFixedAddresses()
    {
        var builder = Compile("""
            extern int value;
            int *pointer = &value;
            int main(void) { return *pointer; }
            """).Compile("int value = 42;", ["-fno-fixed-address-statics"]);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.Contains("FixedAddressValueTypeAttribute", error.Message);
        Assert.Contains("'value'", error.Message);
    }

    [Fact]
    public void ReadOnlyDataRelocationTargetsDoNotRequireFixedAddresses()
    {
        Compile("""
            const int value = 42;
            const int *pointer = &value;
            int main(void) { return *pointer; }
            """, ["-fdata-sections", "-fno-fixed-address-statics"])
            .Link(ConsoleMain).RunAndCheck(42);
    }

    [Theory]
    [InlineData("/OPT:REF", false)]
    [InlineData("/OPT:NOREF", true)]
    public void DiscardedAddressTakingDoesNotRequireFixedAddresses(string option, bool retained)
    {
        var builder = Compile("""
            int value = 42;
            int *unused(void) { return &value; }
            int main(void) { return value; }
            """, ["-ffunction-sections", "-fdata-sections", "-fno-fixed-address-statics"]);
        if (!retained)
            builder.Link(["/entry:main", option]).RunAndCheck(42);
        else
        {
            var error = Assert.Throws<InvalidOperationException>(() => builder.Link(["/entry:main", option]));
            Assert.Contains("FixedAddressValueTypeAttribute", error.Message);
            Assert.Contains("'value'", error.Message);
        }
    }

    [Fact]
    public void UnannotatedReadOnlyAndDiscardedGlobalsDoNotRequireConversion()
    {
        Compile("""
            const int value = 42;
            int unused = 1;
            int main(void) { const int *pointer = &value; return *pointer; }
            """, ["-fdata-sections", "-fno-fixed-address-statics"])
            .Link(["/entry:main", "/OPT:REF", "/include:value"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompilerSwitchControlsFixedAddressAnnotations(bool enabled)
    {
        var builder = Compile("""
            int common;
            int value = 20;
            struct Pair { int first, second; };
            struct Pair pair = { 10, 12 };
            int array[2] = { 20, 22 };
            int main(void) {
                static int local = 42;
                return local + common + value + pair.first + array[0] - 50;
            }
            """, enabled
                ? ["-fno-fixed-address-statics", "-ffixed-address-statics"]
                : ["-ffixed-address-statics", "-fno-fixed-address-statics", "-fno-uco-entrypoints"]);
        if (enabled)
            builder.Link(ConsoleMain).RunAndCheck(42);
        else
        {
            var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
            Assert.Contains("FixedAddressValueTypeAttribute", error.Message);
            builder.MsvcLink(ConsoleMain).RunAndCheck(42);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergedGlobalsAcceptMixedFixedAddressAnnotations(bool reverse)
    {
        const string annotated = "int value; int main(void) { return value; }";
        const string unannotated = "int value = 42;";
        Compile(reverse ? unannotated : annotated,
                reverse ? ["-fno-fixed-address-statics"] : null)
            .Compile(reverse ? annotated : unannotated,
                reverse ? null : ["-fno-fixed-address-statics"])
            .Link(ConsoleMain).RunAndCheck(42);
    }

    [Fact]
    public void FixedAddressAttributeDoesNotRequireCoreLibraryIdentity()
    {
        var builder = Compile("int value = 42; int main(void) { return value; }");
        byte[] bytes = File.ReadAllBytes(builder._objFiles[0]);
        using (var coff = new CoffReader(new MemoryStream(bytes)))
        {
            var md = coff.GetMetadataReader();
            var core = md.AssemblyReferences.Select(md.GetAssemblyReference).Single(reference =>
                md.GetString(reference.Name) == (builder._netCore ? "System.Runtime" : "mscorlib"));
            int offset = coff.CoffHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.String) +
                MetadataTokens.GetHeapOffset(core.Name);
            bytes[offset] = (byte)'X';
        }
        File.WriteAllBytes(builder._objFiles[0], bytes);
        builder.Link(ConsoleMain).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroGlobalsAreZeroInitializedAndMutable(bool dataSections)
    {
        Compile("""
            int common;
            int zero = 0;
            static long long local;
            int array[4];
            int main(void) {
                if (common || zero || local || array[3]) return 1;
                if ((unsigned long long)&local % 8) return 2;
                common = 10; zero = 11; local = 12; array[3] = 9;
                return common + zero + (int)local + array[3];
            }
            """, dataSections ? ["-fdata-sections"] : null).Link(ConsoleMain).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializedGlobalsPreserveBytesAndRemainMutable(bool dataSections)
    {
        Compile("""
            unsigned char prefix = 255;
            short negative = -1234;
            unsigned long long wide = 0xfedcba9876543210ULL;
            float single = 1.5f;
            double negative_zero = -0.0;
            int values[4] = { 1, 2, 3, 4 };
            struct __attribute__((packed)) Packed { char tag; int value; };
            struct Packed packed = { 7, 35 };
            union Bits { unsigned int word; unsigned char bytes[4]; };
            union Bits bits = { 0x12345678 };
            int main(void) {
                if (prefix != 255 || negative != -1234) return 1;
                if (wide != 0xfedcba9876543210ULL || single != 1.5f) return 2;
                if (*(unsigned long long*)&negative_zero != 0x8000000000000000ULL) return 3;
                if (bits.word != 0x12345678 || bits.bytes[0] != 0x78) return 4;
                if (values[0] != 1 || values[1] != 2 || values[2] != 3 || values[3] != 4) return 5;
                if (packed.tag != 7 || packed.value != 35) return 6;
                packed.value += packed.tag;
                values[0] = packed.value;
                return values[0];
            }
            """, dataSections ? ["-fdata-sections"] : null).Link(ConsoleMain).RunAndCheck(42);
    }

    [Theory]
    [InlineData(0x7f812345u, 0x7ff0123456789abcUL)]
    [InlineData(0xffc12345u, 0xfff8123456789abcUL)]
    public void InitializedGlobalsPreserveNaNPayloadsAndPadding(uint singleBits, ulong doubleBits)
    {
        var builder = Compile($$"""
            float single = 1.0f;
            double wide = 1.0;
            struct Padded { unsigned char prefix; unsigned int value; unsigned char suffix; };
            struct Padded padded = { 1, 42, 2 };
            int main(void) {
                if (*(unsigned int*)&single != 0x{{singleBits:X8}}U) return 1;
                if (*(unsigned long long*)&wide != 0x{{doubleBits:X16}}ULL) return 2;
                if (padded.prefix != 1 || padded.value != 42 || padded.suffix != 2) return 3;
                unsigned char *bytes = (unsigned char*)&padded;
                for (int i = 1; i < 4; i++)
                    if (bytes[i] != 0xa5 || bytes[i + 8] != 0x5a) return 4;
                return 42;
            }
            """, ["-fdata-sections"]);
        byte[] bytes = File.ReadAllBytes(builder._objFiles[0]);
        using (var coff = new CoffReader(new MemoryStream(bytes)))
        {
            var symbols = coff.GetSymbolTableReader();
            var strings = coff.GetStringTableReader();
            var sections = coff.GetSectionTableReader();
            Span<byte> Data(string name, int size)
            {
                var symbol = symbols.Symbols.Select(symbols.GetCoffSymbol).Single(s =>
                    s.StorageClass == CoffSymbolStorageClass.External && strings.GetString(s.Name) == name);
                var section = sections.GetCoffSection(sections.Sections.ElementAt(symbol.SectionNumber.Number - 1));
                return bytes.AsSpan(checked(section.PointerToRawData + (int)symbol.Value), size);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(Data("single", 4), singleBits);
            BinaryPrimitives.WriteUInt64LittleEndian(Data("wide", 8), doubleBits);
            var padded = Data("padded", 12);
            padded.Slice(1, 3).Fill(0xa5);
            padded.Slice(9, 3).Fill(0x5a);
        }
        File.WriteAllBytes(builder._objFiles[0], bytes);
        builder.Link(ConsoleMain).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DataRelocationsResolveAcrossInputsAndStorageKinds(bool reverse, bool dataSections)
    {
        const string main = """
            extern int target;
            extern int common;
            int *pointer = &target;
            int *zero_pointer = &common;
            char text[] = "hello";
            char *inside = &text[1];
            char *literal = "answer" + 2;
            int *pointers[2] = { &target, &common };
            struct __attribute__((packed)) Packed { char prefix; int *pointer; };
            struct Packed packed = { 1, &target };
            int main(void) {
                if (pointer != &target || pointers[0] != &target) return 1;
                if (zero_pointer != &common || pointers[1] != &common || common) return 2;
                if (*inside != 'e' || *literal != 's' || packed.pointer != pointer) return 3;
                *packed.pointer += 1;
                return *pointer;
            }
            """;
        const string other = "int target = 41; int common;";
        string[] flags = dataSections ? ["-fdata-sections"] : null;
        Compile(reverse ? other : main, flags)
            .Compile(reverse ? main : other, flags)
            .Link(ConsoleMain).RunAndCheck(42);
    }

    [Fact]
    public void RelocatedReadOnlyGlobalsPointAtRuntimeStorage()
    {
        Compile("""
            int value = 41;
            int *const pointer = &value;
            const int readonly_value = 42;
            const int *other = &readonly_value;
            int main(void) { ++*pointer; return *pointer == *other ? 42 : 1; }
            """, ["-fdata-sections"]).Link(ConsoleMain).RunAndCheck(42);
    }

    [Fact]
    public void SelfAndCyclicPointersSurviveInitializationAndGc()
    {
        Compile("""
            void collect_globals(void);
            struct Node { int value; struct Node *next; };
            extern struct Node second;
            struct Node first = { 20, &second };
            struct Node second = { 22, &first };
            void *self = &self;
            int values[2] = { 20, 22 };
            int *end = &values[2];
            int main(void) {
                struct Node *saved = &first;
                int *saved_values = values;
                collect_globals();
                if (saved != &first || saved_values != values) return 3;
                if (self != &self || first.next != &second || second.next != &first) return 1;
                if (end - values != 2) return 2;
                return first.next->value + second.next->value;
            }
            """).AddAsm2ObjAssembly("ChilinkAssembly.dll").Link(ConsoleMain).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CommonDefinitionsCoalesceAndStrongDefinitionsWin(bool reverse, bool strong)
    {
        const string main = """
            int value;
            int read(void);
            int main(void) {
                if (value != read()) return 1;
                value += 2;
                return read();
            }
            """;
        string other = (strong ? "int value = 40;" : "int value;") + "int read(void) { return value; }";
        Compile(reverse ? other : main).Compile(reverse ? main : other)
            .Link(ConsoleMain).RunAndCheck(strong ? 42 : 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputLocalStaticGlobalsRemainDistinct(bool reverse)
    {
        const string main = """
            static int value = 20;
            static int *pointer = &value;
            int *other(void);
            int main(void) {
                if (pointer == other()) return 1;
                return *pointer + *other();
            }
            """;
        const string other = "static int value = 22; int *other(void) { return &value; }";
        Compile(reverse ? other : main, ["-fdata-sections"])
            .Compile(reverse ? main : other, ["-fdata-sections"]).Link(ConsoleMain).RunAndCheck(42);
    }

    [Theory]
    [InlineData("/OPT:REF", false)]
    [InlineData("/OPT:NOREF", true)]
    [InlineData("/OPT:REF /include:pointer", true)]
    public void DiscardedGlobalInitializersDoNotConflictWithExistingModuleInitializer(string options, bool retained)
    {
        var builder = Compile("""
            int target = 41;
            int *pointer = &target;
            int initialized_module(void);
            int main(void) { return initialized_module(); }
            """, ["-fdata-sections"]).AddAsm2ObjAssembly("ChilinkInitializerAssembly.dll");
        string[] args = ["/entry:main", .. options.Split(' ')];
        if (retained)
        {
            var error = Assert.Throws<InvalidOperationException>(() => builder.Link(args));
            Assert.Contains("module initializer", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
            builder.Link(args).RunAndCheck(42);
    }

    [Fact]
    public void RelocationOnlyDependencyIsRetainedUnderOptRef()
    {
        Compile("""
            extern int target;
            int *pointer = &target;
            int main(void) { return *pointer; }
            """, ["-fdata-sections"])
            .Compile("int target = 42;", ["-fdata-sections"])
            .Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Fact]
    public void ChibilGlobalsInteroperateWithMsvcConsumers()
    {
        const string data = "int target = 41; int zero; int *pointer = &target;";
        const string main = """
            extern int target, zero;
            extern int *pointer;
            int main(void) { if (zero || pointer != &target) return 1; return ++*pointer; }
            """;
        Compile(data).MsvcCompile(main).Link(ConsoleMain).RunAndCheck(42);
    }

    [Fact]
    public void AddressTakenUnannotatedMsvcGlobalsCannotBeConverted()
    {
        var builder = MsvcCompile("int value = 42;")
            .Compile("extern int value; int main(void) { int *pointer = &value; return *pointer; }");
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.Contains("FixedAddressValueTypeAttribute", error.Message);
        Assert.Contains("value", error.Message);
    }

    [Fact]
    public void UnannotatedMsvcValueOnlyGlobalsCanBeConverted()
    {
        MsvcCompile("int value = 42;")
            .Compile("extern int value; int main(void) { return value; }")
            .Link(ConsoleMain).RunAndCheck(42);
    }

    [Fact]
    public void GlobalInitializationWorksWithCrtStartupAndNamedTypeConstructors()
    {
        Compile("""
            int managed_static(void);
            int value = 1;
            int main(int argc, char **argv) { return value + managed_static(); }
            """).AddCrt().AddAsm2ObjAssembly("ChilinkAssembly.dll")
            .Link(["/subsystem:console"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingModuleInitializerConflictsOnlyWhenSynthesisIsNeeded(bool initialized)
    {
        var builder = Compile($$"""
            int initialized_module(void);
            int value{{(initialized ? " = 1" : "")}};
            int main(void) { return value + initialized_module(); }
            """).AddAsm2ObjAssembly("ChilinkInitializerAssembly.dll");
        if (!initialized)
            builder.Link(ConsoleMain).RunAndCheck(42);
        else
        {
            var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
            Assert.Contains("module initializer", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ManagedDllInitializesOnceBeforeFirstCall()
    {
        using var runner = new CompilationBuilder();
        Compile("""
            int value = 40;
            int *pointer = &value;
            int read(void) { return ++*pointer; }
            """).Link(["/dll", "/include:?read@@$$J0YAHXZ",
                "/out:" + Path.Combine(runner._tempDir, "globals.dll")]);
        runner.Compile("int load_global_dll(void); int main(void) { return load_global_dll(); }")
            .AddAsm2ObjAssembly("ChilinkAssembly.dll").Link(ConsoleMain).RunAndCheck(42);
    }

    [Fact]
    public void InitializedDllWithNoInputMethodsInitializesBeforeFieldAccess()
    {
        using var runner = new CompilationBuilder();
        Compile("int value = 42;").Link(["/dll",
            "/out:" + Path.Combine(runner._tempDir, "globals.dll")]);
        runner.Compile("int load_global_dll_value(void); int main(void) { return load_global_dll_value(); }")
            .AddAsm2ObjAssembly("ChilinkAssembly.dll").Link(ConsoleMain).RunAndCheck(42);
    }

    [Fact]
    public void NamedTypeRvaConversionIsStillExcluded()
    {
        var builder = Compile("""
            struct Payload { int value; };
            struct Payload global = { 42 };
            int main(void) { return 42; }
            """);
        byte[] bytes = File.ReadAllBytes(builder._objFiles[0]);
        using (var coff = new CoffReader(new MemoryStream(bytes)))
        {
            var md = coff.GetMetadataReader();
            Assert.True(md.GetTableRowCount(TableIndex.TypeDef) > 1);
            Assert.True(md.GetTableRowCount(TableIndex.Field) < 65536);
            Assert.True(md.GetTableRowCount(TableIndex.MethodDef) < 65536);
            int rowSize = md.GetTableRowSize(TableIndex.TypeDef);
            // Move the module's fields into the first named type.
            int fieldList = coff.CoffHeaders.MetadataStartOffset + md.GetTableMetadataOffset(TableIndex.TypeDef) +
                rowSize * 2 - 4;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(fieldList), 1);
        }
        File.WriteAllBytes(builder._objFiles[0], bytes);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.Contains("named types", error.Message);
    }

    [Fact]
    public void IncompatibleCommonDefinitionsAreRejected()
    {
        var builder = Compile("int value; int main(void) { return value; }").Compile("long long value;");
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.Contains("incompatible storage", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedDataRelocationsFailAtLinkTime(bool wrongKind)
    {
        var builder = Compile("""
            int target = 42;
            int *pointer = &target;
            int main(void) { return *pointer; }
            """, ["-fdata-sections"]);
        byte[] bytes = File.ReadAllBytes(builder._objFiles[0]);
        using (var coff = new CoffReader(new MemoryStream(bytes)))
        {
            var sections = coff.GetSectionTableReader();
            var data = sections.Sections.Select(sections.GetCoffSection).Single(s =>
                s.NumberOfRelocations == 1 &&
                coff.GetRelocationTableReader(s).GetRelocation(0).Type == ImageRelocation.Amd64_ADDR64);
            if (wrongKind)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(data.PointerToRelocations + 8), (ushort)ImageRelocation.Amd64_ADDR32NB);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(data.PointerToRelocations), (uint)data.SizeOfRawData - 1);
        }
        File.WriteAllBytes(builder._objFiles[0], bytes);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.Contains(wrongKind ? "unsupported relocation" : "exceeds its storage", error.Message);
    }

    [Fact]
    public void FieldCoveredByVtfixupIsNotConverted()
    {
        var builder = Compile("long long value = 42; int main(void) { return (int)value; }",
            ["-fno-uco-entrypoints"]);
        byte[] bytes = File.ReadAllBytes(builder._objFiles[0]);
        using (var coff = new CoffReader(new MemoryStream(bytes)))
        {
            var symbols = coff.GetSymbolTableReader();
            var strings = coff.GetStringTableReader();
            var value = symbols.Symbols.Single(h => strings.GetString(symbols.GetCoffSymbol(h).Name) == "value");
            var sections = coff.GetSectionTableReader();
            var fixup = sections.Sections.Select(sections.GetCoffSection)
                .First(s => strings.GetString(s.Name) == ".rdata$ilfixup");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fixup.PointerToRelocations + 4), (uint)value.Index);
        }
        File.WriteAllBytes(builder._objFiles[0], bytes);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.Contains("vtfixup", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldSymbolDataRelocationsPreserveGlobalBoundaryPointers(bool dataSections)
    {
        Compile("""
            int second = 22;
            int first = 20;
            char *end = (char *)&first + sizeof(first);
            int *start = &second;
            int main(void) {
                if (end != (char *)&first + sizeof(first)) return 1;
                if (start != &second) return 2;
                return first + *start;
            }
            """, dataSections ? ["-fdata-sections"] : null)
            .Link(ConsoleMain).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ManagedVtableSlotsUseManagedFunctionPointers(bool isStatic, bool dataSections)
    {
        Compile($$"""
            {{(isStatic ? "static " : "")}}int __clrcall target(int value) { return value + 1; }
            int (__clrcall *pointer)(int) = target;
            void *glob = target;
            void **indirect = &glob;
            int main(void) {
                if (glob != (void *)target || *indirect != (void *)target) return 1;
                return pointer(41);
            }
            """, dataSections ? ["-fdata-sections", "-ffunction-sections"] : null)
            .Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ManagedVtableSlotsResolveAcrossInputs(bool reverse, bool dataSections)
    {
        const string main = """
            int __clrcall target(int value);
            int (__clrcall *pointer)(int) = target;
            void *glob = target;
            int main(void) {
                if (glob != (void *)pointer) return 1;
                return pointer(41);
            }
            """;
        const string definition = "int __clrcall target(int value) { return value + 1; }";
        string[] flags = dataSections ? ["-fdata-sections", "-ffunction-sections"] : null;
        Compile(reverse ? definition : main, flags)
            .Compile(reverse ? main : definition, flags)
            .Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Fact]
    public void ManagedVtableSlotsKeepStaticFunctionsInputLocal()
    {
        Compile("""
            static int __clrcall target(int value) { return value + 1; }
            int (__clrcall *pointer)(int) = target;
            int other(void);
            int main(void) { return pointer(20) + other(); }
            """, ["-fdata-sections", "-ffunction-sections"])
            .Compile("""
                static int __clrcall target(int value) { return value + 2; }
                int (__clrcall *other_pointer)(int) = target;
                int other(void) { return other_pointer(19); }
                """, ["-fdata-sections", "-ffunction-sections"])
            .Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedFunctionPointerAggregateInitializersWork(bool dataSections)
    {
        var builder = Compile("""
            int value = 20;
            int __clrcall target(int value) { return value + 1; }
            int (__clrcall *array[2])(int) = { target, target };
            struct Holder { char prefix; int (__clrcall *callback)(int); int *data; int tag; };
            struct Holder holder = { 'x', target, &value, 20 };
            int main(void) {
                if (holder.prefix != 'x' || holder.tag != 20 || holder.data != &value ||
                    (void *)holder.callback != (void *)target ||
                    (void *)array[0] != (void *)target || (void *)array[1] != (void *)target) return 1;
                return array[0](holder.tag) + holder.callback(*holder.data);
            }
            """, dataSections ? ["-fdata-sections", "-ffunction-sections"] : null);
        builder.Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Fact]
    public void ManagedFunctionPointerWithReturnModifiersWorks()
    {
        var builder = Compile("""
            char __clrcall target(char value) { return value + 1; }
            char (__clrcall *pointer)(char) = target;
            int main(void) {
                if ((void *)pointer != (void *)target) return 1;
                return pointer(41);
            }
            """, ["-fdata-sections", "-ffunction-sections"]);
        builder.Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Fact]
    public void DiscardedManagedVtableSlotsDoNotRequireInitialization()
    {
        Compile("""
            int __clrcall target(int value) { return value + 1; }
            int (__clrcall *pointer)(int) = target;
            int main(void) { return 42; }
            """, ["-fdata-sections", "-ffunction-sections", "-fno-fixed-address-statics"])
            .Link(["/entry:main", "/OPT:REF"]).RunAndCheck(42);
    }

    [Theory]
    [InlineData("int target;", "int pointer = (int)&target;")]
    [InlineData("int target;", "int pointer = (int)&target + 1;")]
    [InlineData("int target;", "int pointers[2] = { (int)&target, 0 };")]
    [InlineData("int target;", "struct { int pointer; int guard; } value = { (int)&target, 0 };")]
    [InlineData("int target;", "union { int pointer; long long wide; } value = { (int)&target };")]
    [InlineData("int target(void) { return 42; }", "int pointer = (int)target;")]
    [InlineData("int target(void) { return 42; }", "struct { int pointer; int guard; } value = { (int)target, 0 };")]
    public void NarrowGlobalAddressInitializersFailExplicitly(string declaration, string initializer)
    {
        CompileExpectingError($$"""
            {{declaration}}
            {{initializer}}
            int main(void) { return 42; }
            """).AssertErrorContains("requires pointer-sized storage");
    }

    [Fact]
    public void LocalAggregateAddressInitializersAllowPointerTruncation()
    {
        var builder = Compile("""
            int target = 42;
            int __clrcall function(void) { return 42; }
            long long address = (long long)&target;
            long long callback = (long long)function;
            int main(void) {
                int values[2] = { (int)&target, 17 };
                struct Holder { int value; int guard; };
                struct Holder holder = { (int)&target, 23 };
                union Value { int value; long long wide; };
                union Value value = { (int)&target };
                int callbacks[2] = { (int)(void *)function, 31 };
                if (address != (long long)&target) return 1;
                if (callback != (long long)function) return 2;
                if (values[0] != (int)&target || values[1] != 17) return 3;
                if (holder.value != (int)&target || holder.guard != 23) return 4;
                if (value.value != (int)&target) return 5;
                if (callbacks[0] != (int)(void *)function || callbacks[1] != 31) return 6;
                return target;
            }
            """);
        builder.Link(ConsoleMain).RunAndCheck(42);
    }

    [Theory]
    [InlineData("void *pointer = (char *)target + 1;", "cannot have an addend")]
    [InlineData("int pointer = (int)target + 1;", "requires pointer-sized storage")]
    [InlineData("int pointer = (int)target;", "requires pointer-sized storage")]
    [InlineData("int pointers[2] = { (int)target, 0 };", "requires pointer-sized storage")]
    [InlineData("struct { int pointer; int guard; } value = { (int)target, 0 };", "requires pointer-sized storage")]
    public void UnsupportedManagedFunctionPointerInitializersFailExplicitly(string initializer, string diagnostic)
    {
        CompileExpectingError($$"""
            int __clrcall target(void) { return 42; }
            {{initializer}}
            int main(void) { return 42; }
            """).AssertErrorContains(diagnostic);
    }

    [Theory]
    [InlineData("kind", "requires a TOKEN relocation")]
    [InlineData("missing", "requires a TOKEN relocation")]
    [InlineData("addend", "nonzero TOKEN relocation addends")]
    [InlineData("symbol", "does not reference a method token")]
    [InlineData("native", "vtfixup storage")]
    [InlineData("offset", "vtfixup storage is outside its section")]
    [InlineData("address", "requires a managed vtfixup slot")]
    public void MalformedManagedVtableSlotsFailExplicitly(string mutation, string diagnostic)
    {
        var builder = Compile("""
            int __clrcall target(int value) { return value + 1; }
            int (__clrcall *pointer)(int) = target;
            int main(void) { return pointer(41); }
            """, ["-fdata-sections", "-ffunction-sections"]);
        byte[] bytes = File.ReadAllBytes(builder._objFiles[0]);
        using (var coff = new CoffReader(new MemoryStream(bytes)))
        {
            var sections = coff.GetSectionTableReader();
            var handles = sections.Sections.ToArray();
            var symbols = coff.GetSymbolTableReader();
            var strings = coff.GetStringTableReader();
            var fixupHandle = handles.Single(h =>
            {
                var section = sections.GetCoffSection(h);
                return strings.GetString(section.Name) == ".rdata$ilfixup" &&
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(section.PointerToRawData + 6)) == 0x02;
            });
            var fixup = sections.GetCoffSection(fixupHandle);
            var pointerHandle = coff.GetRelocationTableReader(fixup).GetRelocation(0).Symbol;
            var pointer = symbols.GetCoffSymbol(pointerHandle);
            var data = sections.GetCoffSection(handles[pointer.SectionNumber.Number - 1]);
            switch (mutation)
            {
                case "kind":
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(data.PointerToRelocations + 8), (ushort)ImageRelocation.Amd64_ADDR64);
                    break;
                case "missing":
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20 + (pointer.SectionNumber.Number - 1) * 40 + 32), 0);
                    break;
                case "addend":
                    bytes[data.PointerToRawData] = 1;
                    break;
                case "symbol":
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(data.PointerToRelocations + 4), (uint)pointerHandle.Index);
                    break;
                case "native":
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(fixup.PointerToRawData + 6), 0x0A);
                    break;
                case "offset":
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fixup.PointerToRawData), 1);
                    break;
                case "address":
                    var md = coff.GetMetadataReader();
                    var target = md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "target");
                    var token = symbols.Symbols.Select(symbols.GetCoffSymbol).Single(s =>
                        s.StorageClass == CoffSymbolStorageClass.ClrToken &&
                        strings.GetString(s.Name) == MetadataTokens.GetToken(target).ToString("X8"));
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(data.PointerToRelocations + 4),
                        (uint)token.GetClrTokenDefinition().AssociatedSymbol.Index);
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(data.PointerToRelocations + 8), (ushort)ImageRelocation.Amd64_ADDR64);
                    int header = 20 + Array.IndexOf(handles, fixupHandle) * 40;
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header + 16), 0);
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(header + 32), 0);
                    break;
            }
        }
        File.WriteAllBytes(builder._objFiles[0], bytes);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.Contains(diagnostic, error.Message);
    }

    [Fact]
    public void OverlappingManagedVtableSlotsFailExplicitly()
    {
        var builder = Compile("""
            int __clrcall target(int value) { return value + 1; }
            int (__clrcall *pointer[2])(int) = { target, target };
            int main(void) { return pointer[0](41); }
            """, ["-fdata-sections", "-ffunction-sections"]);
        byte[] bytes = File.ReadAllBytes(builder._objFiles[0]);
        using (var coff = new CoffReader(new MemoryStream(bytes)))
        {
            var sections = coff.GetSectionTableReader();
            var strings = coff.GetStringTableReader();
            var fixup = sections.Sections.Select(sections.GetCoffSection).Single(s =>
                strings.GetString(s.Name) == ".rdata$ilfixup" && s.SizeOfRawData == 16);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fixup.PointerToRawData + 8), 0);
        }
        File.WriteAllBytes(builder._objFiles[0], bytes);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.Contains("overlapping managed vtfixup slots", error.Message);
    }

    [Theory]
    [InlineData("int target(void); int (*pointer)(void) = target; int main(void) { return pointer(); }", "int target(void) { return 42; }", "native")]
    [InlineData("extern int missing; int *pointer = &missing; int main(void) { return *pointer; }", "int other;", "missing")]
    [InlineData("int value = 1; int main(void) { return value; }", "int value = 2;", "duplicate")]
    public void UnsupportedGlobalDependenciesFailExplicitly(string main, string other, string diagnostic)
    {
        var builder = Compile(main, ["-fno-uco-entrypoints"])
            .Compile(other, ["-fno-uco-entrypoints"]);
        var error = Assert.Throws<InvalidOperationException>(() => builder.Link(ConsoleMain));
        Assert.IsType<Chilink.LinkException>(error.InnerException);
        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
