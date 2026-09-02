using Sudoku.Models;
using Sudoku.Solvers;
using System.Diagnostics;

namespace Sudoku.Generators;

/// <summary>
/// キラーナンプレの問題生成。完成盤面の生成→ケージ分割→唯一解の検証→難易度判定、
/// をリトライしながら行う。
/// 生成処理全体で使える時間に上限を設け、その残り時間を毎回の検証に配分することで、
/// リトライが重なってもトータルの待ち時間が青天井にならないようにしている。
///
/// 【検証の順序】
/// ケージ構造 1件あたりのコストと通過率の実測値は次のとおり:
///
///   検証            コスト        通過率(Expert相当)
///   ケージ分割      0.1ms         100%
///   唯一解検証      5〜40ms        約23%
///   難易度判定      100〜200ms     約48%
///
/// 唯一解検証のほうが「安く、よく落ちる」ため、必ず先に走らせる。
/// 逆順（難易度判定が先）だと、唯一解ですらない構造に対して
/// 毎回100ms以上の人間解法を回すことになり、スループットが数倍悪化する。
/// </summary>
public class KillerSudokuGenerator
{
    public const int DefaultOverallBudgetMs = 10000;
    private const int MinAttemptBudgetMs = 50;

    // Union-Find方式のケージ生成は非常に高速（< 1ms）なので、
    // ケージ生成に割り当てる予算はごく小さくてよい。
    private const int CageBudgetMs = 50;

    // ------------------------------------------------------------
    // 唯一解検証の予算。
    //
    // 実測では、唯一解と確定するケージ構造の検証は平均 5〜40ms、
    // 最悪でも 300ms 以内に終わる。
    // それ以上かかる構造は、探索が爆発している＝ほぼ確実に
    // 「唯一解ではない」か「判定コストが極端に高い」構造なので、
    // 粘るより捨てて引き直したほうが速い。
    //
    // 難易度ごとに変える必要はない。ブロック跨ぎバイアスによって
    // Expert / Master でも検証コストは Hard と同程度に収まっている。
    // ------------------------------------------------------------
    private const int UniquenessBudgetMs = 400;

    // ------------------------------------------------------------
    // 人間解法（難易度判定）の予算。
    //
    // 平均ケージサイズが大きいほど1手あたりの組み合わせ解析が重くなるため、
    // 上位難易度ほど多めに与える。
    // ------------------------------------------------------------
    private static int GetHumanBudgetMs(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 400,
        Difficulty.Normal => 500,
        Difficulty.Hard => 800,
        Difficulty.Expert => 1200,
        Difficulty.Master => 1500,
        _ => 800
    };

    private readonly Random _random;
    private readonly BacktrackingSolver _solver;
    private readonly CageGenerator _cageGenerator;
    private readonly DifficultyScorer _difficultyScorer;

    private Board? _solution;
    private int _attemptsSinceNewSolution;
    private int _totalAttempts;
    private int _solutionRegenerations;

    // 完成盤面そのものは唯一解率にほとんど影響しないので、
    // 高コストな完成盤面生成は頻繁にやり直さない。
    private const int RegenerateSolutionAfter = 300;

    public KillerSudokuGenerator(Random? random = null)
    {
        _random = random ?? new Random();
        _solver = new BacktrackingSolver(_random);
        _cageGenerator = new CageGenerator(_random);
        _difficultyScorer = new DifficultyScorer();
    }

    /// <summary>
    /// 指定予算内で1回の生成を試みる。
    /// 予算切れは通常の失敗なので例外を投げず、nullを返す。
    /// CancellationTokenのキャンセルだけは例外として扱う。
    /// </summary>
    public (Board Solution, List<Cage> Cages)? TryGenerate(
        Difficulty difficulty,
        int budgetMs,
        CancellationToken cancellationToken = default)
    {
        if (budgetMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgetMs));

        var overallStopwatch = Stopwatch.StartNew();

        cancellationToken.ThrowIfCancellationRequested();

        int humanBudgetLimit = GetHumanBudgetMs(difficulty);

        while (overallStopwatch.ElapsedMilliseconds < budgetMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attemptStopwatch = Stopwatch.StartNew();

            // ---------------------------------------------------------
            // 完成盤面の用意
            // ---------------------------------------------------------
            if (_solution is null ||
                _attemptsSinceNewSolution >= RegenerateSolutionAfter)
            {
                _solution = new Board();
                _solutionRegenerations++;

                if (!_solver.TryGenerateFullGrid(_solution, cancellationToken))
                {
                    _solution = null;
                    continue;
                }

                _attemptsSinceNewSolution = 0;
            }

            long remaining = budgetMs - overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            // ---------------------------------------------------------
            // ① ケージ分割
            // ---------------------------------------------------------
            var cageStopwatch = Stopwatch.StartNew();

            var cages =
                _cageGenerator.GenerateCages(
                    _solution,
                    difficulty,
                    CageBudgetMs,
                    cancellationToken);

            cageStopwatch.Stop();

            _attemptsSinceNewSolution++;
            _totalAttempts++;

            if (cages is null)
            {
                attemptStopwatch.Stop();

                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[CageReject] " +
                        $"Attempts={_totalAttempts}, " +
                        $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                        $"Cage={cageStopwatch.ElapsedMilliseconds}ms");
                }

                continue;
            }

            // ---------------------------------------------------------
            // ② 唯一解の検証。
            //
            // 安くて通過率が低い（＝よく落ちる）ので、難易度判定より先に行う。
            // 予算内に判定しきれなかった構造は、粘らずに棄却して引き直す。
            // ---------------------------------------------------------
            remaining = budgetMs - overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            int uniquenessBudget =
                (int)Math.Min(UniquenessBudgetMs, remaining);

            var uniquenessStopwatch = Stopwatch.StartNew();

            int uniquenessResult =
                new KillerBacktrackingSolver(cages, cancellationToken)
                    .CheckUniqueAgainstKnownSolution(
                        new Board(),
                        _solution,
                        timeBudgetMs: uniquenessBudget,
                        cancellationToken: cancellationToken);

            uniquenessStopwatch.Stop();

            if (uniquenessResult != 1)
            {
                attemptStopwatch.Stop();

                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[UniquenessReject] " +
                        $"Result={uniquenessResult}, " +
                        $"Attempts={_totalAttempts}, " +
                        $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                        $"Cage={cageStopwatch.ElapsedMilliseconds}ms, " +
                        $"Unique={uniquenessStopwatch.ElapsedMilliseconds}ms, " +
                        $"Budget={uniquenessBudget}ms");
                }

                continue;
            }

            // ---------------------------------------------------------
            // ③ 唯一解と確定した構造についてのみ、難易度を判定する。
            // ---------------------------------------------------------
            remaining = budgetMs - overallStopwatch.ElapsedMilliseconds;

            if (remaining < MinAttemptBudgetMs)
                break;

            int humanBudget = (int)Math.Min(humanBudgetLimit, remaining);

            var humanStopwatch = Stopwatch.StartNew();

            var humanResult =
                new KillerHumanSolver(cages)
                    .Solve(
                        new Board(),
                        timeBudgetMs: humanBudget,
                        targetDifficulty: difficulty,
                        cancellationToken: cancellationToken);

            humanStopwatch.Stop();

            if (humanResult.EarlyRejected)
            {
                attemptStopwatch.Stop();

                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[EarlyReject] " +
                        $"Difficulty={difficulty}, " +
                        $"Attempts={_totalAttempts}, " +
                        $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                        $"Human={humanStopwatch.ElapsedMilliseconds}ms");
                }

                continue;
            }

            // 人間解法で解ききれない盤面は、どの難易度としても採用しない。
            // プレイヤーがアプリ内の解法で最後まで到達できない盤面になるため。
            if (humanResult.RequiredFallback)
            {
                attemptStopwatch.Stop();

                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[StuckReject] " +
                        $"Requested={difficulty}, " +
                        $"Remaining={humanResult.RemainingCells}, " +
                        $"Attempts={_totalAttempts}, " +
                        $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                        $"Human={humanStopwatch.ElapsedMilliseconds}ms");
                }

                continue;
            }

            var difficultyResult = _difficultyScorer.Evaluate(humanResult);

            if (difficultyResult.Label != difficulty)
            {
                attemptStopwatch.Stop();

                if (SolverDiagnostics.VerboseLogging)
                {
                    Debug.WriteLine(
                        $"[DifficultyReject] " +
                        $"Requested={difficulty}, " +
                        $"Actual={difficultyResult.Label}, " +
                        $"Score={difficultyResult.Score}, " +
                        $"Attempts={_totalAttempts}, " +
                        $"AttemptElapsed={attemptStopwatch.ElapsedMilliseconds}ms, " +
                        $"Human={humanStopwatch.ElapsedMilliseconds}ms");
                }

                continue;
            }

            attemptStopwatch.Stop();

            if (SolverDiagnostics.VerboseLogging)
            {
                Debug.WriteLine(
                    $"[成功] " +
                    $"経過{overallStopwatch.ElapsedMilliseconds}ms, " +
                    $"今回試行={attemptStopwatch.ElapsedMilliseconds}ms " +
                    $"(Cage={cageStopwatch.ElapsedMilliseconds}ms, " +
                    $"Unique={uniquenessStopwatch.ElapsedMilliseconds}ms, " +
                    $"Human={humanStopwatch.ElapsedMilliseconds}ms), " +
                    $"試行{_totalAttempts}回, " +
                    $"完成盤面の作り直し{_solutionRegenerations}回, " +
                    $"Score={difficultyResult.Score}");
            }

            return (_solution, cages);
        }

        return null;
    }

    /// <summary>完成盤面（正解）とケージ分割の両方を返す。</summary>
    public (Board Solution, List<Cage> Cages) Generate(
        Difficulty difficulty,
        int budgetMs,
        CancellationToken cancellationToken = default)
    {
        var result = TryGenerate(difficulty, budgetMs, cancellationToken);

        if (result is { } success)
            return success;

        throw new InvalidOperationException(
            $"キラーナンプレの生成に失敗しました。予算{budgetMs}ms, " +
            $"試行回数: {_totalAttempts}回, " +
            $"完成盤面の作り直し回数: {_solutionRegenerations}回");
    }
}
