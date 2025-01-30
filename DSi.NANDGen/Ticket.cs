using System.Security.Cryptography;

namespace DSi.NANDGen {

    // bare minimum for downloading content
    public class Ticket {
        public byte[] TitleKey;

        public Ticket(byte[] data) {
            // data we're interested in is at 0x1BF
            TitleKey = new byte[16];
            Array.Copy(data, 0x1BF, TitleKey, 0, 16);
        }

        public byte[] DecryptKey(TitleID title) {
            var iv = new byte[16];
            Array.Copy(title.Bytes(), iv, 8);

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = Constants.CommonKey;
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(TitleKey, 0, 16);
        }
    }
}
