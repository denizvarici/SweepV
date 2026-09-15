using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SweepV.Core.Models;
using SweepV.Core.Platform;

namespace SweepV.Core.Scanning
{
    /// <summary>
    /// Builds the tree for a whole NTFS volume by reading the Master File Table directly
    /// (the WizTree approach): one sequential read of $MFT instead of millions of directory
    /// queries. Requires administrator rights and only scans volume roots.
    /// </summary>
    public sealed class MftDiskScanner : IDiskScanner
    {
        private const uint FileSignature = 0x454C4946;   // "FILE"
        private const uint AttrStandardInformation = 0x10;
        private const uint AttrFileName = 0x30;
        private const uint AttrData = 0x80;
        private const uint AttrEnd = 0xFFFFFFFF;
        private const ushort FlagInUse = 0x01;
        private const ushort FlagDirectory = 0x02;
        private const long RootDirectoryFrn = 5;
        private const byte NamespaceDos = 2;
        private const int ReadChunkBytes = 16 * 1024 * 1024;

        /// <summary>True when <paramref name="path"/> is the root of a local NTFS volume and the process is elevated.</summary>
        public static bool CanScan(string path)
        {
            if (!OperatingSystem.IsWindows() || !Elevation.IsElevated)
                return false;
            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetPathRoot(full);
                if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal) ||
                    !string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
                    return false;
                var drive = new DriveInfo(root);
                return drive.IsReady && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public ScanNode ScanDirectory(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (!CanScan(path))
                throw new NotSupportedException("MFT scanning needs administrator rights and the root of an NTFS volume.");

            var root = Path.GetPathRoot(Path.GetFullPath(path))!;
            using var volume = OpenVolume(root);

            // Volume reads must be sector aligned; 4096 covers both 512e and 4Kn disks.
            var boot = new byte[4096];
            ReadExactly(volume, boot, 0);
            var geometry = VolumeGeometry.Parse(boot);

            var mftRuns = ReadMftExtents(volume, geometry);
            var records = new RecordTable();
            ReadAllRecords(volume, geometry, mftRuns, records, root, progress, cancellationToken);

            return BuildTree(records, root, cancellationToken);
        }

        // ---------------------------------------------------------------- reading

        private static List<(long Offset, long Length)> ReadMftExtents(SafeFileHandle volume, VolumeGeometry geometry)
        {
            var cluster = new byte[Math.Max(geometry.ClusterSize, (geometry.RecordSize + geometry.ClusterSize - 1) / geometry.ClusterSize * geometry.ClusterSize)];
            ReadExactly(volume, cluster, geometry.MftOffset);
            var record = cluster.AsSpan(0, geometry.RecordSize).ToArray();
            if (!ApplyFixups(record, geometry.RecordSize))
                throw new InvalidDataException("$MFT record is corrupt.");

            foreach (var attr in EnumerateAttributes(record, geometry.RecordSize))
            {
                if (attr.Type != AttrData || attr.NameLength != 0 || !attr.NonResident)
                    continue;

                var span = record.AsSpan(attr.Offset, attr.Length);
                var dataSize = BinaryPrimitives.ReadInt64LittleEndian(span[0x30..]);
                var runs = ParseDataRuns(span[BinaryPrimitives.ReadUInt16LittleEndian(span[0x20..])..], geometry.ClusterSize);

                // A heavily fragmented $MFT keeps more runs in extension records; bail out so the caller falls back.
                if (runs.Sum(r => r.Length) < dataSize)
                    throw new NotSupportedException("$MFT is too fragmented for the fast scanner.");

                // Trim the tail so we don't read allocated-but-unused space.
                var remaining = dataSize;
                var trimmed = new List<(long, long)>();
                foreach (var (offset, length) in runs)
                {
                    if (remaining <= 0)
                        break;
                    var take = Math.Min(length, remaining);
                    trimmed.Add((offset, take));
                    remaining -= take;
                }
                return trimmed;
            }
            throw new InvalidDataException("$MFT has no data attribute.");
        }

        private static void ReadAllRecords(SafeFileHandle volume, VolumeGeometry geometry, List<(long Offset, long Length)> runs,
            RecordTable records, string root, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            var recordSize = geometry.RecordSize;
            var chunkBytes = ReadChunkBytes / geometry.ClusterSize * geometry.ClusterSize;
            var buffer = new byte[chunkBytes];
            long frn = 0;
            long files = 0, bytes = 0;
            var lastReport = Environment.TickCount64;

            foreach (var (runOffset, runLength) in runs)
            {
                for (long position = 0; position < runLength; position += chunkBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var wanted = (int)Math.Min(chunkBytes, runLength - position);
                    // Volume reads must be sector aligned; round up to a whole cluster.
                    var aligned = (wanted + geometry.ClusterSize - 1) / geometry.ClusterSize * geometry.ClusterSize;
                    ReadExactly(volume, buffer.AsSpan(0, aligned), runOffset + position);

                    for (var offset = 0; offset + recordSize <= wanted; offset += recordSize, frn++)
                    {
                        var record = buffer.AsSpan(offset, recordSize);
                        if (ParseRecord(record, frn, records) is { } size)
                        {
                            files++;
                            bytes += size;
                        }
                    }

                    if (progress is not null && Environment.TickCount64 - lastReport >= 100)
                    {
                        lastReport = Environment.TickCount64;
                        progress.Report(new ScanProgress(files, bytes, $"{root} (reading MFT record {frn:N0})"));
                    }
                }
            }
            progress?.Report(new ScanProgress(files, bytes, root));
        }

        /// <summary>Parses one FILE record into the table. Returns the data size added for a file, if any.</summary>
        private static long? ParseRecord(Span<byte> record, long frn, RecordTable records)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature || !ApplyFixups(record, record.Length))
                return null;

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
            if ((flags & FlagInUse) == 0)
                return null;

            // Extension records carry overflow attributes of a base record.
            var baseRef = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) & 0xFFFFFFFFFFFF);
            var owner = baseRef != 0 ? baseRef : frn;
            ref var entry = ref records.Get(owner);
            if (baseRef == 0)
            {
                entry.InUse = true;
                entry.IsDirectory = (flags & FlagDirectory) != 0;
            }

            long added = 0;
            foreach (var attr in EnumerateAttributes(record, record.Length))
            {
                var span = record.Slice(attr.Offset, attr.Length);
                switch (attr.Type)
                {
                    case AttrStandardInformation when !attr.NonResident:
                    {
                        var value = ResidentValue(span);
                        if (value.Length >= 0x10)
                            entry.ModifiedFileTime = BinaryPrimitives.ReadInt64LittleEndian(value[0x08..]);
                        break;
                    }

                    case AttrFileName when !attr.NonResident:
                    {
                        var value = ResidentValue(span);
                        if (value.Length < 0x42)
                            break;
                        var nameLength = value[0x40];
                        var nameSpace = value[0x41];
                        // Skip 8.3 aliases; keep the first real name (extra hard links are counted once).
                        if (nameSpace == NamespaceDos || entry.Name is not null || value.Length < 0x42 + nameLength * 2)
                            break;
                        entry.Parent = (long)(BinaryPrimitives.ReadUInt64LittleEndian(value) & 0xFFFFFFFFFFFF);
                        entry.Name = Encoding.Unicode.GetString(value.Slice(0x42, nameLength * 2));
                        break;
                    }

                    case AttrData when attr.NameLength == 0:
                    {
                        long size;
                        if (attr.NonResident)
                        {
                            // Sizes are only valid on the first segment of a split attribute.
                            if (BinaryPrimitives.ReadInt64LittleEndian(span[0x10..]) != 0)
                                break;
                            size = BinaryPrimitives.ReadInt64LittleEndian(span[0x30..]);
                        }
                        else
                        {
                            size = BinaryPrimitives.ReadUInt32LittleEndian(span[0x10..]);
                        }
                        entry.Size += size;
                        added += size;
                        break;
                    }
                }
            }
            return baseRef == 0 && !entry.IsDirectory ? added : null;
        }

        // ---------------------------------------------------------------- tree

        private static ScanNode BuildTree(RecordTable records, string root, CancellationToken cancellationToken)
        {
            var count = records.Count;
            var nodes = new ScanNode?[count];
            var rootNode = new ScanNode
            {
                Name = root,
                FullPath = root,
                IsDirectory = true,
                LastModifiedUtc = RecordTable.ToUtc(records.Get(RootDirectoryFrn).ModifiedFileTime)
            };
            nodes[RootDirectoryFrn] = rootNode;

            for (long i = 0; i < count; i++)
            {
                ref var entry = ref records.Get(i);
                if (i == RootDirectoryFrn || !entry.InUse || entry.Name is null)
                    continue;
                nodes[i] = new ScanNode
                {
                    Name = entry.Name,
                    IsDirectory = entry.IsDirectory,
                    SizeInBytes = entry.IsDirectory ? 0 : entry.Size,
                    FileCount = entry.IsDirectory ? 0 : 1,
                    LastModifiedUtc = RecordTable.ToUtc(entry.ModifiedFileTime)
                };
            }

            // Link children to parents. Nodes whose parent chain never reaches the root (orphans, cycles) are dropped.
            for (long i = 0; i < count; i++)
            {
                if (i % 65536 == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var node = nodes[i];
                if (node is null || i == RootDirectoryFrn)
                    continue;
                var parentFrn = records.Get(i).Parent;
                if (parentFrn == i || parentFrn < 0 || parentFrn >= count || nodes[parentFrn] is not { IsDirectory: true } parent)
                    continue;
                node.Parent = parent;
                parent.Children.Add(node);
            }

            AggregateSizes(rootNode);
            rootNode.SortBySizeRecursive();
            return rootNode;
        }

        /// <summary>Post-order size/count roll-up without recursion (NTFS trees can be very deep).</summary>
        private static void AggregateSizes(ScanNode root)
        {
            var stack = new Stack<(ScanNode Node, bool Visited)>();
            stack.Push((root, false));
            while (stack.Count > 0)
            {
                var (node, visited) = stack.Pop();
                if (!node.HasChildren)
                    continue;
                if (!visited)
                {
                    stack.Push((node, true));
                    foreach (var child in node.Children)
                        if (child.IsDirectory)
                            stack.Push((child, false));
                }
                else
                {
                    foreach (var child in node.Children)
                    {
                        node.SizeInBytes += child.SizeInBytes;
                        node.FileCount += child.FileCount;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- NTFS structures

        private readonly record struct AttributeHeader(uint Type, int Offset, int Length, bool NonResident, byte NameLength);

        private static List<AttributeHeader> EnumerateAttributes(ReadOnlySpan<byte> record, int recordSize)
        {
            var result = new List<AttributeHeader>(6);
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
            while (offset + 0x18 <= recordSize)
            {
                var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
                if (type == AttrEnd)
                    break;
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
                if (length < 0x18 || offset + length > recordSize)
                    break;
                result.Add(new AttributeHeader(type, offset, length, record[offset + 8] != 0, record[offset + 9]));
                offset += length;
            }
            return result;
        }

        private static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attribute)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
            return offset + length <= attribute.Length ? attribute.Slice(offset, length) : [];
        }

        /// <summary>Validates and undoes the update-sequence protection on a multi-sector record.</summary>
        private static bool ApplyFixups(Span<byte> record, int recordSize)
        {
            int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]);
            int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..]);
            if (usaCount < 2 || usaOffset + usaCount * 2 > recordSize)
                return false;

            var sectorSize = recordSize / (usaCount - 1);
            var usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
            for (var i = 1; i < usaCount; i++)
            {
                var position = i * sectorSize - 2;
                if (BinaryPrimitives.ReadUInt16LittleEndian(record[position..]) != usn)
                    return false;
                record[position] = record[usaOffset + i * 2];
                record[position + 1] = record[usaOffset + i * 2 + 1];
            }
            return true;
        }

        private static List<(long Offset, long Length)> ParseDataRuns(ReadOnlySpan<byte> runs, int clusterSize)
        {
            var result = new List<(long, long)>();
            long lcn = 0;
            var i = 0;
            while (i < runs.Length && runs[i] != 0)
            {
                var header = runs[i++];
                int lengthSize = header & 0x0F, offsetSize = header >> 4;
                if (lengthSize == 0 || i + lengthSize + offsetSize > runs.Length)
                    break;

                long clusters = 0;
                for (var b = 0; b < lengthSize; b++)
                    clusters |= (long)runs[i + b] << (8 * b);
                i += lengthSize;

                if (offsetSize == 0)
                    throw new NotSupportedException("Sparse $MFT runs are not supported.");

                long delta = 0;
                for (var b = 0; b < offsetSize; b++)
                    delta |= (long)runs[i + b] << (8 * b);
                if ((runs[i + offsetSize - 1] & 0x80) != 0)
                    delta -= 1L << (8 * offsetSize); // sign-extend
                i += offsetSize;

                lcn += delta;
                result.Add((lcn * clusterSize, clusters * clusterSize));
            }
            return result;
        }

        private readonly record struct VolumeGeometry(int ClusterSize, int RecordSize, long MftOffset)
        {
            public static VolumeGeometry Parse(ReadOnlySpan<byte> boot)
            {
                if (!boot.Slice(3, 4).SequenceEqual("NTFS"u8))
                    throw new InvalidDataException("Not an NTFS volume.");

                int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[0x0B..]);
                int sectorsPerCluster = boot[0x0D];
                if (sectorsPerCluster > 0x80)
                    sectorsPerCluster = 1 << (256 - sectorsPerCluster); // very large clusters are stored as a negative shift
                var clusterSize = bytesPerSector * sectorsPerCluster;

                var mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot[0x30..]);
                var recordValue = (sbyte)boot[0x40];
                var recordSize = recordValue > 0 ? recordValue * clusterSize : 1 << -recordValue;

                if (clusterSize <= 0 || recordSize < 512 || recordSize > 65536)
                    throw new InvalidDataException("Unexpected NTFS geometry.");
                return new VolumeGeometry(clusterSize, recordSize, mftLcn * clusterSize);
            }
        }

        /// <summary>Compact per-record scratch data, grown on demand and discarded after the tree is built.</summary>
        private sealed class RecordTable
        {
            private Entry[] _entries = new Entry[1 << 16];

            public long Count { get; private set; }

            public ref Entry Get(long frn)
            {
                if (frn >= _entries.Length)
                    Array.Resize(ref _entries, (int)Math.Min(Array.MaxLength, Math.Max(frn + 1, (long)_entries.Length * 2)));
                if (frn >= Count)
                    Count = frn + 1;
                return ref _entries[frn];
            }

            public static DateTime ToUtc(long fileTime) =>
                fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(fileTime) : default;

            public struct Entry
            {
                public string? Name;
                public long Parent;
                public long Size;
                public long ModifiedFileTime;
                public bool InUse;
                public bool IsDirectory;
            }
        }

        // ---------------------------------------------------------------- native

        private static SafeFileHandle OpenVolume(string root)
        {
            const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
            var device = @"\\.\" + root.TrimEnd('\\');
            var handle = CreateFileW(device, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new IOException($"Cannot open volume {device} (error {Marshal.GetLastPInvokeError()}).");
            return handle;
        }

        private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = RandomAccess.Read(handle, buffer[total..], offset + total);
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected end of volume.");
                total += read;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    }
}
