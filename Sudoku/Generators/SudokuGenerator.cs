using Sudoku.Models;
using Sudoku.Solvers;

namespace Sudoku.Generators;

/// <summary>
/// 完成盤面の生成と、間引きによる問題化を行う。
/// </summary>
public class SudokuGenerator
{
    private readonly BacktrackingSolver _solver;
    private readonly Random _random;

    public SudokuGenerator(Random? random = null)
    {
        _random = random ?? new Random();
        _solver = new BacktrackingSolver(_random);
    }

    /// <summary>
    /// 完成盤面を1つ生成し、唯一解を保ったまま初期配置数がtargetGivens前後になるまで間引いた問題を返す。
    /// </summary>
    public (Board Puzzle, Board Solution) GeneratePuzzle(int targetGivens)
    {
        var fullBoard = new Board();
        if (!_solver.TryGenerateFullGrid(fullBoard))
            throw new InvalidOperationException("完成盤面の生成に失敗しました。");

        var solution = fullBoard.Clone();
        var puzzle = DigHoles(fullBoard, targetGivens);
        return (puzzle, solution);
    }

    private Board DigHoles(Board fullBoard, int targetGivens)
    {
        var puzzle = ToFullyGivenBoard(fullBoard);
        var positions = CreateShuffledPositions();
        int currentGivens = Board.Size * Board.Size;

        foreach (var (row, col) in positions)
        {
            if (currentGivens <= targetGivens) break;

            int removedValue = puzzle.GetCell(row, col).Value!.Value;
            puzzle.ClearGivenAt(row, col);

            int solutionCount = _solver.CountSolutions(puzzle.Clone(), limit: 2);
            if (solutionCount == 1)
            {
                currentGivens--;
            }
            else
            {
                // 複数解になってしまうので、このマスは間引かずに元に戻す
                puzzle.SetGivenAt(row, col, removedValue);
            }
        }

        return puzzle;
    }

    private static Board ToFullyGivenBoard(Board solvedBoard)
    {
        var puzzle = new Board();
        for (int r = 0; r < Board.Size; r++)
            for (int c = 0; c < Board.Size; c++)
                puzzle.SetGivenAt(r, c, solvedBoard.GetCell(r, c).Value!.Value);
        return puzzle;
    }

    private List<(int row, int col)> CreateShuffledPositions()
    {
        var positions = new List<(int row, int col)>();
        for (int r = 0; r < Board.Size; r++)
            for (int c = 0; c < Board.Size; c++)
                positions.Add((r, c));

        for (int i = positions.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (positions[i], positions[j]) = (positions[j], positions[i]);
        }

        return positions;
    }
}