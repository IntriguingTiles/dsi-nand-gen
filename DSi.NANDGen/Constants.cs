namespace DSi.NANDGen {
    public static class Constants {
        public static readonly byte[] CommonKey = [0xAF, 0x1B, 0xF5, 0x16, 0xA8, 0x07, 0xD2, 0x1A, 0xEA, 0x45, 0x98, 0x4F, 0x04, 0x74, 0x28, 0x61];
        public static readonly ushort DriveCylinders = 1024;
        public static readonly byte DriveHeads = 16;
        public static readonly byte DriveSectors = 32;
    }
}
