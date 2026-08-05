namespace AgenticRoslynTool;

/// <summary>
/// The full plan for splitting one input file: which type is kept in place, where the
/// kept file lives, whether a <c>git mv</c> is required, and the target path for each
/// type that is being moved out. A non-null <see cref="SkipReason"/> means the plan
/// refused to split this file and no output should be written.
/// </summary>
/// <param name="Keep">The type that stays in the kept file.</param>
/// <param name="KeptPath">Absolute path where the kept file will live after the split.</param>
/// <param name="GitMove">
/// True when the kept file must be moved via <c>git mv</c> because its current file name
/// does not match the primary type. Handled in the renames phase.
/// </param>
/// <param name="Note">Free-form diagnostic note, propagated into the manifest.</param>
/// <param name="SkipReason">
/// Non-null when the plan refused the split. Reasons include target path collisions,
/// pre-existing target files on disk, and mismatches between a recomputed plan and the
/// manifest handed to the content phase. These refusals are load-bearing.
/// </param>
/// <param name="Files">One planned output file per type the split will produce.</param>
internal sealed record SplitPlan(TopLevelType Keep, string KeptPath, bool GitMove, string? Note, string? SkipReason, IReadOnlyList<PlannedFile> Files);
