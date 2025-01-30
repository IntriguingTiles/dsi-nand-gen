# dsi-nand-gen

> [!CAUTION]
> Do not flash NANDs created with this tool to your DSi without having a way to recover from bricks!

This is a small command-line tool designed to build a brand new DSi NAND from scratch (mostly).

It outputs a nocash-style `nand.bin` that should work in melonDS and no\$gba.

# Known issues
- Nintendo DSi Camera complains about corrupt save data on first boot
  - Saves are supposed to be FAT12 images but the values used in the VBR for existing saves seem somewhat strange to me, so I've left saves as all zeroes for now

# Required files
These can be extracted from an existing DSi NAND with ninfs.
They are expected to be placed in the current working directory.
- `stage2_bootldr.bin`
- `stage2_footer.bin`
- `stage2_infoblk{1,2,3}.bin`

# Optional files
- `sys/TWLFontTable.dat`
  - Some software, like System Settings, will crash without it
  - `--skip-setup` is therefore enabled automatically if this file is missing
- `sys/HWINFO_S.dat` (Unlaunch will be required without it)

# Limitations
As Nintendo never made PictoChat available on NUS, NANDs generated with this tool will not have it installed by default.

# Usage
By default, `DSi.NANDGen` will generate a USA region NAND. `DSi.NANDGen --region JPN` will generate a JPN region NAND.

See `DSi.NANDGen --help` for all available options.

The output is located in `nand`, which is created in the current working directory.

# To-do
- [x] Download titles
- [x] Encrypt tickets
- [x] Build FAT filesystems
- [x] Build NAND image
- [x] Mount NAND with ninfs
- [x] Boot in an emulator
- [x] Boot on real hardware via HiyaCFW

# Future goals
- [ ] Install arbitrary titles
- [x] Install unlaunch 