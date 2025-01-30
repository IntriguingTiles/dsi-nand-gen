using System.Numerics;

namespace DSi.NANDGen {
    public static class ByteArrayExtensions {
        public static UInt128 ToUInt128(this byte[] data) {
            var ret = new UInt128();
            var shift = 15;

            foreach (var b in data) {
                ret |= ((UInt128)b) << shift * 8;
                shift--;
            }

            return ret;
        }
    }

    public static class UInt128Extensions {
        public static UInt128 Reverse4Bytes(this UInt128 value) {
            var ret = new UInt128();
            var rightShift = 0;
            var leftShift = 96;

            for (var i = 0; i < 4; i++) {
                UInt128 tmp = (uint)(value >> rightShift);
                tmp <<= leftShift;
                ret |= tmp;
                rightShift += 8 * 4;
                leftShift -= 8 * 4;
            }

            return ret;
        }
    }
}
