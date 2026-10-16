// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    /// <summary>
    /// Tests for symlinks: short ("fast") ones, whose target is stored inline in
    /// the inode block pointers, and longer ones stored in a data block.
    /// </summary>
    public class SymlinkTests : IDisposable
    {
        private readonly string _testDir;
        private readonly string _imagePath;

        public SymlinkTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"ufs2test_src_{Guid.NewGuid():N}");
            _imagePath = Path.Combine(Path.GetTempPath(), $"ufs2test_{Guid.NewGuid():N}.img");
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            if (File.Exists(_imagePath))
                File.Delete(_imagePath);
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void FastSymlink_ReadTargetAndDelete(int format)
        {
            // An empty file has no data blocks, so it can be turned into a fast symlink in place.
            File.WriteAllBytes(Path.Combine(_testDir, "link"), []);
            var creator = new Ufs2ImageCreator { FilesystemFormat = format };
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            creator.CreateImage(_imagePath, creator.CalculateImageSize(diskSize));
            creator.PopulateFromDirectory(_imagePath, _testDir);

            // 12 bytes spans three UFS1 (32-bit) pointers and two UFS2 (64-bit) pointers.
            byte[] target = "dir/file.txt"u8.ToArray();
            int ptrSize = format == 1 ? 4 : 8;
            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                uint ino = image.ResolvePath("/link");
                var inode = image.ReadInode(ino);
                inode.Mode = Ufs2Constants.IfLnk | 0x1ED;
                inode.Size = target.Length;
                byte[] padded = new byte[16];
                target.CopyTo(padded, 0);
                for (int i = 0; i * ptrSize < target.Length; i++)
                    inode.DirectBlocks[i] = ptrSize == 4
                        ? BitConverter.ToInt32(padded, i * 4)
                        : BitConverter.ToInt64(padded, i * 8);
                image.WriteInode(ino, inode);
            }

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                Assert.Equal(target, image.ReadSymlinkTarget(image.ResolvePath("/link")));
                image.Delete("/link");
                Assert.Throws<FileNotFoundException>(() => image.ResolvePath("/link"));
            }

            AssertFsckClean();
        }

        // 200 bytes is past fs_maxsymlinklen on both formats (60 for UFS1, 120 for UFS2).
        private static readonly string LongTarget = "long/" + new string('x', 195);

        private void AssertFsckClean()
        {
            using var checkImage = new Ufs2Image(_imagePath, readOnly: true);
            var result = checkImage.FsckUfs(preen: true);
            Assert.True(result.Clean, $"Fsck errors: {string.Join("; ", result.Errors)}");
        }

        private void CreateImage(int format)
        {
            var creator = new Ufs2ImageCreator { FilesystemFormat = format };
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            creator.CreateImage(_imagePath, creator.CalculateImageSize(diskSize));
            creator.PopulateFromDirectory(_imagePath, _testDir);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void PopulateFromDirectory_StoresHostSymlinks(int format)
        {
            // Creating host symlinks needs elevation or Developer Mode on Windows
            if (OperatingSystem.IsWindows())
                return;
            Directory.CreateDirectory(Path.Combine(_testDir, "sub"));
            File.WriteAllText(Path.Combine(_testDir, "sub", "file.txt"), "data");
            File.CreateSymbolicLink(Path.Combine(_testDir, "short"), "sub/file.txt");
            File.CreateSymbolicLink(Path.Combine(_testDir, "long"), LongTarget);
            Directory.CreateSymbolicLink(Path.Combine(_testDir, "sub", "loop"), "..");
            CreateImage(format);

            using (var image = new Ufs2Image(_imagePath, readOnly: true))
            {
                Assert.Equal("sub/file.txt"u8.ToArray(), image.ReadSymlinkTarget(image.ResolvePath("/short")));
                Assert.Equal(0, image.ReadInode(image.ResolvePath("/short")).Blocks);
                Assert.Equal(System.Text.Encoding.UTF8.GetBytes(LongTarget), image.ReadSymlinkTarget(image.ResolvePath("/long")));
                var loop = image.ListDirectory(image.ResolvePath("/sub")).Single(e => e.Name == "loop");
                Assert.Equal(Ufs2Constants.DtLnk, loop.FileType);
                Assert.Equal(".."u8.ToArray(), image.ReadSymlinkTarget(loop.Inode));
            }
            AssertFsckClean();
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Add_StoresSymlinksAndDeleteFreesSlowOne(int format)
        {
            if (OperatingSystem.IsWindows())
                return;
            CreateImage(format);
            string hostDir = Path.Combine(_testDir, "host");
            Directory.CreateDirectory(hostDir);
            File.CreateSymbolicLink(Path.Combine(hostDir, "short"), "target");
            File.CreateSymbolicLink(Path.Combine(hostDir, "long"), LongTarget);

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Add("/l", Path.Combine(hostDir, "long"));
                image.Add("/d", hostDir);
                Assert.Equal(System.Text.Encoding.UTF8.GetBytes(LongTarget), image.ReadSymlinkTarget(image.ResolvePath("/l")));
                Assert.Equal("target"u8.ToArray(), image.ReadSymlinkTarget(image.ResolvePath("/d/short")));
                Assert.Equal(System.Text.Encoding.UTF8.GetBytes(LongTarget), image.ReadSymlinkTarget(image.ResolvePath("/d/long")));
            }
            AssertFsckClean();

            using (var image = new Ufs2Image(_imagePath, readOnly: false))
            {
                image.Delete("/l");
                image.Delete("/d");
            }
            AssertFsckClean();
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Extract_WritesHostSymlinks(int format)
        {
            if (OperatingSystem.IsWindows())
                return;
            File.CreateSymbolicLink(Path.Combine(_testDir, "short"), "target");
            File.CreateSymbolicLink(Path.Combine(_testDir, "long"), LongTarget);
            CreateImage(format);

            string outDir = Path.Combine(_testDir, "out");
            using var image = new Ufs2Image(_imagePath, readOnly: true);
            image.Extract("/", outDir);
            Assert.Equal("target", new FileInfo(Path.Combine(outDir, "short")).LinkTarget);
            Assert.Equal(LongTarget, new FileInfo(Path.Combine(outDir, "long")).LinkTarget);

            string single = Path.Combine(_testDir, "single");
            image.Extract("/long", single);
            Assert.Equal(LongTarget, new FileInfo(Path.Combine(single, "long")).LinkTarget);

            // Extracting again over the previous output replaces the links
            image.Extract("/", outDir);
            Assert.Equal("target", new FileInfo(Path.Combine(outDir, "short")).LinkTarget);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void ReadFile_FastSymlinkReturnsTarget(int format)
        {
            CreateImage(format);
            using var image = new Ufs2Image(_imagePath, readOnly: false);
            image.AddSymlink("/", "short", "dir/file.txt"u8.ToArray());
            Assert.Equal("dir/file.txt"u8.ToArray(), image.ReadFile(image.ResolvePath("/short")));
        }
    }
}
