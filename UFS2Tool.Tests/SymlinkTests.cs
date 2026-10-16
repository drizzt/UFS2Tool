// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    /// <summary>
    /// Tests for short ("fast") symlinks, whose target is stored inline in the
    /// inode block pointers instead of in a data block.
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

            using var checkImage = new Ufs2Image(_imagePath, readOnly: true);
            var result = checkImage.FsckUfs(preen: true);
            Assert.True(result.Clean, $"Fsck errors: {string.Join("; ", result.Errors)}");
        }
    }
}
