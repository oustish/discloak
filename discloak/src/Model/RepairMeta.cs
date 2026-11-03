namespace Discloak;

/// <summary>Repair-data header for one catalog file (see Rs).</summary>
public record RepairMeta(string File, long Length, string FileSha256, int BlockSize, int DataBlocks, int ParityBlocks, string[] BlockSha256);
