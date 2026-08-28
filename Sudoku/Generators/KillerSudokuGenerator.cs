using Sudoku.Models;
using Sudoku.Solvers;
using System.Diagnostics;

namespace Sudoku.Generators;

/// <summary>
/// キラーナンプレの問題生成。完成盤面の生成→ケージ分割→唯一解の検証、をリトライしながら行う。
/// 生成処理全体で使える時間に上限（OverallBudgetMs）を設け、その残り時間を毎回の検証に配分することで、
/// リトライが重なってもトータルの待ち時間が青天井にならないようにしている。
///
/// OverallBudgetMsはコンストラクタで指定可能。ParallelKillerSudokuGeneratorが
/// 「短い予算で何度も使い捨てる」ワーカーを構成する際に利用する。
/// </summary>
public class KillerSudokuGenerator
{
    public const int DefaultOverallBudgetMs = 10000; // 生成処理全体に許す時間の上限（10秒。制約伝播により通常はごく短時間で収まるはず）
    private const int MinAttemptBudgetMs = 50; // 1回の検証に最低限確保する時間
    private const int CageBudgetMs = 300;
    private const int HumanBudgetMs = 1500;

    private readonly int _overallBudgetMs;
    private readonly Random _random;
    private readonly BacktrackingSolver _solver;
    private readonly CageGenerator _cageGenerator;
    private readonly DifficultyScorer _difficultyScorer;

    public KillerSudokuGenerator(Random? random = null, int overallBudgetMs = DefaultOverallBudgetMs)
    {
        if (overallBudgetMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(overallBudgetMs));

        _random = random ?? new Random();
        _solver = new BacktrackingSolver(_random);
        _cageGenerator = new CageGenerator(_random);
        _difficultyScorer = new DifficultyScorer();
        _overallBudgetMs = overallBudgetMs;
    }

    /// <summary>完成盤面（正解）とケージ分割の両方を返す。</summary>
    public (Board Solution, List<Cage> Cages) Generate(
        Difficulty difficulty,
        CancellationToken cancellationToken = default)
    {
        var overallStopwatch = System.Diagnostics.Stopwatch.StartNew();

        Board? solution = null;
        int attemptsSinceNewSolution = 0;
        int totalAttempts = 0;
        int solutionRegenerations = 0;
        const int RegenerateSolutionAfter = 200;

        while (overallStopwatch.ElapsedMilliseconds < _overallBudgetMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (solution is null || attemptsSinceNewSolution >= RegenerateSolutionAfter)
            {
                solution = new Board();
                solutionRegenerations++;
                if (!_solver.TryGenerateFullGrid(solution))
                {
                    solution = null;
                    continue;
                }
                attemptsSinceNewSolution = 0;
            }

            long remaining = _overallBudgetMs - overallStopwatch.ElapsedMilliseconds;
            if (remaining < MinAttemptBudgetMs)
                break;

            int cageBudget = (int)Math.Min(
                CageBudgetMs,
                Math.Max(
                    MinAttemptBudgetMs,
                    remaining - HumanBudgetMs - MinAttemptBudgetMs));

            List<Cage> cages;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                cages = _cageGenerator.GenerateCages(solution, difficulty, cageBudget, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                System.Diagnostics.Debug.WriteLine("[KillerSudokuGenerator] InvalidOperationException:");
                System.Diagnostics.Debug.WriteLine(ex.ToString());

                attemptsSinceNewSolution++;
                totalAttempts++;
                continue;
            }

            attemptsSinceNewSolution++;
            totalAttempts++;

            if (!IsCageStructureAcceptable(cages, difficulty))
            {
                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[StructureReject] Difficulty={difficulty}, " +
                        $"Cages={cages.Count}, " +
                        $"Singles={cages.Count(c => c.Cells.Count == 1)}");
                }

                continue;
            }

            remaining = _overallBudgetMs - overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            int humanBudget = (int)Math.Min(HumanBudgetMs, remaining);

            var humanSolver = new KillerHumanSolver(cages);

            var humanResult = humanSolver.Solve(
                new Board(),
                timeBudgetMs: humanBudget,
                targetDifficulty: difficulty);

            if (humanResult.EarlyRejected)
            {
                Debug.WriteLine(
                    $"[EarlyReject] Difficulty={difficulty}, " +
                    $"MaxLevel={humanResult.MaxLevelUsed}");

                continue;
            }

            var difficultyResult = _difficultyScorer.Evaluate(humanResult);

            if (SolverDiagnostics.VerboseLogging)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DifficultyCheck] " +
                    $"Requested={difficulty}, " +
                    $"Actual={difficultyResult.Label}, " +
                    $"Status={difficultyResult.Status}, " +
                    $"Score={difficultyResult.Score}, " +
                    $"MaxLv={difficultyResult.MaxLevel}, " +
                    $"Fallback={difficultyResult.UsedFallback}, " +
                    $"Remaining={difficultyResult.Remaining}");
            }

            if (humanResult.RequiredFallback)
                continue;

            if (difficultyResult.Label != difficulty)
                continue;

            remaining = _overallBudgetMs - overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            var killerSolver = new KillerBacktrackingSolver(cages);

            int solutionCount = killerSolver.CountSolutions(
                new Board(),
                limit: 2,
                timeBudgetMs: (int)remaining);

            if (solutionCount != 1)
                continue;

            if (SolverDiagnostics.VerboseLogging)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[成功] " +
                    $"経過{overallStopwatch.ElapsedMilliseconds}ms, " +
                    $"試行{totalAttempts}回, " +
                    $"完成盤面の作り直し{solutionRegenerations}回");
            }

            return (solution, cages);
        }

        throw new InvalidOperationException(
            $"キラーナンプレの生成に失敗しました。実際の経過時間: {overallStopwatch.ElapsedMilliseconds}ms " +
            $"(予算{_overallBudgetMs}ms), " +
            $"試行回数: {totalAttempts}回, 完成盤面の作り直し回数: {solutionRegenerations}回");
    }

    private static bool IsCageStructureAcceptable(
    List<Cage> cages,
    Difficulty difficulty)
    {
        int singles =
            cages.Count(c => c.Cells.Count == 1);

        int nonSingles =
            cages.Count(c => c.Cells.Count >= 2);

        return difficulty switch
        {
            Difficulty.Easy =>
                singles <= 20,

            Difficulty.Normal =>
                singles <= 14,

            Difficulty.Hard =>
                singles <= 7 &&
                nonSingles >= 18,

            Difficulty.Expert =>
                singles <= 5 &&
                nonSingles >= 20,

            Difficulty.Master =>
                singles <= 3 &&
                nonSingles >= 22,

            _ => true
        };
    }
}