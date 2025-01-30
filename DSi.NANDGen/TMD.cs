using System.Buffers.Binary;

namespace DSi.NANDGen {

    // bare minimum for downloading content
    public class TMD {
        public class Content {
            public uint ID;
            public ushort Index;
            public ushort Type;
            public ulong Size;
            public byte[] Hash;
        }

        public Content[] Contents;

        public TMD(byte[] data) {
            // data we're interested in is at 0x1DE and 0x1E4+
            // this kinda sucks
            var numContents = BinaryPrimitives.ReadUInt16BigEndian(data[0x1DE..]);
            Contents = new Content[numContents];

            for (int i = 0; i < numContents; i++) {
                var index = 0x1E4 + (36 * i);
                Contents[i] = new();
                Contents[i].ID = BinaryPrimitives.ReadUInt32BigEndian(data[index..]);
                index += 4;
                Contents[i].Index = BinaryPrimitives.ReadUInt16BigEndian(data[index..]);
                index += 2;
                Contents[i].Type = BinaryPrimitives.ReadUInt16BigEndian(data[index..]);
                index += 2;
                Contents[i].Size = BinaryPrimitives.ReadUInt64BigEndian(data[index..]);
                index += 8;
                Contents[i].Hash = new byte[20];
                Array.Copy(data, index, Contents[i].Hash, 0, 20);
            }
        }
    }
}
