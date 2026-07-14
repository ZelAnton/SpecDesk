using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpecDesk.Git;

internal sealed class GitReferenceDeleteOperation : IDisposable
{
	private static readonly Lazy<NativeApi> Api = new(NativeApi.Load);
	private readonly NativeApi _api;
	private nint _repository;
	private nint _reference;

	private GitReferenceDeleteOperation(NativeApi api, nint repository, nint reference)
	{
		_api = api;
		_repository = repository;
		_reference = reference;
	}

	internal static GitReferenceDeleteOperation Prepare(
		string repositoryPath,
		string canonicalName,
		string expectedTarget)
	{
		NativeApi api = Api.Value;
		if (api.RepositoryOpen(out nint repository, repositoryPath) != 0)
		{
			throw new RepositoryStateChangedException();
		}
		if (api.ReferenceLookup(out nint reference, repository, canonicalName) != 0)
		{
			api.RepositoryFree(repository);
			throw new RepositoryStateChangedException();
		}
		string? actualTarget = Marshal.PtrToStringUTF8(api.OidToString(api.ReferenceTarget(reference)));
		if (!string.Equals(actualTarget, expectedTarget, StringComparison.OrdinalIgnoreCase))
		{
			api.ReferenceFree(reference);
			api.RepositoryFree(repository);
			throw new RepositoryStateChangedException();
		}
		return new GitReferenceDeleteOperation(api, repository, reference);
	}

	internal void Delete()
	{
		if (_api.BranchDelete(_reference) != 0)
		{
			throw new RepositoryStateChangedException();
		}
	}

	public void Dispose()
	{
		if (_reference != 0)
		{
			_api.ReferenceFree(_reference);
			_reference = 0;
		}
		if (_repository != 0)
		{
			_api.RepositoryFree(_repository);
			_repository = 0;
		}
	}

	private sealed record NativeApi(
		RepositoryOpenDelegate RepositoryOpen,
		RepositoryFreeDelegate RepositoryFree,
		ReferenceLookupDelegate ReferenceLookup,
		ReferenceTargetDelegate ReferenceTarget,
		OidToStringDelegate OidToString,
		BranchDeleteDelegate BranchDelete,
		ReferenceFreeDelegate ReferenceFree)
	{
		internal static NativeApi Load()
		{
			// LibGit2Sharp does not expose reference compare-and-delete. Bind the same native module it already
			// loaded so the ABI/RID always matches, without requiring an independently installed Git executable.
			using Process process = Process.GetCurrentProcess();
			ProcessModule module = process.Modules.Cast<ProcessModule>().FirstOrDefault(candidate =>
				candidate.ModuleName.Contains("git2-", StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException("The native Git engine is not loaded.");
			nint library = NativeLibrary.Load(module.FileName);
			return new NativeApi(
				Load<RepositoryOpenDelegate>(library, "git_repository_open"),
				Load<RepositoryFreeDelegate>(library, "git_repository_free"),
				Load<ReferenceLookupDelegate>(library, "git_reference_lookup"),
				Load<ReferenceTargetDelegate>(library, "git_reference_target"),
				Load<OidToStringDelegate>(library, "git_oid_tostr_s"),
				Load<BranchDeleteDelegate>(library, "git_branch_delete"),
				Load<ReferenceFreeDelegate>(library, "git_reference_free"));
		}

		private static T Load<T>(nint library, string name) where T : Delegate =>
			Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int RepositoryOpenDelegate(
		out nint repository,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void RepositoryFreeDelegate(nint repository);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int ReferenceLookupDelegate(
		out nint reference,
		nint repository,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate nint ReferenceTargetDelegate(nint reference);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate nint OidToStringDelegate(nint oid);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int BranchDeleteDelegate(nint reference);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void ReferenceFreeDelegate(nint reference);
}
