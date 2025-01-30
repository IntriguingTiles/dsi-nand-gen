using System;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;

namespace DSi.NANDGen {
    public class ES {
        public class ESFooter {
            public byte[] Mac;
            public byte Fixed;
            public byte[] Nonce;
            public int Length;

            public ESFooter(byte[] mac, byte[] nonce, int length) {
                Mac = mac;
                Fixed = 0x3A;
                Nonce = nonce;
                Length = length;
            }

            public ESFooter(byte[] key, byte[] fullFooter) {
                Mac = new byte[16];
                Nonce = new byte[12];

                Array.Copy(fullFooter, Mac, 16);
                Array.Copy(fullFooter, 0x11, Nonce, 0, 12);

                var iv = new byte[16];
                Array.Copy(Nonce, 0, iv, 1, 12);

                var decryptedLowerFooter = new byte[16];
                var ctr = new AES.CTR(key, iv);
                ctr.TransformBlock(fullFooter[16..], decryptedLowerFooter);

                Fixed = decryptedLowerFooter[0];

                if (Fixed != 0x3A) {
                    throw new InvalidDataException($"ES footer decryption failed, expected 0x3A but found 0x{Fixed:X02}. Either the data is invalid or the key is wrong.");
                }

                Length = decryptedLowerFooter[13] << 16 | decryptedLowerFooter[13] << 8 | decryptedLowerFooter[15];
            }

            public byte[] Serialize(byte[] key) {
                var fullFooter = new byte[32];
                var lowerFooter = new byte[16];
                var encryptedLowerFooter = new byte[16];

                Array.Copy(Mac, fullFooter, 16);
                lowerFooter[0] = Fixed;
                lowerFooter[13] = (byte)(Length >> 16);
                lowerFooter[14] = (byte)(Length >> 8);
                lowerFooter[15] = (byte)Length;

                var iv = new byte[16];
                Array.Copy(Nonce, 0, iv, 1, 12);

                var ctr = new AES.CTR(key, iv);
                ctr.TransformBlock(lowerFooter, encryptedLowerFooter);

                Array.Copy(Nonce, 0, encryptedLowerFooter, 1, 12);
                Array.Copy(encryptedLowerFooter, 0, fullFooter, 16, 16);

                return fullFooter;
            }
        }

        public byte[] Key;

        public ES(byte[] key) {
            Key = key;
        }

        public byte[] Decrypt(byte[] data) {
            if (data.Length > (0x20000 + 0x20)) {
                throw new NotImplementedException("Data is too long, multiple blocks is unimplemented.");
            }

            var footer = new ESFooter(Key, data[(data.Length - 32)..]);
            var output = new byte[data.Length - 32];
            var mac = new byte[16];
            var ccm = new AES.CCM(Key, footer.Nonce, 16, data.Length - 32);
            ccm.Decrypt(data, output, data.Length - 32, mac);

            if (!mac.SequenceEqual(footer.Mac)) {
                throw new InvalidDataException("ES data decryption failed, MAC mismatch.");
            }

            return output;
        }

        public byte[] Encrypt(byte[] data, byte[]? nonce = null) {
            if (data.Length > 0x20000) {
                throw new NotImplementedException("Data is too long, multiple blocks is unimplemented.");
            }

            if (nonce == null) {
                nonce = new byte[16];
                Random.Shared.NextBytes(nonce);
            }

            // need space for the footer
            var output = new byte[data.Length + 32];
            var mac = new byte[16];
            var ccm = new AES.CCM(Key, nonce, 16, data.Length);
            ccm.Encrypt(data, output, data.Length, mac);

            var footer = new ESFooter(mac, nonce, data.Length);
            var footerData = footer.Serialize(Key);
            Array.Copy(footerData, 0, output, data.Length, 32);

            return output;
        }
    }

    namespace AES {

        // this implementation comes from neimod's taddy, which uses polarssl and is thus GPLv2+
        public class CTR {
            public byte[] Ctr;
            public byte[] Key;
            private readonly Aes _aes;

            public CTR(byte[] key, byte[] ctr) {
                Key = key.Reverse().ToArray();
                Ctr = ctr.Reverse().ToArray();
                _aes = Aes.Create();
                _aes.Mode = CipherMode.ECB;
                _aes.Padding = PaddingMode.None;
            }

            public CTR(byte[] key, UInt128 ctr) : this(key, ((BigInteger)ctr).ToByteArray(true, false)) { }

            private void AddCtr(byte carry) {
                byte sum;

                for (int i = 15; i >= 0; i--) {
                    sum = (byte)(Ctr[i] + carry);

                    if (sum < Ctr[i])
                        carry = 1;
                    else
                        carry = 0;

                    Ctr[i] = sum;
                }
            }

            public void TransformBlock(byte[] input, byte[] output) {
                Debug.Assert(input.Length == output.Length);
                var enc = _aes.CreateEncryptor(Key, new byte[16]);
                var stream = new byte[16];

                enc.TransformBlock(Ctr, 0, Ctr.Length, stream, 0);

                for (int i = 0; i < 16; i++) {
                    output[i] = (byte)(stream[15 - i] ^ input[i]);
                }

                AddCtr(1);
            }

            public void TransformBlock(byte[] output) {
                var enc = _aes.CreateEncryptor(Key, new byte[16]);
                var stream = new byte[16];

                enc.TransformBlock(Ctr, 0, Ctr.Length, stream, 0);

                for (int i = 0; i < 16; i++) {
                    output[i] = stream[15 - i];
                }

                AddCtr(1);
            }
        }

        // ditto
        public class CCM {
            public byte[] Key;
            public byte[] Mac;
            public byte[] S0;
            private readonly Aes _aes;
            private readonly CTR _ctr;

            public CCM(byte[] key, byte[] nonce, int macLength, int payloadLength) {
                Key = key.Reverse().ToArray();
                Mac = new byte[16];
                S0 = new byte[16];

                // pure magic, god i hate cryptography
                var macLengthMagic = (macLength - 2) / 2;
                var payloadLengthMagic = (payloadLength + 15) & ~15;

                Mac[0] = (byte)((macLengthMagic << 3) | 2);

                for (int i = 0; i < 12; i++) {
                    Mac[i + 1] = nonce[11 - i];
                }

                Mac[13] = (byte)(payloadLengthMagic >> 16);
                Mac[14] = (byte)(payloadLengthMagic >> 8);
                Mac[15] = (byte)payloadLengthMagic;

                _aes = Aes.Create();
                _aes.Mode = CipherMode.ECB;
                _aes.Padding = PaddingMode.None;

                var enc = _aes.CreateEncryptor(Key, new byte[16]);
                enc.TransformBlock(Mac, 0, Mac.Length, Mac, 0);

                var ctr = new byte[16];

                ctr[0] = 2;

                for (int i = 0; i < 12; i++) {
                    ctr[i + 1] = nonce[11 - i];
                }

                _ctr = new CTR(key, ctr.Reverse().ToArray());
                _ctr.TransformBlock(S0);
            }

            public void DecryptBlock(byte[] input, byte[] output, byte[] mac) {
                _ctr.TransformBlock(input, output);

                for (int i = 0; i < 16; i++) {
                    Mac[i] ^= output[15 - i];
                }

                var enc = _aes.CreateEncryptor(Key, new byte[16]);
                enc.TransformBlock(Mac, 0, Mac.Length, Mac, 0);

                for (int i = 0; i < 16; i++) {
                    mac[i] = (byte)(Mac[15 - i] ^ S0[i]);
                }
            }

            public void Decrypt(byte[] input, byte[] output, int size, byte[] mac) {
                var offset = 0;
                var block = new byte[16];
                var ctr = new byte[16];

                while (size > 16) {
                    var inputBlock = new byte[16];
                    var outputBlock = new byte[16];

                    Array.Copy(input, offset, inputBlock, 0, 16);
                    DecryptBlock(inputBlock, outputBlock, mac);
                    Array.Copy(outputBlock, 0, output, offset, 16);

                    offset += 16;
                    size -= 16;
                }

                // memcpy(dest, src, size)
                Array.Copy(_ctr.Ctr, ctr, 16);
                Array.Clear(block, 0, 16);
                _ctr.TransformBlock(block, block);
                Array.Copy(ctr, _ctr.Ctr, 16);
                Array.Copy(input, offset, block, 0, size);

                DecryptBlock(block, block, mac);

                Array.Copy(block, 0, output, offset, size);
            }

            public void EncryptBlock(byte[] input, byte[] output, byte[] mac) {
                for (int i = 0; i < 16; i++) {
                    Mac[i] ^= input[15 - i];
                }

                var enc = _aes.CreateEncryptor(Key, new byte[16]);
                enc.TransformBlock(Mac, 0, Mac.Length, Mac, 0);

                for (int i = 0; i < 16; i++) {
                    mac[i] = (byte)(Mac[15 - i] ^ S0[i]);
                }

                _ctr.TransformBlock(input, output);
            }

            public void Encrypt(byte[] input, byte[] output, int size, byte[] mac) {
                var offset = 0;
                var block = new byte[16];

                while (size > 16) {
                    var inputBlock = new byte[16];
                    var outputBlock = new byte[16];

                    Array.Copy(input, offset, inputBlock, 0, 16);
                    EncryptBlock(inputBlock, outputBlock, mac);
                    Array.Copy(outputBlock, 0, output, offset, 16);

                    offset += 16;
                    size -= 16;
                }

                // memcpy(dest, src, size)
                Array.Copy(input, offset, block, 0, size);
                EncryptBlock(block, block, mac);
                Array.Copy(block, 0, output, offset, size);
            }
        }
    }
}
