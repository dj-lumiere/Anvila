using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;

namespace Builder.Execution;

/// <summary>
/// Makes the constant-pool symbols of every COFF object the JIT links (<c>__real@…</c>, <c>__xmm@…</c>,
/// <c>__ymm@…</c>, <c>__zmm@…</c>: the floating-point and vector constants LLVM's MSVC target puts in a COMDAT
/// section named after their bytes) local to that object.
/// <para>
/// LLJIT's own object layer on Windows claims such symbols for the object that defines them
/// (<c>setAutoClaimResponsibilityForObjectSymbols</c>, <c>setOverrideObjectFlagsWithResponsibilityFlags</c>).
/// The JIT here replaces that layer with one built through the C API (<see cref="OrcContiguousMemoryManager"/>, for
/// the SEH section layout), and the C API can't turn those settings on. Without them a module compiled in the JIT
/// reports its constant-pool symbols as resolved although nothing made it responsible for them, and ORC looks each
/// one up in the JITDylib's symbol table without checking it is there: release builds of LLVM read past the table
/// (in a debug build it is an assertion). Whether that reads as an error depends on the table's contents, so a
/// program failed with "Failed to materialize symbols: { (main, { __ymm@… }) }" once Ingrid's library (thousands of
/// symbols) went into the JIT as an object. A precompiled object (the resident base, a layer, Ingrid's library)
/// also lists its constant-pool symbols as weak definitions of the dylib, which another object's copy of the same
/// constant then collides with.
/// </para>
/// <para>
/// A local symbol is neither: the object resolves its own relocations to its own copy, and ORC never sees the name.
/// Every object keeps its own copy of a constant, which only costs the deduplication a linker would do.
/// </para>
/// </summary>
#pragma warning disable S6640 // unsafe is required for the ORC object-transform callback and its memory buffers
internal static unsafe class OrcCoffConstantPools
{
    private const byte StorageClassExternal = 2;
    private const byte StorageClassStatic = 3;

    private static readonly byte[][] PoolPrefixes =
    [
        "__real@"u8.ToArray(),
        "__xmm@"u8.ToArray(),
        "__ymm@"u8.ToArray(),
        "__zmm@"u8.ToArray()
    ];

    /// <summary>The bigobj header's class identifier, which tells it from an import library's header.</summary>
    private static readonly byte[] BigObjClassId =
    [
        0xC7, 0xA1, 0xBA, 0xD1, 0xEE, 0xBA, 0xA9, 0x4B, 0xAF, 0x20, 0xFA, 0xF6, 0x6A, 0xA4, 0xDC, 0xB8
    ];

    /// <summary>Has every object the JIT compiles go through <see cref="Localize"/> before it is linked.</summary>
    internal static void InstallOn(LLVMOrcOpaqueLLJIT* jit)
    {
        delegate* unmanaged[Cdecl]<void*, LLVMOpaqueMemoryBuffer**, LLVMOpaqueError*> transform = &Transform;
        LLVM.OrcObjectTransformLayerSetTransform(ObjTransformLayer: LLVM.OrcLLJITGetObjTransformLayer(J: jit),
            TransformFunction: transform,
            Ctx: null);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static LLVMOpaqueError* Transform(void* ctx, LLVMOpaqueMemoryBuffer** objInOut)
    {
        LLVMOpaqueMemoryBuffer* original = *objInOut;
        var bytes = new ReadOnlySpan<byte>(pointer: LLVM.GetBufferStart(MemBuf: original),
            length: checked((int)LLVM.GetBufferSize(MemBuf: original)));
        if (!HasPoolSymbolsToLocalize(obj: bytes))
        {
            return null;
        }

        byte[] copy = bytes.ToArray();
        Localize(obj: copy);
        byte[] name = "rf_jit_obj\0"u8.ToArray();
        fixed (byte* copyPointer = copy)
        fixed (byte* namePointer = name)
        {
            *objInOut = LLVM.CreateMemoryBufferWithMemoryRangeCopy(InputData: (sbyte*)copyPointer,
                InputDataLength: (nuint)copy.Length,
                BufferName: (sbyte*)namePointer);
        }

        // The transform owns the buffer it is handed: the replacement goes on, the original is done with.
        LLVM.DisposeMemoryBuffer(MemBuf: original);
        return null;
    }

    /// <summary>Makes the constant-pool symbols <paramref name="obj"/> defines local, in place. Anything but a COFF
    /// object is left alone.</summary>
    internal static void Localize(Span<byte> obj)
    {
        Rewrite(obj: obj, write: true);
    }

    private static bool HasPoolSymbolsToLocalize(ReadOnlySpan<byte> obj)
    {
        // Rewrite only writes when asked to, so the read-only view is safe to pass.
        fixed (byte* pointer = obj)
        {
            return Rewrite(obj: new Span<byte>(pointer: pointer, length: obj.Length), write: false);
        }
    }

    /// <summary>Finds the external constant-pool symbols that are defined in a section, and makes them static when
    /// <paramref name="write"/> is set. Returns whether there were any.</summary>
    private static bool Rewrite(Span<byte> obj, bool write)
    {
        if (!TryReadLayout(obj: obj, layout: out SymbolLayout layout))
        {
            return false;
        }

        long stringTable = layout.TableOffset + ((long)layout.Count * layout.EntrySize);
        bool found = false;
        for (long index = 0; index < layout.Count;)
        {
            long entry = layout.TableOffset + (index * layout.EntrySize);
            if (entry + layout.EntrySize > obj.Length)
            {
                break;
            }

            Span<byte> symbol = obj.Slice(start: (int)entry, length: layout.EntrySize);
            int section = layout.EntrySize == 18
                ? BinaryPrimitives.ReadInt16LittleEndian(source: symbol[12..])
                : BinaryPrimitives.ReadInt32LittleEndian(source: symbol[12..]);
            int classOffset = layout.EntrySize - 2;
            if (symbol[classOffset] == StorageClassExternal && section > 0
                && IsPoolName(obj: obj, symbol: symbol, stringTable: stringTable))
            {
                found = true;
                if (!write)
                {
                    return true;
                }

                symbol[classOffset] = StorageClassStatic;
            }

            index += 1 + symbol[layout.EntrySize - 1];
        }

        return found;
    }

    private static bool IsPoolName(ReadOnlySpan<byte> obj, ReadOnlySpan<byte> symbol, long stringTable)
    {
        ReadOnlySpan<byte> name;
        if (BinaryPrimitives.ReadUInt32LittleEndian(source: symbol) == 0)
        {
            long start = stringTable + BinaryPrimitives.ReadUInt32LittleEndian(source: symbol[4..]);
            if (start >= obj.Length)
            {
                return false;
            }

            name = obj[(int)start..];
        }
        else
        {
            name = symbol[..8];
        }

        foreach (byte[] prefix in PoolPrefixes)
        {
            if (name.StartsWith(value: prefix))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct SymbolLayout(long TableOffset, long Count, int EntrySize);

    /// <summary>Reads where the symbol table of a COFF object is, in the regular format (18-byte symbols) or the
    /// bigobj one (20-byte symbols, for objects with more than 65279 sections).</summary>
    private static bool TryReadLayout(ReadOnlySpan<byte> obj, out SymbolLayout layout)
    {
        layout = default;
        if (obj.Length < 20)
        {
            return false;
        }

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(source: obj);
        if (machine == 0 && BinaryPrimitives.ReadUInt16LittleEndian(source: obj[2..]) == 0xFFFF)
        {
            if (obj.Length < 56 || BinaryPrimitives.ReadUInt16LittleEndian(source: obj[4..]) < 2
                                || !obj.Slice(start: 12, length: 16).SequenceEqual(other: BigObjClassId))
            {
                return false;
            }

            layout = new SymbolLayout(TableOffset: BinaryPrimitives.ReadUInt32LittleEndian(source: obj[48..]),
                Count: BinaryPrimitives.ReadUInt32LittleEndian(source: obj[52..]),
                EntrySize: 20);
            return true;
        }

        // The machines LLVM's MSVC targets emit: x86-64, AArch64, x86.
        if (machine is not (0x8664 or 0xAA64 or 0x14C))
        {
            return false;
        }

        layout = new SymbolLayout(TableOffset: BinaryPrimitives.ReadUInt32LittleEndian(source: obj[8..]),
            Count: BinaryPrimitives.ReadUInt32LittleEndian(source: obj[12..]),
            EntrySize: 18);
        return true;
    }
}
