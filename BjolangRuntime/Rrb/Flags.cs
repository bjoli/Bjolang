namespace Bjolang.Runtime.Rrb;

[Flags]
public enum NodeFlags : byte
{
    // Bits 4-8 are currently not in use.
    None = 0,
    IsRelaxed = 1 << 0, // 1
    IsLeaf = 1 << 1, // 2
    IsFrozen = 1 << 2, // 4
    IsPhantom = 1 << 3, // 8: stands in for the items before a list's Gap; see Phantom<T>.
    IsPineapple = 1 << 4, // 16
    Tyrannosaurus = 1 << 5, // 32
    MotherNode = 1 << 6, // 64
    CheckMate = 1 << 7 // 128
}