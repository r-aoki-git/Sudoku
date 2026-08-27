using Sudoku.Models;
using Sudoku.Solvers;
using System.Diagnostics;

namespace Sudoku.Generators;

/// <summary>
/// キラーナンプレの問題生成。完成盤面の生成→ケージ分割→唯一解の検証、をリトライしながら行う。
/// 生成処理全体で使える時間に上限（OverallBudgetMs）を設け、その残り時間を毎回の検証に配分することで、
/// リトライが重なってもトータルの待ち時間が青天井にならないようにしている。
/// </summary>
public class KillerSudokuGenerator
{
    private const int OverallBudgetMs = 10000; // 生成処理全体に許す時間の上限（10秒。制約伝播により通常はごく短時間で収まるはず）
    private const int MinAttemptBudgetMs = 50; // 1回の検証に最低限確保する時間
    private const int CageBudgetMs = 300;
    private const int HumanBudgetMs = 1500;

    private readonly Random _random;
    private readonly BacktrackingSolver _solver;
    private readonly CageGenerator _cageGenerator;
    private readonly DifficultyScorer _difficultyScorer;

    public KillerSudokuGenerator(Random? random = null)
    {
        _random = random ?? new Random();
        _solver = new BacktrackingSolver(_random);
        _cageGenerator = new CageGenerator(_random);
        _difficultyScorer = new DifficultyScorer();
    }

    /// <summary>完成盤面（正解）とケージ分割の両方を返す。</summary>
    public (Board Solution, List<Cage> Cages) Generate(Difficulty difficulty)
    {
        var overallStopwatch = System.Diagnostics.Stopwatch.StartNew();

        Board? solution = null;
        int attemptsSinceNewSolution = 0;
        int totalAttempts = 0;
        int solutionRegenerations = 0;
        const int RegenerateSolutionAfter = 200;

        while (overallStopwatch.ElapsedMilliseconds < OverallBudgetMs)
        {
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

            long remaining = OverallBudgetMs - overallStopwatch.ElapsedMilliseconds;
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
                // CageGeneratorにも同じ総予算を渡す。
                // 生成器内部で長時間ブロックして外側の10秒制限を破らないようにする。
                cages = _cageGenerator.GenerateCages(solution, difficulty, cageBudget);
            }
            catch (InvalidOperationException ex)
            {
                System.Diagnostics.Debug.WriteLine("[KillerSudokuGenerator] InvalidOperationException:");
                System.Diagnostics.Debug.WriteLine(ex.ToString());

                // この完成盤面では合法なケージ分割を作れなかっただけ。
                // 生成全体の失敗にはせず、次の試行へ進む。
                attemptsSinceNewSolution++;
                totalAttempts++;
                continue;
            }

            attemptsSinceNewSolution++;
            totalAttempts++;

            // ====== HumanSolverによる難易度確認 ======
            // 唯一解検証より先に行う。
            // 指定難易度として解けないケージに対して
            // 高コストな唯一解探索を実行しない。

            if (!IsCageStructureAcceptable(cages, difficulty))
            {
                Debug.WriteLine(
                    $"[StructureReject] Difficulty={difficulty}, " +
                    $"Cages={cages.Count}, " +
                    $"Singles={cages.Count(c => c.Cells.Count == 1)}");

                continue;
            }

            var humanSolver = new KillerHumanSolver(cages);

            var humanResult = humanSolver.Solve(new Board(), timeBudgetMs: HumanBudgetMs);

            var difficultyResult = _difficultyScorer.Evaluate(humanResult);

            System.Diagnostics.Debug.WriteLine(
                $"[DifficultyCheck] " +
                $"Requested={difficulty}, " +
                $"Actual={difficultyResult.Label}, " +
                $"Status={difficultyResult.Status}, " +
                $"Score={difficultyResult.Score}, " +
                $"MaxLv={difficultyResult.MaxLevel}, " +
                $"Fallback={difficultyResult.UsedFallback}, " +
                $"Remaining={difficultyResult.Remaining}");

            // 人間解法で最後まで解けなかった場合は不採用
            if (humanResult.RequiredFallback)
                continue;

            // 指定難易度でなければ不採用
            if (difficultyResult.Label != difficulty)
                continue;

            // ====== 唯一解検証 ======
            // 難易度条件を通過した候補だけ高コストな唯一解検証を行う。
            remaining = OverallBudgetMs - overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            var killerSolver = new KillerBacktrackingSolver(cages);

            int solutionCount = killerSolver.CountSolutions(
                new Board(),
                limit: 2,
                timeBudgetMs: (int)remaining);

            if (solutionCount != 1)
                continue;

            System.Diagnostics.Debug.WriteLine(
                $"[成功] " +
                $"経過{overallStopwatch.ElapsedMilliseconds}ms, " +
                $"試行{totalAttempts}回, " +
                $"完成盤面の作り直し{solutionRegenerations}回");

            return (solution, cages);
        }

        throw new InvalidOperationException(
            $"キラーナンプレの生成に失敗しました。実際の経過時間: {overallStopwatch.ElapsedMilliseconds}ms, " +
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