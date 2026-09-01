using Sudoku.Generators;
using Sudoku.Models;
using Sudoku.Solvers;
using System.Configuration;
using System.Data;
using System.Windows;

namespace Sudoku
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 原因確認用の一時的な診断出力。
            // [CageGenerator] のSingles/AvgSizeと、[DifficultyCheck]/[UniquenessReject]
            // のログを確認したら、確認後は必ず false に戻すこと。
            SolverDiagnostics.VerboseLogging = true;

            var scorer = new DifficultyScorer();

            const int TestCount = 5;

            foreach (var requestedDifficulty in new[]
            {
                Difficulty.Hard
            })
            {
                var resultCounts =
                    new Dictionary<Difficulty, int>();

                int fallbackCount = 0;
                int solvedCount = 0;
                int generatedCount = 0;
                long totalGenerationMs = 0;

                for (int i = 0; i < TestCount; i++)
                {
                    try
                    {
                        var stopwatch =
                            System.Diagnostics.Stopwatch.StartNew();

                        var (solution, cages) =
                            ParallelKillerSudokuGenerator.Generate(
                                requestedDifficulty,
                                workerCount: 4,
                                overallTimeoutMs: 10000,
                                perAttemptBudgetMs: 2500,
                                maxEscalations: 1);

                        stopwatch.Stop();

                        generatedCount++;
                        totalGenerationMs +=
                            stopwatch.ElapsedMilliseconds;

                        var humanSolver =
                            new KillerHumanSolver(cages);

                        var humanResult =
                            humanSolver.Solve(new Board());

                        if (humanResult.Solved)
                            solvedCount++;

                        if (humanResult.RequiredFallback)
                            fallbackCount++;

                        var scored =
                            scorer.Evaluate(humanResult);

                        resultCounts[scored.Label] =
                            resultCounts.GetValueOrDefault(scored.Label) + 1;

                        System.Diagnostics.Debug.WriteLine(
                            $"[{requestedDifficulty}] " +
                            $"{i + 1} " +
                            $"生成={stopwatch.ElapsedMilliseconds}ms, " +
                            $"Actual={scored.Label}, " +
                            $"Status={scored.Status}, " +
                            $"Score={scored.Score}, " +
                            $"MaxLv={humanResult.MaxLevelUsed}, " +
                            $"Fallback={humanResult.RequiredFallback}, " +
                            $"FallbackSolved={humanResult.FallbackSolved}, " +
                            $"Remaining={humanResult.RemainingCells}, " +
                            $"Usage=[" +
                            $"{string.Join(
                                ",",
                                humanResult.TechniqueUsageCounts.Select(
                                    kv => $"Lv{kv.Key}={kv.Value}"))}" +
                            "], " +
                            $"Techniques=[" +
                            $"{string.Join(
                                ",",
                                humanResult.TechniqueUsageByName.Select(
                                    kv => $"{kv.Key}={kv.Value}"))}" +
                            "]");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[{requestedDifficulty}] " +
                            $"#{i + 1} FAILED: {ex.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"========== {requestedDifficulty} ==========");

                System.Diagnostics.Debug.WriteLine(
                    $"生成成功数: {generatedCount}/{TestCount}");

                System.Diagnostics.Debug.WriteLine(
                    $"平均生成時間: " +
                    $"{(generatedCount == 0
                        ? 0
                        : (double)totalGenerationMs / generatedCount):F1}ms");

                System.Diagnostics.Debug.WriteLine(
                    $"HumanSolve成功: " +
                    $"{solvedCount}/{TestCount}");

                System.Diagnostics.Debug.WriteLine(
                    $"Fallback: " +
                    $"{fallbackCount}/{TestCount}");

                System.Diagnostics.Debug.WriteLine(
                    $"実測難易度: " +
                    $"{string.Join(
                        ", ",
                        resultCounts.Select(
                            kv => $"{kv.Key}={kv.Value}"))}");
            }
        }
    }

}
