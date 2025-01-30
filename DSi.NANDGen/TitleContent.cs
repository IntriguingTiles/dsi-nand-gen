using System.Security.Cryptography;

namespace DSi.NANDGen {
    public class TitleContent(byte[] data) {
        public byte[] EncryptedContent = data;

        public byte[] DecryptContent(byte[] key, byte[] iv) {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(EncryptedContent, 0, EncryptedContent.Length);
        }
    }
}
