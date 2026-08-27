using Sudoku.Solvers;

namespace Sudoku.Models;

public enum SolveStatus
{
    Solved,
    Stuck,
    Timeout,
    Invalid
}

public sealed class DifficultyResult
{
    public SolveStatus Status { get; init; }
    public Difficulty Requested { get; init; }
    public Difficulty? Actual { get; init; }
    public int Score { get; init; }
    public int MaxLevel { get; init; }
    public int Remaining { get; init; }
    public bool Fallback { get; init; }

    public bool IsTargetDifficulty =>
        Status == SolveStatus.Solved &&
        Actual == Requested;

    public bool IsSolved =>
        Status == SolveStatus.Solved;
}