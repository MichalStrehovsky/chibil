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
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 20);
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
        .Link(["/entry:main", "/subsystem:console"])
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
        .Link(["/entry:main", "/subsystem:console"])
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
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 3);
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
        .Link(["/entry:main", "/subsystem:console"])
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
        .Link(["/entry:main", "/subsystem:console"])
        .RunAndCheck(exitCode: 6);
#endif
    }
}
