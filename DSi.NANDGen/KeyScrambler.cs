using System.Numerics;

namespace DSi.NANDGen {
    public static class KeyScrambler {
        public enum KeyYType {
            ES,
            NAND,
            // STAGE2??
        }

        private static readonly UInt128 esKeyY = new(0x8B5ACCE572C9D056, 0xDCE8179CA9361239);
        private static readonly UInt128 nandKeyY = new(0x0AB9DC76BD4DC4D3, 0x202DDD1DE1A00005);
        private static readonly byte[] constant = [0xFF, 0xFE, 0xFB, 0x4E, 0x29, 0x59, 0x02, 0x58, 0x2A, 0x68, 0x0F, 0x5F, 0x1A, 0x4F, 0x3E, 0x79];

        public static byte[] Scramble(UInt128 keyX, KeyYType keyYType) {
            var keyY = keyYType == KeyYType.ES ? esKeyY : nandKeyY;
            var xored = keyX ^ keyY;
            xored = xored.Reverse4Bytes();
            xored += new UInt128(0xFFFEFB4E29590258, 0x2A680F5F1A4F3E79);
            xored = UInt128.RotateLeft(xored, 42);
            var tmp = ((BigInteger)xored).ToByteArray(true, false);

            return tmp;
        }

        // reference implementation from TWLtool (i think?), known to be correct
        public static byte[] Scramble(byte[] keyX, byte[] keyY) {
            byte[] tmp = new byte[16];

            for (int i = 0; i < 16; i++)
                tmp[i] = (byte)(keyX[i] ^ keyY[i]);

            uint carry = 0;
            for (int i = 0; i < 16; i++) {
                uint res = (uint)(tmp[i] + constant[15 - i] + carry);
                tmp[i] = (byte)(res & 0xFF);
                carry = res >> 8;
            }

            ROL16(tmp, 42);
            return tmp;
        }

        private static void ROL16(byte[] val, int n) {
            int n_coarse = n >> 3;
            int n_fine = n & 7;
            byte[] tmp = new byte[16];

            for (uint i = 0; i < 16; i++) {
                tmp[i] = val[(i - n_coarse) & 0xF];
            }

            for (uint i = 0; i < 16; i++) {
                val[i] = (byte)((tmp[i] << n_fine) | (tmp[(i - 1) & 0xF] >> (8 - n_fine)));
            }
        }
    }
}
