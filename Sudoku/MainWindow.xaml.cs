using System.ComponentModel;
using System.Windows;
using Sudoku.ViewModels;

namespace Sudoku;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ShellViewModel();

        /* ====== 確認用 ======

        var humanSolver = new Sudoku.Solvers.HumanSolver();
        var scorer = new Sudoku.Solvers.DifficultyScorer();

        var easyPuzzle = Sudoku.Models.Board.LoadFromString(
    "530070000600195000098000060800060003400803001700020006060000280000419005000080079");

        var result = humanSolver.Solve(easyPuzzle);
        var difficulty = scorer.Evaluate(result);

        System.Diagnostics.Debug.WriteLine($"Solved: {result.Solved}");
        System.Diagnostics.Debug.WriteLine($"RequiredFallback: {result.RequiredFallback}");
        System.Diagnostics.Debug.WriteLine($"MaxLevelUsed: {result.MaxLevelUsed}");
        System.Diagnostics.Debug.WriteLine($"TechniqueUsageCounts: {string.Join(", ", result.TechniqueUsageCounts.Select(kv => $"Lv{kv.Key}={kv.Value}"))}");
        System.Diagnostics.Debug.WriteLine($"Difficulty: {difficulty.Label} (Score: {difficulty.Score})");


        // ==================================

        var hardPuzzle = Sudoku.Models.Board.LoadFromString(
    "1....7.9..3..2...8..96..5....53..9...1..8...26....4...3......1..4......7..7...3..");

        var result = humanSolver.Solve(hardPuzzle);
        var difficulty = scorer.Evaluate(result);

        System.Diagnostics.Debug.WriteLine($"Solved: {result.Solved}");
        System.Diagnostics.Debug.WriteLine($"RequiredFallback: {result.RequiredFallback}");
        System.Diagnostics.Debug.WriteLine($"MaxLevelUsed: {result.MaxLevelUsed}");
        System.Diagnostics.Debug.WriteLine($"TechniqueUsageCounts: {string.Join(", ", result.TechniqueUsageCounts.Select(kv => $"Lv{kv.Key}={kv.Value}"))}");
        System.Diagnostics.Debug.WriteLine($"Difficulty: {difficulty.Label} (Score: {difficulty.Score})");
        */
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is ShellViewModel shell && shell.CurrentViewModel is GameViewModel game)
            game.SaveCurrentGame();

        base.OnClosing(e);
    }
}