using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.RegularExpressions;

namespace DSi.NANDGen {
    // this is a minimal FAT filesystem implementation that implements just enough to make (and read) a disk image
    // there is no support for renaming, moving, updating, and deleting files/directories
    public class FAT : IDisposable {
        public class VBR {
            public byte[] BS_JmpBoot = [0xE9, 0x00, 0x00];
            public string BS_OEMName = "TWL";
            public ushort BPB_BytesPerSector = 512;
            public byte BPB_SectorsPerCluster = 32;
            public ushort BPB_ReservedSectorCount = 1;
            public byte BPB_NumFATs = 2;
            public ushort BPB_RootEntryCount = 512;
            public ushort BPB_UNUSED_TotalSectors16 = 0; // unused
            public byte BPB_Media = 0xF8;
            public ushort BPB_SectorsPerFAT;
            public ushort BPB_SectorsPerTrack = 0x20;
            public ushort BPB_NumHeads = 0x10;
            public uint BPB_HiddenSectors;
            public uint BPB_TotalSectors;
            public byte BS_DriveNum;
            public byte BS_Reserved = 0;
            public byte BS_BootSig = 0x29;
            public uint BS_VolID = 0x12345678;
            public string BS_VolLabel = "";
            public byte[] MiscData = new byte[456];
            public ushort BS_Sign = 0xAA55;

            public void Serialize(BinaryWriter writer) {
                writer.Write(BS_JmpBoot);
                writer.Write(Encoding.ASCII.GetBytes(BS_OEMName.PadRight(8)));
                writer.Write(BPB_BytesPerSector);
                writer.Write(BPB_SectorsPerCluster);
                writer.Write(BPB_ReservedSectorCount);
                writer.Write(BPB_NumFATs);
                writer.Write(BPB_RootEntryCount);
                writer.Write(BPB_UNUSED_TotalSectors16);
                writer.Write(BPB_Media);
                writer.Write(BPB_SectorsPerFAT);
                writer.Write(BPB_SectorsPerTrack);
                writer.Write(BPB_NumHeads);
                writer.Write(BPB_HiddenSectors);
                writer.Write(BPB_TotalSectors);
                writer.Write(BS_DriveNum);
                writer.Write(BS_Reserved);
                writer.Write(BS_BootSig);
                writer.Write(BS_VolID);
                writer.Write(Encoding.ASCII.GetBytes(BS_VolLabel.PadRight(11)));
                writer.Write(MiscData);
                writer.Write(BS_Sign);
            }

            public void Deserialize(BinaryReader reader) {
                reader.Read(BS_JmpBoot);
                var oemName = new byte[8];
                reader.Read(oemName);
                BS_OEMName = Encoding.ASCII.GetString(oemName);
                BPB_BytesPerSector = reader.ReadUInt16();
                BPB_SectorsPerCluster = reader.ReadByte();
                BPB_ReservedSectorCount = reader.ReadUInt16();
                BPB_NumFATs = reader.ReadByte();
                BPB_RootEntryCount = reader.ReadUInt16();
                BPB_UNUSED_TotalSectors16 = reader.ReadUInt16();
                BPB_Media = reader.ReadByte();
                BPB_SectorsPerFAT = reader.ReadUInt16();
                BPB_SectorsPerTrack = reader.ReadUInt16();
                BPB_NumHeads = reader.ReadUInt16();
                BPB_HiddenSectors = reader.ReadUInt32();
                BPB_TotalSectors = reader.ReadUInt32();
                BS_DriveNum = reader.ReadByte();
                BS_Reserved = reader.ReadByte();
                BS_BootSig = reader.ReadByte();
                BS_VolID = reader.ReadUInt32();
                var volumeLabel = new byte[11];
                reader.Read(volumeLabel);
                BS_VolLabel = Encoding.ASCII.GetString(volumeLabel);
                reader.Read(MiscData);
                BS_Sign = reader.ReadUInt16();
            }
        }

        public class LfnEntry {
            public byte Order;
            public byte[] Name1;
            public Attributes Attributes;
            public byte Type;
            public byte Checksum;
            public byte[] Name2;
            public byte[] Name3;

            public LfnEntry(string name, byte checksum, byte index, bool last) {
                Debug.Assert(name.Length <= 13);

                Order = index;
                if (last) Order |= 0x40;
                Attributes = Attributes.LongName;
                Type = 0;
                Checksum = checksum;

                Name1 = new byte[10];
                Name2 = new byte[12];
                Name3 = new byte[4];

                var combined = new byte[26];

                // unused bytes are filled with 0xFF
                Array.Fill<byte>(combined, 0xFF);

                var strBytes = Encoding.Unicode.GetBytes(name);
                Array.Copy(strBytes, combined, strBytes.Length);

                // names are NULL-terminated if they don't fill the entire 26 bytes
                if (strBytes.Length < 26) {
                    combined[strBytes.Length] = 0x00;
                    combined[strBytes.Length + 1] = 0x00;
                }

                Array.Copy(combined, 0, Name1, 0, Name1.Length);
                Array.Copy(combined, Name1.Length, Name2, 0, Name2.Length);
                Array.Copy(combined, Name1.Length + Name2.Length, Name3, 0, Name3.Length);
            }

            public string GetCombinedName() {
                var combined = Name1.Concat(Name2).Concat(Name3).ToArray();
                var length = combined.Length;

                for (var i = 0; i < combined.Length - 1; i += 2) {
                    if (combined[i] == 0x00 && combined[i + 1] == 0x00) {
                        length = i;
                        break;
                    }
                }

                return Encoding.Unicode.GetString(combined[..length]);
            }

            public LfnEntry() { }

            public void Serialize(BinaryWriter writer) {
                writer.Write(Order);
                writer.Write(Name1);
                writer.Write((byte)Attributes);
                writer.Write(Type);
                writer.Write(Checksum);
                writer.Write(Name2);
                writer.Write((ushort)0); // FstClusLO, must be zero
                writer.Write(Name3);
            }

            public void Deserialize(BinaryReader reader) {
                Order = reader.ReadByte();

                var name1 = new byte[10];
                reader.Read(name1);
                Name1 = name1;

                Attributes = (Attributes)reader.ReadByte();
                Type = reader.ReadByte();
                Checksum = reader.ReadByte();

                var name2 = new byte[12];
                reader.Read(name2);
                Name2 = name2;

                reader.ReadUInt16(); // skip

                var name3 = new byte[4];
                reader.Read(name3);
                Name3 = name3;
            }
        }

        public class DirEntry {
            public string Name;
            public string ShortName;
            public Attributes Attributes;
            public byte Reserved;
            public byte CreationTimeTenth;
            public ushort CreationTime;
            public ushort CreationDate = 0x2821; // 2000-01-01
            public ushort AccessDate;
            public ushort WriteTime;
            public ushort WriteDate = 0x2821; // 2000-01-01
            public uint FirstCluster;
            public uint Size;

            private LfnEntry[]? LfnEntries;

            public DirEntry(string name, Attributes attrs, uint firstCluster, uint size, DirEntry[]? existingEntries = null) {
                Name = name;
                Attributes = attrs;
                FirstCluster = firstCluster;
                Size = size;

                if (existingEntries is not null) {
                    // NOTE: this is not comprehensive, but it's good enough for us
                    // NOTE: we assume that the extension is sane, which it should be
                    #region SFN generation
                    var lossy = false;
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
                    var ext = Path.GetExtension(name).ToUpperInvariant();

                    if (nameWithoutExt.Length == 0 && ext.Length != 0) {
                        // probably some silly name with a . at the start
                        nameWithoutExt = ext;
                        ext = "";
                    }

                    // Path.GetExtension puts a . at the start of the extension if there is an extension
                    // we need to remove this
                    if (ext.StartsWith('.')) {
                        ext = ext[1..];
                    }

                    if (nameWithoutExt.Contains(' ')) {
                        lossy = true;
                        nameWithoutExt = nameWithoutExt.Replace(" ", "");
                    }

                    if (nameWithoutExt.Contains('.')) {
                        lossy = true;
                        nameWithoutExt = nameWithoutExt.Replace(".", "");
                    }

                    var badCharacterRegex = new Regex("[^A-Z0-9$%'\\-_@~`!(){}^#&\\x80-\\xFF]");
                    if (badCharacterRegex.IsMatch(nameWithoutExt)) {
                        lossy = true;
                        nameWithoutExt = badCharacterRegex.Replace(nameWithoutExt, "_");
                    }

                    if (nameWithoutExt.Length > 8) {
                        lossy = true;
                        nameWithoutExt = nameWithoutExt[..8];
                    }

                    if (ext.Length > 3) {
                        lossy = true;
                        ext = ext[..3];
                    }

                    if (!lossy) {
                        // we can use the file names as-is :3
                        // note that we still generate the LFN entry no matter what
                        // as that's what nintendo's fat driver seems to have done
                        ShortName = nameWithoutExt.PadRight(8) + ext.PadRight(3);
                    } else {
                        var suffixNum = 1;
                        var candidate = "";
                        while (suffixNum.ToString().Length < 7) {
                            candidate = nameWithoutExt;

                            if (candidate.Length > 8 - (suffixNum.ToString().Length + 1)) {
                                candidate = candidate[..(8 - (suffixNum.ToString().Length + 1))];
                            }

                            candidate += $"~{suffixNum}";

                            candidate = candidate.PadRight(8);
                            candidate += ext.PadRight(3);

                            // make sure this isn't in the existing entries
                            var alreadyExists = false;
                            foreach (var entry in existingEntries) {
                                if (entry.ShortName == candidate) {
                                    alreadyExists = true;
                                    break;
                                }
                            }

                            if (alreadyExists) {
                                suffixNum++;
                                continue;
                            } else {
                                break;
                            }
                        }

                        if (suffixNum.ToString().Length >= 7) {
                            // we ran out of unique suffixes, this is a problem
                            throw new Exception("Out of available suffixes for short file name");
                        }

                        ShortName = candidate;
                    }
                    #endregion
                    // figure out how many LFN entries we'll need
                    // max length of an LFN entry is 13 chars
                    var lfnEntries = (name.Length + 13 - 1) / 13;
                    LfnEntries = new LfnEntry[lfnEntries];

                    var nameRemaining = name;
                    byte index = 1;

                    while (nameRemaining.Length > 0) {
                        var substr = nameRemaining[..Math.Min(nameRemaining.Length, 13)];
                        nameRemaining = nameRemaining[substr.Length..];
                        LfnEntries[index - 1] = new LfnEntry(substr, GetChecksum(), index, substr.Length < 13);
                        index++;
                    }
                } else {
                    // if we didn't pass existing entries, don't do any LFN stuff
                    // this is used when making entries for "." and ".."
                    ShortName = name.PadRight(11);
                }
            }

            public DirEntry() { }

            private byte GetChecksum() {
                byte sum = 0;
                var shortBytes = Encoding.ASCII.GetBytes(ShortName);

                for (int i = 0; i < ShortName.Length; i++) {
                    sum = (byte)((sum >> 1) + (sum << 7) + shortBytes[i]);
                }

                return sum;
            }

            public int GetBytesNeeded() {
                return 32 + (32 * LfnEntries?.Length ?? 0);
            }

            public void Serialize(BinaryWriter writer) {
                // this needs to handle long file name encoding
                if (LfnEntries is not null) {
                    for (var i = LfnEntries.Length - 1; i >= 0; i--) {
                        LfnEntries[i].Serialize(writer);
                    }
                }

                Debug.Assert(ShortName.Length == 11);
                var shortName = Encoding.ASCII.GetBytes(ShortName);

                if (shortName[0] == 0xE5) shortName[0] = 0x05;

                writer.Write(shortName);
                writer.Write((byte)Attributes);
                writer.Write(Reserved);
                writer.Write(CreationTimeTenth);
                writer.Write(CreationTime);
                writer.Write(CreationDate);
                writer.Write(AccessDate);
                writer.Write((ushort)(FirstCluster >> 16));
                writer.Write(WriteTime);
                writer.Write(WriteDate);
                writer.Write((ushort)FirstCluster);
                writer.Write(Size);
            }

            public void Deserialize(BinaryReader reader) {
                var lfnList = new List<LfnEntry>();

                while (true) {
                    // check attributes to see if this is an LFN entry
                    reader.BaseStream.Seek(11, SeekOrigin.Current);
                    var attribs = reader.ReadByte();
                    reader.BaseStream.Seek(-12, SeekOrigin.Current);

                    if ((attribs & (byte)Attributes.LongNameMask) == (byte)Attributes.LongName) {
                        var entry = new LfnEntry();
                        entry.Deserialize(reader);
                        lfnList.Add(entry);
                    } else {
                        break;
                    }
                }

                var shortName = new byte[11];
                reader.Read(shortName);

                if (shortName[0] == 0x05) shortName[0] = 0xE5;

                if (lfnList.Count > 0) {
                    // the lfn list is read backwards, so we need to reverse it
                    // technically it could be in a random order but i doubt a fat driver would do that on purpose
                    lfnList.Reverse();
                    LfnEntries = [.. lfnList];
                    Name = "";

                    foreach (var entry in LfnEntries) {
                        Name += entry.GetCombinedName();
                    }
                } else {
                    // this should only ever be taken for "." and ".."
                    Name = Encoding.ASCII.GetString(shortName).Trim();
                }

                ShortName = Encoding.ASCII.GetString(shortName);
                Attributes = (Attributes)reader.ReadByte();
                Reserved = reader.ReadByte();
                CreationTimeTenth = reader.ReadByte();
                CreationTime = reader.ReadUInt16();
                CreationDate = reader.ReadUInt16();
                AccessDate = reader.ReadUInt16();
                FirstCluster = ((uint)reader.ReadUInt16()) << 16;
                WriteTime = reader.ReadUInt16();
                WriteDate = reader.ReadUInt16();
                FirstCluster |= reader.ReadUInt16();
                Size = reader.ReadUInt32();
            }
        }

        public enum FatType {
            FAT12,
            FAT16,
        }

        [Flags]
        public enum Attributes {
            ReadOnly = 1 << 0,
            Hidden = 1 << 1,
            System = 1 << 2,
            VolumeLabel = 1 << 3,
            Subdirectory = 1 << 4,
            Archive = 1 << 5,
            Reserved = 1 << 6,
            Reserved2 = 1 << 7,
            LongName = ReadOnly | Hidden | System | VolumeLabel,
            LongNameMask = LongName | Subdirectory | Archive,
        }

        private readonly Stream _file;
        private readonly BinaryReader reader;
        private readonly BinaryWriter writer;
        private bool disposed;

        public bool IsFormatted { get; private set; }
        public FatType Type { get; private set; }
        public VBR? Vbr { get; private set; }

        // this is ushort as it's the maximum size of an entry in the fat that we support
        // it will be serialized and deserialized according to Type
        public ushort[]? Fat { get; private set; }
        public ushort FatEndOfChain;

        public uint FatStartSector { get; private set; }
        public uint FatSectors { get; private set; }
        public uint RootDirStartSector { get; private set; }
        public uint RootDirSectors { get; private set; }
        public uint DataStartSector { get; private set; }
        public uint DataSectors { get; private set; }

        public FAT(Stream file) {
            _file = file;
            reader = new BinaryReader(file, Encoding.UTF8, true);
            writer = new BinaryWriter(file, Encoding.UTF8, true);

            // is this already formatted?
            // check for 0xAA55 at 0x1FE
            _file.Position = 0x1FE;
            var sig = reader.ReadUInt16();
            _file.Position = 0;

            if (sig == 0xAA55) {
                IsFormatted = true;

                Vbr = new VBR();

                Vbr.Deserialize(reader);
                Type = GetFatType(Vbr.BPB_TotalSectors / Vbr.BPB_SectorsPerCluster);

                FatStartSector = Vbr.BPB_ReservedSectorCount;
                FatSectors = (uint)(Vbr.BPB_SectorsPerFAT * Vbr.BPB_NumFATs);

                RootDirStartSector = FatStartSector + FatSectors;
                RootDirSectors = (32u * Vbr.BPB_RootEntryCount + Vbr.BPB_BytesPerSector - 1) / Vbr.BPB_BytesPerSector;

                DataStartSector = RootDirStartSector + RootDirSectors;
                DataSectors = Vbr.BPB_TotalSectors - DataStartSector;

                Fat = ParseFat();
            } else {
                IsFormatted = false;
            }
        }

        private FatType GetFatType(long clusterCount) {
            if (clusterCount <= 4085) {
                return FatType.FAT12;
            } else if (clusterCount >= 4086 && clusterCount <= 65525) {
                return FatType.FAT16;
            } else {
                throw new NotImplementedException("Disk image too big, FAT32 is unimplemented");
            }
        }

        public ushort[] ParseFat() {
            _file.Position = SectorsToBytes(FatStartSector);
            var lengthInBytes = SectorsToBytes(Vbr!.BPB_SectorsPerFAT);
            // TODO: support for FAT12

            if (Type == FatType.FAT16) {
                var length = lengthInBytes / 2;
                var fat = new ushort[length];

                for (var i = 0; i < length; i++) {
                    fat[i] = reader.ReadUInt16();
                }

                FatEndOfChain = fat[1];

                return fat;
            } else {
                // we can't calculate the length as easily i think
                var fatList = new List<ushort>();
                var i = 0;

                while (_file.Position < SectorsToBytes(FatStartSector) + lengthInBytes) {
                    if (i % 2 == 0) {
                        // even bytes always fill the first byte and the lower half of the second byte
                        var num = (ushort)reader.ReadByte();
                        num |= (ushort)((reader.ReadByte() & 0x0F) << 8);
                        fatList.Add(num);
                    } else {
                        // odd bytes always fill the upper half of the first byte and the entire second byte
                        _file.Position -= 1;
                        ushort num = (ushort)((reader.ReadByte() & 0xF0) >> 4);
                        num |= (ushort)(reader.ReadByte() << 4);
                        fatList.Add(num);
                    }
                    i++;
                }

                FatEndOfChain = fatList[1];
                return [.. fatList];
            }
        }

        public void WriteFat() {
            if (!IsFormatted) throw new UnformattedException();

            _file.Position = SectorsToBytes(FatStartSector);

            if (Type == FatType.FAT16) {
                for (var i = 0; i < Fat!.Length; i++) {
                    writer.Write(Fat[i]);
                }

                // position should now be at the second fat copy
                for (var i = 0; i < Fat.Length; i++) {
                    writer.Write(Fat![i]);
                }
            } else {
                // each entry is 1.5 bytes :(
                // this is really annoying to handle

                for (var i = 0; i < Fat!.Length; i++) {
                    if (i % 2 == 0) {
                        // even bytes always fill the first byte and the lower half of the second byte
                        writer.Write((byte)Fat[i]);
                        writer.Write((byte)(Fat[i] >> 8));
                    } else {
                        // odd bytes always fill the upper half of the first byte and the entire second byte
                        _file.Position -= 1;
                        var lower = reader.ReadByte();
                        _file.Position -= 1;
                        writer.Write((byte)(lower | (byte)(Fat[i] & 0xF) << 4));
                        writer.Write((byte)(Fat[i] >> 4));
                    }
                }
            }
        }

        private uint GetFreeClusterIndex(uint start = 2u) {
            if (!IsFormatted) throw new UnformattedException();

            // loop through the fat until we find a free slot
            for (var i = start; i < Fat!.Length; i++) {
                if (Fat[i] == 0x00) return i;
            }

            throw new OutOfClustersException();
        }

        private uint[] GetClusterChain(uint startIndex) {
            var clusters = new List<uint>();

            var curIndex = startIndex;

            while (true) {
                // first two sectors in the FAT are reserved, so we really start at 2
                clusters.Add(curIndex - 2);
                curIndex = Fat![curIndex];
                if (curIndex == FatEndOfChain) break;
            }

            return [.. clusters];
        }

        private uint SectorsToBytes(uint sectors) {
            return sectors * Vbr!.BPB_BytesPerSector;
        }

        private uint ClustersToBytes(uint clusters) {
            return clusters * SectorsToBytes(Vbr!.BPB_SectorsPerCluster);
        }

        public void Format(uint partitionOffset, byte driveNumber, VBR? vbr = null) {
            if (vbr is not null) {
                Vbr = vbr;
            } else {
                Vbr = new VBR();
            }

            var totalClusters = _file.Length / Vbr.BPB_BytesPerSector / Vbr.BPB_SectorsPerCluster;
            Type = GetFatType(totalClusters);

            if (Type == FatType.FAT16) {
                // FAT16: 1 sector on each copy of FAT for every 256 clusters
                Vbr.BPB_SectorsPerFAT = (ushort)(totalClusters / 256 + 1);
            } else {
                // FAT12: 3 sectors on each copy of FAT for every 1024 clusters
                Vbr.BPB_SectorsPerFAT = (ushort)((totalClusters / 1024 + 1) * 3);
            }

            Vbr.BPB_HiddenSectors = partitionOffset / Vbr.BPB_BytesPerSector;
            Vbr.BPB_TotalSectors = (uint)(_file.Length / Vbr.BPB_BytesPerSector);
            Vbr.BS_DriveNum = driveNumber;

            FatStartSector = Vbr.BPB_ReservedSectorCount;
            FatSectors = (uint)(Vbr.BPB_SectorsPerFAT * Vbr.BPB_NumFATs);

            RootDirStartSector = FatStartSector + FatSectors;
            RootDirSectors = (32u * Vbr.BPB_RootEntryCount + Vbr.BPB_BytesPerSector - 1) / Vbr.BPB_BytesPerSector;

            DataStartSector = RootDirStartSector + RootDirSectors;
            DataSectors = Vbr.BPB_TotalSectors - DataStartSector;

            // write out the VBR
            _file.Position = 0x00;
            Vbr.Serialize(writer);

            Fat = ParseFat();

            if (Type == FatType.FAT12) {
                // fat ID
                Fat[0] = (ushort)(0xF00 | Vbr.BPB_Media);
                // end of chain indicator
                Fat[1] = 0xFFF;
            } else {
                // fat ID
                Fat[0] = (ushort)(0xFF00 | Vbr.BPB_Media);
                // end of chain indicator
                Fat[1] = 0xFFFF;
            }

            FatEndOfChain = Fat[1];
            IsFormatted = true;

            WriteFat();
        }

        private List<DirEntry> GetEntries(uint start, uint end) {
            _file.Position = start;
            var entries = new List<DirEntry>();

            // deserialize entries until we hit 0x00
            // we skip any entries that start with 0xE5 as they've been "deleted"
            while (_file.Position < end) {
                var firstByte = reader.ReadByte();
                _file.Position--;

                if (firstByte == 0x00) break;
                if (firstByte == 0xE5) {
                    // skip over entry
                    _file.Position += 32;
                    continue;
                };

                var entry = new DirEntry();
                entry.Deserialize(reader);
                entries.Add(entry);
            }

            return entries;
        }

        public DirEntry[] GetEntries(string path) {
            if (!IsFormatted) throw new UnformattedException();

            if (path == "\\") {
                return [.. GetEntries(SectorsToBytes(RootDirStartSector), SectorsToBytes(RootDirStartSector) + SectorsToBytes(RootDirSectors))];
            } else {
                var entry = GetEntry(path);

                // now get the cluster chain for the directory
                var clusterChain = GetClusterChain(entry.FirstCluster);
                Debug.Assert(clusterChain.Length > 0);

                var entries = new List<DirEntry>();

                // now build the directory listing
                foreach (var cluster in clusterChain) {
                    var start = SectorsToBytes(DataStartSector) + ClustersToBytes(cluster);
                    var end = start + ClustersToBytes(1);
                    entries.AddRange(GetEntries(start, end));
                }

                return [.. entries];
            }
        }

        public DirEntry GetEntry(string path) {
            if (!IsFormatted) throw new UnformattedException();

            var name = path.Split('\\')[^1];
            var prevDir = string.Join('\\', path.Split('\\')[..^1]);
            if (prevDir == "") prevDir = "\\";

            var prevEntries = GetEntries(prevDir);

            return prevEntries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase)) ?? throw new DirectoryNotFoundException();
        }

        public byte[] ReadFile(string path) {
            if (!IsFormatted) throw new UnformattedException();

            var entry = GetEntry(path);

            var clusterChain = GetClusterChain(entry.FirstCluster);
            Debug.Assert(clusterChain.Length > 0);

            var data = new byte[entry.Size];
            var dataLeft = entry.Size;
            var dataOffset = 0u;

            foreach (var cluster in clusterChain) {
                _file.Position = SectorsToBytes(DataStartSector) + ClustersToBytes(cluster);

                if (dataLeft < ClustersToBytes(1)) {
                    _file.Read(data, (int)dataOffset, (int)dataLeft);
                    dataOffset += dataLeft;
                    dataLeft -= dataLeft;
                } else {
                    // read entire cluster
                    _file.Read(data, (int)dataOffset, (int)ClustersToBytes(1));
                    dataOffset += ClustersToBytes(1);
                    dataLeft -= ClustersToBytes(1);
                }
            }

            Debug.Assert(dataLeft == 0);

            return data;
        }

        private bool AddEntry(DirEntry entry, uint start, uint end) {
            _file.Position = start;
            if (start + entry.GetBytesNeeded() > end) return false;

            while (_file.Position + entry.GetBytesNeeded() <= end) {
                var firstByte = reader.ReadByte();
                _file.Position--;

                // both of these indicate that we're free to use the entry
                if (firstByte == 0xE5 || firstByte == 0x00) {
                    // make sure that we have enough space for all of our entries
                    var curPos = _file.Position;
                    var enoughSpace = true;
                    var neededEntries = entry.GetBytesNeeded() / 32;

                    for (var i = 1; i < neededEntries; i++) {
                        _file.Position = curPos + i * 32;
                        var firstByte2 = reader.ReadByte();

                        if (firstByte2 != 0xE5 && firstByte2 != 0x00) {
                            enoughSpace = false;
                            break;
                        }
                    }

                    _file.Position = curPos;

                    if (!enoughSpace) {
                        _file.Position += 32;
                        continue;
                    }

                    // okay, we should be good now
                    entry.Serialize(writer);
                    return true;
                } else {
                    _file.Position += 32;
                }
            }

            return false;
        }

        private void AddEntry(DirEntry entry, string dir, uint firstCluster, uint prevDirFirstCluster) {
            if (dir == "\\") {
                if (!AddEntry(entry, SectorsToBytes(RootDirStartSector), SectorsToBytes(RootDirStartSector) + SectorsToBytes(RootDirSectors))) {
                    throw new OutOfEntriesException();
                }
            } else {
                // we'll need to try each entry in the cluster chain to find a free spot
                var clusterChain = GetClusterChain(prevDirFirstCluster);
                var success = false;

                foreach (var cluster in clusterChain) {
                    var clusterStart = SectorsToBytes(DataStartSector) + ClustersToBytes(cluster);
                    var clusterEnd = clusterStart + ClustersToBytes(1);
                    if (AddEntry(entry, clusterStart, clusterEnd)) {
                        success = true;
                        break;
                    }
                }

                // add a new cluster to the previous directory if we couldn't find a free spot
                if (!success) {
                    // there can only be 65536 entries in a directory (0x200000 bytes), so make sure we're not going over
                    if (clusterChain.Length >= 0x200000 / Vbr!.BPB_BytesPerSector / Vbr.BPB_SectorsPerCluster) throw new OutOfEntriesException();
                    // we need to add a new entry to the cluster chain
                    var newCluster = GetFreeClusterIndex(firstCluster + 1);
                    // clear out the new cluster
                    var clusterStart = SectorsToBytes(DataStartSector) + ClustersToBytes(newCluster - 2);
                    var clusterEnd = clusterStart + ClustersToBytes(1);
                    ClearRange(clusterStart, ClustersToBytes(1));

                    // this should not fail
                    AddEntry(entry, clusterStart, clusterEnd);

                    // mark the cluster as used
                    Fat![newCluster] = FatEndOfChain;
                    // update the end of the cluster chain to point to our new cluster
                    Fat[clusterChain[^1] + 2] = (ushort)newCluster;
                }
            }
        }

        private void ClearRange(uint start, uint length) {
            _file.Position = start;
            var zeroes = new byte[length];
            writer.Write(zeroes);
        }

        public bool EntryExists(string path) {
            var name = path.Split('\\')[^1];
            var dir = string.Join('\\', path.Split('\\')[..^1]);
            if (dir == "") dir = "\\";

            if (GetEntries(dir).FirstOrDefault(e => e.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase)) != null) {
                return true;
            } else {
                return false;
            }
        }

        public bool EntryIsDirectory(string path) {
            var entry = GetEntry(path);
            return entry.Attributes.HasFlag(Attributes.Subdirectory);
        }

        public bool EntryIsFile(string path) {
            var entry = GetEntry(path);
            return !entry.Attributes.HasFlag(Attributes.Subdirectory) && entry.Attributes.HasFlag(Attributes.Archive);
        }

        public void CreateDirectory(string path) {
            if (!IsFormatted) throw new UnformattedException();

            var name = path.Split('\\')[^1];
            var dir = string.Join('\\', path.Split('\\')[..^1]);
            if (dir == "") dir = "\\";

            // make sure that the entry doesn't already exist
            if (EntryExists(path)) {
                // it's fine if it's a directory that already exists
                if (EntryIsDirectory(path)) {
                    return;
                } else {
                    throw new IOException("The directory name is already in use by a file");
                }
            }

            var firstCluster = GetFreeClusterIndex();
            var start = SectorsToBytes(DataStartSector) + ClustersToBytes(firstCluster - 2);
            var end = start + ClustersToBytes(1);

            // clear out the cluster
            ClearRange(start, ClustersToBytes(1));

            var entry = new DirEntry(name, Attributes.Subdirectory, firstCluster, 0, GetEntries(dir));
            var prevDirFirstCluster = 0u;

            if (dir != "\\") {
                var prevEntry = GetEntry(dir);
                prevDirFirstCluster = prevEntry.FirstCluster;
            }

            // we need to add "." and ".." to the new directory
            // it's okay if we fail later on in the process since we won't mark the cluster as used unless we succeed
            // "." points to our own cluster
            var dot = new DirEntry(".", Attributes.Subdirectory, firstCluster, 0);
            // ".." points to the cluster of the previous directory (0 if previous directory is root)
            var dotDot = new DirEntry("..", Attributes.Subdirectory, prevDirFirstCluster, 0);
            // now add both directories to our new cluster
            // these should not fail so there's no point in checking the return value
            AddEntry(dot, start, end);
            AddEntry(dotDot, start, end);

            // now add the new entry to the previous directory
            AddEntry(entry, dir, firstCluster, prevDirFirstCluster);

            // now we can mark our directory's new cluster as used
            Fat![firstCluster] = FatEndOfChain;
            WriteFat();
        }

        public void CreateFile(string path, byte[] data, bool readOnly = false) {
            if (!IsFormatted) throw new UnformattedException();

            var name = path.Split('\\')[^1];
            var dir = string.Join('\\', path.Split('\\')[..^1]);
            if (dir == "") dir = "\\";

            // make sure that the entry doesn't already exist
            if (EntryExists(path)) throw new IOException("Entry already exists");

            // figure out how many clusters we need for the data
            var clusterCount = (data.Length + ClustersToBytes(1) - 1) / ClustersToBytes(1);
            var clusterChain = new uint[clusterCount];
            var lastClusterIndex = 1u; // 1 instead of 2 because we add 1 :3

            for (var i = 0; i < clusterCount; i++) {
                clusterChain[i] = GetFreeClusterIndex(lastClusterIndex + 1) - 2;
                lastClusterIndex = clusterChain[i] + 2;
            }

            // copy in our data
            var dataLeft = (uint)data.Length;
            var dataOffset = 0u;

            foreach (var cluster in clusterChain) {
                _file.Position = SectorsToBytes(DataStartSector) + ClustersToBytes(cluster);

                if (dataLeft < ClustersToBytes(1)) {
                    // since we won't fill the whole cluster, we should zero it first
                    // (we could probably get away with not doing so, but who knows how nintendo's fat driver handles it)
                    ClearRange((uint)_file.Position, ClustersToBytes(1));
                    _file.Position = SectorsToBytes(DataStartSector) + ClustersToBytes(cluster);
                    _file.Write(data, (int)dataOffset, (int)dataLeft);
                    dataOffset += dataLeft;
                    dataLeft -= dataLeft;
                } else {
                    // write entire cluster
                    _file.Write(data, (int)dataOffset, (int)ClustersToBytes(1));
                    dataOffset += ClustersToBytes(1);
                    dataLeft -= ClustersToBytes(1);
                }
            }

            Debug.Assert(dataLeft == 0);

            // now make the entry
            var entry = new DirEntry(name, Attributes.Archive, clusterChain[0] + 2, (uint)data.Length, GetEntries(dir));
            if (readOnly) entry.Attributes |= Attributes.ReadOnly;

            var prevDirFirstCluster = 0u;

            if (dir != "\\") {
                var prevEntry = GetEntry(dir);
                prevDirFirstCluster = prevEntry.FirstCluster;
            }

            AddEntry(entry, dir, lastClusterIndex + 1, prevDirFirstCluster);

            // now set up the cluster chain
            for (var i = 0; i < clusterCount; i++) {
                Fat![clusterChain[i] + 2] = (ushort)(i == clusterCount - 1 ? FatEndOfChain : clusterChain[i + 1] + 2);
            }

            WriteFat();
        }

        protected virtual void Dispose(bool disposing) {
            if (!disposed) {
                if (disposing) {
                    reader.Dispose();
                    writer.Dispose();
                }

                disposed = true;
            }
        }

        public void Dispose() {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    [Serializable]
    internal class OutOfEntriesException : Exception {
        public OutOfEntriesException() : base("The directory table is full") {
        }

        public OutOfEntriesException(string? message) : base(message) {
        }

        public OutOfEntriesException(string? message, Exception? innerException) : base(message, innerException) {
        }
    }

    [Serializable]
    internal class OutOfClustersException : Exception {
        public OutOfClustersException() : base("The disk image is out of free clusters") {
        }

        public OutOfClustersException(string? message) : base(message) {
        }

        public OutOfClustersException(string? message, Exception? innerException) : base(message, innerException) {
        }
    }

    [Serializable]
    internal class UnformattedException : Exception {
        public UnformattedException() : base("The disk image is unformatted") {
        }

        public UnformattedException(string? message) : base(message) {
        }

        public UnformattedException(string? message, Exception? innerException) : base(message, innerException) {
        }
    }
}
