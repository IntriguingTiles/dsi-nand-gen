using System.Text;

namespace DSi.NANDGen {
    public struct TitleID(string combined) {
        public string High = combined.Substring(0, 8);
        public string Low = combined.Substring(8, 8);
        public string? Version;

        public override readonly string ToString() {
            return $"{High}-{Low} ({Encoding.ASCII.GetString(Convert.FromHexString(Low))}){(Version is not null ? $" v{Version}" : "")}";
        }

        public readonly string Combined() {
            return $"{High}{Low}";
        }

        public readonly byte[] Bytes() {
            return BitConverter.GetBytes(Convert.ToInt64(Combined(), 16)).Reverse().ToArray();
        }

        public TitleID(string combined, string version) : this(combined) {
            Version = version;
        }
    }
}
