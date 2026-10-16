// Copyright (c) 2026, SvenGDK
// Licensed under the BSD 2-Clause License. See LICENSE file for details.

using FuseDotNet;
using FuseDotNet.Extensions;
using LTRData.Extensions.Native.Memory;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UFS2Tool
{
    /// <summary>
    /// Implements the FUSE filesystem interface to mount a UFS1/UFS2 image
    /// on a Linux directory. Linux counterpart of <see cref="Ufs2DokanOperations"/>
    /// with the same feature set: read-only by default; read-write allows
    /// modifying, truncating, deleting, renaming (same directory) and chmod/utimes
    /// of existing entries, but not creating new ones.
    /// </summary>
    /// <param name="imagePath">Path to the UFS1/UFS2 filesystem image.</param>
    /// <param name="readOnly">Mount as read-only.</param>
    [SupportedOSPlatform("linux")]
    public class Ufs2FuseOperations(string imagePath, bool readOnly) : IFuseOperations, IDisposable
    {
        private readonly Ufs2Image _image = new(imagePath, readOnly: readOnly);
        private readonly object _lock = new();
        private readonly bool _readOnly = readOnly;

        public void Dispose()
        {
            _image.Dispose();
        }

        /// <summary>
        /// Run an image operation under the lock and map image exceptions to errno.
        /// </summary>
        private PosixResult Run(Func<PosixResult> op)
        {
            try
            {
                lock (_lock)
                {
                    return op();
                }
            }
            catch (FileNotFoundException)
            {
                return PosixResult.ENOENT;
            }
            catch (InvalidOperationException)
            {
                return PosixResult.EACCES;
            }
            catch (ArgumentException)
            {
                return PosixResult.EINVAL;
            }
            catch (IOException)
            {
                return PosixResult.ENOSPC;
            }
        }

        private PosixResult RunWrite(Func<PosixResult> op) => _readOnly ? PosixResult.EROFS : Run(op);

        // TimeSpec mirrors struct timespec, but its public constructors round to milliseconds.
        private static TimeSpec ToTimeSpec(long sec, int nsec)
        {
            TimeSpec ts = default;
            var fields = MemoryMarshal.Cast<TimeSpec, nint>(MemoryMarshal.CreateSpan(ref ts, 1));
            fields[0] = (nint)sec;
            fields[1] = nsec;
            return ts;
        }

        private FuseFileStat ToStat(uint inodeNumber, Ufs2Inode inode) => new()
        {
            st_ino = inodeNumber,
            st_mode = (PosixFileMode)inode.Mode,
            st_nlink = inode.NLink,
            st_uid = inode.Uid,
            st_gid = inode.Gid,
            st_size = inode.Size,
            st_blocks = inode.Blocks,
            st_blksize = _image.Superblock.BSize,
            st_atim = ToTimeSpec(inode.AccessTime, inode.ATimeNsec),
            st_mtim = ToTimeSpec(inode.ModTime, inode.MTimeNsec),
            st_ctim = ToTimeSpec(inode.ChangeTime, inode.CTimeNsec),
            st_birthtim = ToTimeSpec(inode.CreateTime, inode.CreateTimeNsec),
        };

        public void Init(ref FuseConnInfo fuse_conn_info)
        {
        }

        public PosixResult GetAttr(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseFileStat stat, ref FuseFileInfo fileInfo)
        {
            FuseFileStat result = default;
            var rc = Run(() =>
            {
                uint ino = _image.ResolvePath(FuseHelper.GetString(fileNamePtr));
                result = ToStat(ino, _image.ReadInode(ino));
                return PosixResult.Success;
            });
            stat = result;
            return rc;
        }

        public PosixResult Access(ReadOnlyNativeMemory<byte> fileNamePtr, PosixAccessMode mask)
        {
            if (_readOnly && mask.HasFlag(PosixAccessMode.Write))
                return PosixResult.EROFS;
            return Run(() =>
            {
                _image.ResolvePath(FuseHelper.GetString(fileNamePtr));
                return PosixResult.Success;
            });
        }

        public PosixResult OpenDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
        {
            return Run(() =>
                _image.ReadInode(_image.ResolvePath(FuseHelper.GetString(fileNamePtr))).IsDirectory
                    ? PosixResult.Success
                    : PosixResult.ENOTDIR);
        }

        public PosixResult ReadDir(ReadOnlyNativeMemory<byte> fileNamePtr, out IEnumerable<FuseDirEntry> entries,
            ref FuseFileInfo fileInfo, long offset, FuseReadDirFlags flags)
        {
            List<FuseDirEntry> list = [];
            var rc = Run(() =>
            {
                uint dir = _image.ResolvePath(FuseHelper.GetString(fileNamePtr));
                foreach (var entry in _image.ListDirectory(dir))
                {
                    var stat = new FuseFileStat
                    {
                        st_ino = entry.Inode,
                        st_mode = (PosixFileMode)((entry.FileType & 0xF) << 12),
                    };
                    list.Add(new FuseDirEntry(entry.Name, 0, 0, stat));
                }
                return PosixResult.Success;
            });
            entries = list;
            return rc;
        }

        public PosixResult ReleaseDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
            => PosixResult.Success;

        public PosixResult FSyncDir(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo)
            => PosixResult.Success;

        public PosixResult Open(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
        {
            if (_readOnly && ((fileInfo.flags & PosixOpenFlags.AccessModes) != PosixOpenFlags.Read
                || fileInfo.flags.HasFlag(PosixOpenFlags.Truncate)))
                return PosixResult.EROFS;

            return Run(() =>
            {
                var inode = _image.ReadInode(_image.ResolvePath(FuseHelper.GetString(fileNamePtr)));
                if (inode.IsDirectory)
                    return PosixResult.EISDIR;
                return inode.IsRegularFile ? PosixResult.Success : PosixResult.EACCES;
            });
        }

        public PosixResult Read(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> buffer, long position,
            out int readLength, ref FuseFileInfo fileInfo)
        {
            int read = 0;
            var rc = Run(() =>
            {
                byte[] data = _image.ReadFile(_image.ResolvePath(FuseHelper.GetString(fileNamePtr)));
                if (position < data.Length)
                {
                    read = (int)Math.Min(buffer.Length, data.Length - position);
                    data.AsSpan((int)position, read).CopyTo(buffer.Span);
                }
                return PosixResult.Success;
            });
            readLength = read;
            return rc;
        }

        public PosixResult ReadLink(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> target)
        {
            return Run(() =>
            {
                byte[] link = _image.ReadSymlinkTarget(_image.ResolvePath(FuseHelper.GetString(fileNamePtr)));
                // FUSE wants a NUL-terminated string, truncated to the buffer if needed.
                int len = Math.Min(link.Length, target.Length - 1);
                link.AsSpan(0, len).CopyTo(target.Span);
                target.Span[len] = 0;
                return PosixResult.Success;
            });
        }

        public PosixResult StatFs(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseVfsStat statvfs)
        {
            var sb = _image.Superblock;
            // Same accounting as the Dokan GetDiskFreeSpace: free full blocks plus free fragments.
            ulong freeFrags = (ulong)(sb.FreeBlocks * (sb.BSize / sb.FSize) + sb.FreeFragments);
            statvfs = new FuseVfsStat
            {
                f_bsize = (ulong)sb.BSize,
                f_frsize = (ulong)sb.FSize,
                f_blocks = (ulong)sb.TotalBlocks,
                f_bfree = freeFrags,
                f_bavail = _readOnly ? 0 : freeFrags,
                f_files = (ulong)sb.NumCylGroups * (ulong)sb.InodesPerGroup,
                f_ffree = (ulong)sb.FreeInodes,
                f_favail = (ulong)sb.FreeInodes,
                f_namemax = Ufs2Constants.MaxNameLen,
            };
            return PosixResult.Success;
        }

        public PosixResult Write(ReadOnlyNativeMemory<byte> fileNamePtr, ReadOnlyNativeMemory<byte> buffer, long position,
            out int writtenLength, ref FuseFileInfo fileInfo)
        {
            int written = 0;
            var rc = RunWrite(() =>
            {
                string path = FuseHelper.GetString(fileNamePtr);
                // Read existing file data, apply the write, then replace (as the Dokan backend does)
                byte[] existingData = _image.ReadFile(_image.ResolvePath(path));
                long newSize = Math.Max(existingData.Length, position + buffer.Length);
                if (newSize > int.MaxValue)
                    return PosixResult.EFBIG;
                byte[] newData = new byte[(int)newSize];
                existingData.CopyTo(newData, 0);
                buffer.Span.CopyTo(newData.AsSpan((int)position));
                _image.ReplaceFileContent(path, newData);
                written = buffer.Length;
                return PosixResult.Success;
            });
            writtenLength = written;
            return rc;
        }

        public PosixResult Truncate(ReadOnlyNativeMemory<byte> fileNamePtr, long size)
        {
            if (size < 0 || size > int.MaxValue)
                return PosixResult.EINVAL;

            return RunWrite(() =>
            {
                string path = FuseHelper.GetString(fileNamePtr);
                byte[] existingData = _image.ReadFile(_image.ResolvePath(path));
                byte[] newData = new byte[(int)size];
                existingData.AsSpan(0, Math.Min(existingData.Length, (int)size)).CopyTo(newData);
                _image.ReplaceFileContent(path, newData);
                return PosixResult.Success;
            });
        }

        public PosixResult Unlink(ReadOnlyNativeMemory<byte> fileNamePtr)
        {
            return RunWrite(() =>
            {
                string path = FuseHelper.GetString(fileNamePtr);
                if (_image.ReadInode(_image.ResolvePath(path)).IsDirectory)
                    return PosixResult.EISDIR;
                _image.Delete(path);
                return PosixResult.Success;
            });
        }

        public PosixResult RmDir(ReadOnlyNativeMemory<byte> fileNamePtr)
        {
            return RunWrite(() =>
            {
                string path = FuseHelper.GetString(fileNamePtr);
                uint ino = _image.ResolvePath(path);
                if (!_image.ReadInode(ino).IsDirectory)
                    return PosixResult.ENOTDIR;
                // Ufs2Image.Delete is recursive; rmdir(2) must refuse a non-empty directory.
                if (_image.ListDirectory(ino).Any(e => e.Name != "." && e.Name != ".."))
                    return LinuxResult.ENOTEMPTY;
                _image.Delete(path);
                return PosixResult.Success;
            });
        }

        public PosixResult Rename(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
        {
            return RunWrite(() =>
            {
                string oldPath = FuseHelper.GetString(from);
                string newPath = FuseHelper.GetString(to);
                // Ufs2Image.Rename only renames within the parent directory.
                if (Ufs2Image.GetParentPath(oldPath) != Ufs2Image.GetParentPath(newPath))
                    return PosixResult.EXDEV;

                uint src = _image.ResolvePath(oldPath);
                uint dst;
                try
                {
                    dst = _image.ResolvePath(newPath);
                }
                catch (FileNotFoundException)
                {
                    dst = 0;
                }

                if (dst != 0)
                {
                    if (dst == src)
                        return PosixResult.Success;
                    // rename(2) replaces an existing non-directory target atomically from the caller's view.
                    if (_image.ReadInode(src).IsDirectory || _image.ReadInode(dst).IsDirectory)
                        return PosixResult.EEXIST;
                    _image.Delete(newPath);
                }

                _image.Rename(oldPath, Ufs2Image.GetLastComponent(newPath));
                return PosixResult.Success;
            });
        }

        public PosixResult ChMod(NativeMemory<byte> fileNamePtr, PosixFileMode mode)
        {
            return RunWrite(() =>
            {
                _image.Chmod(FuseHelper.GetString(fileNamePtr.AsReadOnly()), (ushort)mode);
                return PosixResult.Success;
            });
        }

        public PosixResult UTime(ReadOnlyNativeMemory<byte> fileNamePtr, TimeSpec atime, TimeSpec mtime, ref FuseFileInfo fileInfo)
        {
            return RunWrite(() =>
            {
                uint ino = _image.ResolvePath(FuseHelper.GetString(fileNamePtr));
                var inode = _image.ReadInode(ino);
                var now = TimeSpec.Now();

                if (!atime.IsOmit)
                {
                    var t = atime.IsPseudoNow ? now : atime;
                    inode.AccessTime = t.tv_sec;
                    inode.ATimeNsec = (int)t.tv_nsec;
                }
                if (!mtime.IsOmit)
                {
                    var t = mtime.IsPseudoNow ? now : mtime;
                    inode.ModTime = t.tv_sec;
                    inode.MTimeNsec = (int)t.tv_nsec;
                }
                inode.ChangeTime = now.tv_sec;
                inode.CTimeNsec = (int)now.tv_nsec;
                _image.WriteInode(ino, inode);
                return PosixResult.Success;
            });
        }

        public PosixResult Release(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
            => PosixResult.Success;

        public PosixResult Flush(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
            => PosixResult.Success;

        public PosixResult FSync(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo)
            => PosixResult.Success;

        // Creating new inodes is not supported through the mount, same as the Dokan backend.
        public PosixResult Create(ReadOnlyNativeMemory<byte> fileNamePtr, int mode, ref FuseFileInfo fileInfo)
            => _readOnly ? PosixResult.EROFS : PosixResult.ENOTSUP;

        public PosixResult MkDir(ReadOnlyNativeMemory<byte> fileNamePtr, PosixFileMode mode)
            => _readOnly ? PosixResult.EROFS : PosixResult.ENOTSUP;

        public PosixResult SymLink(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
            => _readOnly ? PosixResult.EROFS : PosixResult.ENOTSUP;

        public PosixResult Link(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
            => _readOnly ? PosixResult.EROFS : PosixResult.ENOTSUP;

        public PosixResult ChOwn(NativeMemory<byte> fileNamePtr, int uid, int gid)
            => _readOnly ? PosixResult.EROFS : PosixResult.EPERM;

        public PosixResult FAllocate(NativeMemory<byte> fileNamePtr, FuseAllocateMode mode, long offset, long length,
            ref FuseFileInfo fileInfo)
            => PosixResult.ENOTSUP;

        public PosixResult IoCtl(ReadOnlyNativeMemory<byte> fileNamePtr, int cmd, nint arg, ref FuseFileInfo fileInfo,
            FuseIoctlFlags flags, nint data)
            => PosixResult.ENOTSUP;
    }
}
