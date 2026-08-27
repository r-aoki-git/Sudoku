namespace Sudoku.Solvers;

/// <summary>
/// HumanSolverによる求解の結果。DifficultyScorerの入力になる。
/// </summary>
public record HumanSolveResult(
    bool Solved,
    bool RequiredFallback,
    bool FallbackSolved,
    int MaxLevelUsed,
    int RemainingCells,
    IReadOnlyDictionary<int, int> TechniqueUsageCounts,
    IReadOnlyDictionary<string, int> TechniqueUsageByName);