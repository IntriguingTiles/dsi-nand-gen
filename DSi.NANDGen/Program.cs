using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DSi.NANDGen {
    internal class Program {
        enum Region {
            JPN,
            USA,
            EUR,
            AUS,
            CHN,
            KOR
        }

        static readonly Dictionary<Region, int> regionSupportedLangs = new() {
            { Region.JPN, 0x01 },
            { Region.USA, 0x26 },
            { Region.EUR, 0x3E },
            { Region.AUS, 0x02 },
            { Region.CHN, 0x40 },
            { Region.KOR, 0x80 },
        };

        static readonly Dictionary<Region, byte> regionDefaultCountries = new() {
            { Region.JPN, 1 }, // Japan
            { Region.USA, 18 }, // Canada
            { Region.EUR, 110 }, // United Kingdom
            { Region.AUS, 65 }, // Australia
            { Region.CHN, 160 }, // China
            { Region.KOR, 136 }, // South Korea
        };

        static readonly Dictionary<Region, byte> regionDefaultLangs = new() {
            { Region.JPN, 0 }, // Japanese
            { Region.USA, 1 }, // English
            { Region.EUR, 1 }, // English
            { Region.AUS, 1 }, // English
            { Region.CHN, 6 }, // Simplified Chinese
            { Region.KOR, 7 }, // Korean
        };

        static async Task Main(string[] args) {
            var regionOption = new Option<Region>(
                name: "--region",
                description: "The region to send to the update server",
                getDefaultValue: () => Region.USA);

            var cidOption = new Option<string?>(
                name: "--cid",
                description: "The eMMC CID to use in hex (16 bytes) [default: Random]");

            var consoleIdOption = new Option<string?>(
                name: "--consoleid",
                description: "The CPU/Console ID to use in hex (8 bytes) [default: Random]");

            var unlaunchOption = new Option<bool>(
                name: "--unlaunch",
                description: "Installs Unlaunch (automatically enabled when HWINFO_S.dat is invalid)");

            var unlaunchFileOption = new Option<FileInfo?>(
                name: "--unlaunch-file",
                description: "The path to unlaunch.dsi, useful for installing a modified version of Unlaunch");

            var cleanOption = new Option<bool>(
                name: "--clean",
                description: "Deletes everything in the \"nand\" directory");

            var sizeOption = new Option<string>(
                name: "--size",
                description: "The size of the NAND in MiB",
                getDefaultValue: () => "240.0").FromAmong(
                    "240.0",
                    "245.5"
                );

            var waitOption = new Option<bool>(
                name: "--wait",
                description: "Waits for a keypress after downloading all titles, useful for adding a valid HWINFO_s.dat and TWLFontTable.dat");

            var skipSetupOption = new Option<bool>(
                name: "--skip-setup",
                description: "Skips the initial setup process");

            var rootCommand = new RootCommand("Generate a DSi NAND") {
                regionOption,
                cidOption,
                consoleIdOption,
                unlaunchOption,
                unlaunchFileOption,
                cleanOption,
                sizeOption,
                waitOption,
                skipSetupOption
            };

            rootCommand.SetHandler(async (context) => {
                var region = context.ParseResult.GetValueForOption(regionOption);
                var cid = context.ParseResult.GetValueForOption(cidOption);
                var consoleId = context.ParseResult.GetValueForOption(consoleIdOption);
                var unlaunch = context.ParseResult.GetValueForOption(unlaunchOption);
                var unlaunchFile = context.ParseResult.GetValueForOption(unlaunchFileOption);
                var clean = context.ParseResult.GetValueForOption(cleanOption);
                var size = context.ParseResult.GetValueForOption(sizeOption)!;
                var wait = context.ParseResult.GetValueForOption(waitOption);
                var skipSetup = context.ParseResult.GetValueForOption(skipSetupOption);

                await DoIt(region, cid, consoleId, unlaunch, unlaunchFile, clean, size, wait, skipSetup);
            });

            await rootCommand.InvokeAsync(args);
        }


        static async Task DoIt(Region region, string? cidString, string? consoleIdString, bool installUnlaunch, FileInfo? unlaunchPath, bool clean, string nandSize, bool wait, bool skipSetup) {
            // check for required stage2 files
            var requiredFiles = new string[] {
                "stage2_bootldr.bin",
                "stage2_footer.bin",
                "stage2_infoblk1.bin",
                "stage2_infoblk2.bin",
                "stage2_infoblk3.bin",
            };

            foreach (var file in requiredFiles) {
                if (!File.Exists(file)) {
                    Console.Error.WriteLine($"Missing required file {file}");
                    return;
                }
            }

            var cid = new byte[16];
            Random.Shared.NextBytes(cid);

            var consoleId = (ulong)Random.Shared.NextInt64();

            if (cidString is not null) {
                cid = Enumerable.Range(0, cidString.Length)
                     .Where(x => x % 2 == 0)
                     .Select(x => Convert.ToByte(cidString.Substring(x, 2), 16))
                     .ToArray();
            }

            if (consoleIdString is not null) {
                if (!ulong.TryParse(consoleIdString, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out consoleId)) {
                    throw new ArgumentException("Invalid CPU/Console ID");
                }
            }

            if (clean && Directory.Exists("nand")) {
                Console.WriteLine("Cleaning...");

                // we have to unmark everything as read-only
                var dirInfo = new DirectoryInfo("nand") {
                    Attributes = FileAttributes.Normal
                };

                foreach (var info in dirInfo.GetFileSystemInfos("*", SearchOption.AllDirectories)) {
                    info.Attributes = FileAttributes.Normal;
                }

                Directory.Delete("nand", true);
            }

            var consoleIdHigh = (consoleId >> 32) ^ 0xC80C4B72;
            var consoleIdLow = (uint)consoleId;
            var esKeyX = new UInt128(0x4E00004A4A00004E, consoleIdHigh << 32 | consoleIdLow);
            var es = new ES(KeyScrambler.Scramble(esKeyX, KeyScrambler.KeyYType.ES));

            // build a skeleton
            Directory.CreateDirectory("nand/partition1/import");
            Directory.CreateDirectory("nand/partition1/progress");
            Directory.CreateDirectory("nand/partition1/shared1");
            Directory.CreateDirectory("nand/partition1/shared2/launcher");
            Directory.CreateDirectory("nand/partition1/sys/log");
            Directory.CreateDirectory("nand/partition1/tmp/es/write");
            Directory.CreateDirectory("nand/partition2/photo");

            // valid regions: USA, EUR, JPN, AUS, KOR, CHN
            Console.WriteLine($"Getting list of titles to download for {region}...");
            var titles = await CDN.GetSystemUpdate(region.ToString());

            if (!titles.Any(t => t.Combined() == "0003000F484E4341")) {
                // some regions (CHN, KOR) don't have the wifi firmware in the update for whatever reason
                Array.Resize(ref titles, titles.Length + 1);
                titles[^1] = new("0003000F484E4341");
            }

            Console.WriteLine($"{titles.Length} titles to download");

            foreach (var title in titles) {
                await DownloadTitle(title, es);
            }

            if (wait) {
                Console.WriteLine("Press any key to continue when you're done with your changes...");
                Console.ReadKey();
            }

            // make HWINFO_N.dat
            if (!File.Exists("nand/partition1/sys/HWINFO_N.dat")) MakeHWInfoN();

            var needsHnaa = false;

            // if HWINFO_S.dat exists, check the region on it and make sure that it's the region we're building the NAND for
            if (File.Exists("nand/partition1/sys/HWINFO_S.dat")) {
                using var fs = new FileStream("nand/partition1/sys/HWINFO_S.dat", FileMode.Open);
                fs.Position = 0x90;
                var hwInfoRegion = (Region)fs.ReadByte();
                if (hwInfoRegion != region) {
                    Console.WriteLine($"The provided HWINFO_S.dat is for {hwInfoRegion}, but the generated NAND is for {region}. Unlaunch will be installed as HNAA and HWINFO_S.dat will be modified to support that.");

                    // set launcher ID
                    fs.Position = 0xA0;
                    fs.WriteByte(0x41); // 'A'

                    installUnlaunch = true;
                    needsHnaa = true;
                } else {
                    fs.Position = 0xA0;

                    if (fs.ReadByte() == 0x41) {
                        installUnlaunch = true;
                        needsHnaa = true;
                    }
                }
            } else {
                // make HWINFO_S.dat
                MakeHWInfoS(region);
                installUnlaunch = true;
                needsHnaa = true;
            }

            if (needsHnaa) {
                // grab the TMD from an existing launcher
                var launchers = Directory.GetDirectories("nand/partition1/title/00030017");
                Directory.CreateDirectory("nand/partition1/title/00030017/484e4141/content");
                if (File.Exists("nand/partition1/title/00030017/484e4141/content/title.tmd")
                    && File.GetAttributes("nand/partition1/title/00030017/484e4141/content/title.tmd").HasFlag(FileAttributes.ReadOnly)) {
                    File.SetAttributes("nand/partition1/title/00030017/484e4141/content/title.tmd", FileAttributes.Normal);
                }


                foreach (var launcher in launchers) {
                    // skip HNAA
                    if (Path.GetFileName(launcher).Equals("484e4141", StringComparison.InvariantCultureIgnoreCase)) continue;

                    if (File.Exists($"{launcher}/content/title.tmd")) {
                        File.Copy($"{launcher}/content/title.tmd", "nand/partition1/title/00030017/484e4141/content/title.tmd", true);
                        break;
                    }
                }
            }

            if (skipSetup || !File.Exists("nand/partition1/sys/TWLFontTable.dat")) {
                MakeTWLCFG(region);
                // copy it to TWLCFG1.dat as well, otherwise melonDS 1.0 RC gets upset
                File.Copy("nand/partition1/shared1/TWLCFG0.dat", "nand/partition1/shared1/TWLCFG1.dat");
            }

            // install unlaunch
            if (installUnlaunch) await InstallUnlaunch(unlaunchPath);

            // build the primary partitions
            MakePartition("partition1", 0x0010EE00, 0xCDF1200, 0);
            MakePartition("partition2", 0x0CF09A00, 0x20B6600, 1);

            // dummy partition, doesn't contain any data, size depends on the size of the nand chip
            // it's 0x5B4A00 big on bigger nand chips
            MakePartition("partition3", 0, nandSize == "245.5" ? 0x5B4A00U : 0x34600U, 0, true);

            // build nand image
            MakeNandImage(cid, consoleId, nandSize == "245.5");
        }

        static async Task DownloadTitle(TitleID title, ES es) {
            string basePath = $"nand/partition1/title/{title.High.ToLower()}/{title.Low.ToLower()}";
            Directory.CreateDirectory($"{basePath}/content");
            Directory.CreateDirectory($"{basePath}/data");
            Directory.CreateDirectory($"nand/partition1/ticket/{title.High.ToLower()}");

            Console.WriteLine($"Downloading {title}...");
            Console.WriteLine("\tDownloading TMD...");
            (var tmd, var tmdRawData) = await CDN.DownloadTMD(title);

            if (File.Exists($"{basePath}/content/title.tmd") && File.GetAttributes($"{basePath}/content/title.tmd").HasFlag(FileAttributes.ReadOnly)) {
                File.SetAttributes($"{basePath}/content/title.tmd", FileAttributes.Normal);
            }

            // write the tmd to disk minus the cert chain (always 520 bytes? never seen a DSi tmd with more than 1 content entry...)
            File.WriteAllBytes($"{basePath}/content/title.tmd", tmdRawData[..(0x1E4 + 36 * tmd.Contents.Length)]);

            Debug.Assert(tmd.Contents.Length == 1);

            Console.WriteLine("\tDownloading ticket...");
            (var ticket, var ticketRawData) = await CDN.DownloadTicket(title);

            // build cert.sys if we haven't already
            if (!File.Exists("nand/partition1/sys/cert.sys")) {
                // order should be XS..6, CA..1, CP..7
                // XS..6 and CA..1 are both in the tik
                var certSysData = new byte[2560];
                Array.Copy(ticketRawData, 0x2A4, certSysData, 0, ticketRawData.Length - 0x2A4);
                Array.Copy(tmdRawData, 0x208, certSysData, ticketRawData.Length - 0x2A4, 0x300);
                File.WriteAllBytes("nand/partition1/sys/cert.sys", certSysData);
            }

            // write the encrypted ticket to disk minus the cert chain
            var encTicket = es.Encrypt(ticketRawData[..0x2A4], null);
            File.WriteAllBytes($"nand/partition1/ticket/{title.High.ToLower()}/{title.Low.ToLower()}.tik", encTicket);

            var key = ticket.DecryptKey(title);

            for (int i = 0; i < tmd.Contents.Length; i++) {
                Console.WriteLine($"\tDownloading content {i + 1}/{tmd.Contents.Length} ({tmd.Contents[i].Size / 1024} KB)...");

                var content = await CDN.DownloadContent(title, tmd.Contents[i]);
                var iv = new byte[16];

                iv[0] = (byte)tmd.Contents[i].Index;

                var decrypted = content.DecryptContent(key, iv);
                var hash = SHA1.HashData(decrypted);

                if (!hash.SequenceEqual(tmd.Contents[i].Hash)) {
                    Console.WriteLine("\t\tHash check failed, content is corrupt or failed to decrypt");
                    Console.WriteLine($"\t\tOur hash: {Convert.ToHexString(hash)}");
                    Console.WriteLine($"\t\tTMD hash: {Convert.ToHexString(tmd.Contents[i].Hash)}");
                    throw new InvalidDataException("Hash mismatch");
                }

                if (File.Exists($"{basePath}/content/{tmd.Contents[i].ID:x8}.app") && File.GetAttributes($"{basePath}/content/{tmd.Contents[i].ID:x8}.app").HasFlag(FileAttributes.ReadOnly)) {
                    File.SetAttributes($"{basePath}/content/{tmd.Contents[i].ID:x8}.app", FileAttributes.Normal);
                }

                // write the decrypted content to disk
                File.WriteAllBytes($"{basePath}/content/{tmd.Contents[i].ID:x8}.app", decrypted);

                // if this is a bootable rom, check to see what save data should be created, if any
                // libmagic determines if a file is a DS rom by checking for 0x21A29A6951AEFF24 at 0xC0
                // this seems to work, so it's what we will do as well
                if (decrypted.Length >= 0xC8 && BitConverter.ToUInt64(decrypted, 0xC0) == 0x21A29A6951AEFF24) {
                    // size of public.sav is at 0x238
                    // size of private.sav is at 0x23C
                    var publicSize = BitConverter.ToUInt32(decrypted, 0x238);
                    var privateSize = BitConverter.ToUInt32(decrypted, 0x23C);

                    // technically these should be formatted as FAT16 but whatever formatter nintendo uses does some weird stuff
                    // this approach does seem to work although DSi Camera complains about corrupt save data

                    if (publicSize > 0) {
                        File.WriteAllBytes($"{basePath}/data/public.sav", new byte[publicSize]);
                    }

                    if (privateSize > 0) {
                        File.WriteAllBytes($"{basePath}/data/private.sav", new byte[privateSize]);
                    }
                }
            }
        }

        static async Task InstallUnlaunch(FileInfo? unlaunchPath) {
            byte[] data;

            if (unlaunchPath is null) {
                // grab the latest version of unlaunch
                Console.WriteLine("Downloading Unlaunch...");
                using var http = new HttpClient();
                using var res = await http.GetAsync("https://problemkaputt.de/unlaunch.zip");
                res.EnsureSuccessStatusCode();
                using var zip = new ZipArchive(res.Content.ReadAsStream());
                var entry = zip.GetEntry("UNLAUNCH.DSI") ?? throw new Exception("Unlaunch zip doesn't have UNLAUNCH.DSI in it");
                using var stream = entry.Open();
                data = new byte[entry.Length];
                stream.ReadExactly(data);
            } else {
                data = File.ReadAllBytes(unlaunchPath.FullName);
            }

            // append it to the title.tmd for each launcher
            var launchers = Directory.GetDirectories("nand/partition1/title/00030017");

            foreach (var launcher in launchers) {
                var dirName = Path.GetFileName(launcher)!.ToUpperInvariant();

                // see if the title.tmd is already over what it should be
                var info = new FileInfo($"{launcher}/content/title.tmd");
                if (info.Length > 520) {
                    Console.WriteLine($"Skipping installing Unlaunch to 00030017-{dirName} ({Encoding.ASCII.GetString(Convert.FromHexString(dirName))}) as it seems to already be installed");
                    continue;
                }

                Console.WriteLine($"Installing Unlaunch to 00030017-{dirName} ({Encoding.ASCII.GetString(Convert.FromHexString(dirName))})...");

                // append the unlaunch data
                using (var fs = new FileStream($"{launcher}/content/title.tmd", FileMode.Open)) {
                    fs.Position = fs.Length;
                    fs.Write(data);
                }

                // now mark everything in the content dir as read-only
                var files = Directory.GetFiles($"{launcher}/content");

                foreach (var file in files) {
                    File.SetAttributes(file, FileAttributes.ReadOnly);
                }
            }
        }

        static void MakeHWInfoN() {
            Console.WriteLine("Generating HWINFO_N.dat...");
            using var stream = new FileStream("nand/partition1/sys/HWINFO_N.dat", FileMode.Create);
            using var writer = new BinaryWriter(stream);
            stream.Position = 0x80;
            // header, version?
            writer.Write(0x1);
            // header, size
            writer.Write(0x14);
            // unknown per-console id
            writer.Write(Random.Shared.Next());
            // tad export per-console id
            var tadId = new byte[0x10];
            Random.Shared.NextBytes(tadId);
            writer.Write(tadId);
            // compute hash for 0x88 to 0x9B
            stream.Position = 0x88;
            var data = new byte[0x14];
            stream.Read(data);
            var hash = SHA1.HashData(data);
            // write hash at 0x00
            stream.Position = 0;
            writer.Write(hash);
            // fill the rest of the file with 0xFF
            stream.Position = 0x9C;
            var dummyData = new byte[0x3F64];
            Array.Fill<byte>(dummyData, 0xFF);
            writer.Write(dummyData);
        }

        static void MakeHWInfoS(Region region) {
            Console.WriteLine("Generating HWINFO_S.dat...");
            using var stream = new FileStream("nand/partition1/sys/HWINFO_S.dat", FileMode.Create);
            using var writer = new BinaryWriter(stream);
            // skip the signature data
            stream.Position = 0x80;
            // header, version?
            writer.Write(0x1);
            // header, size
            writer.Write(0x1C);
            // supported languages
            writer.Write(regionSupportedLangs[region]);
            // unknown
            writer.Write(0x0);
            // region
            writer.Write((byte)region);

            // serial number
            var serialNo = "T";

            switch (region) {
                case Region.JPN:
                    serialNo += "JF";
                    break;
                case Region.USA:
                    serialNo += "W";
                    break;
                case Region.EUR:
                    serialNo += "EF";
                    break;
                case Region.AUS:
                    serialNo += "AG";
                    break;
                case Region.CHN:
                    serialNo += "CF";
                    break;
                case Region.KOR:
                    serialNo += "KF";
                    break;
            }

            var nums = new int[8];

            for (var i = 0; i < 8; i++) {
                nums[i] = Random.Shared.Next() % 10;
                serialNo += nums[i];
            }

            serialNo += (250 - (nums[0] + nums[2] + nums[4] + nums[6]) - 3 * (nums[1] + nums[3] + nums[5] + nums[7])) % 10;

            var serialNoBytes = new byte[12];
            Encoding.ASCII.GetBytes(serialNo, serialNoBytes);

            writer.Write(serialNoBytes);

            // unknown
            writer.Write([0x00, 0x00, 0x3C]);
            // launcher ID
            // if we're making HWINFO_S.dat, then it will never be valid, so always set HNAA as the launcher ID
            writer.Write(Encoding.ASCII.GetBytes("HNAA").Reverse().ToArray());
            // fill the rest of the file with 0xFF
            var dummyData = new byte[0x3F5C];
            Array.Fill<byte>(dummyData, 0xFF);
            writer.Write(dummyData);
        }

        static void MakeTWLCFG(Region region) {
            Console.WriteLine("Generating TWLCFG0.dat...");
            using var stream = new FileStream("nand/partition1/shared1/TWLCFG0.dat", FileMode.Create);
            using var writer = new BinaryWriter(stream);

            stream.Position = 0x80;

            // version?
            writer.Write((byte)0x01);
            // update counter
            writer.Write((byte)0x00);
            // garbage
            writer.Write((short)0x00);
            // size
            writer.Write(0x128);
            // config
            writer.Write(0x0F);
            // garbage
            writer.Write((byte)0x00);
            // country code
            writer.Write(regionDefaultCountries[region]);
            // language
            writer.Write(regionDefaultLangs[region]);
            // RTC year
            writer.Write((byte)Math.Min(DateTime.Now.Year - 2000, 99));
            // RTC offset in seconds
            writer.Write((int)(DateTime.Now - new DateTime(2000, 01, 01, 0, 0, 0)).TotalSeconds);
            // skip over garbage
            stream.Position = 0xAC;
            // unknown
            writer.Write((byte)0x03);
            // skip over garbage
            stream.Position = 0xB8;
            // touchscreen calibration
            writer.Write(0x033F02DF);
            writer.Write(0x0D3B2020);
            writer.Write(0xA0E00CD3);
            // skip over garbage
            stream.Position = 0xCC;
            // favorite color
            writer.Write((byte)0x0B); // light blue
            // garbage
            writer.Write((byte)0x00);
            // birthday month
            writer.Write((byte)0x01);
            // birthday day
            writer.Write((byte)0x01);
            // nickname
            var nickname = new byte[0x16];
            Encoding.Unicode.GetBytes(":3", nickname);
            writer.Write(nickname);
            // message
            var message = new byte[0x36];
            Encoding.Unicode.GetBytes("hello", message);
            writer.Write(message);
            // skip over garbage
            stream.Position = 0x1B0;
            // fill the rest with 0xFF
            var garbage = new byte[0x3E50];
            Array.Fill<byte>(garbage, 0xFF);
            writer.Write(garbage);
            // hash it
            stream.Position = 0x88;
            var data = new byte[0x127];
            stream.ReadExactly(data);
            stream.Position = 0x00;
            stream.Write(SHA1.HashData(data));
        }

        static void MakeNandImage(byte[] cid, ulong consoleId, bool bigNand) {
            Console.WriteLine("Building nand.bin...");

            // make nand.bin
            using var fs = new FileStream("nand/nand.bin", FileMode.Create);
            //fs.Seek((bigNand ? 0x0F580000 : 0x0F000000) - 1, SeekOrigin.Begin);
            //fs.WriteByte(0);

            Console.WriteLine("Writing MBR...");

            // make MBR
            var part1 = new MBR.Partition(MBR.Partition.PartitionType.FAT16B, 0x0010EE00 / 512, 0xCDF1200 / 512);
            // yes, i know that this *should* be FAT12 and not FAT16B, but it's what nintendo used in their MBR
            var part2 = new MBR.Partition(MBR.Partition.PartitionType.FAT16B, 0x0CF09A00 / 512, 0x20B6600 / 512);
            var part3 = new MBR.Partition(MBR.Partition.PartitionType.FAT12, (bigNand ? 0x0EFCB600U : 0x0EFCBA00U) / 512, (bigNand ? 0x5B4A00U : 0x34600U) / 512);
            var part4 = new MBR.Partition(MBR.Partition.PartitionType.None);
            var mbr = new MBR([part1, part2, part3, part4]);

            // write MBR
            using var mbrStream = new MemoryStream();
            using var mbrWriter = new BinaryWriter(mbrStream);
            mbr.Serialize(mbrWriter);

            fs.Position = 0;
            mbrStream.Position = 0;
            StreamCryptedCopy(mbrStream, fs, 0, cid, consoleId, false);

            // write stage2 info
            for (var i = 1; i <= 3; i++) {
                Console.WriteLine($"Writing stage2_infoblk{i}.bin...");
                using var infoStream = new FileStream($"stage2_infoblk{i}.bin", FileMode.Open);
                infoStream.CopyTo(fs);
            }

            // write stage2 binary
            Console.WriteLine("Writing stage2_bootldr.bin...");
            using var bootStream = new FileStream("stage2_bootldr.bin", FileMode.Open);
            bootStream.CopyTo(fs);

            // write stage2 footer
            Console.WriteLine("Writing stage2_footer.bin");
            using var footerStream = new FileStream("stage2_footer.bin", FileMode.Open);

            // write partition 1
            Console.WriteLine("Writing partition1...");
            fs.Position = 0x0010EE00;
            using var part1Stream = new FileStream("nand/partition1.img", FileMode.Open);
            StreamCryptedCopy(part1Stream, fs, (ulong)fs.Position, cid, consoleId);

            // write partition 2
            Console.WriteLine("Writing partition2...");
            fs.Position = 0x0CF09A00;
            using var part2Stream = new FileStream("nand/partition2.img", FileMode.Open);
            StreamCryptedCopy(part2Stream, fs, (ulong)fs.Position, cid, consoleId);

            // write partition 3 (unencrypted)
            Console.WriteLine("Writing partition3...");
            fs.Position = bigNand ? 0x0EFCB600 : 0x0EFCBA00;
            using var part3Stream = new FileStream("nand/partition3.img", FileMode.Open);
            part3Stream.CopyTo(fs);

            // write nocash footer
            Console.WriteLine("Writing nocash footer...");
            using var writer = new BinaryWriter(fs);
            writer.Write(Encoding.ASCII.GetBytes("DSi eMMC CID/CPU"));
            writer.Write(cid);
            writer.Write(consoleId);
            writer.Write(new byte[24]);
        }

        static void StreamCryptedCopy(Stream source, Stream destination, ulong offset, byte[] cid, ulong consoleId, bool showProgress = true) {
            Debug.Assert(source.Length % 16 == 0);

            var consoleIdHigh = consoleId >> 32;
            var consoleIdLow = (ulong)(uint)consoleId;
            var upper = consoleIdLow << 32 | (consoleIdLow ^ 0x24EE6906);
            var lower = (consoleIdHigh ^ 0xE65B601D) << 32 | consoleIdHigh;
            var nandKeyX = new UInt128(upper, lower);
            var key = KeyScrambler.Scramble(nandKeyX, KeyScrambler.KeyYType.NAND);

            var ctrArray = SHA1.HashData(cid)[..16];
            var ctr = ctrArray.Reverse().ToArray().ToUInt128();

            ctr += offset / 16;
            var aes = new AES.CTR(key, ctr);

            if (showProgress) Console.Write("\t0%");

            while (source.Position < source.Length) {
                var enc = new byte[16];
                var dec = new byte[16];

                source.Read(enc);
                aes.TransformBlock(enc, dec);
                destination.Write(dec);

                if (showProgress && source.Position % 1000000 == 0) {
                    Console.CursorLeft = 0;
                    Console.Write($"\t{(float)(source.Position) / source.Length:P} ({source.Position}/{source.Length})");
                }
            }

            if (showProgress) {
                Console.CursorLeft = 0;
                Console.WriteLine($"\t{(float)(source.Position) / source.Length:P} ({source.Position}/{source.Length})");
            }
        }

        static void MakePartition(string name, uint offset, uint size, byte driveNumber, bool leaveEmpty = false) {
            Console.WriteLine($"Creating disk image for {name}...");
            using var fs = new FileStream($"nand/{name}.img", FileMode.Create);
            fs.Seek(size - 1, SeekOrigin.Begin);
            fs.WriteByte(0);

            if (!leaveEmpty) {
                Console.WriteLine("\tFormatting...");
                using var fat = new FAT(fs);
                fat.Format(offset, driveNumber);

                Console.WriteLine("\tCopying data...");
                AddDirectory(fat, $"nand/{name}");
            }
        }

        static void AddDirectory(FAT fat, string rootPath, string path = "") {
            if (path != "") {
                //Console.WriteLine($"mkdir: {path}");
                fat.CreateDirectory(path);
            }

            var dirs = Directory.GetDirectories(rootPath + path);

            foreach (var dir in dirs) {
                AddDirectory(fat, rootPath, path + $"\\{Path.GetFileName(dir)}");
            }

            var files = Directory.GetFiles(rootPath + path);

            foreach (var file in files) {
                //Console.WriteLine($"mkfile: {path}\\{Path.GetFileName(file)}");
                fat.CreateFile(path + $"\\{Path.GetFileName(file)}", File.ReadAllBytes(file), File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
            }
        }
    }
}