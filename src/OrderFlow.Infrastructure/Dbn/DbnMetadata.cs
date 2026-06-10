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
    public const ushort SchemaMbo = 0;
    public const ushort SchemaMixed = 0xFFFF;

    public bool IsMboSchema => RawSchema == SchemaMbo;
}
