// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    /// <summary>
    /// In-place add/replace/rename/delete must leave the metadata that FreeBSD
    /// fsck_ffs checks consistent: fragment tails, per-CG counts, the csum array,
    /// superblock totals, cg_initediblk and DIRBLKSIZ directory chunks.
    /// </summary>
    public class InPlaceConsistencyTests : IDisposable
    {
        private readonly string _testDir;
        private readonly string _imagePath;

        public InPlaceConsistencyTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"ufs2ip_{Guid.NewGuid():N}");
            _imagePath = Path.Combine(Path.GetTempPath(), $"ufs2ip_{Guid.NewGuid():N}.img");
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            if (File.Exists(_imagePath)) File.Delete(_imagePath);
            if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true);
        }

        [Theory]
        [InlineData(4096, 512, 2)]
        [InlineData(32768, 4096, 2)]
        [InlineData(32768, 4096, 1)]
        public void InPlaceOps_KeepFsckPass5StateConsistent(int blockSize, int fragSize, int format)
        {
            var rnd = new Random(1);
            byte[] Bytes(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }

            string src = Path.Combine(_testDir, "src");
            Directory.CreateDirectory(Path.Combine(src, "d1", "sub"));
            for (int i = 1; i <= 20; i++)
                File.WriteAllBytes(Path.Combine(src, "d1", $"f{i}"), Bytes(i * 1297 % 9000 + 1));
            File.WriteAllBytes(Path.Combine(src, "d1", "sub", "g"), Bytes(3000));

            var creator = new Ufs2ImageCreator
            {
                BlockSize = blockSize,
                FragmentSize = fragSize,
                BlocksPerCylGroup = 64,
                // One inode per block: the late adds below need ~blockSize/40 inodes
                BytesPerInode = blockSize,
                MinFreePercent = 0,
                FilesystemFormat = format,
            };
            creator.CreateImageFromDirectory(_imagePath, src, 1024L * blockSize);

            var expected = new Dictionary<string, byte[]>();
            string Local(string name, byte[] data)
            {
                string p = Path.Combine(_testDir, name);
                File.WriteAllBytes(p, data);
                return p;
            }

            // Enough entries to push /d1 past its first directory block
            int lateCount = blockSize / 40 + 20;
            byte[] tiny = Bytes(2);
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Add("/tiny", Local("tiny", tiny));
                expected["/tiny"] = tiny;
                byte[] big = Bytes(20 * blockSize + 123);
                image.Add("/big", Local("big", big));
                expected["/big"] = big;
                image.AddDirectory("/", "newdir");
                image.Add("/newdir/x", Local("x", tiny));
                expected["/newdir/x"] = tiny;

                image.Rename("/d1/f3", "a_much_longer_name_for_f3");
                byte[] f5 = Bytes(3 * blockSize + 100);
                image.ReplaceFileContent("/d1/f5", f5);
                expected["/d1/f5"] = f5;
                image.ReplaceFileContent("/d1/f6", tiny);
                expected["/d1/f6"] = tiny;
                image.Delete("/d1/f7");
                image.Delete("/d1/sub");

                string late = Local("late", tiny);
                for (int i = 0; i < lateCount; i++)
                    image.AddFile("/d1", late, $"late_entry_with_a_longer_name_{i:D4}");
                expected[$"/d1/late_entry_with_a_longer_name_{lateCount - 1:D4}"] = tiny;
            }

            AssertPass5StateConsistent(_imagePath);

            using var check = new Ufs2Image(_imagePath, readOnly: true);
            var result = check.FsckUfs();
            Assert.True(result.Clean, string.Join("\n", result.Errors));
            foreach (var (path, data) in expected)
                Assert.Equal(data, check.ReadFile(check.ResolvePath(path)));

            // A tiny file holds one fragment, not a whole block
            Assert.Equal(fragSize / 512, check.ReadInode(check.ResolvePath("/tiny")).Blocks);

            var d1 = check.ReadInode(check.ResolvePath("/d1"));
            Assert.True(d1.Size > blockSize, "test should grow /d1 past one block");
            AssertDirChunksValid(d1, blockSize, fragSize);
        }

        [Theory]
        [InlineData(4096, 512, 2)]
        [InlineData(32768, 4096, 1)]
        public void InPlaceAdds_ReuseFreeFragments(int blockSize, int fragSize, int format)
        {
            string src = Path.Combine(_testDir, "src");
            Directory.CreateDirectory(src);
            var creator = new Ufs2ImageCreator
            {
                BlockSize = blockSize,
                FragmentSize = fragSize,
                BlocksPerCylGroup = 64,
                BytesPerInode = 4 * blockSize,
                MinFreePercent = 0,
                FilesystemFormat = format,
            };
            creator.CreateImageFromDirectory(_imagePath, src, 1024L * blockSize);

            string tinyPath = Path.Combine(_testDir, "tiny");
            File.WriteAllBytes(tinyPath, new byte[] { 1, 2, 3 });
            int fragsPerBlock = blockSize / fragSize;

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                long TotalFree() => image.Superblock.FreeBlocks * fragsPerBlock + image.Superblock.FreeFragments;

                image.AddFile("/", tinyPath, "seed");
                long blocks = image.Superblock.FreeBlocks, free = TotalFree();

                // Every free fragment left in partial blocks takes one tiny file
                int room = (int)image.Superblock.FreeFragments;
                Assert.True(room > 0);
                for (int i = 0; i < room; i++)
                    image.AddFile("/", tinyPath, $"t{i}");
                Assert.Equal(blocks, image.Superblock.FreeBlocks);
                Assert.Equal(free - room, TotalFree());

                image.Delete("/t0");
                image.AddFile("/", tinyPath, "again");
                Assert.Equal(blocks, image.Superblock.FreeBlocks);
                Assert.Equal(free - room, TotalFree());
            }

            AssertPass5StateConsistent(_imagePath);

            using var check = new Ufs2Image(_imagePath, readOnly: true);
            var result = check.FsckUfs();
            Assert.True(result.Clean, string.Join("\n", result.Errors));
            foreach (string name in new[] { "seed", "again", "t1" })
                Assert.Equal(new byte[] { 1, 2, 3 }, check.ReadFile(check.ResolvePath("/" + name)));
        }

        [Theory]
        [InlineData(4096, 512, 2)]
        [InlineData(32768, 4096, 1)]
        public void ShrinkingReplace_FreesBlocksPastNewEnd(int blockSize, int fragSize, int format)
        {
            var rnd = new Random(1);
            byte[] Bytes(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }

            string src = Path.Combine(_testDir, "src");
            Directory.CreateDirectory(src);
            File.WriteAllBytes(Path.Combine(src, "direct"), Bytes(3 * blockSize + 100));
            File.WriteAllBytes(Path.Combine(src, "indirect"), Bytes(20 * blockSize + 123));
            File.WriteAllBytes(Path.Combine(src, "empty"), Bytes(2 * blockSize));
            var creator = new Ufs2ImageCreator
            {
                BlockSize = blockSize,
                FragmentSize = fragSize,
                BlocksPerCylGroup = 64,
                BytesPerInode = 4 * blockSize,
                MinFreePercent = 0,
                FilesystemFormat = format,
            };
            creator.CreateImageFromDirectory(_imagePath, src, 1024L * blockSize);

            byte[] small = Bytes(10);
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                int fragsPerBlock = blockSize / fragSize;
                long TotalFree() => image.Superblock.FreeBlocks * fragsPerBlock + image.Superblock.FreeFragments;

                foreach (var (path, data) in new[] { ("/direct", small), ("/indirect", small), ("/empty", Array.Empty<byte>()) })
                {
                    uint ino = image.ResolvePath(path);
                    long before = TotalFree(), oldBlocks = image.ReadInode(ino).Blocks;
                    image.ReplaceFileContent(path, data);
                    var inode = image.ReadInode(ino);
                    Assert.Equal(before + (oldBlocks - inode.Blocks) / (fragSize / 512), TotalFree());
                    Assert.All(inode.DirectBlocks.Skip(1), b => Assert.Equal(0, b));
                    Assert.All(inode.IndirectBlocks, b => Assert.Equal(0, b));
                }
            }

            AssertPass5StateConsistent(_imagePath);

            using var check = new Ufs2Image(_imagePath, readOnly: true);
            var result = check.FsckUfs();
            Assert.True(result.Clean, string.Join("\n", result.Errors));
            Assert.Equal(small, check.ReadFile(check.ResolvePath("/direct")));
            Assert.Equal(small, check.ReadFile(check.ResolvePath("/indirect")));
            Assert.Empty(check.ReadFile(check.ResolvePath("/empty")));
            Assert.Equal(0, check.ReadInode(check.ResolvePath("/empty")).Blocks);
        }

        [Fact]
        public void GrowingReplace_PastSingleIndirect_KeepsData()
        {
            var rnd = new Random(1);
            string src = Path.Combine(_testDir, "src");
            Directory.CreateDirectory(src);
            var old = new byte[20 * 4096];
            rnd.NextBytes(old);
            File.WriteAllBytes(Path.Combine(src, "f"), old);
            var creator = new Ufs2ImageCreator { BlockSize = 4096, FragmentSize = 512, MinFreePercent = 0 };
            creator.CreateImageFromDirectory(_imagePath, src, 1024L * 4096);

            // 20 blocks already use the single-indirect block; 30 must not land in a double-indirect
            var data = new byte[30 * 4096];
            rnd.NextBytes(data);
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.ReplaceFileContent("/f", data);

            AssertPass5StateConsistent(_imagePath);
            using var check = new Ufs2Image(_imagePath, readOnly: true);
            Assert.True(check.FsckUfs().Clean);
            Assert.Equal(data, check.ReadFile(check.ResolvePath("/f")));
            Assert.Equal(0, check.ReadInode(check.ResolvePath("/f")).IndirectBlocks[1]);
        }

        [Fact]
        public void Ufs1_NoBackupSuperblockAtUfs2Offset()
        {
            // 96-frag groups at 512-byte frags would put CG 1's backup superblock at
            // exactly 64K, which FreeBSD then reads as the primary
            var creator = new Ufs2ImageCreator
            {
                FilesystemFormat = 1, BlockSize = 4096, FragmentSize = 512,
                BlocksPerCylGroup = 12, BytesPerInode = 12000,
            };
            creator.CreateImage(_imagePath, 1024 * 1024);
            using (var fs = File.OpenRead(_imagePath))
            using (var reader = new BinaryReader(fs))
            {
                fs.Position = Ufs2Constants.SuperblockOffset + 0x55C;
                Assert.NotEqual(Ufs2Constants.Ufs1Magic, reader.ReadInt32());
                Assert.Equal(Ufs2Constants.Ufs1SuperblockOffset, Ufs2Superblock.FindPrimaryOffset(fs));
            }

            string tiny = Path.Combine(_testDir, "tiny");
            File.WriteAllBytes(tiny, new byte[] { 1 });
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
                image.AddFile("/", tiny);

            AssertPass5StateConsistent(_imagePath);
        }

        /// <summary>
        /// The fsck_ffs pass 5 checks FsckUfs does not do: per-CG counts against the
        /// free-fragment bitmap, the csum array, superblock totals and cg_initediblk.
        /// </summary>
        private static void AssertPass5StateConsistent(string imagePath)
        {
            using var fs = File.OpenRead(imagePath);
            using var reader = new BinaryReader(fs);
            fs.Position = Ufs2Superblock.FindPrimaryOffset(fs);
            var sb = Ufs2Superblock.ReadFrom(reader);

            long ndir = 0, nbfree = 0, nifree = 0, nffree = 0;
            for (int cg = 0; cg < sb.NumCylGroups; cg++)
            {
                fs.Position = ((long)cg * sb.CylGroupSize + sb.CblkNo) * sb.FSize;
                byte[] cgd = reader.ReadBytes(sb.CgSize);
                int I(int off) => BitConverter.ToInt32(cgd, off);
                int ndblk = I(0x14), iusedoff = I(0x5C), freeoff = I(0x60);

                int bf = 0, ff = 0;
                for (int blk = 0; blk * sb.FragsPerBlock < ndblk; blk++)
                {
                    int free = 0;
                    for (int f = blk * sb.FragsPerBlock; f < (blk + 1) * sb.FragsPerBlock; f++)
                        if (f < ndblk && (cgd[freeoff + f / 8] & (1 << (f % 8))) != 0) free++;
                    if (free == sb.FragsPerBlock) bf++; else ff += free;
                }
                Assert.Equal(bf, I(0x1C));
                Assert.Equal(ff, I(0x24));

                fs.Position = sb.CsAddr * sb.FSize + (long)cg * Ufs2Constants.CsumStructSize;
                for (int k = 0; k < 4; k++)
                    Assert.Equal(I(0x18 + k * 4), reader.ReadInt32());

                // UFS1 initializes every inode and leaves cg_initediblk at 0
                for (int ino = I(0x78); sb.IsUfs2 && ino < sb.InodesPerGroup; ino++)
                    Assert.True((cgd[iusedoff + ino / 8] & (1 << (ino % 8))) == 0,
                        $"CG {cg} inode {ino} is used but past cg_initediblk");

                ndir += I(0x18); nbfree += I(0x1C); nifree += I(0x20); nffree += I(0x24);
            }

            Assert.Equal(ndir, sb.Directories);
            Assert.Equal(nbfree, sb.FreeBlocks);
            Assert.Equal(nifree, sb.FreeInodes);
            Assert.Equal(nffree, sb.FreeFragments);
        }

        /// <summary>Every DIRBLKSIZ chunk inside the directory size must start with a live record.</summary>
        private void AssertDirChunksValid(Ufs2Inode dir, int blockSize, int fragSize)
        {
            using var fs = File.OpenRead(_imagePath);
            using var reader = new BinaryReader(fs);
            for (long off = 0; off < dir.Size; off += Ufs2Constants.DirBlockSize)
            {
                fs.Position = dir.DirectBlocks[off / blockSize] * fragSize + off % blockSize + 4;
                Assert.True(reader.ReadUInt16() > 0, $"zero d_reclen at offset {off}");
            }
        }
    }
}
