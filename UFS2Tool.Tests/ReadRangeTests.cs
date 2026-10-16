// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

namespace UFS2Tool.Tests
{
    /// <summary>
    /// Tests for ReadFileRange and streaming extraction, which read a file
    /// piecewise instead of loading it whole into one byte array.
    /// </summary>
    public class ReadRangeTests : IDisposable
    {
        private const int BlockSize = 4096;
        // Past the double indirect boundary for both UFS1 (12 + 1024 blocks) and UFS2 (12 + 512 blocks)
        private const int FileSize = 5_000_003;

        private readonly string _testDir;
        private readonly string _imagePath;
        private readonly string _outputDir;

        public ReadRangeTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"ufs2test_src_{Guid.NewGuid():N}");
            _imagePath = Path.Combine(Path.GetTempPath(), $"ufs2test_{Guid.NewGuid():N}.img");
            _outputDir = Path.Combine(Path.GetTempPath(), $"ufs2test_out_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testDir);
        }

        public void Dispose()
        {
            if (File.Exists(_imagePath))
                File.Delete(_imagePath);
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
            if (Directory.Exists(_outputDir))
                Directory.Delete(_outputDir, recursive: true);
        }

        private byte[] CreateImageWithBigFile(int format)
        {
            byte[] data = new byte[FileSize];
            new Random(format).NextBytes(data);
            File.WriteAllBytes(Path.Combine(_testDir, "big.bin"), data);

            var creator = new Ufs2ImageCreator
            {
                FilesystemFormat = format,
                BlockSize = BlockSize,
                FragmentSize = BlockSize / 8,
            };
            var (_, diskSize, _) = Ufs2ImageCreator.CalculateDirectorySizes(_testDir, creator.BlockSize);
            creator.CreateImage(_imagePath, creator.CalculateImageSize(diskSize));
            creator.PopulateFromDirectory(_imagePath, _testDir);
            return data;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void ReadFileRange_MatchesSourceAcrossIndirectBoundaries(int format)
        {
            byte[] data = CreateImageWithBigFile(format);
            int ptrsPerBlock = BlockSize / (format == 1 ? 4 : 8);
            long directEnd = 12L * BlockSize;
            long singleEnd = (12L + ptrsPerBlock) * BlockSize;

            using var image = new Ufs2Image(_imagePath, readOnly: true);
            uint ino = image.ResolvePath("/big.bin");

            (long pos, int len)[] ranges =
            [
                (0, 100),
                (1000, 3 * BlockSize),
                (directEnd - 77, 200),
                (singleEnd - 5000, 10000),
                (123_457, 300_001),
                (FileSize - 10, 10),
            ];
            foreach (var (pos, len) in ranges)
            {
                byte[] buf = new byte[len];
                Assert.Equal(len, image.ReadFileRange(ino, pos, buf));
                Assert.True(data.AsSpan((int)pos, len).SequenceEqual(buf), $"mismatch at {pos}+{len}");
            }

            // Short read at the tail, nothing at or past EOF
            byte[] tail = new byte[100];
            Assert.Equal(3, image.ReadFileRange(ino, FileSize - 3, tail));
            Assert.True(data.AsSpan(FileSize - 3).SequenceEqual(tail.AsSpan(0, 3)));
            Assert.Equal(0, image.ReadFileRange(ino, FileSize, tail));
            Assert.Equal(0, image.ReadFileRange(ino, FileSize + 12345, tail));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Extract_StreamsFileMatchingSource(int format)
        {
            byte[] data = CreateImageWithBigFile(format);

            using (var image = new Ufs2Image(_imagePath, readOnly: true))
                image.Extract("/big.bin", _outputDir);

            Assert.Equal(data, File.ReadAllBytes(Path.Combine(_outputDir, "big.bin")));
        }
    }
}
