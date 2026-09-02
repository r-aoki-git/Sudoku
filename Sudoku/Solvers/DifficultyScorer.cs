namespace Sudoku.Solvers;

public enum Difficulty { Easy, Normal, Hard, Expert, Master }

public enum DifficultySolveStatus
{
    Solved,
    Stuck,
    Timeout,
    Invalid
}

public record DifficultyResult(
    Difficulty Label,
    int Score,
    DifficultySolveStatus Status,
    int MaxLevel,
    int Remaining,
    bool UsedFallback);

/// <summary>
/// HumanSolverの結果からスコアと難易度ラベルを算出する。
///
/// 【設計方針】
/// スコアは「その盤面を解くのに、レベル2以上の推論が何回必要だったか」で決まる。
/// レベル1（Naked / Hidden Single、ケージ合計だけを見た候補削り）は
/// どんな盤面でも大量に発生するため、難易度の指標にはならない。
///
/// 【スコア境界の根拠】
/// 平均ケージサイズを 1.8 / 2.2 / 2.5 / 2.8 / 3.0 と変えて
/// 唯一解の盤面を数百枚ずつ実測したときのスコア分布（中央値）は
///   1.8 → 15、2.2 → 86、2.5 → 379、2.8 → 712、3.0 → 777
/// だった。境界はこの分布の谷にあたる位置へ置いている。
///
/// 【Masterの定義】
/// 「人間解法で解けない盤面」をMasterとはしない。
/// それはプレイヤーがアプリ内のヒント機能で最後まで到達できない盤面を意味し、
/// 難易度ではなく破綻だからである。Masterは
/// 「人間解法で解けるが、レベル3以上の推論を最も多く要求する盤面」とする。
/// </summary>
public class DifficultyScorer
{
    /// <summary>
    /// レベルごとの1回あたりの重み。
    /// レベル1は難易度に寄与しない（どの盤面でも数百回発生するため）。
    /// </summary>
    private static readonly Dictionary<int, int> LevelWeights = new()
    {
        [1] = 0,
        [2] = 2,
        [3] = 6,
        [4] = 25,
    };

    /// <summary>
    /// レベルの重みに上乗せする、テクニック固有のボーナス。
    /// 同じレベルの中でも、人間にとっての負荷が違うものを区別する。
    /// </summary>
    private static readonly Dictionary<string, int> TechniqueBonus = new()
    {
        // Level 2
        ["45 Rule (Single Unit)"] = 0,

        // Level 3
        ["Killer Locked Candidates"] = 0,
        ["Locked Candidates"] = 0,
        ["Cage Combination"] = 1,
        ["Naked Pair"] = 1,
        ["Hidden Pair"] = 2,
        ["Naked Triple"] = 3,
        ["Hidden Triple"] = 4,
        ["Innie / Outie"] = 4,
        ["Killer Pair / Triple / Quad"] = 5,

        // Level 4
        ["X-Wing"] = 10,
        ["Swordfish"] = 20,
    };

    /// <summary>
    /// 各難易度のスコア上限（この値未満なら、その難易度）。
    /// Masterだけは上限を持たない。
    /// </summary>
    private static int? GetScoreUpperBoundExclusive(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => 40,
        Difficulty.Normal => 200,
        Difficulty.Hard => 500,
        Difficulty.Expert => 900,
        _ => null,
    };

    public DifficultyResult Evaluate(HumanSolveResult result)
    {
        int score =
            ComputeScore(
                result.TechniqueUsageCounts,
                result.TechniqueUsageByName);

        // 人間解法で最後まで解けなかった盤面は、どの難易度でもない。
        // 呼び出し側（生成器）はこれを棄却する。
        if (result.RequiredFallback)
        {
            return new DifficultyResult(
                Difficulty.Master,
                score,
                DifficultySolveStatus.Stuck,
                result.MaxLevelUsed,
                result.RemainingCells,
                true);
        }

        var label = GetDifficultyLabel(score);

        if (SolverDiagnostics.VerboseLogging)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DifficultyScorer] " +
                $"MaxLevel={result.MaxLevelUsed}, " +
                $"Score={score}, " +
                $"Label={label}");
        }

        return new DifficultyResult(
            label,
            score,
            DifficultySolveStatus.Solved,
            result.MaxLevelUsed,
            result.RemainingCells,
            false);
    }

    /// <summary>
    /// 求解の途中経過から、目標難易度へまだ到達しうるかを判定する。
    ///
    /// スコアは求解が進むほど単調非減少なので、
    /// 「現時点で既に目標難易度の上限を超えている」と分かった時点で、
    /// その後どれだけ求解を続けても目標難易度には一致しえないと確定できる。
    /// HumanSolver側はこれを使って、見込みのない求解を早期に打ち切る（枝刈り）。
    /// </summary>
    public static bool CanStillReach(
        Difficulty target,
        IReadOnlyDictionary<int, int> usageByLevelSoFar,
        IReadOnlyDictionary<string, int> usageByNameSoFar)
    {
        int? upperBound = GetScoreUpperBoundExclusive(target);

        // Masterには上限がないため、まだ否定できない。
        if (upperBound is null)
            return true;

        return ComputeScore(usageByLevelSoFar, usageByNameSoFar) < upperBound.Value;
    }

    private static int ComputeScore(
        IReadOnlyDictionary<int, int> usageByLevel,
        IReadOnlyDictionary<string, int> usageByName)
    {
        int score = 0;

        foreach (var (level, count) in usageByLevel)
        {
            if (count <= 0)
                continue;

            if (LevelWeights.TryGetValue(level, out int weight))
                score += count * weight;
        }

        foreach (var (name, count) in usageByName)
        {
            if (count <= 0)
                continue;

            if (TechniqueBonus.TryGetValue(name, out int bonus))
                score += count * bonus;
        }

        return score;
    }

    private static Difficulty GetDifficultyLabel(int score)
    {
        foreach (var candidate in new[]
                 {
                     Difficulty.Easy,
                     Difficulty.Normal,
                     Difficulty.Hard,
                     Difficulty.Expert,
                 })
        {
            var bound = GetScoreUpperBoundExclusive(candidate);

            if (bound is not null && score < bound.Value)
                return candidate;
        }

        return Difficulty.Master;
    }
}
