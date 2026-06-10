using OrderFlow.Domain.Primitives;

namespace OrderFlow.Infrastructure.Dbn;

/// <summary>Parsed DBN file metadata header. See <see cref="DbnDecoder"/> for layout references.</summary>
public sealed record DbnMetadata(
    byte Version,
    string Dataset,
    ushort RawSchema,
    Timestamp Start,
    Timestamp End,
    ulong Limit,
    byte STypeIn,
    byte STypeOut,
    bool TsOut,
    ushort SymbolCstrLen,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> PartialSymbols,
    IReadOnlyList<string> NotFoundSymbols,
    int MappingCount)
{
    // Schema ids per databento/dbn rust/dbn/src/enums.rs: Mbo = 0, Mbp1 = 1, Mbp10 = 2.
    public const ushort SchemaMbp10 = 2;
    public const ushort SchemaMixed = 0xFFFF;

    public bool IsMbp10Schema => RawSchema == SchemaMbp10;
}
