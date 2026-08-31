using Sudoku.Models;
using Sudoku.Solvers.Techniques;

namespace Sudoku.Solvers;

/// <summary>
/// キラーナンプレ向けのHumanSolver。通常モードの基本テクニック（Naked / Hidden Single）に加え、
/// ケージ合計の組み合わせ推論・45の法則・イニー/アウティー等のキラー固有テクニックを追加する。
/// </summary>
public class KillerHumanSolver : HumanSolver
{
    public KillerHumanSolver(List<Cage> cages)
        : base(
            BuildTechniques(cages),
            (board, cancellationToken) =>
                new KillerBacktrackingSolver(
                    cages,
                    cancellationToken)
                    .TrySolve(
                        board,
                        timeBudgetMs: 5000,
                        cancellationToken: cancellationToken))
    {
    }

    private static List<ISolvingTechnique> BuildTechniques(List<Cage> cages) => new()
    {
        // ---------------------------------------------------------
        // レベル1
        // ---------------------------------------------------------
        new NakedSingleTechnique(),
        new HiddenSingleTechnique(),
        new CageForcedComboTechnique(cages),

        // ---------------------------------------------------------
        // レベル2
        // ---------------------------------------------------------
        new FortyFiveRuleTechnique(cages),

        // ---------------------------------------------------------
        // レベル3
        // ---------------------------------------------------------
        new KillerLockedCandidatesTechnique(),
        new KillerPairTripleTechnique(cages),
        new InnieOutieTechnique(cages),

        new NakedSubsetTechnique(2),
        new HiddenSubsetTechnique(2),
        new NakedSubsetTechnique(3),
        new HiddenSubsetTechnique(3),

        // ---------------------------------------------------------
        // レベル4
        // ---------------------------------------------------------
        new FishTechnique(2),
        new FishTechnique(3),
    };
}