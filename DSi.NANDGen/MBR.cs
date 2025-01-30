namespace DSi.NANDGen {
    public class MBR {
        public class CHS {
            public byte Heads;
            public byte Sectors;
            public ushort Cylinders;

            public CHS(uint lba) {
                Heads = (byte)(lba / Constants.DriveSectors % Constants.DriveHeads);
                Sectors = (byte)((lba % Constants.DriveSectors) + 1);
                Cylinders = (ushort)(lba / (Constants.DriveHeads * Constants.DriveSectors));
            }

            public CHS() { }

            public void Serialize(BinaryWriter writer) {
                writer.Write(Heads);
                writer.Write((byte)((Sectors & 0x3F) | ((Cylinders & 0x300) >> 2)));
                writer.Write((byte)Cylinders);
            }

            public void Deserialize(BinaryReader reader) {
                Heads = reader.ReadByte();
                Sectors = (byte)(reader.ReadByte() & 0x3F);
                reader.BaseStream.Position--;
                Cylinders = (ushort)(((reader.ReadByte() & 0xC0) << 2) | reader.ReadByte());
            }
        }

        public class Partition {
            public enum PartitionType {
                None,
                FAT12,
                FAT16B = 6,
            };

            public byte Status = 0;
            public CHS FirstSector;
            public PartitionType Type;
            public CHS LastSector;
            public uint LBAFirstSector = 0;
            public uint NumSectors = 0;

            public Partition(PartitionType type, uint lbaFirstSector, uint numSectors) {
                FirstSector = new(lbaFirstSector);
                Type = type;
                LastSector = new(lbaFirstSector + numSectors - 1);
                LBAFirstSector = lbaFirstSector;
                NumSectors = numSectors;
            }

            public Partition(PartitionType type) {
                FirstSector = new();
                Type = type;
                LastSector = new();
            }

            public void Serialize(BinaryWriter writer) {
                writer.Write(Status);
                FirstSector.Serialize(writer);
                writer.Write((byte)Type);
                LastSector.Serialize(writer);
                writer.Write(LBAFirstSector);
                writer.Write(NumSectors);
            }

            public void Deserialize(BinaryReader reader) {
                Status = reader.ReadByte();
                FirstSector.Deserialize(reader);
                Type = (PartitionType)reader.ReadByte();
                LastSector.Deserialize(reader);
                LBAFirstSector = reader.ReadUInt32();
                NumSectors = reader.ReadUInt32();
            }
        }

        public byte[] BootCode = new byte[446];
        public Partition[] Partitions;
        public ushort Signature = 0xAA55;

        public MBR(Partition[] partitions) {
            Partitions = partitions;
        }

        public MBR() { }

        public void Serialize(BinaryWriter writer) {
            if (Partitions.Length != 4) throw new InvalidDataException("There must be exactly 4 partitions");
            writer.Write(BootCode);

            foreach (var partition in Partitions) {
                partition.Serialize(writer);
            }

            writer.Write(Signature);
        }

        public void Deserialize(BinaryReader reader) {
            BootCode = reader.ReadBytes(446);
            Partitions = new Partition[4];

            for (var i = 0; i < Partitions.Length; i++) {
                Partitions[i] = new Partition(Partition.PartitionType.None, 0, 0);
                Partitions[i].Deserialize(reader);
            }

            Signature = reader.ReadUInt16();
        }
    }
}
