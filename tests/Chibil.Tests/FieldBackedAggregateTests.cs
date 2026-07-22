using Xunit;

namespace Chibil.Tests;

public class FieldBackedAggregateTests : ChibiTestBase
{
    [Fact]
    public void FieldBackedNestedAggregateBehavior()
    {
        Compile("""
            struct Outer {
                int prefix;
                struct {
                    int x;
                    struct {
                        int y;
                    } inner;
                } anon;
                int suffix;
            };

            int bump(int *p) {
                *p = *p + 10;
                return *p;
            }

            int main(void) {
                struct Outer value;
                struct Outer *p = &value;

                p->prefix = 1;
                p->anon.x = 2;
                p->anon.inner.y = 3;
                p->suffix = 4;

                if (bump(&p->anon.inner.y) != 13)
                    return 10;

                return p->prefix + p->anon.x + p->anon.inner.y + p->suffix;
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 20);
    }

    [Fact]
    public void FieldBackedScopedStructTagsDoNotCollide()
    {
        Compile("""
            struct S {
                int x;
            };

            int main(void) {
                struct S outer;
                outer.x = 3;

                {
                    struct S {
                        int y;
                        int z;
                    };
                    struct S inner;
                    inner.y = 4;
                    inner.z = 5;

                    return outer.x + inner.y + inner.z;
                }
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 12);
    }

    [Fact]
    public void FieldBackedUnionAndBitfieldBehavior()
    {
        Compile("""
            struct StatusRegister {
                unsigned int mode : 3;
                unsigned int enabled : 1;
                unsigned int error : 2;
                unsigned int reserved : 2;
            };

            union Number {
                int i;
                unsigned char bytes[4];
            };

            struct Device {
                union Number number;
                struct StatusRegister status;
            };

            int main(void) {
                struct Device device = { 0 };
                device.number.i = 0x01020304;
                if (device.number.bytes[0] != 4)
                    return 10;

                device.status.mode = 5;
                device.status.enabled = 1;
                device.status.error = 2;
                device.status.reserved = 3;

                if (device.status.mode != 5)
                    return 20;
                if (device.status.enabled != 1)
                    return 30;
                if (device.status.error != 2)
                    return 40;
                if (device.status.reserved != 3)
                    return 50;

                return device.number.bytes[0] + device.status.mode + device.status.enabled +
                    device.status.error + device.status.reserved;
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 15);
    }

    [Fact]
    public void FieldBackedBoolAndCharFieldsBehavior()
    {
        Compile("""
            struct Flags {
                _Bool enabled;
                char signedByte;
                unsigned char unsignedByte;
            };

            int main(void) {
                struct Flags flags;
                flags.enabled = 3;
                flags.signedByte = -1;
                flags.unsignedByte = 200;

                if (flags.enabled != 1)
                    return 10;
                if (flags.signedByte != -1)
                    return 20;
                if (flags.unsignedByte != 200)
                    return 30;

                return flags.enabled + flags.signedByte + flags.unsignedByte;
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 200);
    }

    [Fact]
    public void FieldBackedPackedStructBehavior()
    {
        Compile("""
            struct __attribute__((packed)) Packed {
                char c;
                int x;
            };

            int main(void) {
                struct Packed values[2];

                values[0].c = 1;
                values[0].x = 0x11223344;
                values[1].c = 2;
                values[1].x = 0x55667788;

                if (sizeof(struct Packed) != 5)
                    return 10;
                if ((char *)&values[0].x - (char *)&values[0] != 1)
                    return 20;
                if ((char *)&values[1].x - (char *)&values[1] != 1)
                    return 30;
                if (values[0].x != 0x11223344)
                    return 40;
                if (values[1].x != 0x55667788)
                    return 50;

                return values[0].c + values[1].c;
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 3);
    }

    [Fact]
    public void FieldBackedFlexibleArrayInitializerBehavior()
    {
        Compile("""
            struct Packet {
                int length;
                int values[];
            };

            int main(void) {
                struct Packet packet = { 3, { 4, 5, 6 } };

                if (packet.length != 3)
                    return 10;
                if (sizeof(packet) != 16)
                    return 15;
                if (packet.values[0] != 4)
                    return 20;
                if (packet.values[1] != 5)
                    return 30;
                if (packet.values[2] != 6)
                    return 40;

                return packet.length + packet.values[0] + packet.values[1] + packet.values[2];
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 18);
    }

    [Fact]
    public void FieldBackedGlobalFlexibleArrayInitializerBehavior()
    {
        Compile("""
            struct Packet {
                int length;
                int values[];
            };

            struct Packet packet = { 3, { 4, 5, 6 } };

            int main(void) {
                if (packet.length != 3)
                    return 10;
                if (packet.values[0] != 4)
                    return 20;
                if (packet.values[1] != 5)
                    return 30;
                if (packet.values[2] != 6)
                    return 40;

                return packet.length + packet.values[0] + packet.values[1] + packet.values[2];
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 18);
    }

    [Fact]
    public void FieldBackedFlexibleArrayMemberBaseAddress()
    {
        // A flexible array member must be addressed by offset, not as a metadata
        // field. The incomplete array would otherwise get pointer size/alignment
        // and place the member at the wrong offset (here it follows a single char,
        // so its natural offset is 4, not the managed pointer-aligned location).
        Compile("""
            struct S {
                char header;
                int values[];
            };

            int main(void) {
                struct S s = { 9, { 4, 5, 6 } };

                char *base = (char *)&s;
                char *vp = (char *)&s.values[0];
                if (vp - base != 4)
                    return 50 + (int)(vp - base);
                if (s.values[0] != 4)
                    return 20;
                if (s.values[1] != 5)
                    return 30;
                if (s.values[2] != 6)
                    return 40;

                return 7;
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 7);
    }

    [Fact]
    public void FieldBackedMemberAlignmentBehavior()
    {
        const string source = """
            struct MemberAligned {
                char prefix;
                _Alignas(16) int value;
                char suffix;
            };

            int main(void) {
                struct MemberAligned item;

                item.prefix = 1;
                item.value = 0x11223344;
                item.suffix = 2;

                if (sizeof(struct MemberAligned) != 32)
                    return 10;
                if ((char *)&item.value - (char *)&item != 16)
                    return 20;
                if ((char *)&item.suffix - (char *)&item != 20)
                    return 30;
                if (item.value != 0x11223344)
                    return 40;

                return item.prefix + item.suffix;
            }
            """;

#if FIELD_BACKED_AGGREGATES
        CompileExpectingError(source)
            .AssertErrorContains("aligned struct members");
#else
        Compile(source)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 3);
#endif
    }

    [Fact]
    public void FieldBackedTypeAlignmentBehavior()
    {
        const string source = """
            struct __attribute__((aligned(16))) Inner {
                char c;
            };

            struct Outer {
                char prefix;
                struct Inner inner;
                char suffix;
            };

            int main(void) {
                struct Outer outer;

                outer.prefix = 1;
                outer.inner.c = 2;
                outer.suffix = 3;

                if (sizeof(struct Inner) != 16)
                    return 10;
                if (sizeof(struct Outer) != 48)
                    return 20;
                if ((char *)&outer.inner - (char *)&outer != 16)
                    return 30;
                if ((char *)&outer.suffix - (char *)&outer != 32)
                    return 40;

                return outer.prefix + outer.inner.c + outer.suffix;
            }
            """;

#if FIELD_BACKED_AGGREGATES
        CompileExpectingError(source)
            .AssertErrorContains("aligned aggregate types");
#else
        Compile(source)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 6);
#endif
    }

    [Fact]
    public void FieldBackedForwardDeclaredTaglessNestedMemberBehavior()
    {
        // A tagged aggregate may be forward-declared and then defined later. The
        // definition is parsed into a temporary type that is merged into the
        // existing forward-declared instance. Tagless nested members recorded the
        // temporary type as their EnclosingAggregate, so the merge must keep those
        // references resolving to the canonical enclosing type; otherwise the
        // field-backed registry reserves a duplicate TypeDef for the enclosing
        // aggregate and field materialization fails.
        Compile("""
            struct Outer;

            struct Outer {
                int prefix;
                struct {
                    int x;
                    int y;
                } anon;
                int suffix;
            };

            int main(void) {
                struct Outer o;

                o.prefix = 1;
                o.anon.x = 2;
                o.anon.y = 3;
                o.suffix = 4;

                return o.prefix + o.anon.x + o.anon.y + o.suffix;
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 10);
    }

    [Fact]
    public void FieldBackedFlexibleArrayClonePassByValueBehavior()
    {
        // A completed flexible-array aggregate initializer produces a clone whose
        // concrete size exceeds the canonical TypeDef size; it is stored as a raw
        // byte buffer. Reading the whole clone by value (passing it to a function)
        // must copy the fixed portion through the correct managed type.
        Compile("""
            struct Packet {
                int length;
                int values[];
            };

            int first(struct Packet p) {
                return p.length;
            }

            int main(void) {
                struct Packet packet = { 3, { 4, 5, 6 } };
                return first(packet) + packet.values[0] + packet.values[2];
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 13);
    }

    [Fact]
    public void FieldBackedFlexibleArrayCloneStructMemberBehavior()
    {
        // A flexible-array clone is stored as a raw byte buffer, yet its non-flexible
        // members are accessed through the canonical aggregate TypeDef. Reading a
        // struct-typed non-flexible member by value must copy the correct bytes.
        Compile("""
            struct Inner {
                int a;
                int b;
            };

            struct Packet {
                struct Inner hdr;
                int values[];
            };

            int main(void) {
                struct Packet packet = { { 7, 8 }, { 4, 5, 6 } };
                struct Inner copy = packet.hdr;
                return copy.a + copy.b + packet.values[1];
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 20);
    }

    [Fact]
    public void FieldBackedFlexibleArrayCloneAssignmentCopiesTrailingStorage()
    {
        Compile("""
            struct Packet {
                int length;
                int values[];
            };

            int main(void) {
                struct Packet packet = { 3, { 4, 5, 6 } };
                typeof(packet) copy;

                copy = packet;

                if (copy.length != 3)
                    return 10;
                if (copy.values[0] != 4)
                    return 20;
                if (copy.values[1] != 5)
                    return 30;
                if (copy.values[2] != 6)
                    return 40;

                return copy.length + copy.values[0] + copy.values[1] + copy.values[2];
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 18);
    }

    [Fact]
    public void FieldBackedTaglessNestedArrayElementBehavior()
    {
        // A fixed-size array whose element type is a tagless nested struct member
        // is represented AddressOnly under the MSVC model (no TypeDef). Reserving
        // the array TypeDef (here forced by naming the member's array type via
        // typeof) must not try to materialize a TypeDef for the tagless element.
        Compile("""
            struct Outer {
                struct {
                    int x;
                } arr[2];
            };

            int main(void) {
                struct Outer outer;
                typeof(outer.arr) copy;

                copy[0].x = 5;
                copy[1].x = 9;

                if (copy[0].x != 5)
                    return 10;
                if (copy[1].x != 9)
                    return 20;

                return copy[0].x + copy[1].x;
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 14);
    }

    [Fact]
    public void FieldBackedForwardTypeAndFieldReferencesLink()
    {
        Compile("""
            struct Value;

            struct Value *identity(struct Value *value) {
                return value;
            }

            struct Value {
                int number;
            };

            int read_value(struct Value *value) {
                return value->number;
            }

            int main(void) {
                struct Value value;
                value.number = 42;
                return read_value(identity(&value));
            }
            """)
        .MsvcLink(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 42);
    }
}
