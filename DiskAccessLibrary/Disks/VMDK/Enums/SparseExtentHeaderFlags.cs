using System;

namespace DiskAccessLibrary.VMDK
{
    [Flags]
    public enum SparseExtentHeaderFlags : uint
    {
        ValidNewLineDetectionTest = 0x00000001,
        HasRedundantGrainTable = 0x00000002,
        UseZeroedGrainGTEs = 0x00000004, // SparseExtentHeader version 2 and above, see Virtual Disk Format 5.0
        UseCompressionForGrains = 0x00010000,
        HasMarkers = 0x00020000,
    }
}
