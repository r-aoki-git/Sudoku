using Sudoku.Models;
using Sudoku.Solvers.Techniques;
using System.Diagnostics;

namespace Sudoku.Solvers;

/// <summary>
/// 人間の解法テクニックを易しい順に適用して盤面を解く。
/// どのテクニックでも進められなくなったら、唯一解の担保のためバックトラッキングにフォールバックする
/// （この場合 RequiredFallback = true。難易度判定でMasterの根拠になる）。
///
/// テクニック一覧とフォールバック処理は、キラーモード向けのサブクラス（KillerHumanSolver）から
/// 差し替えられるよう、protectedコンストラクタで注入できる形にしている。
/// </summary>
public class HumanSolver
{
    private readonly List<ISolvingTechnique> _techniques;
    private readonly Func<Board, bool> _fallbackSolve;

    /// <summary> 通常モード向けの標準構成 </summary>
    public HumanSolver() : this(CreateClassicTechniques(), board => new BacktrackingSolver().TrySolve(board))
    {
    }
    /// <summary> キラーモードなど、テクニック一覧とフォールバック処理を差し替えたいサブクラス向け </summary>
    protected HumanSolver(List<ISolvingTechnique> techniques, Func<Board, bool> fallbackSolve)
    {
        _techniques = techniques;
        _fallbackSolve = fallbackSolve;
    }

    private static List<ISolvingTechnique> CreateClassicTechniques() => new()
    {
        new NakedSingleTechnique(),
        new HiddenSingleTechnique(),
        new LockedCandidatesTechnique(),
        new NakedSubsetTechnique(2),
        new HiddenSubsetTechnique(2),
        new NakedSubsetTechnique(3),
        new HiddenSubsetTechnique(3),
        new FishTechnique(2),
        new FishTechnique(3),
    };

    /// <param name="targetDifficulty">
    /// 生成処理から狙っている難易度が分かっている場合に指定する。
    /// スコア・MaxLevelは求解が進むにつれて単調非減少のため、
    /// 「これ以上進めても目標難易度には一致し得ない」と判明した時点で即座に打ち切る（枝刈り）。
    /// 難易度判定を伴わない用途（「解答を見る」機能など）ではnullのままでよい。
    /// </param>
    public HumanSolveResult Solve(
        Board board,
        int timeBudgetMs = 1500,
        Difficulty? targetDifficulty = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(board);

        if (timeBudgetMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeBudgetMs));

        var stopwatch = Stopwatch.StartNew();

        cancellationToken.ThrowIfCancellationRequested();

        var workingBoard = board.Clone();

        var usageCounts =
            new Dictionary<int, int>();

        var usageByName =
            new Dictionary<string, int>();

        int maxLevelUsed = 0;

        var candidates =
            CandidateGrid.Calculate(workingBoard);

        while (!workingBoard.IsComplete())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ---------------------------------------------------------
            // HumanSolver全体の時間制限
            // ---------------------------------------------------------
            if (stopwatch.ElapsedMilliseconds >= timeBudgetMs)
            {
                int remainingCells =
                    CountRemainingCells(workingBoard);

                return new HumanSolveResult(
                    Solved: false,
                    RequiredFallback: true,
                    FallbackSolved: false,
                    MaxLevelUsed: maxLevelUsed,
                    RemainingCells: remainingCells,
                    TechniqueUsageCounts: usageCounts,
                    TechniqueUsageByName: usageByName);
            }

            bool progressed = false;

            foreach (var technique in _techniques)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (stopwatch.ElapsedMilliseconds >= timeBudgetMs)
                {
                    int remainingCells =
                        CountRemainingCells(workingBoard);

                    return new HumanSolveResult(
                        Solved: false,
                        RequiredFallback: true,
                        FallbackSolved: false,
                        MaxLevelUsed: maxLevelUsed,
                        RemainingCells: remainingCells,
                        TechniqueUsageCounts: usageCounts,
                        TechniqueUsageByName: usageByName);
                }

                if (!technique.TryApply(
                        workingBoard,
                        candidates))
                {
                    continue;
                }

                usageCounts[technique.Level] =
                    usageCounts.GetValueOrDefault(technique.Level) + 1;

                usageByName[technique.Name] =
                    usageByName.GetValueOrDefault(technique.Name) + 1;

                maxLevelUsed =
                    Math.Max(
                        maxLevelUsed,
                        technique.Level);

                progressed = true;

                if (technique.PlacesValue)
                {
                    candidates =
                        CandidateGrid.Calculate(
                            workingBoard);
                }

                // ---------------------------------------------------------
                // 目標難易度による早期打ち切り（枝刈り）
                // ---------------------------------------------------------
                if (targetDifficulty.HasValue &&
                    !DifficultyScorer.CanStillReach(
                        targetDifficulty.Value,
                        maxLevelUsed,
                        usageByName))
                {
                    int remainingCells =
                        CountRemainingCells(workingBoard);

                    return new HumanSolveResult(
                        Solved: false,
                        RequiredFallback: false,
                        FallbackSolved: false,
                        MaxLevelUsed: maxLevelUsed,
                        RemainingCells: remainingCells,
                        TechniqueUsageCounts: usageCounts,
                        TechniqueUsageByName: usageByName,
                        EarlyRejected: true);
                }

                break;
            }

            if (!progressed)
            {
                int remainingCells =
                    CountRemainingCells(workingBoard);

                bool solvedByFallback =
                    _fallbackSolve(workingBoard);

                return new HumanSolveResult(
                    Solved: solvedByFallback,
                    RequiredFallback: true,
                    FallbackSolved: solvedByFallback,
                    MaxLevelUsed: maxLevelUsed,
                    RemainingCells: remainingCells,
                    TechniqueUsageCounts: usageCounts,
                    TechniqueUsageByName: usageByName);
            }
        }

        return new HumanSolveResult(
            Solved: true,
            RequiredFallback: false,
            FallbackSolved: false,
            MaxLevelUsed: maxLevelUsed,
            RemainingCells: 0,
            TechniqueUsageCounts: usageCounts,
            TechniqueUsageByName: usageByName);
    }

    private static int CountRemainingCells(Board board)
    {
        int remainingCells = 0;

        for (int r = 0; r < Board.Size; r++)
        {
            for (int c = 0; c < Board.Size; c++)
            {
                if (!board.GetCell(r, c).HasValue)
                {
                    remainingCells++;
                }
            }
        }

        return remainingCells;
    }
}