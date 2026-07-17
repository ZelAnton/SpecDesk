using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpecDesk.Host;

internal enum HandleDeleteFailure
{
	None,
	RootChanged,
	OutsideRoot,
	ReparsePoint,
	Directory,
	Missing,
	ReadOnly,
	Locked,
	AccessDenied,
	Unavailable,
}

internal readonly record struct HandleDeleteResult(
	HandleDeleteFailure Failure,
	int NativeError = 0,
	bool DeletedActiveDocument = false)
{
	public bool Succeeded => Failure == HandleDeleteFailure.None;
}

/// <summary>
/// Deletes a Windows file through the same kernel handle that was validated. A pathname-only
/// check followed by <see cref="File.Delete(string)"/> is unsafe here: an intermediate directory can
/// be replaced with a junction between those calls and redirect the deletion outside Disk. Opening the
/// root and target first, validating their final handle paths, and setting disposition on that target
/// handle binds validation and deletion to one filesystem object even if the namespace changes later.
/// </summary>
internal static class WindowsHandleFileDeletion
{
	private const uint DeleteAccess = 0x00010000;
	private const uint FileReadAttributes = 0x00000080;
	private const uint FileShareRead = 0x00000001;
	private const uint FileShareWrite = 0x00000002;
	private const uint FileShareDelete = 0x00000004;
	private const uint OpenExisting = 3;
	private const uint FileFlagBackupSemantics = 0x02000000;
	private const uint FileFlagOpenReparsePoint = 0x00200000;
	private const uint FileDispositionDelete = 0x00000001;
	private const uint FileDispositionPosixSemantics = 0x00000002;
	private const int ErrorFileNotFound = 2;
	private const int ErrorPathNotFound = 3;
	private const int ErrorAccessDenied = 5;
	private const int ErrorSharingViolation = 32;
	private const int ErrorLockViolation = 33;

	public static HandleDeleteResult Delete(
		string root,
		string target,
		Func<string, string, bool>? reparseCheckOverride = null,
		string? activeDocumentPath = null)
	{
		if (!OperatingSystem.IsWindows())
		{
			return new(HandleDeleteFailure.Unavailable);
		}

		using SafeFileHandle rootHandle = CreateFile(
			root,
			FileReadAttributes,
			FileShareRead | FileShareWrite | FileShareDelete,
			IntPtr.Zero,
			OpenExisting,
			FileFlagBackupSemantics | FileFlagOpenReparsePoint,
			IntPtr.Zero);
		if (rootHandle.IsInvalid)
		{
			return new(HandleDeleteFailure.RootChanged, Marshal.GetLastPInvokeError());
		}

		if (!TryGetAttributes(rootHandle, out FileAttributeTagInfo rootInfo)
			|| rootInfo.FileAttributes.HasFlag(FileAttributes.ReparsePoint)
			|| !rootInfo.FileAttributes.HasFlag(FileAttributes.Directory)
			|| !TryGetFinalPath(rootHandle, out string? finalRoot)
			|| !AreSameCanonicalHandlePath(root, finalRoot)
			|| !TryGetIdentity(rootHandle, out FileIdentity rootIdentity))
		{
			return new(HandleDeleteFailure.RootChanged, Marshal.GetLastPInvokeError());
		}
		// The race hook sits at the old pathname-only boundary: after the preliminary link check but before
		// the target is opened. If the namespace changes here, the handle below opens the new object and its
		// final path/identity validation fails closed instead of deleting through that changed pathname.
		if ((reparseCheckOverride ?? AppAssetResolver.HasReparseTraversal)(root, target))
		{
			return new(HandleDeleteFailure.ReparsePoint);
		}

		using SafeFileHandle targetHandle = CreateFile(
			target,
			DeleteAccess | FileReadAttributes,
			FileShareRead | FileShareWrite | FileShareDelete,
			IntPtr.Zero,
			OpenExisting,
			FileFlagBackupSemantics | FileFlagOpenReparsePoint,
			IntPtr.Zero);
		if (targetHandle.IsInvalid)
		{
			int error = Marshal.GetLastPInvokeError();
			return new(MapOpenFailure(error), error);
		}

		if (!TryGetAttributes(targetHandle, out FileAttributeTagInfo targetInfo))
		{
			return new(HandleDeleteFailure.Unavailable, Marshal.GetLastPInvokeError());
		}
		if (targetInfo.FileAttributes.HasFlag(FileAttributes.ReparsePoint))
		{
			return new(HandleDeleteFailure.ReparsePoint);
		}
		if (targetInfo.FileAttributes.HasFlag(FileAttributes.Directory))
		{
			return new(HandleDeleteFailure.Directory);
		}
		if (targetInfo.FileAttributes.HasFlag(FileAttributes.ReadOnly))
		{
			return new(HandleDeleteFailure.ReadOnly);
		}
		if (!TryGetFinalPath(targetHandle, out string? finalTarget)
			|| !IsCanonicalHandleDescendant(finalRoot, finalTarget)
			|| !TryGetIdentity(targetHandle, out FileIdentity targetIdentity))
		{
			return new(HandleDeleteFailure.OutsideRoot, Marshal.GetLastPInvokeError());
		}

		bool deletesActiveDocument = false;
		if (activeDocumentPath is not null)
		{
			using SafeFileHandle activeDocumentHandle = CreateFile(
				activeDocumentPath,
				FileReadAttributes,
				FileShareRead | FileShareWrite | FileShareDelete,
				IntPtr.Zero,
				OpenExisting,
				FileFlagBackupSemantics | FileFlagOpenReparsePoint,
				IntPtr.Zero);
			if (activeDocumentHandle.IsInvalid)
			{
				int error = Marshal.GetLastPInvokeError();
				return new(MapOpenFailure(error), error);
			}
			if (!TryGetAttributes(activeDocumentHandle, out FileAttributeTagInfo activeInfo)
				|| activeInfo.FileAttributes.HasFlag(FileAttributes.ReparsePoint)
				|| activeInfo.FileAttributes.HasFlag(FileAttributes.Directory)
				|| !TryGetFinalPath(activeDocumentHandle, out string? finalActiveDocument))
			{
				return new(HandleDeleteFailure.Unavailable, Marshal.GetLastPInvokeError());
			}
			deletesActiveDocument = AreSameCanonicalHandlePath(finalActiveDocument, finalTarget);
		}

		// Re-open the namespace and compare stable file IDs. A later root/target swap fails closed here; if a
		// swap happens after this comparison, disposition still applies to targetHandle, never the new path.
		if (!NamespaceStillNamesSameObjects(
			root, target, finalRoot, finalTarget, rootIdentity, targetIdentity))
		{
			return new(HandleDeleteFailure.RootChanged);
		}

		FileDispositionInfoEx disposition = new()
		{
			Flags = FileDispositionDelete | FileDispositionPosixSemantics,
		};
		if (SetFileInformationByHandle(
			targetHandle,
			FileInfoByHandleClass.FileDispositionInfoEx,
			ref disposition,
			(uint)Marshal.SizeOf<FileDispositionInfoEx>()))
		{
			return new(HandleDeleteFailure.None, DeletedActiveDocument: deletesActiveDocument);
		}

		int dispositionError = Marshal.GetLastPInvokeError();
		// Some Windows filesystems reject POSIX disposition for a file whose parent was renamed while its
		// handle remained open. The legacy handle disposition is equally object-bound, so it is a safe
		// compatibility fallback for every Ex failure, not a return to pathname deletion.
		FileDispositionInfo legacyDisposition = new() { DeleteFile = 1 };
		if (SetFileInformationByHandle(
			targetHandle,
			FileInfoByHandleClass.FileDispositionInfo,
			ref legacyDisposition,
			(uint)Marshal.SizeOf<FileDispositionInfo>()))
		{
			return new(HandleDeleteFailure.None, DeletedActiveDocument: deletesActiveDocument);
		}
		dispositionError = Marshal.GetLastPInvokeError();

		return new(MapOpenFailure(dispositionError), dispositionError);
	}

	private static HandleDeleteFailure MapOpenFailure(int error) => error switch
	{
		ErrorFileNotFound or ErrorPathNotFound => HandleDeleteFailure.Missing,
		ErrorSharingViolation or ErrorLockViolation => HandleDeleteFailure.Locked,
		ErrorAccessDenied => HandleDeleteFailure.AccessDenied,
		_ => HandleDeleteFailure.Unavailable,
	};

	private static bool TryGetAttributes(SafeFileHandle handle, out FileAttributeTagInfo info) =>
		GetFileInformationByHandleEx(
			handle,
			FileInfoByHandleClass.FileAttributeTagInfo,
			out info,
			(uint)Marshal.SizeOf<FileAttributeTagInfo>());

	private static bool NamespaceStillNamesSameObjects(
		string root,
		string target,
		string expectedFinalRoot,
		string expectedFinalTarget,
		FileIdentity expectedRoot,
		FileIdentity expectedTarget)
	{
		using SafeFileHandle rootProbe = CreateFile(
			root, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete,
			IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
		if (rootProbe.IsInvalid
			|| !TryGetAttributes(rootProbe, out FileAttributeTagInfo rootInfo)
			|| rootInfo.FileAttributes.HasFlag(FileAttributes.ReparsePoint)
			|| !TryGetFinalPath(rootProbe, out string? currentFinalRoot)
			|| !AreSameCanonicalHandlePath(currentFinalRoot, expectedFinalRoot)
			|| !TryGetIdentity(rootProbe, out FileIdentity currentRoot)
			|| currentRoot != expectedRoot)
		{
			return false;
		}

		using SafeFileHandle targetProbe = CreateFile(
			target, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete,
			IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
		return !targetProbe.IsInvalid
			&& TryGetAttributes(targetProbe, out FileAttributeTagInfo targetInfo)
			&& !targetInfo.FileAttributes.HasFlag(FileAttributes.ReparsePoint)
			&& TryGetFinalPath(targetProbe, out string? currentFinalTarget)
			&& AreSameCanonicalHandlePath(currentFinalTarget, expectedFinalTarget)
			&& IsCanonicalHandleDescendant(currentFinalRoot, currentFinalTarget)
			&& TryGetIdentity(targetProbe, out FileIdentity currentTarget)
			&& currentTarget == expectedTarget;
	}

	private static bool TryGetIdentity(SafeFileHandle handle, out FileIdentity identity)
	{
		if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
		{
			identity = default;
			return false;
		}
		identity = new(
			information.VolumeSerialNumber,
			information.FileIndexHigh,
			information.FileIndexLow);
		return true;
	}

	private static bool TryGetFinalPath(SafeFileHandle handle, out string path)
	{
		char[] buffer = new char[512];
		for (int attempt = 0; attempt < 3; attempt++)
		{
			uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
			if (length == 0)
			{
				path = string.Empty;
				return false;
			}
			if (length < buffer.Length)
			{
				path = NormalizeHandlePath(new string(buffer, 0, checked((int)length)));
				return true;
			}
			buffer = new char[checked((int)length + 1)];
		}
		path = string.Empty;
		return false;
	}

	private static string NormalizeHandlePath(string path)
	{
		const string uncPrefix = @"\\?\UNC\";
		const string devicePrefix = @"\\?\";
		if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return @"\\" + path[uncPrefix.Length..];
		}
		return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
			? path[devicePrefix.Length..]
			: path;
	}

	/// <summary>
	/// Directory-entry identity for destructive operations (policy 3 in <see cref="PathIdentity"/>).
	/// Final paths returned by Windows name the actual directory entries. Their ancestry must therefore
	/// remain case-sensitive even on the usual case-insensitive NTFS configuration: a parent directory may
	/// opt into case sensitivity and contain separate <c>Root</c> and <c>root</c> siblings. Only the volume
	/// authority (a drive designator, or an UNC server/share pair) is compared case-insensitively because those
	/// names are not directory entries within that volume. This is the strictest of the host's path policies
	/// and, like the session-document policy, is case-sensitive and fails closed on purpose.
	/// </summary>
	internal static bool IsCanonicalHandleDescendant(string root, string candidate)
	{
		const char separator = '\\';
		CanonicalWindowsPath rootPath = SplitCanonicalWindowsPath(root);
		CanonicalWindowsPath candidatePath = SplitCanonicalWindowsPath(candidate);
		if (!string.Equals(rootPath.Authority, candidatePath.Authority, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string rootWithSeparator = rootPath.Tail.TrimEnd(separator) + separator;
		if (candidatePath.Tail.Length <= rootWithSeparator.Length)
		{
			return false;
		}
		return candidatePath.Tail.StartsWith(rootWithSeparator, StringComparison.Ordinal);
	}

	internal static bool AreSameCanonicalHandlePath(string left, string right)
	{
		CanonicalWindowsPath leftPath = SplitCanonicalWindowsPath(left);
		CanonicalWindowsPath rightPath = SplitCanonicalWindowsPath(right);
		return string.Equals(leftPath.Authority, rightPath.Authority, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(
				leftPath.Tail.TrimEnd('\\'), rightPath.Tail.TrimEnd('\\'), StringComparison.Ordinal);
	}

	internal static bool TryAreSameCanonicalEntryPath(string left, string right, out bool same)
	{
		same = false;
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}
		using SafeFileHandle leftHandle = CreateFile(
			left, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete,
			IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
		using SafeFileHandle rightHandle = CreateFile(
			right, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete,
			IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
		if (leftHandle.IsInvalid
			|| rightHandle.IsInvalid
			|| !TryGetAttributes(leftHandle, out FileAttributeTagInfo leftInfo)
			|| !TryGetAttributes(rightHandle, out FileAttributeTagInfo rightInfo)
			|| leftInfo.FileAttributes.HasFlag(FileAttributes.ReparsePoint)
			|| rightInfo.FileAttributes.HasFlag(FileAttributes.ReparsePoint)
			|| !TryGetFinalPath(leftHandle, out string? finalLeft)
			|| !TryGetFinalPath(rightHandle, out string? finalRight))
		{
			return false;
		}
		same = AreSameCanonicalHandlePath(finalLeft, finalRight);
		return true;
	}

	private static CanonicalWindowsPath SplitCanonicalWindowsPath(string path)
	{
		string normalized = NormalizeHandlePath(path);
		if (HasDriveDesignator(normalized))
		{
			return new(normalized[..2], normalized[2..]);
		}
		if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
		{
			return new(string.Empty, normalized);
		}

		int serverEnd = normalized.IndexOf('\\', 2);
		if (serverEnd <= 2 || serverEnd == normalized.Length - 1)
		{
			return new(string.Empty, normalized);
		}
		int shareEnd = normalized.IndexOf('\\', serverEnd + 1);
		if (shareEnd == serverEnd + 1)
		{
			return new(string.Empty, normalized);
		}
		return shareEnd < 0
			? new(normalized, string.Empty)
			: new(normalized[..shareEnd], normalized[shareEnd..]);
	}

	private static bool HasDriveDesignator(string path) =>
		path.Length >= 2 && path[1] == ':';

	private readonly record struct CanonicalWindowsPath(string Authority, string Tail);

	[StructLayout(LayoutKind.Sequential)]
	private struct FileAttributeTagInfo
	{
		public FileAttributes FileAttributes;
		public uint ReparseTag;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FileDispositionInfoEx
	{
		public uint Flags;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FileDispositionInfo
	{
		public byte DeleteFile;
	}

	private readonly record struct FileIdentity(
		uint VolumeSerialNumber,
		uint FileIndexHigh,
		uint FileIndexLow);

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeFileTime
	{
		public uint LowDateTime;
		public uint HighDateTime;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ByHandleFileInformation
	{
		public uint FileAttributes;
		public NativeFileTime CreationTime;
		public NativeFileTime LastAccessTime;
		public NativeFileTime LastWriteTime;
		public uint VolumeSerialNumber;
		public uint FileSizeHigh;
		public uint FileSizeLow;
		public uint NumberOfLinks;
		public uint FileIndexHigh;
		public uint FileIndexLow;
	}

	private enum FileInfoByHandleClass
	{
		FileDispositionInfo = 4,
		FileAttributeTagInfo = 9,
		FileDispositionInfoEx = 21,
	}

	[DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeFileHandle CreateFile(
		string fileName,
		uint desiredAccess,
		uint shareMode,
		IntPtr securityAttributes,
		uint creationDisposition,
		uint flagsAndAttributes,
		IntPtr templateFile);

	[DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetFileInformationByHandleEx(
		SafeFileHandle file,
		FileInfoByHandleClass fileInformationClass,
		out FileAttributeTagInfo fileInformation,
		uint bufferSize);

	[DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetFileInformationByHandle(
		SafeFileHandle file,
		out ByHandleFileInformation fileInformation);

	[DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern uint GetFinalPathNameByHandle(
		SafeFileHandle file,
		[Out] char[] filePath,
		uint filePathLength,
		uint flags);

	[DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetFileInformationByHandle(
		SafeFileHandle file,
		FileInfoByHandleClass fileInformationClass,
		ref FileDispositionInfoEx fileInformation,
		uint bufferSize);

	[DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetFileInformationByHandle(
		SafeFileHandle file,
		FileInfoByHandleClass fileInformationClass,
		ref FileDispositionInfo fileInformation,
		uint bufferSize);
}
