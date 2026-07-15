using LibGit2Sharp;
using SpecDesk.Git;

namespace SpecDesk.Git.Tests;

[TestFixture]
public sealed class LocalRepositoryManagerTests
{
	private string _root = string.Empty;
	private string _baseBranch = string.Empty;
	private readonly LibGit2RepositoryCloner _manager = new();

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "specdesk-local-manager-" + Guid.NewGuid().ToString("N"));
		Repository.Init(_root);
		using Repository repository = new(_root);
		File.WriteAllText(Path.Combine(_root, "spec.md"), "base");
		Commands.Stage(repository, "spec.md");
		Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
		repository.Commit("base", author, author);
		_baseBranch = repository.Head.FriendlyName;
		repository.CreateBranch("feature");
		repository.Network.Remotes.Add("origin", _root);
	}

	[TearDown]
	public void TearDown()
	{
		if (!Directory.Exists(_root))
		{
			return;
		}
		foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}
		Directory.Delete(_root, recursive: true);
	}

	[Test]
	public void SwitchBranchSafely_ProtectsAndRestoresTrackedAndUntrackedWorkPerBranch()
	{
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
			File.WriteAllText(Path.Combine(_root, "spec.md"), "feature work");
			File.WriteAllText(Path.Combine(_root, "notes.txt"), "feature notes");
		}

		BranchSwitchResult toMain = _manager.SwitchBranchSafely(_root, _root, "feature", _baseBranch);
		Assert.Multiple(() =>
		{
			Assert.That(toMain.CreatedSafetyCopy, Is.True);
			Assert.That(toMain.RestoredSafetyCopy, Is.False);
			Assert.That(File.ReadAllText(Path.Combine(_root, "spec.md")), Is.EqualTo("base"));
			Assert.That(File.Exists(Path.Combine(_root, "notes.txt")), Is.False);
		});

		File.WriteAllText(Path.Combine(_root, "spec.md"), "main work");
		BranchSwitchResult toFeature = _manager.SwitchBranchSafely(_root, _root, _baseBranch, "feature");

		using Repository result = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(toFeature.CreatedSafetyCopy, Is.True);
			Assert.That(toFeature.RestoredSafetyCopy, Is.True);
			Assert.That(toFeature.HasConflicts, Is.False);
			Assert.That(result.Head.FriendlyName, Is.EqualTo("feature"));
			Assert.That(File.ReadAllText(Path.Combine(_root, "spec.md")), Is.EqualTo("feature work"));
			Assert.That(File.ReadAllText(Path.Combine(_root, "notes.txt")), Is.EqualTo("feature notes"));
			Assert.That(result.Stashes.Count(), Is.EqualTo(1), "only main's protected work should remain");
		});
	}

	[Test]
	public void SwitchBranchSafely_MutationBoundaryRunsImmediatelyBeforeCheckout()
	{
		string? observedBranch = null;

		_manager.SwitchBranchSafely(
			_root,
			_root,
			_baseBranch,
			"feature",
			onMutationStarting: () =>
			{
				using Repository observed = new(_root);
				observedBranch = observed.Head.FriendlyName;
			});

		using Repository completed = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(observedBranch, Is.EqualTo(_baseBranch));
			Assert.That(completed.Head.FriendlyName, Is.EqualTo("feature"));
		});
	}

	[Test]
	public void CreateBranch_CreatesAndChecksOutALocalWorkingLineWithoutLosingUnfinishedFiles()
	{
		File.WriteAllText(Path.Combine(_root, "notes.txt"), "unfinished");

		LocalRepositoryInfo result = _manager.CreateBranch(
			_root, _root, _baseBranch, "q3-review");

		using Repository repository = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(result.CurrentBranch, Is.EqualTo("q3-review"));
			Assert.That(repository.Head.FriendlyName, Is.EqualTo("q3-review"));
			Assert.That(repository.Branches["q3-review"], Is.Not.Null);
			Assert.That(File.ReadAllText(Path.Combine(_root, "notes.txt")), Is.EqualTo("unfinished"));
		});
	}

	[Test]
	public void RenameBranch_UpdatesTheCurrentLocalNameAndRejectsTheMainWorkingLine()
	{
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
		}

		LocalRepositoryInfo result = _manager.RenameBranch(
			_root, _root, "feature", "feature", "approved-draft", _baseBranch);

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(result.CurrentBranch, Is.EqualTo("approved-draft"));
			Assert.That(inspected.Head.FriendlyName, Is.EqualTo("approved-draft"));
			Assert.That(inspected.Branches["feature"], Is.Null);
			Assert.That(inspected.Branches["approved-draft"], Is.Not.Null);
			Assert.That(
				() => _manager.RenameBranch(
					_root, _root, "approved-draft", _baseBranch, "renamed-main", _baseBranch),
				Throws.InvalidOperationException);
		});
	}

	[Test]
	public void RenameClone_MovesOnlyTheExactLocalFolderAndReturnsItsInspectedState()
	{
		string original = _root;
		CloneRenameResult result = _manager.RenameClone(
			original, original, _baseBranch, "quarterly-specs");
		_root = result.Path;

		Assert.Multiple(() =>
		{
			Assert.That(Directory.Exists(original), Is.False);
			Assert.That(Directory.Exists(result.Path), Is.True);
			Assert.That(Path.GetFileName(result.Path), Is.EqualTo("quarterly-specs"));
			Assert.That(result.Repository.DefaultBranch, Is.EqualTo(_baseBranch));
			Assert.That(File.ReadAllText(Path.Combine(result.Path, "spec.md")), Is.EqualTo("base"));
		});
	}

	[Test]
	public void RenameClone_LinkedWorktreeLeavesBothTreesInPlace()
	{
		string linkedPath = _root + "-linked-rename";
		try
		{
			using (Repository repository = new(_root))
			{
				repository.Worktrees.Add("feature", "feature-linked-rename", linkedPath, isLocked: false);
			}

			RepositoryHasLinkedWorktreesException? error = Assert.Throws<RepositoryHasLinkedWorktreesException>(
				() => _manager.RenameClone(_root, _root, _baseBranch, "renamed-with-linked-copy"));

			Assert.Multiple(() =>
			{
				Assert.That(error!.LinkedWorktrees, Has.Count.EqualTo(1));
				Assert.That(Repository.IsValid(_root), Is.True);
				Assert.That(Repository.IsValid(linkedPath), Is.True);
				Assert.That(Directory.Exists(Path.Combine(
					Directory.GetParent(_root)!.FullName,
					"renamed-with-linked-copy")), Is.False);
			});
		}
		finally
		{
			DeleteTestTree(linkedPath);
		}
	}

	[Test]
	public void SwitchBranchSafely_CreatesALocalTrackingBranchFromRemoteInventory()
	{
		using (Repository repository = new(_root))
		{
			Branch local = repository.Branches["feature"]!;
			repository.Branches.Remove(local);
			repository.Network.Remotes.Remove("origin");
			repository.Network.Remotes.Add("origin", "https://github.com/octo/specs.git");
			repository.Refs.Add("refs/remotes/origin/feature", repository.Head.Tip.Id);
		}

		BranchSwitchResult result = _manager.SwitchBranchSafely(
			_root, "https://github.com/octo/specs", _baseBranch, "feature");

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(result.CurrentBranch, Is.EqualTo("feature"));
			Assert.That(inspected.Head.FriendlyName, Is.EqualTo("feature"));
			Assert.That(inspected.Head.TrackedBranch?.FriendlyName, Is.EqualTo("origin/feature"));
		});
	}

	[Test]
	public void SwitchBranchSafely_MissingBranchLeavesLocalWorkInPlace()
	{
		File.WriteAllText(Path.Combine(_root, "spec.md"), "work in progress");
		File.WriteAllText(Path.Combine(_root, "notes.txt"), "local notes");

		Assert.Throws<InvalidOperationException>(() =>
			_manager.SwitchBranchSafely(_root, _root, _baseBranch, "missing"));

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(inspected.Head.FriendlyName, Is.EqualTo(_baseBranch));
			Assert.That(File.ReadAllText(Path.Combine(_root, "spec.md")), Is.EqualTo("work in progress"));
			Assert.That(File.ReadAllText(Path.Combine(_root, "notes.txt")), Is.EqualTo("local notes"));
			Assert.That(inspected.Stashes, Is.Empty);
		});
	}

	[Test]
	public void SwitchBranchSafely_DifferentOriginFailsBeforeProtectingOrSwitching()
	{
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
		}
		File.WriteAllText(Path.Combine(_root, "spec.md"), "work in the other repository");
		string differentRepository = Path.Combine(Path.GetDirectoryName(_root)!, "different-repository.git");

		Assert.Throws<RepositoryIdentityMismatchException>(() =>
			_manager.SwitchBranchSafely(_root, differentRepository, "feature", _baseBranch));

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(inspected.Head.FriendlyName, Is.EqualTo("feature"));
			Assert.That(inspected.Stashes, Is.Empty);
			Assert.That(File.ReadAllText(Path.Combine(_root, "spec.md")),
				Is.EqualTo("work in the other repository"));
		});
	}

	[Test]
	public void SwitchBranchSafely_ChangedCurrentBranchFailsBeforeThePersistenceCallback()
	{
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
		}
		bool callbackInvoked = false;

		Assert.Throws<InvalidOperationException>(() =>
			_manager.SwitchBranchSafely(
				_root,
				_root,
				_baseBranch,
				_baseBranch,
				() => callbackInvoked = true));

		Assert.That(callbackInvoked, Is.False);
	}

	[Test]
	public void SwitchBranchSafely_CurrentCleanBranchRestoresItsRememberedWork()
	{
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
			File.WriteAllText(Path.Combine(_root, "spec.md"), "remembered feature work");
		}
		_manager.SwitchBranchSafely(_root, _root, "feature", _baseBranch);
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
		}

		BranchSwitchResult result = _manager.SwitchBranchSafely(_root, _root, "feature", "feature");

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(result.RestoredSafetyCopy, Is.True);
			Assert.That(File.ReadAllText(Path.Combine(_root, "spec.md")), Is.EqualTo("remembered feature work"));
			Assert.That(inspected.Stashes, Is.Empty);
		});
	}

	[Test]
	public void DeleteBranch_ConfirmedCurrentLineProtectsThenDiscardsItsWork()
	{
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
			File.WriteAllText(Path.Combine(_root, "spec.md"), "discarded feature work");
			File.WriteAllText(Path.Combine(_root, "notes.txt"), "discarded notes");
		}
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, "feature", "feature");
		Assert.Multiple(() =>
		{
			Assert.That(risks.HasUncommitted, Is.True);
			Assert.That(risks.HasUnpushed, Is.True);
		});

		BranchDeletionResult result = _manager.DeleteBranch(
			_root, _root, "feature", _baseBranch, risks.ConfirmationToken);

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(result.SwitchedCurrentBranch, Is.True);
			Assert.That(inspected.Head.FriendlyName, Is.EqualTo(_baseBranch));
			Assert.That(inspected.Branches["feature"], Is.Null);
			Assert.That(inspected.Stashes, Is.Empty);
			Assert.That(File.ReadAllText(Path.Combine(_root, "spec.md")), Is.EqualTo("base"));
			Assert.That(File.Exists(Path.Combine(_root, "notes.txt")), Is.False);
		});
	}

	[Test]
	public void DeleteBranch_RejectsConfirmationAfterModifiedFileChangesAgain()
	{
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
		}
		File.WriteAllText(Path.Combine(_root, "spec.md"), "first edit");
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, "feature", "feature");
		File.WriteAllText(Path.Combine(_root, "spec.md"), "new edit after confirmation");

		Assert.Throws<RepositoryStateChangedException>(() =>
			_manager.DeleteBranch(_root, _root, "feature", _baseBranch, risks.ConfirmationToken));

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(inspected.Head.FriendlyName, Is.EqualTo("feature"));
			Assert.That(inspected.Branches["feature"], Is.Not.Null);
			Assert.That(File.ReadAllText(Path.Combine(_root, "spec.md")), Is.EqualTo("new edit after confirmation"));
		});
	}

	[Test]
	public void InspectDeletionRisks_RejectsDifferentRepositoryIdentity()
	{
		Assert.Throws<RepositoryIdentityMismatchException>(() =>
			_manager.InspectDeletionRisks(_root, _root + "-different", _baseBranch, "feature"));

		using Repository inspected = new(_root);
		Assert.That(inspected.Branches["feature"], Is.Not.Null);
	}

	[Test]
	public void InspectDeletionRisks_CurrentBranchChanged_DoesNotRunPersistenceCallback()
	{
		bool callbackRan = false;

		Assert.Throws<InvalidOperationException>(() =>
			_manager.InspectDeletionRisks(
				_root,
				_root,
				"feature",
				"feature",
				() => callbackRan = true));

		Assert.That(callbackRan, Is.False);
	}

	[Test]
	public void DeleteBranch_RejectsDifferentRepositoryIdentityBeforeMutation()
	{
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, _baseBranch, "feature");

		Assert.Throws<RepositoryIdentityMismatchException>(() =>
			_manager.DeleteBranch(
				_root, _root + "-different", "feature", _baseBranch, risks.ConfirmationToken));

		using Repository inspected = new(_root);
		Assert.That(inspected.Branches["feature"], Is.Not.Null);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void DeleteBranch_ConcurrentRefUpdateIsNeverDeleted(bool packedRef)
	{
		ObjectId replacementTip;
		ObjectId confirmedTip;
		using (Repository repository = new(_root))
		{
			confirmedTip = repository.Branches["feature"]!.Tip.Id;
			Commands.Checkout(repository, "feature");
			File.WriteAllText(Path.Combine(_root, "replacement.txt"), "new external tip");
			Commands.Stage(repository, "replacement.txt");
			Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
			replacementTip = repository.Commit("external feature update", author, author).Id;
			Commands.Checkout(repository, _baseBranch);
			repository.Refs.UpdateTarget(repository.Refs["refs/heads/feature"], confirmedTip);
		}
		if (packedRef)
		{
			using Repository repository = new(_root);
			string gitDirectory = repository.Info.Path;
			File.WriteAllText(
				Path.Combine(gitDirectory, "packed-refs"),
				$"# pack-refs with: peeled fully-peeled sorted \n{confirmedTip.Sha} refs/heads/feature\n");
			File.Delete(Path.Combine(gitDirectory, "refs", "heads", "feature"));
		}
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(
			_root, _root, _baseBranch, "feature");

		Assert.Throws<RepositoryStateChangedException>(() => LibGit2RepositoryCloner.DeleteBranchCore(
			_root,
			_root,
			"feature",
			_baseBranch,
			risks.ConfirmationToken,
			() =>
			{
				using Repository external = new(_root);
				external.Refs.UpdateTarget(external.Refs["refs/heads/feature"], replacementTip);
			}));

		using Repository inspected = new(_root);
		Assert.That(inspected.Branches["feature"]!.Tip.Id, Is.EqualTo(replacementTip));
	}

	[Test]
	public void DeleteBranch_ConcurrentCurrentRefUpdateRestoresTheWorkingLineAndSafetyCopy()
	{
		ObjectId confirmedTip;
		ObjectId replacementTip;
		using (Repository repository = new(_root))
		{
			confirmedTip = repository.Branches["feature"]!.Tip.Id;
			Commands.Checkout(repository, "feature");
			File.WriteAllText(Path.Combine(_root, "replacement.txt"), "external commit");
			Commands.Stage(repository, "replacement.txt");
			Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
			replacementTip = repository.Commit("external feature update", author, author).Id;
			Commands.Checkout(repository, _baseBranch);
			repository.Refs.UpdateTarget(repository.Refs["refs/heads/feature"], confirmedTip);
			Commands.Checkout(repository, "feature");
		}
		File.WriteAllText(Path.Combine(_root, "spec.md"), "unfinished manager edit");
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(
			_root, _root, "feature", "feature");

		Assert.Throws<RepositoryStateChangedException>(() => LibGit2RepositoryCloner.DeleteBranchCore(
			_root,
			_root,
			"feature",
			_baseBranch,
			risks.ConfirmationToken,
			() =>
			{
				using Repository external = new(_root);
				external.Refs.UpdateTarget(external.Refs["refs/heads/feature"], replacementTip);
			}));

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(inspected.Head.FriendlyName, Is.EqualTo("feature"));
			Assert.That(inspected.Head.Tip.Id, Is.EqualTo(replacementTip));
			Assert.That(File.ReadAllText(Path.Combine(_root, "spec.md")), Is.EqualTo("unfinished manager edit"));
			Assert.That(inspected.Stashes, Is.Empty);
		});
	}

	[Test]
	public void DeleteBranch_ConfirmedPackedRefIsDeleted()
	{
		ObjectId confirmedTip;
		string gitDirectory;
		using (Repository repository = new(_root))
		{
			confirmedTip = repository.Branches["feature"]!.Tip.Id;
			gitDirectory = repository.Info.Path;
		}
		File.WriteAllText(
			Path.Combine(gitDirectory, "packed-refs"),
			$"# pack-refs with: peeled fully-peeled sorted \n{confirmedTip.Sha} refs/heads/feature\n");
		File.Delete(Path.Combine(gitDirectory, "refs", "heads", "feature"));
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(
			_root, _root, _baseBranch, "feature");

		_manager.DeleteBranch(_root, _root, "feature", _baseBranch, risks.ConfirmationToken);

		using Repository inspected = new(_root);
		Assert.That(inspected.Branches["feature"], Is.Null);
	}

	[Test]
	public void DeleteBranch_CheckedOutInLinkedWorktreeIsNeverDeleted()
	{
		string linkedPath = _root + "-linked";
		try
		{
			using (Repository repository = new(_root))
			{
				repository.Worktrees.Add("feature", "feature-linked", linkedPath, isLocked: false);
			}
			RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(
				_root, _root, _baseBranch, "feature");

			Assert.Throws<InvalidOperationException>(() => _manager.DeleteBranch(
				_root, _root, "feature", _baseBranch, risks.ConfirmationToken));

			using Repository inspected = new(_root);
			Assert.That(inspected.Branches["feature"], Is.Not.Null);
		}
		finally
		{
			DeleteTestTree(linkedPath);
		}
	}

	[Test]
	public void DeleteClone_CleanLinkedWorktreeBlocksDeletionAndLeavesBothTreesIntact()
	{
		string linkedPath = _root + "-linked-clean";
		try
		{
			using (Repository repository = new(_root))
			{
				repository.Worktrees.Add("feature", "feature-linked-clean", linkedPath, isLocked: false);
			}

			RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, _baseBranch);
			LinkedWorktreeDeletionRisk linked = risks.LinkedWorktrees!.Single();
			Assert.Multiple(() =>
			{
				Assert.That(linked.Path, Is.EqualTo(Path.GetFullPath(linkedPath).TrimEnd(Path.DirectorySeparatorChar)));
				Assert.That(linked.HasUncommitted, Is.False);
				Assert.That(linked.HasConflicts, Is.False);
			});

			RepositoryHasLinkedWorktreesException? error = Assert.Throws<RepositoryHasLinkedWorktreesException>(
				() => _manager.DeleteClone(_root, _root, risks.ConfirmationToken));

			Assert.Multiple(() =>
			{
				Assert.That(error!.LinkedWorktrees, Has.Count.EqualTo(1));
				Assert.That(Repository.IsValid(_root), Is.True);
				Assert.That(Repository.IsValid(linkedPath), Is.True);
				Assert.That(File.ReadAllText(Path.Combine(linkedPath, "spec.md")), Is.EqualTo("base"));
			});
		}
		finally
		{
			DeleteTestTree(linkedPath);
		}
	}

	[Test]
	public void DeleteClone_DirtyLinkedWorktreeReportsRiskAndLeavesItsWorkIntact()
	{
		string linkedPath = _root + "-linked-dirty";
		try
		{
			using (Repository repository = new(_root))
			{
				repository.Worktrees.Add("feature", "feature-linked-dirty", linkedPath, isLocked: false);
			}
			string unfinishedPath = Path.Combine(linkedPath, "unfinished.md");
			File.WriteAllText(unfinishedPath, "unfinished linked work");

			RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, _baseBranch);
			LinkedWorktreeDeletionRisk linked = risks.LinkedWorktrees!.Single();
			Assert.That(linked.HasUncommitted, Is.True);

			Assert.Throws<RepositoryHasLinkedWorktreesException>(() =>
				_manager.DeleteClone(_root, _root, risks.ConfirmationToken));

			Assert.Multiple(() =>
			{
				Assert.That(Repository.IsValid(_root), Is.True);
				Assert.That(Repository.IsValid(linkedPath), Is.True);
				Assert.That(File.ReadAllText(unfinishedPath), Is.EqualTo("unfinished linked work"));
			});
		}
		finally
		{
			DeleteTestTree(linkedPath);
		}
	}

	[Test]
	public void DeleteClone_LinkedWorktreeAddedAfterConfirmationIsRecheckedBeforeMove()
	{
		string linkedPath = _root + "-linked-race";
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, _baseBranch);
		try
		{
			Assert.Throws<RepositoryHasLinkedWorktreesException>(() =>
				LibGit2RepositoryCloner.DeleteCloneCore(
					_root,
					_root,
					risks.ConfirmationToken,
					() =>
					{
						using Repository repository = new(_root);
						repository.Worktrees.Add("feature", "feature-linked-race", linkedPath, isLocked: false);
					}));

			Assert.Multiple(() =>
			{
				Assert.That(Repository.IsValid(_root), Is.True);
				Assert.That(Repository.IsValid(linkedPath), Is.True);
			});
		}
		finally
		{
			DeleteTestTree(linkedPath);
		}
	}

	[Test]
	public void DeleteClone_MovesAndRemovesTheExactlyConfirmedTree()
	{
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, _baseBranch);

		_manager.DeleteClone(_root, _root, risks.ConfirmationToken);

		Assert.That(Directory.Exists(_root), Is.False);
	}

	[Test]
	public void DeleteClone_RejectsDifferentRepositoryIdentityBeforeMovingTheTree()
	{
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, _baseBranch);

		Assert.Throws<RepositoryIdentityMismatchException>(() =>
			_manager.DeleteClone(_root, _root + "-different", risks.ConfirmationToken));

		Assert.That(Directory.Exists(_root), Is.True);
	}

	[Test]
	public void DeleteClone_PathReplacementAfterVerificationIsRestoredAndNeverDeleted()
	{
		string verifiedClone = _root + "-verified";
		string replacementFile = Path.Combine(_root, "replacement.txt");
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, _baseBranch);
		try
		{
			Assert.Throws<RepositoryStateChangedException>(() => LibGit2RepositoryCloner.DeleteCloneCore(
				_root,
				_root,
				risks.ConfirmationToken,
				() =>
				{
					Directory.Move(_root, verifiedClone);
					Directory.CreateDirectory(_root);
					File.WriteAllText(replacementFile, "must survive");
				}));

			Assert.Multiple(() =>
			{
				Assert.That(File.ReadAllText(replacementFile), Is.EqualTo("must survive"));
				Assert.That(Repository.IsValid(verifiedClone), Is.True);
				Assert.That(File.ReadAllText(Path.Combine(verifiedClone, "spec.md")), Is.EqualTo("base"));
			});
		}
		finally
		{
			DeleteTestTree(verifiedClone);
		}
	}

	[Test]
	public void DeleteClone_PostMoveFailureWithClaimedOriginalReportsPreservedQuarantine()
	{
		string? quarantinePath = null;
		string replacementPath = Path.Combine(_root, "replacement.txt");
		int mutationBoundaries = 0;
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, _baseBranch);
		try
		{
			RepositoryQuarantinedCloneException? error = Assert.Throws<RepositoryQuarantinedCloneException>(() =>
				LibGit2RepositoryCloner.DeleteCloneCore(
					_root,
					_root,
					risks.ConfirmationToken,
					beforeMove: null,
					onMutationStarting: () =>
					{
						Assert.That(Directory.Exists(_root), Is.True);
						mutationBoundaries++;
					},
					afterMove: (movedPath, originalPath) =>
					{
						quarantinePath = movedPath;
						Assert.That(Directory.Exists(originalPath), Is.False);
						Directory.CreateDirectory(originalPath);
						File.WriteAllText(replacementPath, "replacement must survive");
						throw new IOException("Simulated post-move verification failure.");
					}));

			Assert.Multiple(() =>
			{
				Assert.That(mutationBoundaries, Is.EqualTo(1));
				Assert.That(error?.QuarantinePath, Is.EqualTo(quarantinePath));
				Assert.That(quarantinePath, Is.Not.Null);
				Assert.That(Repository.IsValid(quarantinePath!), Is.True);
				Assert.That(File.ReadAllText(Path.Combine(quarantinePath!, "spec.md")), Is.EqualTo("base"));
				Assert.That(File.ReadAllText(replacementPath), Is.EqualTo("replacement must survive"));
			});
		}
		finally
		{
			if (quarantinePath is not null)
			{
				DeleteTestTree(quarantinePath);
			}
		}
	}

	[Test]
	public void DeleteBranch_RejectsRemoteBranchNames()
	{
		using (Repository repository = new(_root))
		{
			repository.Network.Remotes.Remove("origin");
			repository.Network.Remotes.Add("origin", "https://github.com/octo/specs.git");
			repository.Refs.Add("refs/remotes/origin/review", repository.Head.Tip.Id);
		}

		Assert.Throws<InvalidOperationException>(() =>
			_manager.InspectDeletionRisks(
				_root, "https://github.com/octo/specs.git", _baseBranch, "origin/review"));

		using Repository inspected = new(_root);
		Assert.That(inspected.Branches["origin/review"], Is.Not.Null);
	}

	[Test]
	public void DeleteClone_IgnoredLocalFileRequiresFreshConfirmationWhenItChanges()
	{
		File.WriteAllText(Path.Combine(_root, ".gitignore"), "private.txt");
		using (Repository repository = new(_root))
		{
			Commands.Stage(repository, ".gitignore");
			Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
			repository.Commit("ignore local file", author, author);
		}
		File.WriteAllText(Path.Combine(_root, "private.txt"), "first local value");
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, _baseBranch);
		Assert.That(risks.HasUncommitted, Is.True);
		File.WriteAllText(Path.Combine(_root, "private.txt"), "changed after confirmation");

		Assert.Throws<RepositoryStateChangedException>(() =>
			_manager.DeleteClone(_root, _root, risks.ConfirmationToken));

		Assert.That(File.ReadAllText(Path.Combine(_root, "private.txt")), Is.EqualTo("changed after confirmation"));
	}

	[Test]
	public void DeleteClone_NestedIgnoredFileRequiresFreshConfirmationWhenItChanges()
	{
		File.WriteAllText(Path.Combine(_root, ".gitignore"), "private/");
		using (Repository repository = new(_root))
		{
			Commands.Stage(repository, ".gitignore");
			Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
			repository.Commit("ignore local folder", author, author);
		}
		string privateDirectory = Path.Combine(_root, "private");
		Directory.CreateDirectory(privateDirectory);
		string privateFile = Path.Combine(privateDirectory, "token.txt");
		File.WriteAllText(privateFile, "first local value");
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, _baseBranch);
		Assert.That(risks.HasUncommitted, Is.True);
		File.WriteAllText(privateFile, "changed after confirmation");

		Assert.Throws<RepositoryStateChangedException>(() =>
			_manager.DeleteClone(_root, _root, risks.ConfirmationToken));

		Assert.That(File.ReadAllText(privateFile), Is.EqualTo("changed after confirmation"));
	}

	[Test]
	public void SwitchBranchSafely_IgnoredFileTrackedByTargetIsNeverOverwritten()
	{
		Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
			File.WriteAllText(Path.Combine(_root, "private.txt"), "target branch value");
			Commands.Stage(repository, "private.txt");
			repository.Commit("track target file", author, author);
			Commands.Checkout(repository, _baseBranch);
			File.WriteAllText(Path.Combine(_root, ".gitignore"), "private.txt");
			Commands.Stage(repository, ".gitignore");
			repository.Commit("ignore private file", author, author);
		}
		string privateFile = Path.Combine(_root, "private.txt");
		File.WriteAllText(privateFile, "protected local value");

		Assert.Throws<InvalidOperationException>(() =>
			_manager.SwitchBranchSafely(_root, _root, _baseBranch, "feature"));

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(inspected.Head.FriendlyName, Is.EqualTo(_baseBranch));
			Assert.That(File.ReadAllText(privateFile), Is.EqualTo("protected local value"));
			Assert.That(inspected.Stashes, Is.Empty);
		});
	}

	[Test]
	public void DeleteBranch_IgnoredFileTrackedByDefaultIsNeverOverwritten()
	{
		Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
		using (Repository repository = new(_root))
		{
			File.WriteAllText(Path.Combine(_root, "private.txt"), "default branch value");
			Commands.Stage(repository, "private.txt");
			repository.Commit("track default file", author, author);
			Commands.Checkout(repository, "feature");
			File.WriteAllText(Path.Combine(_root, ".gitignore"), "private.txt");
			Commands.Stage(repository, ".gitignore");
			repository.Commit("ignore private file", author, author);
		}
		string privateFile = Path.Combine(_root, "private.txt");
		File.WriteAllText(privateFile, "protected local value");
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(_root, _root, "feature", "feature");

		Assert.Throws<InvalidOperationException>(() =>
			_manager.DeleteBranch(_root, _root, "feature", _baseBranch, risks.ConfirmationToken));

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(inspected.Head.FriendlyName, Is.EqualTo("feature"));
			Assert.That(inspected.Branches["feature"], Is.Not.Null);
			Assert.That(File.ReadAllText(privateFile), Is.EqualTo("protected local value"));
			Assert.That(inspected.Stashes, Is.Empty);
		});
	}

	[Test]
	public void Inspect_AllowsMutatingOnlyLocalNonDefaultBranches()
	{
		using (Repository repository = new(_root))
		{
			repository.Network.Remotes.Remove("origin");
			repository.Network.Remotes.Add("origin", "https://github.com/octo/specs.git");
			repository.Refs.Add("refs/remotes/origin/remote-only", repository.Head.Tip.Id);
		}

		LocalRepositoryInfo info = _manager.Inspect(_root, _baseBranch);

		Assert.Multiple(() =>
		{
			Assert.That(info.Branches.Single(branch => branch.Name == _baseBranch).CanDelete, Is.False);
			Assert.That(info.Branches.Single(branch => branch.Name == _baseBranch).CanRename, Is.False);
			Assert.That(info.Branches.Single(branch => branch.Name == "feature").CanDelete, Is.True);
			Assert.That(info.Branches.Single(branch => branch.Name == "feature").CanRename, Is.True);
			Assert.That(info.Branches.Single(branch => branch.Name == "remote-only").CanDelete, Is.False);
			Assert.That(info.Branches.Single(branch => branch.Name == "remote-only").CanRename, Is.False);
		});
	}

	[Test]
	public void InspectExpected_ValidatesOriginBeforeReturningRepositoryState()
	{
		LocalRepositoryInfo info = _manager.InspectExpected(_root, _root, _baseBranch);

		Assert.That(info.DefaultBranch, Is.EqualTo(_baseBranch));
		Assert.Throws<RepositoryIdentityMismatchException>(() =>
			_manager.InspectExpected(
				_root,
				"https://github.com/other/repository.git",
				_baseBranch));
	}

	[Test]
	public void Inspect_ReportsAheadDirtyStateAndBranchSafetyCopies()
	{
		Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
		using (Repository repository = new(_root))
		{
			repository.Network.Remotes.Remove("origin");
			repository.Network.Remotes.Add("origin", "https://github.com/octo/specs.git");
			Commands.Checkout(repository, "feature");
			File.WriteAllText(Path.Combine(_root, "spec.md"), "local feature commit");
			Commands.Stage(repository, "spec.md");
			repository.Commit("local feature", author, author);
			Commands.Checkout(repository, _baseBranch);
			Branch remoteWork = repository.CreateBranch("remote-work");
			Commands.Checkout(repository, remoteWork);
			File.WriteAllText(Path.Combine(_root, "spec.md"), "remote feature commit");
			Commands.Stage(repository, "spec.md");
			Commit remoteTip = repository.Commit("remote feature", author, author);
			Commands.Checkout(repository, "feature");
			repository.Branches.Remove(remoteWork);
			repository.Refs.Add("refs/remotes/origin/feature", remoteTip.Id);
			repository.Branches.Update(
				repository.Branches["feature"]!,
				updater => updater.TrackedBranch = "refs/remotes/origin/feature");
			File.WriteAllText(Path.Combine(_root, "notes.txt"), "unfinished local note");
		}
		_manager.SwitchBranchSafely(_root, "https://github.com/octo/specs", "feature", _baseBranch);

		LocalRepositoryInfo info = _manager.Inspect(_root, _baseBranch);
		LocalBranchInfo feature = info.Branches.Single(branch => branch.Name == "feature");

		Assert.Multiple(() =>
		{
			Assert.That(info.CurrentBranch, Is.EqualTo(_baseBranch));
			Assert.That(info.Status.StashCount, Is.EqualTo(1));
			Assert.That(feature.Status.Ahead, Is.EqualTo(1));
			Assert.That(feature.Status.Behind, Is.EqualTo(1));
			Assert.That(feature.Status.HasUncommitted, Is.False);
			Assert.That(feature.Status.StashCount, Is.EqualTo(1));
			Assert.That(feature.CanRename, Is.False);
		});
	}

	[Test]
	public void Inspect_ReportsKnownConflictsOnlyOnTheCheckedOutWorkingLine()
	{
		Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
			File.WriteAllText(Path.Combine(_root, "spec.md"), "feature content");
			Commands.Stage(repository, "spec.md");
			repository.Commit("feature content", author, author);
			Commands.Checkout(repository, _baseBranch);
			File.WriteAllText(Path.Combine(_root, "spec.md"), "main content");
			Commands.Stage(repository, "spec.md");
			repository.Commit("main content", author, author);
			Commands.Checkout(repository, "feature");

			MergeResult merge = repository.Merge(repository.Branches[_baseBranch], author);
			Assert.That(merge.Status, Is.EqualTo(MergeStatus.Conflicts));
		}

		LocalRepositoryInfo info = _manager.Inspect(_root, _baseBranch);
		LocalBranchInfo feature = info.Branches.Single(branch => branch.Name == "feature");
		LocalBranchInfo main = info.Branches.Single(branch => branch.Name == _baseBranch);
		Assert.Multiple(() =>
		{
			Assert.That(info.CurrentBranch, Is.EqualTo("feature"));
			Assert.That(info.Status.HasConflicts, Is.True);
			Assert.That(info.Status.HasUncommitted, Is.True);
			Assert.That(feature.Status.HasConflicts, Is.True);
			Assert.That(main.Status.HasConflicts, Is.False);
		});
	}

	[Test]
	public void Fetch_UpdatesRemoteReferencesWithoutChangingTheWorkingTree()
	{
		string remotePath = Path.Combine(Path.GetTempPath(), "specdesk-fetch-remote-" + Guid.NewGuid().ToString("N"));
		string consumerPath = Path.Combine(Path.GetTempPath(), "specdesk-fetch-consumer-" + Guid.NewGuid().ToString("N"));
		try
		{
			Repository.Init(remotePath, isBare: true);
			ObjectId remoteTip;
			using (Repository producer = new(_root))
			{
				producer.Network.Remotes.Remove("origin");
				Remote origin = producer.Network.Remotes.Add("origin", remotePath);
				producer.Network.Push(origin, $"refs/heads/{_baseBranch}:refs/heads/{_baseBranch}");
				File.WriteAllText(Path.Combine(_root, "spec.md"), "new remote content");
				Commands.Stage(producer, "spec.md");
				Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
				remoteTip = producer.Commit("remote update", author, author).Id;
				producer.Network.Push(origin, $"refs/heads/{_baseBranch}:refs/heads/{_baseBranch}");
			}

			Repository.Clone(remotePath, consumerPath, new CloneOptions { BranchName = _baseBranch });
			using (Repository before = new(consumerPath))
			{
				Assert.That(before.Branches[$"origin/{_baseBranch}"]!.Tip.Id, Is.EqualTo(remoteTip));
				// Rewind the local remote-tracking reference so the test proves Fetch advances it.
				before.Refs.UpdateTarget(before.Refs[$"refs/remotes/origin/{_baseBranch}"], before.Head.Tip.Parents.Single().Id);
			}

			_manager.Fetch(
				consumerPath,
				remotePath,
				_baseBranch,
				accessToken: null,
				CancellationToken.None);

			using Repository after = new(consumerPath);
			Assert.Multiple(() =>
			{
				Assert.That(after.Branches[$"origin/{_baseBranch}"]!.Tip.Id, Is.EqualTo(remoteTip));
				Assert.That(File.ReadAllText(Path.Combine(consumerPath, "spec.md")), Is.EqualTo("new remote content"));
				Assert.That(after.RetrieveStatus().IsDirty, Is.False);
			});
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
		}
	}

	[Test]
	public void Fetch_DifferentOriginFailsBeforeContactingTheRemote()
	{
		string differentRepository = Path.Combine(Path.GetDirectoryName(_root)!, "different-repository.git");
		ObjectId before;
		using (Repository repository = new(_root))
		{
			before = repository.Head.Tip.Id;
		}

		Assert.Throws<RepositoryIdentityMismatchException>(() =>
			_manager.Fetch(
				_root,
				differentRepository,
				_baseBranch,
				accessToken: null,
				CancellationToken.None));

		using Repository inspected = new(_root);
		Assert.Multiple(() =>
		{
			Assert.That(inspected.Head.Tip.Id, Is.EqualTo(before));
			Assert.That(inspected.RetrieveStatus().IsDirty, Is.False);
		});
	}

	[Test]
	public void PullFastForward_UpdatesACleanCurrentWorkingLine()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		try
		{
			ObjectId remoteTip = CommitAndPushProducer("new remote content");

			_manager.PullFastForward(
				consumerPath, remotePath, _baseBranch, _baseBranch, accessToken: null, CancellationToken.None);

			using Repository consumer = new(consumerPath);
			Assert.Multiple(() =>
			{
				Assert.That(consumer.Head.Tip.Id, Is.EqualTo(remoteTip));
				Assert.That(File.ReadAllText(Path.Combine(consumerPath, "spec.md")), Is.EqualTo("new remote content"));
				Assert.That(consumer.RetrieveStatus().IsDirty, Is.False);
			});
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
		}
	}

	[Test]
	public void PullFastForward_MutationBoundaryRunsImmediatelyBeforeTheFastForward()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		try
		{
			ObjectId remoteTip = CommitAndPushProducer("new remote content");
			ObjectId localTip;
			using (Repository before = new(consumerPath))
			{
				localTip = before.Head.Tip.Id;
			}
			ObjectId? observedTip = null;

			_manager.PullFastForward(
				consumerPath,
				remotePath,
				_baseBranch,
				_baseBranch,
				accessToken: null,
				CancellationToken.None,
				onMutationStarting: () =>
				{
					using Repository observed = new(consumerPath);
					observedTip = observed.Head.Tip.Id;
				});

			using Repository completed = new(consumerPath);
			Assert.Multiple(() =>
			{
				Assert.That(observedTip, Is.EqualTo(localTip));
				Assert.That(completed.Head.Tip.Id, Is.EqualTo(remoteTip));
			});
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
		}
	}

	[Test]
	public void PullFastForward_DifferentOriginFailsBeforeFetchingOrChangingTheWorkingTree()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		try
		{
			CommitAndPushProducer("new remote content");
			string differentRepository = Path.Combine(Path.GetDirectoryName(remotePath)!, "different.git");

			Assert.Throws<RepositoryIdentityMismatchException>(() =>
				_manager.PullFastForward(
					consumerPath,
					differentRepository,
					_baseBranch,
					_baseBranch,
					accessToken: null,
					CancellationToken.None));

			using Repository consumer = new(consumerPath);
			Assert.Multiple(() =>
			{
				Assert.That(consumer.Head.Tip.MessageShort, Is.EqualTo("base"));
				Assert.That(File.ReadAllText(Path.Combine(consumerPath, "spec.md")), Is.EqualTo("base"));
				Assert.That(consumer.RetrieveStatus().IsDirty, Is.False);
			});
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
		}
	}

	[Test]
	public void PullFastForward_ChangedCurrentBranchFailsBeforeThePersistenceCallback()
	{
		using (Repository repository = new(_root))
		{
			Commands.Checkout(repository, "feature");
		}
		bool callbackInvoked = false;

		Assert.Throws<InvalidOperationException>(() =>
			_manager.PullFastForward(
				_root,
				_root,
				_baseBranch,
				_baseBranch,
				accessToken: null,
				CancellationToken.None,
				() => callbackInvoked = true));

		Assert.That(callbackInvoked, Is.False);
	}

	[Test]
	public void PullFastForward_UnfinishedFilesAreNeverOverwritten()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		try
		{
			CommitAndPushProducer("new remote content");
			using (Repository before = new(consumerPath))
			{
				File.WriteAllText(Path.Combine(consumerPath, "spec.md"), "unfinished local content");
			}

			Assert.Throws<InvalidOperationException>(() =>
				_manager.PullFastForward(
					consumerPath, remotePath, _baseBranch, _baseBranch, accessToken: null, CancellationToken.None));

			using Repository consumer = new(consumerPath);
			Assert.Multiple(() =>
			{
				Assert.That(File.ReadAllText(Path.Combine(consumerPath, "spec.md")),
					Is.EqualTo("unfinished local content"));
				Assert.That(consumer.Head.Tip.MessageShort, Is.EqualTo("base"));
			});
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
		}
	}

	[Test]
	public void PullFastForward_IgnoredLocalFilesInTheWayAreNeverOverwritten()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		try
		{
			using (Repository producer = new(_root))
			{
				File.WriteAllText(Path.Combine(_root, "private.txt"), "shared remote content");
				Commands.Stage(producer, "private.txt");
				Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
				producer.Commit("add shared file", author, author);
				producer.Network.Push(
					producer.Network.Remotes["origin"]!,
					$"refs/heads/{_baseBranch}:refs/heads/{_baseBranch}");
			}
			Directory.CreateDirectory(Path.Combine(consumerPath, ".git", "info"));
			File.WriteAllText(Path.Combine(consumerPath, ".git", "info", "exclude"), "private.txt\n");
			File.WriteAllText(Path.Combine(consumerPath, "private.txt"), "private local content");

			Assert.Catch(() =>
				_manager.PullFastForward(
					consumerPath, remotePath, _baseBranch, _baseBranch, accessToken: null, CancellationToken.None));

			using Repository consumer = new(consumerPath);
			Assert.Multiple(() =>
			{
				Assert.That(File.ReadAllText(Path.Combine(consumerPath, "private.txt")),
					Is.EqualTo("private local content"));
				Assert.That(consumer.Head.Tip.MessageShort, Is.EqualTo("base"));
			});
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
		}
	}

	[Test]
	public void PushBranchSafely_SharesTheCurrentWorkingLineAndUpdatesTracking()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		try
		{
			ObjectId localTip;
			using (Repository consumer = new(consumerPath))
			{
				File.WriteAllText(Path.Combine(consumerPath, "spec.md"), "local saved version");
				Commands.Stage(consumer, "spec.md");
				Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
				localTip = consumer.Commit("local saved version", author, author).Id;
			}

			_manager.PushBranchSafely(
				consumerPath, remotePath, _baseBranch, _baseBranch, "test-token", CancellationToken.None);

			using Repository remote = new(remotePath);
			using Repository result = new(consumerPath);
			Assert.Multiple(() =>
			{
				Assert.That(remote.Branches[_baseBranch]!.Tip.Id, Is.EqualTo(localTip));
				Assert.That(result.Head.TrackedBranch?.FriendlyName, Is.EqualTo($"origin/{_baseBranch}"));
				Assert.That(result.Head.TrackingDetails.AheadBy, Is.Zero);
				Assert.That(result.Head.TrackingDetails.BehindBy, Is.Zero);
			});
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
		}
	}

	[Test]
	public void PushBranchSafely_DifferentOriginFailsBeforeFetchingOrPushing()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		try
		{
			ObjectId remoteTip;
			using (Repository remote = new(remotePath))
			{
				remoteTip = remote.Branches[_baseBranch]!.Tip.Id;
			}
			using (Repository consumer = new(consumerPath))
			{
				File.WriteAllText(Path.Combine(consumerPath, "spec.md"), "must not be pushed");
				Commands.Stage(consumer, "spec.md");
				Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
				consumer.Commit("local saved version", author, author);
			}
			string differentRepository = Path.Combine(Path.GetDirectoryName(remotePath)!, "different.git");

			Assert.Throws<RepositoryIdentityMismatchException>(() =>
				_manager.PushBranchSafely(
					consumerPath,
					differentRepository,
					_baseBranch,
					_baseBranch,
					"test-token",
					CancellationToken.None));

			using Repository remoteAfter = new(remotePath);
			Assert.That(remoteAfter.Branches[_baseBranch]!.Tip.Id, Is.EqualTo(remoteTip));
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
		}
	}

	[Test]
	public void PushBranchSafely_DifferentGitHubPushUrlFailsBeforeCredentialsOrRemoteMutation()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		try
		{
			ObjectId remoteTip;
			using (Repository remote = new(remotePath))
			{
				remoteTip = remote.Branches[_baseBranch]!.Tip.Id;
			}
			const string expectedUrl = "https://github.com/acme/specs.git";
			using (Repository consumer = new(consumerPath))
			{
				consumer.Config.Set("remote.origin.url", expectedUrl);
				consumer.Config.Set("remote.origin.pushurl", "https://github.com/other/specs.git");
			}

			Assert.Throws<RepositoryIdentityMismatchException>(() =>
				_manager.PushBranchSafely(
					consumerPath,
					expectedUrl,
					_baseBranch,
					_baseBranch,
					"test-token",
					CancellationToken.None));

			using Repository remoteAfter = new(remotePath);
			Assert.That(remoteAfter.Branches[_baseBranch]!.Tip.Id, Is.EqualTo(remoteTip));
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
		}
	}

	[TestCase("fetch", "http://github.com/acme/specs.git")]
	[TestCase("fetch", "file://github.com/acme/specs.git")]
	[TestCase("pull", "http://github.com/acme/specs.git")]
	[TestCase("pull", "file://github.com/acme/specs.git")]
	[TestCase("push", "http://github.com/acme/specs.git")]
	[TestCase("push", "file://github.com/acme/specs.git")]
	public void RepositoryTransfer_InsecureGitHubTransportFailsBeforeCallbacksOrMutation(
		string operation,
		string insecureUrl)
	{
		const string expectedUrl = "https://github.com/acme/specs.git";
		using (Repository repository = new(_root))
		{
			repository.Config.Set("remote.origin.url", operation == "push" ? expectedUrl : insecureUrl);
			if (operation == "push")
			{
				repository.Config.Set("remote.origin.pushurl", insecureUrl);
			}
		}
		ObjectId headBefore;
		Dictionary<string, string> refsBefore;
		using (Repository repository = new(_root))
		{
			headBefore = repository.Head.Tip.Id;
			refsBefore = repository.Refs.ToDictionary(
				reference => reference.CanonicalName,
				reference => reference.TargetIdentifier,
				StringComparer.Ordinal);
		}
		string contentBefore = File.ReadAllText(Path.Combine(_root, "spec.md"));
		bool networkStarted = false;
		bool credentialsRequested = false;
		bool beforeMutation = false;
		bool mutationStarted = false;

		Assert.Throws<RepositoryIdentityMismatchException>(() =>
		{
			switch (operation)
			{
				case "fetch":
					LibGit2RepositoryCloner.Fetch(
						_root,
						expectedUrl,
						_baseBranch,
						accessToken: "must-not-be-used",
						beforeNetwork: () => networkStarted = true,
						beforeCredentials: () => credentialsRequested = true,
						ct: CancellationToken.None);
					break;
				case "pull":
					LibGit2RepositoryCloner.PullFastForward(
						_root,
						expectedUrl,
						_baseBranch,
						_baseBranch,
						accessToken: "must-not-be-used",
						beforeMutation: () => beforeMutation = true,
						onMutationStarting: () => mutationStarted = true,
						beforeNetwork: () => networkStarted = true,
						beforeCredentials: () => credentialsRequested = true,
						ct: CancellationToken.None);
					break;
				case "push":
					LibGit2RepositoryCloner.PushBranchSafely(
						_root,
						expectedUrl,
						_baseBranch,
						_baseBranch,
						"must-not-be-used",
						beforeNetwork: () => networkStarted = true,
						beforeCredentials: () => credentialsRequested = true,
						ct: CancellationToken.None);
					break;
				default:
					Assert.Fail($"Unknown operation {operation}.");
					break;
			}
		});

		using Repository inspected = new(_root);
		Dictionary<string, string> refsAfter = inspected.Refs.ToDictionary(
			reference => reference.CanonicalName,
			reference => reference.TargetIdentifier,
			StringComparer.Ordinal);
		Assert.Multiple(() =>
		{
			Assert.That(networkStarted, Is.False);
			Assert.That(credentialsRequested, Is.False);
			Assert.That(beforeMutation, Is.False);
			Assert.That(mutationStarted, Is.False);
			Assert.That(inspected.Head.Tip.Id, Is.EqualTo(headBefore));
			Assert.That(refsAfter, Is.EqualTo(refsBefore));
			Assert.That(File.ReadAllText(Path.Combine(_root, "spec.md")), Is.EqualTo(contentBefore));
			Assert.That(inspected.RetrieveStatus().IsDirty, Is.False);
		});
	}

	[Test]
	public void PushBranchSafely_RefusesWhenGitHubHasNewerVersions()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		try
		{
			ObjectId remoteTip = CommitAndPushProducer("new remote content");
			using (Repository consumer = new(consumerPath))
			{
				File.WriteAllText(Path.Combine(consumerPath, "local.md"), "local saved version");
				Commands.Stage(consumer, "local.md");
				Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
				consumer.Commit("local saved version", author, author);
			}

			Assert.Throws<InvalidOperationException>(() =>
				_manager.PushBranchSafely(
					consumerPath, remotePath, _baseBranch, _baseBranch, "test-token", CancellationToken.None));

			using Repository remote = new(remotePath);
			Assert.That(remote.Branches[_baseBranch]!.Tip.Id, Is.EqualTo(remoteTip));
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
		}
	}

	[Test]
	public void Fetch_OriginChangedAfterValidationStillUsesCapturedRemote()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		string replacementRemote = Path.Combine(
			Path.GetTempPath(), "specdesk-sync-replacement-" + Guid.NewGuid().ToString("N"));
		Repository.Init(replacementRemote, isBare: true);
		try
		{
			ObjectId expectedTip = CommitAndPushProducer("expected remote update");

			LibGit2RepositoryCloner.Fetch(
				consumerPath,
				remotePath,
				_baseBranch,
				accessToken: null,
				beforeNetwork: () => ReplaceOriginUrl(consumerPath, replacementRemote),
				beforeCredentials: null,
				ct: CancellationToken.None);

			using Repository inspected = new(consumerPath);
			Assert.That(inspected.Branches[$"origin/{_baseBranch}"]!.Tip.Id, Is.EqualTo(expectedTip));
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
			DeleteTestTree(replacementRemote);
		}
	}

	[Test]
	public void PullFastForward_OriginChangedAfterValidationStillUsesCapturedRemote()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		string replacementRemote = Path.Combine(
			Path.GetTempPath(), "specdesk-sync-replacement-" + Guid.NewGuid().ToString("N"));
		Repository.Init(replacementRemote, isBare: true);
		try
		{
			ObjectId expectedTip = CommitAndPushProducer("expected pulled content");

			LibGit2RepositoryCloner.PullFastForward(
				consumerPath,
				remotePath,
				_baseBranch,
				_baseBranch,
				accessToken: null,
				beforeMutation: null,
				onMutationStarting: null,
				beforeNetwork: () => ReplaceOriginUrl(consumerPath, replacementRemote),
				beforeCredentials: null,
				ct: CancellationToken.None);

			using Repository inspected = new(consumerPath);
			Assert.Multiple(() =>
			{
				Assert.That(inspected.Head.Tip.Id, Is.EqualTo(expectedTip));
				Assert.That(File.ReadAllText(Path.Combine(consumerPath, "spec.md")),
					Is.EqualTo("expected pulled content"));
			});
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
			DeleteTestTree(replacementRemote);
		}
	}

	[Test]
	public void PushBranchSafely_OriginAndPushUrlChangedAfterValidationStillUsesCapturedRemote()
	{
		(string remotePath, string consumerPath) = CreateRemoteFixture();
		string replacementRemote = Path.Combine(
			Path.GetTempPath(), "specdesk-sync-replacement-" + Guid.NewGuid().ToString("N"));
		Repository.Init(replacementRemote, isBare: true);
		try
		{
			ObjectId localTip;
			using (Repository consumer = new(consumerPath))
			{
				File.WriteAllText(Path.Combine(consumerPath, "spec.md"), "captured remote push");
				Commands.Stage(consumer, "spec.md");
				Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
				localTip = consumer.Commit("local saved version", author, author).Id;
			}

			LibGit2RepositoryCloner.PushBranchSafely(
				consumerPath,
				remotePath,
				_baseBranch,
				_baseBranch,
				"test-token",
				beforeNetwork: () => ReplaceOriginAndPushUrl(consumerPath, replacementRemote),
				beforeCredentials: null,
				ct: CancellationToken.None);

			using Repository expected = new(remotePath);
			using Repository replacement = new(replacementRemote);
			Assert.Multiple(() =>
			{
				Assert.That(expected.Branches[_baseBranch]!.Tip.Id, Is.EqualTo(localTip));
				Assert.That(replacement.Branches[_baseBranch], Is.Null);
			});
		}
		finally
		{
			DeleteTestTree(consumerPath);
			DeleteTestTree(remotePath);
			DeleteTestTree(replacementRemote);
		}
	}

	[Test]
	public void Inspect_NoUpstreamLocalBranchShowsUnsharedVersionsAndDeletionRisk()
	{
		LocalRepositoryInfo info = _manager.Inspect(_root, _baseBranch);
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(
			_root, _root, _baseBranch, "feature");

		Assert.Multiple(() =>
		{
			Assert.That(info.Branches.Single(branch => branch.Name == "feature").Status.Ahead, Is.GreaterThan(0));
			Assert.That(risks.HasUnpushed, Is.True);
		});
	}

	[Test]
	public void Inspect_UnrelatedTrackedHistoryFailsSafeAsUnshared()
	{
		using (Repository repository = new(_root))
		{
			Branch feature = repository.Branches["feature"]!;
			Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
			Commit unrelated = repository.ObjectDatabase.CreateCommit(
				author,
				author,
				"unrelated remote root",
				feature.Tip.Tree,
				[],
				prettifyMessage: false);
			Reference remote = repository.Refs.Add("refs/remotes/origin/feature", unrelated.Id);
			repository.Branches.Update(feature, updater => updater.TrackedBranch = remote.CanonicalName);
		}

		LocalRepositoryInfo info = _manager.Inspect(_root, _baseBranch);
		RepositoryDeletionRisks risks = _manager.InspectDeletionRisks(
			_root, _root, _baseBranch, "feature");

		Assert.Multiple(() =>
		{
			LocalRepositoryStatus status = info.Branches.Single(branch => branch.Name == "feature").Status;
			Assert.That(status.Ahead, Is.GreaterThan(0));
			Assert.That(status.Behind, Is.GreaterThan(0));
			Assert.That(risks.HasUnpushed, Is.True);
		});
	}

	private static void ReplaceOriginUrl(string repositoryPath, string replacementUrl)
	{
		using Repository repository = new(repositoryPath);
		repository.Config.Set("remote.origin.url", replacementUrl);
	}

	private static void ReplaceOriginAndPushUrl(string repositoryPath, string replacementUrl)
	{
		using Repository repository = new(repositoryPath);
		repository.Config.Set("remote.origin.url", replacementUrl);
		repository.Config.Set("remote.origin.pushurl", replacementUrl);
	}

	private (string RemotePath, string ConsumerPath) CreateRemoteFixture()
	{
		string remotePath = Path.Combine(Path.GetTempPath(), "specdesk-sync-remote-" + Guid.NewGuid().ToString("N"));
		string consumerPath = Path.Combine(Path.GetTempPath(), "specdesk-sync-consumer-" + Guid.NewGuid().ToString("N"));
		Repository.Init(remotePath, isBare: true);
		using (Repository producer = new(_root))
		{
			producer.Network.Remotes.Remove("origin");
			Remote origin = producer.Network.Remotes.Add("origin", remotePath);
			producer.Network.Push(origin, $"refs/heads/{_baseBranch}:refs/heads/{_baseBranch}");
		}
		Repository.Clone(remotePath, consumerPath, new CloneOptions { BranchName = _baseBranch });
		return (remotePath, consumerPath);
	}

	private ObjectId CommitAndPushProducer(string content)
	{
		using Repository producer = new(_root);
		File.WriteAllText(Path.Combine(_root, "spec.md"), content);
		Commands.Stage(producer, "spec.md");
		Signature author = new("SpecDesk tests", "tests@example.invalid", DateTimeOffset.Now);
		ObjectId tip = producer.Commit("remote update", author, author).Id;
		Remote origin = producer.Network.Remotes["origin"]!;
		producer.Network.Push(origin, $"refs/heads/{_baseBranch}:refs/heads/{_baseBranch}");
		return tip;
	}

	private static void DeleteTestTree(string path)
	{
		if (!Directory.Exists(path))
		{
			return;
		}
		foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}
		Directory.Delete(path, recursive: true);
	}

}
